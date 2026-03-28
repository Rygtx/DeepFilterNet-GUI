using System;
using System.Windows;
using System.Windows.Controls;
using System.Linq;
using DeepFilterNetGui.Services;

namespace DeepFilterNetGui;

public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string StartupAppName = "DeepFilterNetGui";
    private readonly AppSettings _settings;
    private readonly Action<AppSettings>? _onSettingsChanged;
    private bool _updating;
    private bool _audioParamsBound;

    public SettingsWindow(AppSettings settings, Action<AppSettings>? onSettingsChanged = null)
    {
        InitializeComponent();
        _settings = settings;
        _onSettingsChanged = onSettingsChanged;
        Loaded += OnLoaded;

        FileLogToggle.Checked += OnFileLogChanged;
        FileLogToggle.Unchecked += OnFileLogChanged;
        AutoStartToggle.Checked += OnAutoStartChanged;
        AutoStartToggle.Unchecked += OnAutoStartChanged;
        MinimizeToTrayToggle.Checked += OnMinimizeToTrayChanged;
        MinimizeToTrayToggle.Unchecked += OnMinimizeToTrayChanged;
        CloseToTrayToggle.Checked += OnCloseToTrayChanged;
        CloseToTrayToggle.Unchecked += OnCloseToTrayChanged;
        StartToTrayToggle.Checked += OnStartToTrayChanged;
        StartToTrayToggle.Unchecked += OnStartToTrayChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _updating = true;
        bool autoStart = StartupManager.IsEnabled(StartupAppName);
        AutoStartToggle.IsChecked = autoStart;
        _settings.EnableAutoStart = autoStart;
        FileLogToggle.IsChecked = _settings.EnableFileLogging;
        MinimizeToTrayToggle.IsChecked = _settings.MinimizeToTray;
        CloseToTrayToggle.IsChecked = _settings.CloseToTray;
        StartToTrayToggle.IsChecked = _settings.StartToTray;
        VersionText.Text = $"版本 {AppVersion.GetVersion()}";
        BindAudioParams();
        _updating = false;
    }

    private void OnFileLogChanged(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;
        bool enable = FileLogToggle.IsChecked == true;
        _settings.EnableFileLogging = enable;
        SettingsStore.Save(_settings);
        AppLogger.SetFileLoggingEnabled(enable);
        _onSettingsChanged?.Invoke(_settings);
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;
        bool desired = AutoStartToggle.IsChecked == true;
        bool enabled = desired ? StartupManager.Enable(StartupAppName) : StartupManager.Disable(StartupAppName);
        bool actual = StartupManager.IsEnabled(StartupAppName);
        _updating = true;
        AutoStartToggle.IsChecked = actual;
        _updating = false;
        _settings.EnableAutoStart = actual;
        SettingsStore.Save(_settings);

        if (!enabled)
        {
            AppLogger.Warning("开机启动切换失败。");
        }
    }

    private void OnMinimizeToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    private void OnCloseToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;
        _settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    private void OnStartToTrayChanged(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;
        _settings.StartToTray = StartToTrayToggle.IsChecked == true;
        SettingsStore.Save(_settings);
    }

    private void BindAudioParams()
    {
        if (_audioParamsBound)
        {
            RefreshAudioParamSelections();
            return;
        }

        if (_settings.AudioSampleRate <= 0)
        {
            _settings.AudioSampleRate = 48000;
            SettingsStore.Save(_settings);
        }

        BindAudioParam(AudioSampleRateCombo, new[] { 16000, 44100, 48000, 88200, 96000 },
            () => _settings.AudioSampleRate, v => _settings.AudioSampleRate = v, "采样率(Hz)", allowAuto: false);
        _audioParamsBound = true;
    }

    private void RefreshAudioParamSelections()
    {
        _updating = true;
        AudioSampleRateCombo.SelectedValue = _settings.AudioSampleRate;
        _updating = false;
    }

    private void BindAudioParam(ComboBox comboBox, IReadOnlyCollection<int> options, Func<int> getter, Action<int> setter, string label, bool allowAuto = false)
    {
        var list = options.Select(v => new SettingOption<int>(v, v.ToString())).ToList();
        if (allowAuto)
            list.Insert(0, new SettingOption<int>(0, "自动"));

        int current = getter();
        if (!list.Any(o => o.Value == current))
        {
            string labelText = current == 0 ? "自动" : current.ToString();
            list.Insert(allowAuto ? 1 : 0, new SettingOption<int>(current, labelText));
        }

        comboBox.DisplayMemberPath = nameof(SettingOption<int>.Label);
        comboBox.SelectedValuePath = nameof(SettingOption<int>.Value);
        comboBox.ItemsSource = list;
        comboBox.SelectedValue = current;
        comboBox.Tag = new AudioParamBinding(getter, setter, label);
        comboBox.SelectionChanged += OnAudioParamSelectionChanged;
    }

    private void OnAudioParamSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating)
            return;

        if (sender is not ComboBox comboBox)
            return;

        if (comboBox.Tag is not AudioParamBinding binding)
            return;

        if (comboBox.SelectedValue is not int value)
            return;

        if (value == binding.Get())
            return;

        binding.Set(value);
        SettingsStore.Save(_settings);
        _onSettingsChanged?.Invoke(_settings);
    }

    private sealed record AudioParamBinding(Func<int> Get, Action<int> Set, string Label);
    private sealed record SettingOption<T>(T Value, string Label);
}

