using System.Runtime.InteropServices;

namespace DeepFilterNetGui.Services;

public sealed class DeepFilterRuntime : IDisposable
{
    private const string DllName = "deepfilter_runtime_bridge";
    private const int DefaultRuntimeSampleRate = 48000;
    private const int QueueReserveMultiplier = 8;
    private readonly object _sync = new();
    private readonly int _hostSampleRate;
    private readonly int _channelCount;
    private readonly ChannelState[] _channelStates;
    private readonly float[][] _hostInputScratch;
    private readonly float[][] _hostOutputScratch;
    private readonly float[][] _resampledInput;
    private readonly float[][] _resampledOutput;
    private readonly NativeResampler _inputResampler = new();
    private readonly NativeResampler _outputResampler = new();
    private IntPtr _state;
    private int _frameSize;
    private int _runtimeSampleRate = DefaultRuntimeSampleRate;
    private bool _ready;
    private bool _primed;
    private float _attenLimDb;
    private float _postFilterBeta;
    private ReduceMaskMode _reduceMask;
    private float[] _frameInput = Array.Empty<float>();
    private float[] _frameOutput = Array.Empty<float>();

    public DeepFilterRuntime(
        int hostSampleRate,
        int channelCount,
        float attenLimDb = 100f,
        float postFilterBeta = 0f,
        ReduceMaskMode reduceMask = ReduceMaskMode.Independent)
    {
        if (hostSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(hostSampleRate));
        if (channelCount is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(channelCount));

        _hostSampleRate = hostSampleRate;
        _channelCount = channelCount;
        _attenLimDb = Math.Clamp(attenLimDb, 0f, 100f);
        _postFilterBeta = Math.Clamp(postFilterBeta, 0f, 0.05f);
        _reduceMask = NormalizeReduceMask(reduceMask);

        _channelStates = Enumerable.Range(0, _channelCount).Select(_ => new ChannelState()).ToArray();
        _hostInputScratch = Enumerable.Range(0, _channelCount).Select(_ => Array.Empty<float>()).ToArray();
        _hostOutputScratch = Enumerable.Range(0, _channelCount).Select(_ => Array.Empty<float>()).ToArray();
        _resampledInput = Enumerable.Range(0, _channelCount).Select(_ => Array.Empty<float>()).ToArray();
        _resampledOutput = Enumerable.Range(0, _channelCount).Select(_ => Array.Empty<float>()).ToArray();

        lock (_sync)
        {
            if (!InitializeNoLock())
                throw new InvalidOperationException("DeepFilter runtime 初始化失败。");
        }
    }

    public int FrameSize
    {
        get
        {
            lock (_sync)
            {
                return _frameSize;
            }
        }
    }

    public int RuntimeSampleRate
    {
        get
        {
            lock (_sync)
            {
                return _runtimeSampleRate;
            }
        }
    }

    public int ChannelCount => _channelCount;

