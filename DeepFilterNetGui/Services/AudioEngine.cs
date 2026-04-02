using System;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PortAudioSharp;
using DeepFilterNetGui.Audio;

namespace DeepFilterNetGui.Services;

public sealed class AudioEngine : IDisposable
{
    private IWaveIn? _capture;
    private IWavePlayer? _output;
    private AsioOut? _asioOut;
    private BufferedWaveProvider? _inputBuffer;
    private ISampleProvider? _inputSampleProvider;
    private ISampleProvider? _outputSampleProvider;
    private DeepFilterRuntime? _runtime;
    private DenoiseSampleProvider? _denoiseProvider;
    private float _attenLimDb = 100f;
    private float _postFilterBeta;
    private ReduceMaskMode _reduceMask = ReduceMaskMode.Independent;
    private WaveFormat? _inputFormat;
    private WaveFormat? _outputFormat;
    private int _pipelineSampleRate;
    private int _processingChannels;
    private int _asioSampleRate;
    private int _asioInputChannels = 1;
    private int _asioOutputChannels = 2;
    private byte[]? _captureByteBuffer;
    private float[]? _captureFloatBuffer;
    private float[]? _asioInterleavedBuffer;
    private TimingAccumulator? _inputResampleTiming;
    private TimingAccumulator? _outputChainTiming;
    private Stream? _paStream;
    private Stream.Callback? _paCallback;
    private bool _paUseInput;
    private bool _paUseOutput;
    private bool _paStopping;
    private int _paSampleRate;
    private int _paInputChannels;
    private int _paOutputChannels;
    private SampleFormat _paSampleFormat;
    private float[]? _paInputBuffer;
    private short[]? _paInputInt16;
    private float[]? _paOutputBuffer;
    private short[]? _paOutputInt16;
    private AudioEngineConfig? _config;

    public event Action<float[]>? WaveformAvailable;
    public event Action<float[]>? SpectrumAvailable;
    public event Action<Metrics>? MetricsAvailable;

    public bool IsRunning { get; private set; }
    public int ActualInputSampleRate { get; private set; }
    public int ActualOutputSampleRate { get; private set; }
    public int ActualBufferSamples { get; private set; }
    public string ProcessingChannelMode { get; private set; } = "未运行";

    public void SetPostFilterBeta(float beta)
    {
        _postFilterBeta = Math.Clamp(beta, 0f, 0.05f);
        _runtime?.SetPostFilterBeta(_postFilterBeta);
    }

    public void SetDenoiseAttenLimit(float attenLimDb)
    {
        _attenLimDb = Math.Clamp(attenLimDb, 0f, 100f);
        _runtime?.SetAttenLimit(_attenLimDb);
    }

    public void SetReduceMask(ReduceMaskMode reduceMask)
    {
        _reduceMask = NormalizeReduceMask(reduceMask);
        _runtime?.SetReduceMask(_reduceMask);
    }

    public static IReadOnlyList<AudioDeviceItem> GetInputDevices(AudioBackendType backend)
    {
        switch (backend)
        {
            case AudioBackendType.Mme:
                return Enumerable.Range(0, WaveIn.DeviceCount)
                    .Select(i =>
                    {
                        var caps = WaveIn.GetCapabilities(i);
                        return new AudioDeviceItem(i.ToString(), caps.ProductName);
                    })
                    .ToList();
            case AudioBackendType.Asio:
                return GetAsioDrivers();
            case AudioBackendType.Ks:
                return PortAudioManager.GetKsInputDevices();
            case AudioBackendType.Wdm:
            default:
                using (var enumerator = new MMDeviceEnumerator())
                {
                    return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                        .Select(d => new AudioDeviceItem(d.ID, d.FriendlyName))
                        .ToList();
                }
        }
    }

