using System.Diagnostics;
using NAudio.Wave;
using DeepFilterNetGui.Audio;

namespace DeepFilterNetGui.Services;

internal sealed class DenoiseSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly DeepFilterNetDenoiser _denoiser;
    private readonly StftProcessor _stft;
    private readonly Func<double> _latencyProvider;
    private readonly Action<float[]>? _waveform;
    private readonly Action<float[]>? _spectrum;
    private readonly Action<Metrics>? _metrics;
    private readonly Func<TimingSnapshot?>? _inputResampleSnapshot;
    private readonly Func<TimingSnapshot?>? _outputChainSnapshot;

    private readonly int _hopSize;
    private readonly int _sampleRate;
    private readonly int _fftBins;
    private readonly float[] _hop;
    private readonly float[] _spec;
    private readonly float[] _outHop;
    private int _outIndex;

    private readonly Stopwatch _uiStopwatch = Stopwatch.StartNew();
    private long _lastUiTick;
    private double _avgMs;
    private volatile bool _stopping;
    private bool _loggedError;

    public DenoiseSampleProvider(
        ISampleProvider source,
        DeepFilterNetDenoiser denoiser,
        int sampleRate,
        Func<double> latencyProvider,
        Action<float[]>? waveform,
        Action<float[]>? spectrum,
        Action<Metrics>? metrics,
        Func<TimingSnapshot?>? inputResampleSnapshot,
        Func<TimingSnapshot?>? outputChainSnapshot)
    {
        _source = source;
        _denoiser = denoiser;
        _hopSize = _denoiser.FrameSize;
        _sampleRate = sampleRate;
        int fftSize = ChooseFftSize(_hopSize);
        _stft = new StftProcessor(fftSize, _hopSize);
        _fftBins = fftSize / 2 + 1;
        _latencyProvider = latencyProvider;
        _waveform = waveform;
        _spectrum = spectrum;
        _metrics = metrics;
        _inputResampleSnapshot = inputResampleSnapshot;
        _outputChainSnapshot = outputChainSnapshot;
        WaveFormat = source.WaveFormat;

        _hop = new float[_hopSize];
        _spec = new float[_fftBins * 2];
        _outHop = new float[_hopSize];
        _outIndex = _hopSize;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_stopping)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        try
        {
            int written = 0;
            while (written < count)
            {
                if (_outIndex >= _hopSize)
                {
                    int read = _source.Read(_hop, 0, _hopSize);
                    if (read <= 0)
                    {
                        Array.Clear(_hop, 0, _hopSize);
                    }
                    else if (read < _hopSize)
                    {
                        Array.Clear(_hop, read, _hopSize - read);
                    }

                    long t0 = Stopwatch.GetTimestamp();
                    _denoiser.ProcessFrame(_hop, _outHop);
                    long t1 = Stopwatch.GetTimestamp();

                    _stft.AnalyzeTo(_outHop, _spec);
                    long t2 = Stopwatch.GetTimestamp();

                    double inferMs = ToMs(t1 - t0);
                    double frameMs = inferMs + ToMs(t2 - t1);
                    UpdateMetrics(frameMs, inferMs, _spec);
                    _outIndex = 0;
                }

                int toCopy = Math.Min(count - written, _hopSize - _outIndex);
                Array.Copy(_outHop, _outIndex, buffer, offset + written, toCopy);
                _outIndex += toCopy;
                written += toCopy;
            }

            return written;
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

    private void UpdateMetrics(double frameMs, double onnxMs, float[] spec)
    {
        _avgMs = _avgMs == 0 ? frameMs : _avgMs * 0.9 + frameMs * 0.1;
        double rtf = frameMs / (_hopSize * 1000.0 / _sampleRate);

        var metrics = new Metrics
        {
            OnnxMs = onnxMs,
            FrameMs = frameMs,
            AvgMs = _avgMs,
            Rtf = rtf,
            LatencyMs = _latencyProvider(),
            InRms = ComputeRms(_hop),
            OutRms = ComputeRms(_outHop),
            Fps = frameMs > 0 ? 1000.0 / frameMs : 0
        };

        var now = _uiStopwatch.ElapsedMilliseconds;
        if (now - _lastUiTick > 33)
        {
            _lastUiTick = now;
            _waveform?.Invoke((float[])_outHop.Clone());
            _spectrum?.Invoke(ComputeSpectrum(spec));
            _metrics?.Invoke(metrics);
        }

    }

    private static double ToMs(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static float ComputeRms(float[] samples)
    {
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += samples[i] * samples[i];
        }
        return (float)Math.Sqrt(sum / samples.Length);
    }

    private float[] ComputeSpectrum(float[] spec)
    {
        int bins = _fftBins;
        var mags = new float[bins];
        for (int i = 0; i < bins; i++)
        {
            float re = spec[i * 2];
            float im = spec[i * 2 + 1];
            mags[i] = (float)Math.Sqrt(re * re + im * im);
        }
        return mags;
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