    public int LatencySamples
    {
        get
        {
            lock (_sync)
            {
                return GetLatencySamplesNoLock();
            }
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _ready;
            }
        }
    }

    public void SetAttenLimit(float attenLimDb)
    {
        lock (_sync)
        {
            _attenLimDb = Math.Clamp(attenLimDb, 0f, 100f);
            if (_state != IntPtr.Zero)
            {
                dfgui_set_atten_lim(_state, _attenLimDb);
            }
        }
    }

    public void SetPostFilterBeta(float beta)
    {
        lock (_sync)
        {
            _postFilterBeta = Math.Clamp(beta, 0f, 0.05f);
            if (_state != IntPtr.Zero)
            {
                dfgui_set_post_filter_beta(_state, _postFilterBeta);
            }
        }
    }

    public void SetReduceMask(ReduceMaskMode reduceMask)
    {
        lock (_sync)
        {
            var normalized = NormalizeReduceMask(reduceMask);
            if (_reduceMask == normalized)
                return;

            _reduceMask = normalized;
            ReinitializeNoLock();
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            ReinitializeNoLock();
        }
    }

    public void ProcessFrame(float[] inputInterleaved, int frames, float[] outputInterleaved)
    {
        if (inputInterleaved == null)
            throw new ArgumentNullException(nameof(inputInterleaved));
        if (outputInterleaved == null)
            throw new ArgumentNullException(nameof(outputInterleaved));
        if (frames < 0)
            throw new ArgumentOutOfRangeException(nameof(frames));
        if (inputInterleaved.Length < frames * _channelCount)
            throw new ArgumentException("输入缓冲长度不足。", nameof(inputInterleaved));
        if (outputInterleaved.Length < frames * _channelCount)
            throw new ArgumentException("输出缓冲长度不足。", nameof(outputInterleaved));

        if (frames == 0)
        {
            Array.Clear(outputInterleaved, 0, outputInterleaved.Length);
            return;
        }

        lock (_sync)
        {
            if (!_ready && !InitializeNoLock())
            {
                Array.Clear(outputInterleaved, 0, frames * _channelCount);
                return;
            }

            EnsureHostScratchCapacity(frames);
            Deinterleave(inputInterleaved, frames, _hostInputScratch);

            _inputResampler.Push(_hostInputScratch, frames);
            int resampledInputFrames = _inputResampler.DrainAvailable(_resampledInput);

            if (resampledInputFrames > 0)
            {
                for (int channel = 0; channel < _channelCount; channel++)
                {
                    _channelStates[channel].InputQueue.Push(_resampledInput[channel].AsSpan(0, resampledInputFrames));
                }
            }

            while (CanProcessFrameNoLock())
            {
                for (int channel = 0; channel < _channelCount; channel++)
                {
                    _channelStates[channel].InputQueue.Pop(_frameInput.AsSpan(channel * _frameSize, _frameSize), _frameSize);
                }

                _ = dfgui_process_frame(_state, _frameInput, _frameOutput);

                for (int channel = 0; channel < _channelCount; channel++)
                {
                    _channelStates[channel].OutputQueue.Push(_frameOutput.AsSpan(channel * _frameSize, _frameSize));
                }
            }

            int processedAvailable = GetProcessedSamplesAvailableNoLock();
            if (processedAvailable > 0)
            {
                for (int channel = 0; channel < _channelCount; channel++)
                {
                    EnsureChannelArrayCapacity(_resampledOutput, channel, processedAvailable);
                    _channelStates[channel].OutputQueue.Pop(_resampledOutput[channel].AsSpan(0, processedAvailable), processedAvailable);
                }

                _outputResampler.Push(_resampledOutput, processedAvailable);
            }

            if (!_primed)
            {
                _primed = _outputResampler.HasBufferedOutput();
            }

            if (!_primed)
            {
                Array.Clear(outputInterleaved, 0, frames * _channelCount);
                return;
            }

            int written = _outputResampler.Produce(_hostOutputScratch, frames);
            Interleave(_hostOutputScratch, frames, written, outputInterleaved);
        }
    }

    public void Process(float[] inputInterleaved, int frames, float[] outputInterleaved)
    {
        ProcessFrame(inputInterleaved, frames, outputInterleaved);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            ShutdownNoLock();
        }

        _inputResampler.Dispose();
        _outputResampler.Dispose();
        GC.SuppressFinalize(this);
    }

    ~DeepFilterRuntime()
    {
        try
        {
            Dispose();
        }
        catch
        {
            // ignore finalizer errors
        }
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

    private bool InitializeNoLock()
    {
        ShutdownNoLock();

        _state = dfgui_create((nuint)_channelCount, _attenLimDb, _postFilterBeta, (int)_reduceMask);
        if (_state == IntPtr.Zero)
            return false;

        _frameSize = checked((int)dfgui_get_frame_length(_state));
        _runtimeSampleRate = checked((int)dfgui_get_sample_rate(_state));
        int reportedChannels = checked((int)dfgui_get_channel_count(_state));

        if (_frameSize <= 0 || _runtimeSampleRate <= 0 || reportedChannels != _channelCount)
        {
            ShutdownNoLock();
            return false;
        }

        _frameInput = new float[_frameSize * _channelCount];
        _frameOutput = new float[_frameSize * _channelCount];

        if (!_inputResampler.Reset(
                NativeResampler.ResamplerMode.FixedOut,
                _hostSampleRate,
                _runtimeSampleRate,
                _frameSize,
                _channelCount)
            || !_outputResampler.Reset(
                NativeResampler.ResamplerMode.FixedIn,
                _runtimeSampleRate,
                _hostSampleRate,
                _frameSize,
                _channelCount))
        {
            ShutdownNoLock();
            return false;
        }

        foreach (var channelState in _channelStates)
        {
            channelState.InputQueue.Clear();
            channelState.OutputQueue.Clear();
            channelState.InputQueue.Reserve(_frameSize * QueueReserveMultiplier);
            channelState.OutputQueue.Reserve(_frameSize * QueueReserveMultiplier);
        }

        _primed = false;
        _ready = true;
        return true;
    }

    private void ReinitializeNoLock()
    {
        InitializeNoLock();
    }

    private void ShutdownNoLock()
    {
        if (_state != IntPtr.Zero)
        {
            dfgui_free(_state);
            _state = IntPtr.Zero;
        }

        _ready = false;
        _primed = false;
        _frameSize = 0;
        _runtimeSampleRate = DefaultRuntimeSampleRate;
        _frameInput = Array.Empty<float>();
        _frameOutput = Array.Empty<float>();

        foreach (var channelState in _channelStates)
        {
            channelState.InputQueue.Clear();
            channelState.OutputQueue.Clear();
        }

        _inputResampler.Clear();
        _outputResampler.Clear();
    }

    private bool CanProcessFrameNoLock()
    {
        if (_frameSize <= 0)
            return false;

        for (int channel = 0; channel < _channelCount; channel++)
        {
            if (_channelStates[channel].InputQueue.Count < _frameSize)
                return false;
        }

        return true;
    }

    private int GetProcessedSamplesAvailableNoLock()
    {
        int available = int.MaxValue;
        for (int channel = 0; channel < _channelCount; channel++)
        {
            available = Math.Min(available, _channelStates[channel].OutputQueue.Count);
        }

        return available == int.MaxValue ? 0 : available;
    }

    private int GetLatencySamplesNoLock()
    {
        if (!_ready || _hostSampleRate <= 0 || _runtimeSampleRate <= 0)
            return 0;

        double modelLatencyHostSamples = (_frameSize * (double)_hostSampleRate) / _runtimeSampleRate;
        double inputResamplerDelayHostSamples = (_inputResampler.OutputDelay * (double)_hostSampleRate) / _runtimeSampleRate;
        double outputResamplerDelayHostSamples = _outputResampler.OutputDelay;

        return Math.Max(0, (int)Math.Ceiling(modelLatencyHostSamples + inputResamplerDelayHostSamples + outputResamplerDelayHostSamples) + 1);
    }

    private void EnsureHostScratchCapacity(int frames)
    {
        for (int channel = 0; channel < _channelCount; channel++)
        {
            EnsureChannelArrayCapacity(_hostInputScratch, channel, frames);
            EnsureChannelArrayCapacity(_hostOutputScratch, channel, frames);
        }
    }

    private static void EnsureChannelArrayCapacity(float[][] arrays, int channel, int length)
    {
        if (arrays[channel].Length < length)
        {
            Array.Resize(ref arrays[channel], length);
        }
    }

    private void Deinterleave(float[] source, int frames, float[][] destination)
    {
        for (int channel = 0; channel < _channelCount; channel++)
        {
            var channelBuffer = destination[channel];
            for (int frame = 0; frame < frames; frame++)
            {
                channelBuffer[frame] = source[(frame * _channelCount) + channel];
            }
        }
    }

    private void Interleave(float[][] source, int requestedFrames, int writtenFrames, float[] destination)
    {
        Array.Clear(destination, 0, requestedFrames * _channelCount);

        for (int frame = 0; frame < writtenFrames; frame++)
        {
            int destinationBase = frame * _channelCount;
            for (int channel = 0; channel < _channelCount; channel++)
            {
                destination[destinationBase + channel] = source[channel][frame];
            }
        }
    }

    private sealed class ChannelState
    {
        public FloatQueue InputQueue { get; } = new();
        public FloatQueue OutputQueue { get; } = new();
    }

    private sealed class NativeResampler : IDisposable
    {
        public enum ResamplerMode
        {
            FixedIn,
            FixedOut
        }

        private IntPtr _state;
        private int _channelCount;
        private bool _passthrough;
        private int _inputFramesNext;
        private int _outputFramesMax;
        private int _outputDelay;
        private FloatQueue[] _inputQueues = Array.Empty<FloatQueue>();
        private FloatQueue[] _outputQueues = Array.Empty<FloatQueue>();
        private float[] _processInput = Array.Empty<float>();
        private float[] _processOutput = Array.Empty<float>();

        public int OutputDelay => _outputDelay;

        public bool Reset(ResamplerMode mode, int inputSampleRate, int outputSampleRate, int chunkSize, int channelCount)
        {
            Release();

            if (inputSampleRate <= 0 || outputSampleRate <= 0 || chunkSize <= 0 || channelCount <= 0)
                return false;

            EnsureChannelCount(channelCount);
            _channelCount = channelCount;
            _passthrough = inputSampleRate == outputSampleRate;

            if (_passthrough)
            {
                _inputFramesNext = chunkSize;
                _outputFramesMax = chunkSize;
                _outputDelay = 0;
                Clear();
                return true;
            }

            _state = mode switch
            {
                ResamplerMode.FixedIn => dfgui_resampler_create_fixed_in((nuint)inputSampleRate, (nuint)outputSampleRate, (nuint)chunkSize, 1, (nuint)channelCount),
                ResamplerMode.FixedOut => dfgui_resampler_create_fixed_out((nuint)inputSampleRate, (nuint)outputSampleRate, (nuint)chunkSize, 1, (nuint)channelCount),
                _ => IntPtr.Zero
            };

            if (_state == IntPtr.Zero)
            {
                Release();
                return false;
            }

            RefreshFrameCounts();
            if (_inputFramesNext <= 0 || _outputFramesMax <= 0)
            {
                Release();
                return false;
            }

            Clear();
            return true;
        }

        public void Clear()
        {
            foreach (var inputQueue in _inputQueues)
            {
                inputQueue.Clear();
            }

            foreach (var outputQueue in _outputQueues)
            {
                outputQueue.Clear();
            }

            if (_state != IntPtr.Zero)
            {
                dfgui_resampler_reset(_state);
                RefreshFrameCounts();
            }
        }

        public void Push(float[][] channelData, int frames)
        {
            if (_channelCount == 0 || frames <= 0)
                return;

            if (_passthrough)
            {
                for (int channel = 0; channel < _channelCount; channel++)
                {
                    _outputQueues[channel].Push(channelData[channel].AsSpan(0, frames));
                }

                return;
            }

            for (int channel = 0; channel < _channelCount; channel++)
            {
                _inputQueues[channel].Push(channelData[channel].AsSpan(0, frames));
            }

            ProcessAvailableInput();
        }

        public int DrainAvailable(float[][] destination)
        {
            ProcessAvailableInput();

            int available = GetAvailableOutputSamples();
            if (available <= 0)
                return 0;

            for (int channel = 0; channel < _channelCount; channel++)
            {
                if (destination[channel].Length < available)
                {
                    Array.Resize(ref destination[channel], available);
                }

                _outputQueues[channel].Pop(destination[channel].AsSpan(0, available), available);
            }

            return available;
        }

        public int Produce(float[][] destination, int maxOutputSamples)
        {
            if (_channelCount == 0 || maxOutputSamples <= 0)
                return 0;

            ProcessAvailableInput();

            int available = Math.Min(maxOutputSamples, GetAvailableOutputSamples());
            if (available <= 0)
                return 0;

            for (int channel = 0; channel < _channelCount; channel++)
            {
                if (destination[channel].Length < maxOutputSamples)
                {
                    Array.Resize(ref destination[channel], maxOutputSamples);
                }

                _outputQueues[channel].Pop(destination[channel].AsSpan(0, available), available);
                if (available < maxOutputSamples)
                {
                    destination[channel].AsSpan(available, maxOutputSamples - available).Clear();
                }
            }

            return available;
        }

        public bool HasBufferedOutput()
        {
            ProcessAvailableInput();
            return GetAvailableOutputSamples() > 0;
        }

        public void Dispose()
        {
            Release();
            GC.SuppressFinalize(this);
        }

        private void Release()
        {
            if (_state != IntPtr.Zero)
            {
                dfgui_resampler_free(_state);
                _state = IntPtr.Zero;
            }

            foreach (var inputQueue in _inputQueues)
            {
                inputQueue.Clear();
            }

            foreach (var outputQueue in _outputQueues)
            {
                outputQueue.Clear();
            }

            _channelCount = 0;
            _passthrough = false;
            _inputFramesNext = 0;
            _outputFramesMax = 0;
            _outputDelay = 0;
            _processInput = Array.Empty<float>();
            _processOutput = Array.Empty<float>();
        }

        private void EnsureChannelCount(int channelCount)
        {
            if (_inputQueues.Length == channelCount && _outputQueues.Length == channelCount)
                return;

            _inputQueues = Enumerable.Range(0, channelCount).Select(_ => new FloatQueue()).ToArray();
            _outputQueues = Enumerable.Range(0, channelCount).Select(_ => new FloatQueue()).ToArray();
        }

        private void RefreshFrameCounts()
        {
            if (_state == IntPtr.Zero)
                return;

            _inputFramesNext = checked((int)dfgui_resampler_get_input_frames_next(_state));
            _outputFramesMax = checked((int)dfgui_resampler_get_output_frames_max(_state));
            _outputDelay = checked((int)dfgui_resampler_get_output_delay(_state));
        }

        private void ProcessAvailableInput()
        {
            if (_passthrough || _state == IntPtr.Zero)
                return;

            while (CanProcessInput())
            {
                int inputSamples = _channelCount * _inputFramesNext;
                int outputSamples = _channelCount * _outputFramesMax;

                if (_processInput.Length < inputSamples)
                {
                    Array.Resize(ref _processInput, inputSamples);
                }

                if (_processOutput.Length < outputSamples)
                {
                    Array.Resize(ref _processOutput, outputSamples);
                }

                for (int channel = 0; channel < _channelCount; channel++)
                {
                    _inputQueues[channel].Pop(_processInput.AsSpan(channel * _inputFramesNext, _inputFramesNext), _inputFramesNext);
                }

                int produced = checked((int)dfgui_resampler_process(_state, _processInput, (nuint)_inputFramesNext, _processOutput, (nuint)_outputFramesMax));
                if (produced <= 0)
                    break;

                for (int channel = 0; channel < _channelCount; channel++)
                {
                    _outputQueues[channel].Push(_processOutput.AsSpan(channel * _outputFramesMax, produced));
                }

                RefreshFrameCounts();
            }
        }

        private int GetAvailableOutputSamples()
        {
            if (_outputQueues.Length == 0)
                return 0;

            int available = int.MaxValue;
            foreach (var outputQueue in _outputQueues)
            {
                available = Math.Min(available, outputQueue.Count);
            }

            return available == int.MaxValue ? 0 : available;
        }

        private bool CanProcessInput()
        {
            if (_state == IntPtr.Zero || _inputFramesNext <= 0 || _outputFramesMax <= 0)
                return false;

            int available = int.MaxValue;
            foreach (var inputQueue in _inputQueues)
            {
                available = Math.Min(available, inputQueue.Count);
            }

            return available >= _inputFramesNext;
        }
    }

    private sealed class FloatQueue
    {
        private float[] _buffer = Array.Empty<float>();
        private int _readPosition;
        private int _writePosition;

        public int Count => _writePosition - _readPosition;

        public void Clear()
        {
            _readPosition = 0;
            _writePosition = 0;
        }

        public void Reserve(int count)
        {
            if (_buffer.Length < count)
            {
                Array.Resize(ref _buffer, count);
            }
        }

        public void Push(ReadOnlySpan<float> data)
        {
            if (data.IsEmpty)
                return;

            EnsureWritableCapacity(data.Length);
            data.CopyTo(_buffer.AsSpan(_writePosition));
            _writePosition += data.Length;
        }

        public int Pop(Span<float> destination, int count)
        {
            int toCopy = Math.Min(count, Count);
            if (toCopy <= 0)
                return 0;

            _buffer.AsSpan(_readPosition, toCopy).CopyTo(destination);
            _readPosition += toCopy;
            CompactIfNeeded();
            return toCopy;
        }

        private void EnsureWritableCapacity(int additionalCount)
        {
            if (_buffer.Length - _writePosition >= additionalCount)
                return;

            Compact(force: true);
            if (_buffer.Length - _writePosition >= additionalCount)
                return;

            int newLength = Math.Max(_writePosition + additionalCount, Math.Max(256, _buffer.Length * 2));
            Array.Resize(ref _buffer, newLength);
        }

        private void CompactIfNeeded()
        {
            if (_readPosition == 0)
                return;

            if (_readPosition > 4096 || _readPosition > _buffer.Length / 2)
            {
                Compact(force: true);
            }
        }

        private void Compact(bool force)
        {
            if (!force || _readPosition == 0)
                return;

            int count = Count;
            if (count > 0)
            {
                Array.Copy(_buffer, _readPosition, _buffer, 0, count);
            }

            _readPosition = 0;
            _writePosition = count;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dfgui_create(nuint channels, float attenLimDb, float postFilterBeta, int reduceMask);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dfgui_free(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint dfgui_get_frame_length(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint dfgui_get_sample_rate(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint dfgui_get_channel_count(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dfgui_set_atten_lim(IntPtr state, float attenLimDb);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dfgui_set_post_filter_beta(IntPtr state, float postFilterBeta);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern float dfgui_process_frame(IntPtr state, float[] input, float[] output);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dfgui_resampler_create_fixed_in(
        nuint inputSampleRate,
        nuint outputSampleRate,
        nuint chunkSizeIn,
        nuint subChunks,
        nuint channels);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dfgui_resampler_create_fixed_out(
        nuint inputSampleRate,
        nuint outputSampleRate,
        nuint chunkSizeOut,
        nuint subChunks,
        nuint channels);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dfgui_resampler_free(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dfgui_resampler_reset(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint dfgui_resampler_get_input_frames_next(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint dfgui_resampler_get_output_frames_max(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint dfgui_resampler_get_output_delay(IntPtr state);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern nuint dfgui_resampler_process(
        IntPtr state,
        float[] input,
        nuint inputFrames,
        float[] output,
        nuint outputCapacityFrames);
}
