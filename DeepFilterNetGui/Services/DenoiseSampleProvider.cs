using System.Diagnostics;
using NAudio.Wave;
using DeepFilterNetGui.Audio;

namespace DeepFilterNetGui.Services;

internal sealed class DenoiseSampleProvider : ISampleProvider
{
    private const float InputActivityThreshold = 1.0e-6f;
    private const double SilentResetThresholdSeconds = 0.5;
    private readonly ISampleProvider _source;
    private readonly DeepFilterRuntime _runtime;
    private readonly StftProcessor _stft;
    private readonly Func<double> _latencyProvider;
    private readonly Action<float[]>? _waveform;
    private readonly Action<float[]>? _spectrum;
    private readonly Action<Metrics>? _metrics;
    private readonly int _sourceChannels;
    private readonly int _outputChannels;
    private readonly int _sampleRate;
    private readonly int _analysisHopSize;
    private readonly int _fftBins;
    private readonly float[] _analysisHop;
    private readonly float[] _analysisSpec;
    private readonly Stopwatch _uiStopwatch = Stopwatch.StartNew();
    private float[] _sourceBuffer = Array.Empty<float>();
    private float[] _processedBuffer = Array.Empty<float>();
    private float[] _inputMonitor = Array.Empty<float>();
    private float[] _outputMonitor = Array.Empty<float>();
    private int _analysisFill;
    private long _lastUiTick;
    private long _silentInputFrames;
    private double _avgMs;
    private bool _runtimeResetForCurrentSilence;
    private bool _loggedError;
    private volatile bool _stopping;

    public DenoiseSampleProvider(
        ISampleProvider source,
        DeepFilterRuntime runtime,
        int sampleRate,
        int outputChannels,
        Func<double> latencyProvider,
        Action<float[]>? waveform,
        Action<float[]>? spectrum,
        Action<Metrics>? metrics)
    {
        _source = source;
        _runtime = runtime;
        _sourceChannels = source.WaveFormat.Channels;
        _outputChannels = Math.Max(1, outputChannels);
        _sampleRate = sampleRate;
        _latencyProvider = latencyProvider;
        _waveform = waveform;
        _spectrum = spectrum;
        _metrics = metrics;
        _analysisHopSize = Math.Max(1, _runtime.FrameSize);
        _stft = new StftProcessor(ChooseFftSize(_analysisHopSize), _analysisHopSize);
        _fftBins = _stft.FftSize / 2 + 1;
        _analysisHop = new float[_analysisHopSize];
        _analysisSpec = new float[_fftBins * 2];
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, _outputChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_stopping)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int outputFrames = count / _outputChannels;
        if (outputFrames <= 0)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        try
        {
            EnsureScratchCapacity(outputFrames);

            int sourceSamplesRequested = outputFrames * _sourceChannels;
            int sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesRequested);
            if (sourceSamplesRead < sourceSamplesRequested)
            {
                Array.Clear(_sourceBuffer, sourceSamplesRead, sourceSamplesRequested - sourceSamplesRead);
            }

            UpdateSilenceState(outputFrames);

            long t0 = Stopwatch.GetTimestamp();
            if (_runtimeResetForCurrentSilence)
            {
                Array.Clear(_processedBuffer, 0, outputFrames * _runtime.ChannelCount);
            }
            else
            {
                _runtime.ProcessFrame(_sourceBuffer, outputFrames, _processedBuffer);
            }
            long t1 = Stopwatch.GetTimestamp();

            BuildMonitorMix(_sourceBuffer, outputFrames, _sourceChannels, _inputMonitor);
            BuildMonitorMix(_processedBuffer, outputFrames, _runtime.ChannelCount, _outputMonitor);
            CaptureAnalysisFrames(outputFrames);

            WriteOutput(buffer, offset, outputFrames);

            long t2 = Stopwatch.GetTimestamp();
            double inferMs = ToMs(t1 - t0);
            double frameMs = inferMs + ToMs(t2 - t1);
            UpdateMetrics(outputFrames, frameMs, inferMs);

