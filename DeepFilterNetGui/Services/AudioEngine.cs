using System;
using System.Diagnostics;
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
    private const int TargetSampleRate = 48000;

    private IWaveIn? _capture;
    private IWavePlayer? _output;
    private AsioOut? _asioOut;
    private BufferedWaveProvider? _inputBuffer;
    private ISampleProvider? _inputSampleProvider;
    private ISampleProvider? _outputSampleProvider;
    private DeepFilterNetDenoiser? _denoiser;
    private DenoiseSampleProvider? _denoiseProvider;
    private float _attenLimDb = 100f;
    private float _postFilterBeta = 0f;
    private WaveFormat? _inputFormat;
    private WaveFormat? _outputFormat;
    private int _asioSampleRate;
    private int _asioInputChannels = 1;
    private int _asioOutputChannels = 2;
    private byte[]? _asioByteBuffer;
    private float[]? _asioInterleavedBuffer;
    private float[]? _asioMonoBuffer;
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
    private float[]? _paInputMono;
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

    public void SetPostFilterBeta(float beta)
    {
        _postFilterBeta = Math.Clamp(beta, 0f, 0.05f);
        _denoiser?.SetPostFilterBeta(_postFilterBeta);
    }

    public void SetDenoiseAttenLimit(float attenLimDb)
    {
        _attenLimDb = Math.Clamp(attenLimDb, 0f, 100f);
        _denoiser?.SetAttenLimit(_attenLimDb);
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

    public void Start(AudioBackendType inputBackend, AudioBackendType outputBackend, AudioDeviceItem inputDevice, AudioDeviceItem outputDevice, string modelPath, AppSettings settings)
    {
        if (IsRunning)
            return;
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        _config = BuildConfig(settings);
        ActualInputSampleRate = 0;
        ActualOutputSampleRate = 0;
        ActualBufferSamples = 0;

        _denoiser = new DeepFilterNetDenoiser(modelPath, _attenLimDb);
        _denoiser.SetPostFilterBeta(_postFilterBeta);
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

        LogStartInfo(inputBackend, outputBackend, inputDevice, outputDevice, modelPath);
        if (inputBackend == AudioBackendType.Ks || outputBackend == AudioBackendType.Ks)
        {
            AppLogger.Info("KS后端使用 PortAudio WDM-KS。");
        }

        if (_inputSampleProvider == null)
            throw new InvalidOperationException("输入音频链路未初始化。");

        _denoiseProvider = new DenoiseSampleProvider(
            _inputSampleProvider,
            _denoiser,
            TargetSampleRate,
            () => _inputBuffer?.BufferedDuration.TotalMilliseconds ?? 0,
            WaveformAvailable,
            SpectrumAvailable,
            MetricsAvailable,
            GetInputResampleSnapshot,
            GetOutputChainSnapshot
        );

        ISampleProvider outputSample = _denoiseProvider;
        if (_outputFormat != null && _outputFormat.SampleRate != TargetSampleRate)
        {
            outputSample = new WdlResamplingSampleProvider(outputSample, _outputFormat.SampleRate);
        }
        if (_outputFormat != null && _outputFormat.Channels > 1)
        {
            outputSample = new MonoToStereoSampleProvider(outputSample);
        }

        _outputChainTiming = new TimingAccumulator();
        outputSample = new TimingSampleProvider(outputSample, _outputChainTiming);

        _outputSampleProvider = outputSample;
        if (_asioOut != null)
        {
            _asioOut.InitRecordAndPlayback(outputSample.ToWaveProvider(), _asioInputChannels, _asioSampleRate);
        }
        else if (_output != null)
        {
            _output.Init(outputSample.ToWaveProvider());
        }

        if (_paStream != null)
        {
            _paStream.Start();
        }

        _capture?.StartRecording();
        _output?.Play();
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

        try
        {
            _capture?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"释放录音资源失败: {ex.Message}");
        }

        try
        {
            _output?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"释放播放资源失败: {ex.Message}");
        }

        try
        {
            _paStream?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"释放KS流失败: {ex.Message}");
        }

        try
        {
            _asioOut?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"释放ASIO资源失败: {ex.Message}");
        }

        try
        {
            _denoiser?.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"释放推理资源失败: {ex.Message}");
        }

        _capture = null;
        _output = null;
        _asioOut = null;
        _denoiser = null;
        _inputBuffer = null;
        _inputSampleProvider = null;
        _outputSampleProvider = null;
        _denoiseProvider = null;
        _inputResampleTiming = null;
        _outputChainTiming = null;
        _asioByteBuffer = null;
        _asioInterleavedBuffer = null;
        _asioMonoBuffer = null;
        _asioSampleRate = 0;
        _paStream = null;
        _paCallback = null;
        _paUseInput = false;
        _paUseOutput = false;
        _paSampleRate = 0;
        _paInputChannels = 0;
        _paOutputChannels = 0;
        _paInputBuffer = null;
        _paInputMono = null;
        _paInputInt16 = null;
        _paOutputBuffer = null;
        _paOutputInt16 = null;
        _config = null;
        ActualInputSampleRate = 0;
        ActualOutputSampleRate = 0;
        ActualBufferSamples = 0;

        IsRunning = false;
        AppLogger.Info("已停止。");
    }

    private void SetupCapture(AudioBackendType backend, AudioDeviceItem inputDevice)
    {
        switch (backend)
        {
            case AudioBackendType.Mme:
                int inDevice = ParseDeviceNumber(inputDevice.Id);
                var waveIn = new WaveInEvent
                {
                    DeviceNumber = inDevice
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

        BuildInputSampleProvider();
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
                _output = waveOut;
                _outputFormat = _inputFormat ?? new WaveFormat(44100, 16, 2);
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

        _asioInputChannels = GetAsioDefaultInputChannels(_asioOut);
        _asioOutputChannels = GetAsioDefaultOutputChannels(_asioOut);

        _asioSampleRate = ChooseAsioSampleRate(_asioOut, _config?.SampleRate ?? 48000);
        if (_asioSampleRate <= 0)
            throw new InvalidOperationException("ASIO 不支持所选采样率，请调整采样率或切换后端。");
        _inputFormat = WaveFormat.CreateIeeeFloatWaveFormat(_asioSampleRate, 1);
        _outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(_asioSampleRate, _asioOutputChannels);
        ActualInputSampleRate = _inputFormat.SampleRate;
        ActualOutputSampleRate = _outputFormat.SampleRate;

        CreateInputBuffer();

        _asioOut.AudioAvailable += OnAsioAudioAvailable;
        BuildInputSampleProvider();
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
                                _inputFormat = WaveFormat.CreateIeeeFloatWaveFormat(_paSampleRate, 1);
                                CreateInputBuffer();
                                BuildInputSampleProvider();
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

    private void BuildInputSampleProvider()
    {
        if (_inputBuffer == null)
            throw new InvalidOperationException("输入缓冲未初始化。");

        ISampleProvider inputSample = _inputBuffer.ToSampleProvider();
        if (inputSample.WaveFormat.Channels > 1)
        {
            var stereoToMono = new StereoToMonoSampleProvider(inputSample)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
            inputSample = stereoToMono;
        }

        if (inputSample.WaveFormat.SampleRate != TargetSampleRate)
        {
            _inputResampleTiming = new TimingAccumulator();
            inputSample = new TimingSampleProvider(
                new WdlResamplingSampleProvider(inputSample, TargetSampleRate),
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
        if (_inputBuffer == null)
            return;

        int channels = Math.Max(1, e.InputBuffers.Length);
        int frames = e.SamplesPerBuffer;
        int required = frames * channels;

        if (_asioInterleavedBuffer == null || _asioInterleavedBuffer.Length < required)
            _asioInterleavedBuffer = new float[required];

        e.GetAsInterleavedSamples(_asioInterleavedBuffer);

        if (channels == 1)
        {
            AddFloatSamples(_asioInterleavedBuffer, frames);
        }
        else
        {
            if (_asioMonoBuffer == null || _asioMonoBuffer.Length < frames)
                _asioMonoBuffer = new float[frames];
            bool average = false;
            int idx = 0;
            if (average)
            {
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += _asioInterleavedBuffer[idx++];
                    }
                    _asioMonoBuffer[i] = sum / channels;
                }
            }
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    _asioMonoBuffer[i] = _asioInterleavedBuffer[idx];
                    idx += channels;
                }
            }
            AddFloatSamples(_asioMonoBuffer, frames);
        }
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

        if (_paUseInput && input != IntPtr.Zero && _inputBuffer != null)
        {
            int totalSamples = frames * Math.Max(1, _paInputChannels);
            if (_paSampleFormat == SampleFormat.Float32)
            {
                if (_paInputBuffer == null || _paInputBuffer.Length < totalSamples)
                    _paInputBuffer = new float[totalSamples];
                Marshal.Copy(input, _paInputBuffer, 0, totalSamples);
                if (_paInputChannels <= 1)
                {
                    AddFloatSamples(_paInputBuffer, frames);
                }
                else
                {
                    if (_paInputMono == null || _paInputMono.Length < frames)
                        _paInputMono = new float[frames];
                    int idx = 0;
                    for (int i = 0; i < frames; i++)
                    {
                        float sum = 0;
                        for (int ch = 0; ch < _paInputChannels; ch++)
                        {
                            sum += _paInputBuffer[idx++];
                        }
                        _paInputMono[i] = sum / _paInputChannels;
                    }
                    AddFloatSamples(_paInputMono, frames);
                }
            }
            else
            {
                if (_paInputInt16 == null || _paInputInt16.Length < totalSamples)
                    _paInputInt16 = new short[totalSamples];
                Marshal.Copy(input, _paInputInt16, 0, totalSamples);
                if (_paInputMono == null || _paInputMono.Length < frames)
                    _paInputMono = new float[frames];
                if (_paInputChannels <= 1)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        _paInputMono[i] = _paInputInt16[i] / 32768f;
                    }
                }
                else
                {
                    int idx = 0;
                    for (int i = 0; i < frames; i++)
                    {
                        int sum = 0;
                        for (int ch = 0; ch < _paInputChannels; ch++)
                        {
                            sum += _paInputInt16[idx++];
                        }
                        _paInputMono[i] = (sum / (float)_paInputChannels) / 32768f;
                    }
                }
                AddFloatSamples(_paInputMono, frames);
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

    private void LogStartInfo(AudioBackendType inputBackend, AudioBackendType outputBackend, AudioDeviceItem inputDevice, AudioDeviceItem outputDevice, string modelPath)
    {
        AppLogger.Info($"启动参数: InputBackend={inputBackend} OutputBackend={outputBackend} Model={modelPath}");
        AppLogger.Info("推理引擎: DeepFilterNet3");
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
        if (_inputFormat != null && _inputFormat.SampleRate != TargetSampleRate)
        {
            AppLogger.Info($"输入重采样: {_inputFormat.SampleRate} -> {TargetSampleRate}");
        }
        if (_outputFormat != null && _outputFormat.SampleRate != TargetSampleRate)
        {
            AppLogger.Info($"输出重采样: {TargetSampleRate} -> {_outputFormat.SampleRate}");
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

    private TimingSnapshot? GetInputResampleSnapshot()
    {
        return _inputResampleTiming?.SnapshotAndReset();
    }

    private TimingSnapshot? GetOutputChainSnapshot()
    {
        return _outputChainTiming?.SnapshotAndReset();
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
        if (maxChannels >= 1)
            channels.Add(1);
        if (maxChannels >= 2)
            channels.Add(2);
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

    private void AddFloatSamples(float[] samples, int count)
    {
        if (_inputBuffer == null)
            return;

        int bytes = count * sizeof(float);
        if (_asioByteBuffer == null || _asioByteBuffer.Length < bytes)
        {
            _asioByteBuffer = new byte[bytes];
        }

        Buffer.BlockCopy(samples, 0, _asioByteBuffer, 0, bytes);
        _inputBuffer.AddSamples(_asioByteBuffer, 0, bytes);
    }

    public void Dispose()
    {
        Stop();
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

    private sealed record AudioEngineConfig(
        int SampleRate);
}

