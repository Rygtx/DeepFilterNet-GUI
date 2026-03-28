using System.Windows;
using DeepFilterNetGui.Services;

namespace DeepFilterNetGui;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var settings = SettingsStore.LoadOrCreate();
        AppLogger.Initialize(settings.EnableFileLogging);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PortAudioManager.Terminate();
        AppLogger.Shutdown();
        base.OnExit(e);
    }
}

