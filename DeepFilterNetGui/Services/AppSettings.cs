namespace DeepFilterNetGui.Services;

public sealed class AppSettings
{
    public bool HasLaunchedBefore { get; set; } = false;
    public bool ShowLogPanel { get; set; } = false;
    public bool EnableFileLogging { get; set; } = false;
    public bool EnableAutoStart { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; } = false;
    public bool StartToTray { get; set; } = false;
    public string? LastInputBackend { get; set; }
    public string? LastOutputBackend { get; set; }
    public string? LastInputDeviceId { get; set; }
    public string? LastOutputDeviceId { get; set; }
    public string? LastModelName { get; set; }
    public int AudioSampleRate { get; set; } = 48000;
}

