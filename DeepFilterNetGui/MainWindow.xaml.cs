using System.IO;
using System.Linq;
using System.Windows;
using System.Threading;
using System.ComponentModel;
using DeepFilterNetGui.Audio;
using DeepFilterNetGui.Services;
using DeepFilterNetGui.ViewModels;
using Wpf.Ui.Controls;
namespace DeepFilterNetGui;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly AudioEngine _engine;
    private readonly Action<LogEntry> _logHandler;
    private readonly AppSettings _settings;
    private SettingsWindow? _settingsWindow;
    private volatile bool _uiPaused;
    private bool _forceExit;
    private long _lastMetricsTick;
    private const int MetricsUiIntervalMs = 200;

    public MainWindow()
    {
        InitializeComponent();

        var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "DeepFilterNet3_onnx.tar.gz");
        _engine = new AudioEngine();
        _settings = SettingsStore.LoadOrCreate();
        _viewModel = new MainViewModel(Start, Stop, modelPath);
        _viewModel.AppVersion = AppVersion.GetVersion();
        _logHandler = entry => Dispatcher.InvokeAsync(() => _viewModel.AddLog(entry.ToLine()));
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        StateChanged += OnStateChanged;

        LoadBackends();
        ApplySettings();
        HookEngineEvents();

        Loaded += OnLoaded;
        Closed += OnClosed;
        Closing += OnClosing;
    }

    private void LoadBackends()
    {
        _viewModel.Backends.Clear();
        _viewModel.Backends.Add(new AudioBackendItem(AudioBackendType.Wdm, "WDM"));
        _viewModel.Backends.Add(new AudioBackendItem(AudioBackendType.Mme, "MME"));
        _viewModel.Backends.Add(new AudioBackendItem(AudioBackendType.Ks, "KS"));
        _viewModel.Backends.Add(new AudioBackendItem(AudioBackendType.Asio, "ASIO"));
        var first = _viewModel.Backends.FirstOrDefault();
        _viewModel.SelectedInputBackend = first;
        _viewModel.SelectedOutputBackend = first;
    }

    private void ReloadInputDevices(AudioBackendItem? backend)
    {
        _viewModel.InputDevices.Clear();
        if (backend == null)
            return;

        foreach (var device in AudioEngine.GetInputDevices(backend.Backend))
            _viewModel.InputDevices.Add(device);

        _viewModel.SelectedInputDevice = _viewModel.InputDevices.FirstOrDefault();
    }

    private void ReloadOutputDevices(AudioBackendItem? backend)
    {
        _viewModel.OutputDevices.Clear();
        if (backend == null)
            return;

        foreach (var device in AudioEngine.GetOutputDevices(backend.Backend))
            _viewModel.OutputDevices.Add(device);

        _viewModel.SelectedOutputDevice = _viewModel.OutputDevices.FirstOrDefault();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedInputBackend))
        {
            EnforceAsioCoupling(true);
            ReloadInputDevices(_viewModel.SelectedInputBackend);
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedOutputBackend))
        {
            EnforceAsioCoupling(false);
            ReloadOutputDevices(_viewModel.SelectedOutputBackend);
        }
        else if (e.PropertyName == nameof(MainViewModel.SelectedOutputDevice))
        {
            if (IsAsioSelected() &&
                _viewModel.SelectedOutputDevice != null &&
                _viewModel.SelectedInputDevice != _viewModel.SelectedOutputDevice)
            {
                _viewModel.SelectedInputDevice = _viewModel.SelectedOutputDevice;
            }
        }
    }

    private void EnforceAsioCoupling(bool inputChanged)
    {
        if (!IsAsioSelected())
            return;

        var asioItem = _viewModel.Backends.FirstOrDefault(b => b.Backend == AudioBackendType.Asio);
        if (asioItem == null)
            return;

        if (inputChanged && _viewModel.SelectedOutputBackend?.Backend != AudioBackendType.Asio)
            _viewModel.SelectedOutputBackend = asioItem;
        if (!inputChanged && _viewModel.SelectedInputBackend?.Backend != AudioBackendType.Asio)
            _viewModel.SelectedInputBackend = asioItem;
    }

    private bool IsAsioSelected()
    {
        return _viewModel.SelectedInputBackend?.Backend == AudioBackendType.Asio ||
               _viewModel.SelectedOutputBackend?.Backend == AudioBackendType.Asio;
    }

    private void HookEngineEvents()
    {
        _engine.WaveformAvailable += samples =>
        {
            if (_uiPaused)
                return;
            Dispatcher.InvokeAsync(() =>
            {
                if (_uiPaused)
                    return;
                _viewModel.UpdateWaveform(samples);
            });
        };
        _engine.SpectrumAvailable += magnitudes =>
        {
            if (_uiPaused)
                return;
            Dispatcher.InvokeAsync(() =>
            {
                if (_uiPaused)
                    return;
                _viewModel.UpdateSpectrum(magnitudes);
            });
        };
        _engine.MetricsAvailable += metrics =>
        {
            if (_uiPaused)
                return;
            Dispatcher.InvokeAsync(() =>
            {
                if (_uiPaused)
                    return;
                long now = Environment.TickCount64;
                if (now - _lastMetricsTick < MetricsUiIntervalMs)
                    return;
                _lastMetricsTick = now;
                _viewModel.UpdateMetrics(metrics);
            });
        };
        AppLogger.Logged += _logHandler;
    }

    private void ApplySettings()
    {
        _viewModel.ShowLogPanel = _settings.ShowLogPanel;

        if (!string.IsNullOrWhiteSpace(_settings.LastInputBackend))
        {
            var backend = _viewModel.Backends.FirstOrDefault(b =>
                string.Equals(b.Name, _settings.LastInputBackend, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(b.Backend.ToString(), _settings.LastInputBackend, StringComparison.OrdinalIgnoreCase));
            if (backend != null)
                _viewModel.SelectedInputBackend = backend;
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastOutputBackend))
        {
            var backend = _viewModel.Backends.FirstOrDefault(b =>
                string.Equals(b.Name, _settings.LastOutputBackend, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(b.Backend.ToString(), _settings.LastOutputBackend, StringComparison.OrdinalIgnoreCase));
            if (backend != null)
                _viewModel.SelectedOutputBackend = backend;
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastInputDeviceId))
        {
            var input = _viewModel.InputDevices.FirstOrDefault(d => d.Id == _settings.LastInputDeviceId);
            if (input != null)
                _viewModel.SelectedInputDevice = input;
        }

        if (!string.IsNullOrWhiteSpace(_settings.LastOutputDeviceId))
        {
            var output = _viewModel.OutputDevices.FirstOrDefault(d => d.Id == _settings.LastOutputDeviceId);
            if (output != null)
                _viewModel.SelectedOutputDevice = output;
        }

    }

    private void Start()
    {
        if (_viewModel.SelectedInputBackend == null || _viewModel.SelectedOutputBackend == null)
        {
            AppLogger.Warning("请选择输入与输出后端。");
            ShowUserPrompt("提示", "请选择输入与输出后端。", ControlAppearance.Caution);
            return;
        }

        if (_viewModel.SelectedInputDevice == null || _viewModel.SelectedOutputDevice == null)
        {
            AppLogger.Warning("请选择输入与输出设备。");
            ShowUserPrompt("提示", "请选择输入与输出设备。", ControlAppearance.Caution);
            return;
        }

        if (!File.Exists(_viewModel.ModelPath))
        {
            AppLogger.Error($"模型文件不存在: {_viewModel.ModelPath}");
            ShowUserPrompt("模型不存在", "模型文件不存在，请将模型放入 Models 文件夹。", ControlAppearance.Danger);
            return;
        }

        try
        {
            var inputDevice = _viewModel.SelectedInputDevice;
            var outputDevice = _viewModel.SelectedOutputDevice;
            var inputBackend = _viewModel.SelectedInputBackend?.Backend ?? AudioBackendType.Wdm;
            var outputBackend = _viewModel.SelectedOutputBackend?.Backend ?? AudioBackendType.Wdm;

            if (inputBackend == AudioBackendType.Asio || outputBackend == AudioBackendType.Asio)
            {
                if (inputBackend != AudioBackendType.Asio || outputBackend != AudioBackendType.Asio)
                {
                    AppLogger.Warning("ASIO 后端需要输入/输出同时选择 ASIO，已自动对齐。");
                    ShowUserPrompt("提示", "ASIO 后端需要输入/输出同时选择 ASIO，已自动对齐。", ControlAppearance.Info);
                }
                inputBackend = AudioBackendType.Asio;
                outputBackend = AudioBackendType.Asio;
                if (!string.Equals(inputDevice.Id, outputDevice.Id, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warning("ASIO输入输出必须为同一驱动，将使用输出设备。");
                    ShowUserPrompt("提示", "ASIO 输入输出必须为同一驱动，将使用输出设备。", ControlAppearance.Info);
                    inputDevice = outputDevice;
                }
            }

            _engine.Start(inputBackend, outputBackend, inputDevice, outputDevice, _viewModel.ModelPath, _settings);
            UpdateRuntimeAudioInfo();
            _viewModel.IsRunning = true;
            _viewModel.StatusText = "运行中";
        }
        catch (Exception ex)
        {
            if (ex.HResult != 0)
            {
                AppLogger.Error($"启动失败。HRESULT=0x{ex.HResult:X8}", ex);
            }
            else
            {
                AppLogger.Error("启动失败。", ex);
            }
            ShowUserPrompt("启动失败", "启动失败，请查看日志并确认设备与模型是否可用。", ControlAppearance.Danger);
            _viewModel.StatusText = "启动失败";
            ClearRuntimeAudioInfo();
        }
    }

    private void Stop()
    {
        try
        {
            _engine.Stop();
            _viewModel.IsRunning = false;
            _viewModel.StatusText = "已停止";
            ClearRuntimeAudioInfo();
        }
        catch (Exception ex)
        {
            AppLogger.Error("停止失败。", ex);
            ShowUserPrompt("停止失败", "停止失败，请查看日志。", ControlAppearance.Danger);
            _viewModel.StatusText = "停止失败";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _settings.HasLaunchedBefore = true;
        _settings.ShowLogPanel = _viewModel.ShowLogPanel;
        _settings.LastInputBackend = _viewModel.SelectedInputBackend?.Name;
        _settings.LastOutputBackend = _viewModel.SelectedOutputBackend?.Name;
        _settings.LastInputDeviceId = _viewModel.SelectedInputDevice?.Id;
        _settings.LastOutputDeviceId = _viewModel.SelectedOutputDevice?.Id;
        SettingsStore.Save(_settings);

        AppLogger.Logged -= _logHandler;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _engine.Dispose();
        UnregisterTrayIcon();
    }

    private void TryAutoStart()
    {
        if (_viewModel.IsRunning)
            return;

        if (!HasStartupHistory())
            return;

        if (_viewModel.SelectedInputDevice == null ||
            _viewModel.SelectedOutputDevice == null)
        {
            AppLogger.Warning("自动启动失败：上次的设备不可用。");
            ShowUserPrompt("自动启动失败", "上次的设备不可用，请重新选择。", ControlAppearance.Caution);
            return;
        }

        AppLogger.Info("检测到非首次启动，自动开始实时推理。");
        Start();
    }

    private bool HasStartupHistory()
    {
        if (_settings.HasLaunchedBefore)
            return true;

        return !string.IsNullOrWhiteSpace(_settings.LastInputDeviceId) &&
               !string.IsNullOrWhiteSpace(_settings.LastOutputDeviceId);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == System.Windows.WindowState.Minimized)
        {
            _uiPaused = true;
            if (_settings.MinimizeToTray)
            {
                MoveToTray();
                AppLogger.Info("已最小化到托盘。");
            }
        }
        else
        {
            _uiPaused = false;
        }
    }

    private void RestoreFromTray()
    {
        _uiPaused = false;
        ShowInTaskbar = true;
        Show();
        if (WindowState == System.Windows.WindowState.Minimized)
            WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(TryAutoStart, System.Windows.Threading.DispatcherPriority.Background);
        if (_settings.StartToTray)
        {
            Dispatcher.InvokeAsync(() =>
            {
                MoveToTray();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
        RegisterTrayIcon();
    }

    private void ShowUserPrompt(string title, string message, ControlAppearance appearance)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var dialog = new Wpf.Ui.Controls.MessageBox
                {
                    Title = title,
                    Content = message,
                    ShowTitle = true,
                    PrimaryButtonText = "确定",
                    IsSecondaryButtonEnabled = false,
                    IsCloseButtonEnabled = false,
                    PrimaryButtonAppearance = appearance
                };
                await dialog.ShowDialogAsync(false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"弹窗失败: {ex.Message}");
            }
        });
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_settings.CloseToTray && !_forceExit)
        {
            e.Cancel = true;
            MoveToTray();
        }
    }

    private void MoveToTray()
    {
        _uiPaused = true;
        ShowInTaskbar = false;
        Hide();
        RegisterTrayIcon();
    }

    private void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplySettingsChanged);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ApplySettingsChanged(AppSettings settings)
    {
        _viewModel.ShowLogPanel = settings.ShowLogPanel;
    }

    private void UpdateRuntimeAudioInfo()
    {
        _viewModel.ActualInputSampleRate = _engine.ActualInputSampleRate;
        _viewModel.ActualOutputSampleRate = _engine.ActualOutputSampleRate;
        _viewModel.ActualBufferSamples = _engine.ActualBufferSamples;
    }

    private void ClearRuntimeAudioInfo()
    {
        _viewModel.ActualInputSampleRate = 0;
        _viewModel.ActualOutputSampleRate = 0;
        _viewModel.ActualBufferSamples = 0;
    }

    private void RegisterTrayIcon()
    {
        if (TrayIcon != null && !TrayIcon.IsRegistered)
        {
            TrayIcon.Register();
        }
    }

    private void UnregisterTrayIcon()
    {
        if (TrayIcon != null && TrayIcon.IsRegistered)
        {
            TrayIcon.Unregister();
        }
    }

    private void OnTrayLeftClick(object sender, RoutedEventArgs e)
    {
        RestoreFromTray();
    }

    private void OnTrayOpenClick(object sender, RoutedEventArgs e)
    {
        RestoreFromTray();
    }

    private void OnTraySettingsClick(object sender, RoutedEventArgs e)
    {
        ShowSettings();
    }

    private void OnTrayExitClick(object sender, RoutedEventArgs e)
    {
        _forceExit = true;
        System.Windows.Application.Current.Shutdown();
    }
}