    public static IReadOnlyList<AudioDeviceItem> GetOutputDevices(AudioBackendType backend)
    {
        switch (backend)
        {
            case AudioBackendType.Mme:
                return Enumerable.Range(0, WaveOut.DeviceCount)
                    .Select(i =>
                    {
                        var caps = WaveOut.GetCapabilities(i);
                        return new AudioDeviceItem(i.ToString(), caps.ProductName);
                    })
                    .ToList();
            case AudioBackendType.Asio:
                return GetAsioDrivers();
            case AudioBackendType.Ks:
                return PortAudioManager.GetKsOutputDevices();
            case AudioBackendType.Wdm:
            default:
                using (var enumerator = new MMDeviceEnumerator())
                {
                    return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                        .Select(d => new AudioDeviceItem(d.ID, d.FriendlyName))
                        .ToList();
                }
        }
    }

    private static IReadOnlyList<AudioDeviceItem> GetAsioDrivers()
    {
        try
        {
            var drivers = AsioOut.GetDriverNames();
            return drivers.Select(name => new AudioDeviceItem(name, name)).ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"枚举ASIO驱动失败: {ex.Message}");
            return Array.Empty<AudioDeviceItem>();
        }
    }

    public void Start(AudioBackendType inputBackend, AudioBackendType outputBackend, AudioDeviceItem inputDevice, AudioDeviceItem outputDevice, AppSettings settings)
    {
        if (IsRunning)
            return;
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        _config = BuildConfig(settings);
        _reduceMask = NormalizeReduceMask(settings.ReduceMask);
        ActualInputSampleRate = 0;
        ActualOutputSampleRate = 0;
        ActualBufferSamples = 0;
        ProcessingChannelMode = "未运行";

        _paStopping = false;

        bool useKsInput = inputBackend == AudioBackendType.Ks;
        bool useKsOutput = outputBackend == AudioBackendType.Ks;

        if (inputBackend == AudioBackendType.Asio || outputBackend == AudioBackendType.Asio)
        {
            SetupAsio(inputDevice, outputDevice);
        }
        else
        {
            if (useKsInput || useKsOutput)
            {
                SetupPortAudioStream(useKsInput, useKsOutput, inputDevice, outputDevice);
            }

            if (!useKsInput)
            {
                SetupCapture(inputBackend, inputDevice);
            }

            if (!useKsOutput)
            {
                SetupOutput(outputBackend, outputDevice);
            }
        }

        if (_inputFormat == null || _outputFormat == null)
            throw new InvalidOperationException("音频格式未初始化。");

        _pipelineSampleRate = DeterminePipelineSampleRate();
        _processingChannels = NormalizeCaptureChannels(_inputFormat.Channels);
        ProcessingChannelMode = _processingChannels == 2 ? "Stereo" : "Mono";
        ActualOutputSampleRate = _pipelineSampleRate;

        BuildInputSampleProvider(_pipelineSampleRate, _processingChannels);

        if (_inputSampleProvider == null)
            throw new InvalidOperationException("输入音频链路未初始化。");

        _runtime = new DeepFilterRuntime(_pipelineSampleRate, _processingChannels, _attenLimDb, _postFilterBeta, _reduceMask);

        _denoiseProvider = new DenoiseSampleProvider(
            _inputSampleProvider,
            _runtime,
            _pipelineSampleRate,
            Math.Max(1, _outputFormat.Channels),
            GetEstimatedLatencyMs,
            WaveformAvailable,
            SpectrumAvailable,
            MetricsAvailable
        );

        _outputChainTiming = new TimingAccumulator();
        _outputSampleProvider = new TimingSampleProvider(_denoiseProvider, _outputChainTiming);
        if (_asioOut != null)
        {
            _asioOut.InitRecordAndPlayback(_outputSampleProvider.ToWaveProvider(), _asioInputChannels, _asioSampleRate);
        }
        else if (_output != null)
        {
            _output.Init(_outputSampleProvider.ToWaveProvider());
        }

        if (_paStream != null)
        {
            _paStream.Start();
        }

        _capture?.StartRecording();
        _output?.Play();
        LogStartInfo(inputBackend, outputBackend, inputDevice, outputDevice);
        if (inputBackend == AudioBackendType.Ks || outputBackend == AudioBackendType.Ks)
        {
            AppLogger.Info("KS 后端使用 PortAudio WDM-KS。");
        }
        IsRunning = true;
        AppLogger.Info("已启动实时推理。");
    }

    public void Stop()
    {
        if (!IsRunning)
            return;

        _paStopping = true;
        _denoiseProvider?.RequestStop();

        try
        {
            _capture?.StopRecording();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"停止录音失败: {ex.Message}");
        }

        try
        {
            _output?.Stop();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"停止播放失败: {ex.Message}");
        }

        try
        {
            if (_paStream != null && _paStream.IsActive)
                _paStream.Stop();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"停止KS流失败: {ex.Message}");
        }

        if (_capture != null)
        {
            _capture.DataAvailable -= OnCaptureDataAvailable;
        }
        if (_asioOut != null)
        {
            _asioOut.AudioAvailable -= OnAsioAudioAvailable;
        }

        var outputToDispose = ReferenceEquals(_output, _asioOut) ? null : _output;

        DisposeSilently(_capture, "释放录音资源失败");
        DisposeSilently(outputToDispose, "释放播放资源失败");
        DisposeSilently(_paStream, "释放 KS 流失败");
        DisposeSilently(_asioOut, "释放 ASIO 资源失败");
        DisposeSilently(_runtime, "释放推理资源失败");

        _capture = null;
        _output = null;
        _asioOut = null;
        _runtime = null;
        _inputBuffer = null;
        _inputSampleProvider = null;
        _outputSampleProvider = null;
        _denoiseProvider = null;
        _inputResampleTiming = null;
        _outputChainTiming = null;
        _captureByteBuffer = null;
        _captureFloatBuffer = null;
        _asioInterleavedBuffer = null;
        _asioSampleRate = 0;
        _paStream = null;
        _paCallback = null;
        _paUseInput = false;
        _paUseOutput = false;
        _paSampleRate = 0;
        _paInputChannels = 0;
        _paOutputChannels = 0;
        _paInputBuffer = null;
        _paInputInt16 = null;
        _paOutputBuffer = null;
        _paOutputInt16 = null;
        _inputFormat = null;
        _outputFormat = null;
        _pipelineSampleRate = 0;
        _processingChannels = 0;
        _config = null;
        ActualInputSampleRate = 0;
        ActualOutputSampleRate = 0;
        ActualBufferSamples = 0;
        ProcessingChannelMode = "未运行";

        IsRunning = false;
        AppLogger.Info("已停止。");
    }

    private void SetupCapture(AudioBackendType backend, AudioDeviceItem inputDevice)
    {
        switch (backend)
        {
            case AudioBackendType.Mme:
                int inDevice = ParseDeviceNumber(inputDevice.Id);
                var inputCaps = WaveIn.GetCapabilities(inDevice);
                var waveIn = new WaveInEvent
                {
                    DeviceNumber = inDevice,
                    WaveFormat = new WaveFormat(
                        _config?.SampleRate ?? 48000,
                        16,
                        Math.Clamp(inputCaps.Channels, 1, 2))
                };
                _capture = waveIn;
                _capture.DataAvailable += OnCaptureDataAvailable;
                _inputFormat = waveIn.WaveFormat;
                break;
            case AudioBackendType.Ks:
                throw new InvalidOperationException("KS后端需要使用 PortAudio WDM-KS。");
            case AudioBackendType.Wdm:
            default:
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var device = enumerator.GetDevice(inputDevice.Id);
                    var capture = new WasapiCapture(device)
                    {
                        ShareMode = AudioClientShareMode.Shared
                    };

                    _capture = capture;
                    _capture.DataAvailable += OnCaptureDataAvailable;
                    _inputFormat = capture.WaveFormat;
                }
                break;
        }

        if (_inputFormat == null)
            throw new InvalidOperationException("无法获取输入设备格式。");

        CreateInputBuffer();
    }

    private void SetupOutput(AudioBackendType backend, AudioDeviceItem outputDevice)
    {
        switch (backend)
        {
            case AudioBackendType.Mme:
                int outDevice = ParseDeviceNumber(outputDevice.Id);
                var waveOut = new WaveOutEvent
                {
                    DeviceNumber = outDevice
                };
                var outputCaps = WaveOut.GetCapabilities(outDevice);
                _output = waveOut;
                _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(_config?.SampleRate ?? 48000, Math.Max(1, outputCaps.Channels));
                ActualOutputSampleRate = _outputFormat.SampleRate;
                break;
            case AudioBackendType.Ks:
                throw new InvalidOperationException("KS后端需要使用 PortAudio WDM-KS。");
            case AudioBackendType.Wdm:
            default:
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var device = enumerator.GetDevice(outputDevice.Id);
                    _outputFormat = device.AudioClient.MixFormat;
                    int latencyMs = GetWasapiDefaultLatencyMs(device);
                    if (latencyMs <= 0)
                        throw new InvalidOperationException("无法获取 WDM 默认延迟，请检查设备或切换后端。");
                    _output = new WasapiOut(device,
                        AudioClientShareMode.Shared,
                        true,
                        latencyMs);
                    ActualOutputSampleRate = _outputFormat.SampleRate;
                }
                break;
        }
    }

    private void SetupAsio(AudioDeviceItem inputDevice, AudioDeviceItem outputDevice)
    {
        string driverName = outputDevice.Id;
        if (!string.Equals(inputDevice.Id, outputDevice.Id, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Warning("ASIO输入输出必须为同一驱动，已使用输出设备作为驱动。");
        }

        _asioOut = new AsioOut(driverName);
        _output = _asioOut;

        _asioInputChannels = NormalizeCaptureChannels(GetAsioDefaultInputChannels(_asioOut));
        _asioOutputChannels = GetAsioDefaultOutputChannels(_asioOut);

        _asioSampleRate = ChooseAsioSampleRate(_asioOut, _config?.SampleRate ?? 48000);
        if (_asioSampleRate <= 0)
            throw new InvalidOperationException("ASIO 不支持所选采样率，请调整采样率或切换后端。");
        _inputFormat = WaveFormat.CreateIeeeFloatWaveFormat(_asioSampleRate, _asioInputChannels);
        _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(_asioSampleRate, _asioOutputChannels);
        ActualInputSampleRate = _inputFormat.SampleRate;
        ActualOutputSampleRate = _outputFormat.SampleRate;

        CreateInputBuffer();

        _asioOut.AudioAvailable += OnAsioAudioAvailable;
    }

    private void SetupPortAudioStream(bool useInput, bool useOutput, AudioDeviceItem inputDevice, AudioDeviceItem outputDevice)
    {
        if (!PortAudioManager.EnsureInitialized())
            throw new InvalidOperationException("PortAudio 初始化失败，无法启用 KS 后端。");

        _paUseInput = useInput;
        _paUseOutput = useOutput;

        int inputIndex = useInput ? ParseDeviceNumber(inputDevice.Id) : PortAudio.NoDevice;
        int outputIndex = useOutput ? ParseDeviceNumber(outputDevice.Id) : PortAudio.NoDevice;

        DeviceInfo inputInfo = default;
        DeviceInfo outputInfo = default;

        if (useInput)
        {
            if (!PortAudioManager.IsWdmKsDevice(inputIndex))
                throw new InvalidOperationException("所选输入设备不是 WDM-KS 设备。");

            inputInfo = PortAudio.GetDeviceInfo(inputIndex);
            if (inputInfo.maxInputChannels <= 0)
                throw new InvalidOperationException("KS 输入设备没有可用通道。");
        }

        if (useOutput)
        {
            if (!PortAudioManager.IsWdmKsDevice(outputIndex))
                throw new InvalidOperationException("所选输出设备不是 WDM-KS 设备。");

            outputInfo = PortAudio.GetDeviceInfo(outputIndex);
            if (outputInfo.maxOutputChannels <= 0)
                throw new InvalidOperationException("KS 输出设备没有可用通道。");
        }

        var sampleRates = BuildSampleRateCandidates(_config?.SampleRate ?? 48000);

        var inputChannels = useInput ? BuildInputChannelCandidates(inputInfo.maxInputChannels) : new List<int> { 0 };
        var outputChannels = useOutput ? BuildOutputChannelCandidates(outputInfo.maxOutputChannels) : new List<int> { 0 };

        var formats = new[] { SampleFormat.Float32, SampleFormat.Int16 };

        foreach (var format in formats)
        {
            foreach (var rate in sampleRates)
            {
                foreach (var inCh in inputChannels)
                {
                    foreach (var outCh in outputChannels)
                    {
                        if (useInput && inCh <= 0)
                            continue;
                        if (useOutput && outCh <= 0)
                            continue;

                        StreamParameters? inParams = null;
                        StreamParameters? outParams = null;

                        if (useInput)
                        {
                            inParams = new StreamParameters
                            {
                                device = inputIndex,
                                channelCount = inCh,
                                sampleFormat = format,
                                suggestedLatency = inputInfo.defaultLowInputLatency,
                                hostApiSpecificStreamInfo = IntPtr.Zero
                            };
                        }

                        if (useOutput)
                        {
                            outParams = new StreamParameters
                            {
                                device = outputIndex,
                                channelCount = outCh,
                                sampleFormat = format,
                                suggestedLatency = outputInfo.defaultLowOutputLatency,
                                hostApiSpecificStreamInfo = IntPtr.Zero
                            };
                        }

                        try
                        {
                            _paCallback = OnPortAudioCallback;
                            var stream = new Stream(inParams, outParams, rate, 0, StreamFlags.ClipOff, _paCallback, IntPtr.Zero);
                            _paStream = stream;
                            _paSampleFormat = format;
                            _paSampleRate = (int)Math.Round(rate);
                            _paInputChannels = useInput ? inCh : 0;
                            _paOutputChannels = useOutput ? outCh : 0;

                            if (useInput)
                            {
                                _inputFormat = WaveFormat.CreateIeeeFloatWaveFormat(_paSampleRate, NormalizeCaptureChannels(_paInputChannels));
                                CreateInputBuffer();
                                ActualInputSampleRate = _inputFormat.SampleRate;
                            }

                            if (useOutput)
                            {
                                _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(_paSampleRate, Math.Max(1, _paOutputChannels));
                                ActualOutputSampleRate = _outputFormat.SampleRate;
                            }

                            AppLogger.Info($"KS(PortAudio) 流打开: SR={_paSampleRate}Hz InCh={_paInputChannels} OutCh={_paOutputChannels} Format={_paSampleFormat}");
                            return;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Debug($"KS打开失败: SR={rate} InCh={inCh} OutCh={outCh} Format={format} Err={ex.Message}");
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException("KS 后端无法打开任何可用格式，请更换设备或后端。");
    }

    private void BuildInputSampleProvider(int targetSampleRate, int processingChannels)
    {
        if (_inputBuffer == null)
            throw new InvalidOperationException("输入缓冲未初始化。");

        ISampleProvider inputSample = _inputBuffer.ToSampleProvider();
        if (inputSample.WaveFormat.Channels != processingChannels || inputSample.WaveFormat.Channels > 2)
        {
            inputSample = new ChannelMapSampleProvider(inputSample, processingChannels);
        }

        if (inputSample.WaveFormat.SampleRate != targetSampleRate)
        {
            _inputResampleTiming = new TimingAccumulator();
            inputSample = new TimingSampleProvider(
                new WdlResamplingSampleProvider(inputSample, targetSampleRate),
                _inputResampleTiming
            );
        }

        _inputSampleProvider = inputSample;
    }

    private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
    {
        _inputBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
    }

    private void OnAsioAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        int channels = Math.Max(1, e.InputBuffers.Length);
        int frames = e.SamplesPerBuffer;
        int required = frames * channels;

        if (_asioInterleavedBuffer == null || _asioInterleavedBuffer.Length < required)
            _asioInterleavedBuffer = new float[required];

        e.GetAsInterleavedSamples(_asioInterleavedBuffer);
        AddCapturedFloatSamples(_asioInterleavedBuffer, frames, channels);
    }

    private StreamCallbackResult OnPortAudioCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (_paStopping)
            return StreamCallbackResult.Continue;

        int frames = (int)frameCount;
        if (frames <= 0)
            return StreamCallbackResult.Continue;

        if (_paUseInput && input != IntPtr.Zero)
        {
            int totalSamples = frames * Math.Max(1, _paInputChannels);
            if (_paSampleFormat == SampleFormat.Float32)
            {
                if (_paInputBuffer == null || _paInputBuffer.Length < totalSamples)
                    _paInputBuffer = new float[totalSamples];
                Marshal.Copy(input, _paInputBuffer, 0, totalSamples);
                AddCapturedFloatSamples(_paInputBuffer, frames, Math.Max(1, _paInputChannels));
            }
            else
            {
                if (_paInputInt16 == null || _paInputInt16.Length < totalSamples)
                    _paInputInt16 = new short[totalSamples];
                Marshal.Copy(input, _paInputInt16, 0, totalSamples);
                AddCapturedInt16Samples(_paInputInt16, frames, Math.Max(1, _paInputChannels));
            }
        }

        if (_paUseOutput && output != IntPtr.Zero && _outputSampleProvider != null)
        {
            int totalSamples = frames * Math.Max(1, _paOutputChannels);
            if (_paOutputBuffer == null || _paOutputBuffer.Length < totalSamples)
                _paOutputBuffer = new float[totalSamples];

            int read = _outputSampleProvider.Read(_paOutputBuffer, 0, totalSamples);
            if (read < totalSamples)
            {
                Array.Clear(_paOutputBuffer, read, totalSamples - read);
            }

            if (_paSampleFormat == SampleFormat.Float32)
            {
                Marshal.Copy(_paOutputBuffer, 0, output, totalSamples);
            }
            else
            {
                if (_paOutputInt16 == null || _paOutputInt16.Length < totalSamples)
                    _paOutputInt16 = new short[totalSamples];

                for (int i = 0; i < totalSamples; i++)
                {
                    float v = _paOutputBuffer[i];
                    v = Math.Clamp(v, -1f, 1f);
                    _paOutputInt16[i] = (short)Math.Round(v * short.MaxValue);
                }
                Marshal.Copy(_paOutputInt16, 0, output, totalSamples);
            }
        }

        return StreamCallbackResult.Continue;
    }

    private void LogStartInfo(AudioBackendType inputBackend, AudioBackendType outputBackend, AudioDeviceItem inputDevice, AudioDeviceItem outputDevice)
    {
        AppLogger.Info($"启动参数: InputBackend={inputBackend} OutputBackend={outputBackend}");
        AppLogger.Info("推理引擎: DeepFilterNet Runtime (embedded model)");
        AppLogger.Info($"ReduceMask: {_reduceMask.ToDisplayName()}");
        AppLogger.Info($"处理通道: {ProcessingChannelMode}");
        AppLogger.Info($"输入设备: {inputDevice.Name} ({inputDevice.Id})");
        AppLogger.Info($"输出设备: {outputDevice.Name} ({outputDevice.Id})");

        if (_inputFormat != null)
        {
            AppLogger.Info($"输入格式: {_inputFormat.SampleRate} Hz, {_inputFormat.Channels} ch, {_inputFormat.BitsPerSample} bits, {_inputFormat.Encoding}");
        }
        if (_outputFormat != null)
        {
            AppLogger.Info($"输出格式: {_outputFormat.SampleRate} Hz, {_outputFormat.Channels} ch, {_outputFormat.BitsPerSample} bits, {_outputFormat.Encoding}");
        }
        if (_inputFormat != null && _inputFormat.SampleRate != _pipelineSampleRate)
        {
            AppLogger.Info($"输入重采样: {_inputFormat.SampleRate} -> {_pipelineSampleRate}");
        }
        if ((inputBackend == AudioBackendType.Ks || outputBackend == AudioBackendType.Ks) && _paSampleRate > 0)
        {
            AppLogger.Info($"KS(PortAudio): SR={_paSampleRate}Hz InCh={_paInputChannels} OutCh={_paOutputChannels} Format={_paSampleFormat}");
        }
        if ((inputBackend == AudioBackendType.Asio || outputBackend == AudioBackendType.Asio) && _asioSampleRate > 0)
        {
            AppLogger.Info($"ASIO采样率: {_asioSampleRate} Hz 输入通道={_asioInputChannels} 输出通道={_asioOutputChannels}");
        }
    }

    private double GetEstimatedLatencyMs()
    {
        double inputBufferedMs = _inputBuffer?.BufferedDuration.TotalMilliseconds ?? 0;
        if (_runtime == null || _pipelineSampleRate <= 0)
            return inputBufferedMs;
        return inputBufferedMs + (_runtime.LatencySamples * 1000.0 / _pipelineSampleRate);
    }

    private static int ParseDeviceNumber(string id)
    {
        return int.TryParse(id, out int value) ? value : 0;
    }

    private static List<double> BuildSampleRateCandidates(int preferredRate)
    {
        var rates = new List<double>();
        if (preferredRate > 0)
            rates.Add(preferredRate);
        return rates
            .Where(r => r > 0)
            .Distinct()
            .ToList();
    }

    private static List<int> BuildInputChannelCandidates(int maxChannels)
    {
        var channels = new List<int>();
        if (maxChannels >= 2)
            channels.Add(2);
        if (maxChannels >= 1)
            channels.Add(1);
        if (maxChannels > 2)
            channels.Add(maxChannels);
        return channels.Distinct().ToList();
    }

    private static List<int> BuildOutputChannelCandidates(int maxChannels)
    {
        var channels = new List<int>();
        if (maxChannels >= 2)
            channels.Add(2);
        if (maxChannels >= 1)
            channels.Add(1);
        if (maxChannels > 2)
            channels.Add(maxChannels);
        return channels.Distinct().ToList();
    }

    private static int ChooseAsioSampleRate(AsioOut asioOut, int preferredRate)
    {
        if (preferredRate <= 0)
            return 0;

        try
        {
            if (asioOut.IsSampleRateSupported(preferredRate))
                return preferredRate;
        }
        catch
        {
            // ignore
        }

        AppLogger.Warning($"ASIO 不支持采样率 {preferredRate} Hz。");
        return 0;
    }

    private void AddCapturedFloatSamples(float[] interleavedSamples, int frames, int sourceChannels)
    {
        if (_inputBuffer == null || _inputFormat == null || frames <= 0)
            return;

        int targetChannels = NormalizeCaptureChannels(_inputFormat.Channels);
        int totalSamples = frames * targetChannels;
        EnsureCaptureBuffers(totalSamples);
        ChannelMapSampleProvider.MapChannels(interleavedSamples, frames, sourceChannels, _captureFloatBuffer!, 0, targetChannels);
        int bytes = totalSamples * sizeof(float);
        Buffer.BlockCopy(_captureFloatBuffer!, 0, _captureByteBuffer!, 0, bytes);
        _inputBuffer.AddSamples(_captureByteBuffer!, 0, bytes);
    }

    private void AddCapturedInt16Samples(short[] interleavedSamples, int frames, int sourceChannels)
    {
        if (_inputBuffer == null || _inputFormat == null || frames <= 0)
            return;

        int targetChannels = NormalizeCaptureChannels(_inputFormat.Channels);
        int totalSamples = frames * targetChannels;
        EnsureCaptureBuffers(totalSamples);
        int channelsToUse = Math.Min(Math.Max(sourceChannels, 1), 2);

        for (int frame = 0; frame < frames; frame++)
        {
            int sourceBase = frame * sourceChannels;
            int destinationBase = frame * targetChannels;

            if (targetChannels == 1)
            {
                if (channelsToUse == 1)
                {
                    _captureFloatBuffer![destinationBase] = interleavedSamples[sourceBase] / 32768f;
                }
                else
                {
                    float left = interleavedSamples[sourceBase] / 32768f;
                    float right = interleavedSamples[sourceBase + 1] / 32768f;
                    _captureFloatBuffer![destinationBase] = 0.5f * (left + right);
                }
            }
            else
            {
                if (channelsToUse == 1)
                {
                    float mono = interleavedSamples[sourceBase] / 32768f;
                    _captureFloatBuffer![destinationBase] = mono;
                    _captureFloatBuffer[destinationBase + 1] = mono;
                }
                else
                {
                    _captureFloatBuffer![destinationBase] = interleavedSamples[sourceBase] / 32768f;
                    _captureFloatBuffer[destinationBase + 1] = interleavedSamples[sourceBase + 1] / 32768f;
                }
            }
        }

        int bytes = totalSamples * sizeof(float);
        Buffer.BlockCopy(_captureFloatBuffer!, 0, _captureByteBuffer!, 0, bytes);
        _inputBuffer.AddSamples(_captureByteBuffer!, 0, bytes);
    }

    public void Dispose()
    {
        Stop();
    }

    private void EnsureCaptureBuffers(int totalSamples)
    {
        if (_captureFloatBuffer == null || _captureFloatBuffer.Length < totalSamples)
            _captureFloatBuffer = new float[totalSamples];
        int totalBytes = totalSamples * sizeof(float);
        if (_captureByteBuffer == null || _captureByteBuffer.Length < totalBytes)
            _captureByteBuffer = new byte[totalBytes];
    }

    private static int GetWasapiDefaultLatencyMs(MMDevice device)
    {
        try
        {
            long defaultPeriod = device.AudioClient.DefaultDevicePeriod;
            double ms = defaultPeriod / 10000.0;
            return (int)Math.Max(1, Math.Round(ms));
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"读取 WDM 默认延迟失败: {ex.Message}");
            return 0;
        }
    }

    private static int GetAsioDefaultInputChannels(AsioOut asioOut)
    {
        try
        {
            int count = asioOut.DriverInputChannelCount;
            if (count > 0)
                return count;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"读取 ASIO 输入通道数失败: {ex.Message}");
        }
        AppLogger.Warning("无法获取 ASIO 输入通道数，将使用 1 通道作为回退。");
        return 1;
    }

    private static int GetAsioDefaultOutputChannels(AsioOut asioOut)
    {
        try
        {
            int count = asioOut.DriverOutputChannelCount;
            if (count > 0)
                return count;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"读取 ASIO 输出通道数失败: {ex.Message}");
        }
        AppLogger.Warning("无法获取 ASIO 输出通道数，将使用 2 通道作为回退。");
        return 2;
    }

    private static int NormalizeCaptureChannels(int channels)
    {
        return channels >= 2 ? 2 : 1;
    }

    private void CreateInputBuffer()
    {
        if (_inputFormat == null)
            throw new InvalidOperationException("输入格式未初始化。");

        _inputBuffer = new BufferedWaveProvider(_inputFormat)
        {
            DiscardOnBufferOverflow = true
        };

        ActualInputSampleRate = _inputFormat.SampleRate;
        int align = _inputFormat.BlockAlign;
        ActualBufferSamples = align > 0 ? _inputBuffer.BufferLength / align : 0;
    }

    private int DeterminePipelineSampleRate()
    {
        if (_outputFormat != null && _outputFormat.SampleRate > 0)
            return _outputFormat.SampleRate;
        return NormalizeSampleRate(_config?.SampleRate ?? 48000);
    }

    private static AudioEngineConfig BuildConfig(AppSettings settings)
    {
        return new AudioEngineConfig(
            NormalizeSampleRate(settings.AudioSampleRate)
        );
    }

    private static int NormalizeSampleRate(int value)
    {
        if (value > 0)
            return value;
        AppLogger.Warning($"AudioSampleRate 无效({value})，将改为 48000。");
        return 48000;
    }

    private static ReduceMaskMode NormalizeReduceMask(ReduceMaskMode reduceMask)
    {
        return reduceMask switch
        {
            ReduceMaskMode.Independent => ReduceMaskMode.Independent,
            ReduceMaskMode.Maximum => ReduceMaskMode.Maximum,
            ReduceMaskMode.Mean => ReduceMaskMode.Mean,
            _ => ReduceMaskMode.Independent
        };
    }

    private static void DisposeSilently(IDisposable? disposable, string errorMessage)
    {
        if (disposable == null)
            return;

        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"{errorMessage}: {ex.Message}");
        }
    }

    private sealed record AudioEngineConfig(
        int SampleRate);
}

