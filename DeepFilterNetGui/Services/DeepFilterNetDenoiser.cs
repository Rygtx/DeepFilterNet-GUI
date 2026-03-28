using System;
using System.Runtime.InteropServices;

namespace DeepFilterNetGui.Services;

public sealed class DeepFilterNetDenoiser : IDisposable
{
    private const string DllName = "df.dll";
    private IntPtr _state;
    private readonly int _frameSize;
    private readonly object _sync = new();
    private float _attenLimDb;
    private float _postFilterBeta;

    public DeepFilterNetDenoiser(string modelPath, float attenLimDb = 100f)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("模型路径不能为空。", nameof(modelPath));

        _attenLimDb = attenLimDb;
        _state = df_create(modelPath, attenLimDb, IntPtr.Zero);
        if (_state == IntPtr.Zero)
            throw new InvalidOperationException("DeepFilterNet 初始化失败。");

        _frameSize = checked((int)df_get_frame_length(_state));
        if (_frameSize <= 0)
            throw new InvalidOperationException($"DeepFilterNet 帧大小无效: {_frameSize}");
    }

    public int FrameSize => _frameSize;
    public float AttenLimDb => _attenLimDb;

    public float PostFilterBeta => _postFilterBeta;

    public void SetPostFilterBeta(float beta)
    {
        lock (_sync)
        {
            if (_state == IntPtr.Zero)
                return;
            _postFilterBeta = beta;
            df_set_post_filter_beta(_state, beta);
        }
    }

    public void SetAttenLimit(float attenLimDb)
    {
        lock (_sync)
        {
            if (_state == IntPtr.Zero)
                return;
            _attenLimDb = attenLimDb;
            df_set_atten_lim(_state, attenLimDb);
        }
    }


    public float ProcessFrame(float[] input, float[] output)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));
        if (output == null)
            throw new ArgumentNullException(nameof(output));
        if (input.Length != _frameSize)
            throw new ArgumentException($"input 长度必须为 {_frameSize}。", nameof(input));
        if (output.Length != _frameSize)
            throw new ArgumentException($"output 长度必须为 {_frameSize}。", nameof(output));

        lock (_sync)
        {
            return df_process_frame(_state, input, output);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_state != IntPtr.Zero)
            {
                df_free(_state);
                _state = IntPtr.Zero;
            }
        }
        GC.SuppressFinalize(this);
    }

    ~DeepFilterNetDenoiser()
    {
        if (_state != IntPtr.Zero)
        {
            try
            {
                df_free(_state);
            }
            catch
            {
                // ignore
            }
            _state = IntPtr.Zero;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr df_create(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        float attenLimDb,
        IntPtr logLevel);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern UIntPtr df_get_frame_length(IntPtr st);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void df_set_atten_lim(IntPtr st, float limDb);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void df_set_post_filter_beta(IntPtr st, float beta);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern float df_process_frame(IntPtr st, float[] input, float[] output);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void df_free(IntPtr model);
}

