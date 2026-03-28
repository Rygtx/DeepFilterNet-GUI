using System.Runtime.InteropServices;
using System.Collections.Generic;
using PortAudioSharp;
using DeepFilterNetGui.Audio;

namespace DeepFilterNetGui.Services;

internal static class PortAudioManager
{
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static int? _wdmksHostApiIndex;
    private static bool _loggedNoWdmks;

    public static bool EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized)
                return true;

            try
            {
                PortAudio.Initialize();
                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Error("PortAudio 初始化失败，无法启用 KS 后端。", ex);
                return false;
            }
        }
    }

    public static void Terminate()
    {
        lock (InitLock)
        {
            if (!_initialized)
                return;

            try
            {
                PortAudio.Terminate();
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"PortAudio 关闭失败: {ex.Message}");
            }
            finally
            {
                _initialized = false;
            }
        }
    }

    public static IReadOnlyList<AudioDeviceItem> GetKsInputDevices()
    {
        return GetKsDevices(true);
    }

    public static IReadOnlyList<AudioDeviceItem> GetKsOutputDevices()
    {
        return GetKsDevices(false);
    }

    public static bool IsWdmKsDevice(int deviceIndex)
    {
        if (!EnsureInitialized())
            return false;

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        int? wdmks = GetWdmksHostApiIndex();
        return wdmks.HasValue && info.hostApi == wdmks.Value;
    }

    private static IReadOnlyList<AudioDeviceItem> GetKsDevices(bool isInput)
    {
        var devices = new List<AudioDeviceItem>();
        if (!EnsureInitialized())
            return devices;

        int? wdmks = GetWdmksHostApiIndex();
        if (!wdmks.HasValue)
        {
            if (!_loggedNoWdmks)
            {
                AppLogger.Warning("PortAudio 未发现 WDM-KS Host API，KS 后端不可用。");
                _loggedNoWdmks = true;
            }
            return devices;
        }

        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (info.hostApi != wdmks.Value)
                continue;
            if (isInput && info.maxInputChannels <= 0)
                continue;
            if (!isInput && info.maxOutputChannels <= 0)
                continue;
            devices.Add(new AudioDeviceItem(i.ToString(), info.name));
        }

        return devices;
    }

    private static int? GetWdmksHostApiIndex()
    {
        if (_wdmksHostApiIndex.HasValue)
            return _wdmksHostApiIndex.Value;

        int index = PortAudioNative.Pa_HostApiTypeIdToHostApiIndex(PaHostApiTypeId.Wdmks);
        if (index < 0)
            return null;

        _wdmksHostApiIndex = index;
        return index;
    }

    private enum PaHostApiTypeId
    {
        InDevelopment = 0,
        DirectSound = 1,
        MME = 2,
        ASIO = 3,
        SoundManager = 4,
        CoreAudio = 5,
        OSS = 7,
        ALSA = 8,
        AL = 9,
        BeOS = 10,
        Wdmks = 11,
        JACK = 12,
        WASAPI = 13,
        AudioScienceHPI = 14
    }

    private static class PortAudioNative
    {
        [DllImport("portaudio", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Pa_HostApiTypeIdToHostApiIndex(PaHostApiTypeId type);
    }
}