            return count;
        }
        catch (Exception ex)
        {
            if (!_loggedError)
            {
                _loggedError = true;
                AppLogger.Error("音频处理发生异常，已进入静音保护。", ex);
            }

            _stopping = true;
            Array.Clear(buffer, offset, count);
            return count;
        }
    }

    public void RequestStop()
    {
        _stopping = true;
    }

    private void EnsureScratchCapacity(int frames)
    {
        int sourceSamples = frames * _sourceChannels;
        int processedSamples = frames * _runtime.ChannelCount;

        if (_sourceBuffer.Length < sourceSamples)
            Array.Resize(ref _sourceBuffer, sourceSamples);
        if (_processedBuffer.Length < processedSamples)
            Array.Resize(ref _processedBuffer, processedSamples);
        if (_inputMonitor.Length < frames)
            Array.Resize(ref _inputMonitor, frames);
        if (_outputMonitor.Length < frames)
            Array.Resize(ref _outputMonitor, frames);
    }

    private void UpdateSilenceState(int frames)
    {
        bool hasRealInput = false;
        int totalSamples = frames * _sourceChannels;
        for (int i = 0; i < totalSamples; i++)
        {
            if (Math.Abs(_sourceBuffer[i]) > InputActivityThreshold)
            {
                hasRealInput = true;
                break;
            }
        }

        if (hasRealInput)
        {
            _silentInputFrames = 0;
            _runtimeResetForCurrentSilence = false;
            return;
        }

        _silentInputFrames = Math.Min(long.MaxValue - frames, _silentInputFrames) + frames;
        if (!_runtimeResetForCurrentSilence && _silentInputFrames >= GetSilentResetThresholdFrames())
        {
            _runtime.Reset();
            _runtimeResetForCurrentSilence = true;
        }
    }

    private long GetSilentResetThresholdFrames()
    {
        if (_sampleRate <= 0)
            return long.MaxValue;
        return (long)Math.Ceiling(_sampleRate * SilentResetThresholdSeconds);
    }

    private void BuildMonitorMix(float[] interleaved, int frames, int channels, float[] destination)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            int sourceBase = frame * channels;
            if (channels <= 1)
            {
                destination[frame] = interleaved[sourceBase];
            }
            else
            {
                destination[frame] = 0.5f * (interleaved[sourceBase] + interleaved[sourceBase + 1]);
            }
        }
    }

    private void CaptureAnalysisFrames(int frames)
    {
        int sourceIndex = 0;
        while (sourceIndex < frames)
        {
            int toCopy = Math.Min(_analysisHopSize - _analysisFill, frames - sourceIndex);
            Array.Copy(_outputMonitor, sourceIndex, _analysisHop, _analysisFill, toCopy);
            _analysisFill += toCopy;
            sourceIndex += toCopy;

            if (_analysisFill == _analysisHopSize)
            {
                _stft.AnalyzeTo(_analysisHop, _analysisSpec);
                _analysisFill = 0;
            }
        }
    }

    private void WriteOutput(float[] buffer, int offset, int frames)
    {
        Array.Clear(buffer, offset, frames * _outputChannels);

        int runtimeChannels = _runtime.ChannelCount;
        for (int frame = 0; frame < frames; frame++)
        {
            int destinationBase = offset + frame * _outputChannels;
            int sourceBase = frame * runtimeChannels;

            if (runtimeChannels == 1)
            {
                float mono = _processedBuffer[sourceBase];
                buffer[destinationBase] = mono;
                if (_outputChannels > 1)
                    buffer[destinationBase + 1] = mono;
            }
            else
            {
                float left = _processedBuffer[sourceBase];
                float right = _processedBuffer[sourceBase + 1];
                if (_outputChannels == 1)
                {
                    buffer[destinationBase] = 0.5f * (left + right);
                }
                else
                {
                    buffer[destinationBase] = left;
                    buffer[destinationBase + 1] = right;
                }
            }
        }
    }

    private void UpdateMetrics(int frames, double frameMs, double inferMs)
    {
        _avgMs = _avgMs == 0 ? frameMs : (_avgMs * 0.9) + (frameMs * 0.1);
        double rtf = frames > 0 ? frameMs / (frames * 1000.0 / _sampleRate) : 0;

        var metrics = new Metrics
        {
            InferMs = inferMs,
            FrameMs = frameMs,
            AvgMs = _avgMs,
            Rtf = rtf,
            LatencyMs = _latencyProvider(),
            InRms = ComputeRms(_inputMonitor, frames),
            OutRms = ComputeRms(_outputMonitor, frames),
            Fps = frameMs > 0 ? 1000.0 / frameMs : 0
        };

        long now = _uiStopwatch.ElapsedMilliseconds;
        if (now - _lastUiTick > 33)
        {
            _lastUiTick = now;
            _waveform?.Invoke((float[])_analysisHop.Clone());
            _spectrum?.Invoke(ComputeSpectrum());
            _metrics?.Invoke(metrics);
        }
    }

    private static double ToMs(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static float ComputeRms(float[] samples, int length)
    {
        if (length <= 0)
            return 0;

        double sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += samples[i] * samples[i];
        }

        return (float)Math.Sqrt(sum / length);
    }

    private float[] ComputeSpectrum()
    {
        var magnitudes = new float[_fftBins];
        for (int i = 0; i < _fftBins; i++)
        {
            float re = _analysisSpec[i * 2];
            float im = _analysisSpec[i * 2 + 1];
            magnitudes[i] = (float)Math.Sqrt((re * re) + (im * im));
        }

        return magnitudes;
    }

    private static int ChooseFftSize(int hopSize)
    {
        int size = 1;
        while (size < hopSize * 2)
        {
            size <<= 1;
        }

        return size;
    }
}
