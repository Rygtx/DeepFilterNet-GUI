using System.Text.Json;
using System.IO;

namespace DeepFilterNetGui.Services;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string SettingsPath { get; } =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deepfilternet3.settings.json");

    public static AppSettings LoadOrCreate()
    {
        if (!File.Exists(SettingsPath))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            if (NormalizeDefaults(settings))
                Save(settings);
            return settings;
        }
        catch
        {
            var fallback = new AppSettings();
            NormalizeDefaults(fallback);
            Save(fallback);
            return fallback;
        }
    }

    public static void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsPath, json);
    }

    private static bool NormalizeDefaults(AppSettings settings)
    {
        bool changed = false;
        if (settings.AudioSampleRate <= 0)
        {
            settings.AudioSampleRate = 48000;
            changed = true;
        }
        if (settings.PostFilterBeta < 0 || settings.PostFilterBeta > 0.05f)
        {
            settings.PostFilterBeta = 0f;
            changed = true;
        }
        if (settings.DenoiseAttenLimitDb < 0 || settings.DenoiseAttenLimitDb > 100)
        {
            settings.DenoiseAttenLimitDb = 100;
            changed = true;
        }
        return changed;
    }

}

