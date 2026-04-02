using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeepFilterNetGui.Audio;

namespace DeepFilterNetGui.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int WaveformWidth = 1000;
    private const int WaveformHeight = 1000;
    private const int SpectrumWidth = 1000;
    private const int SpectrumHeight = 1000;

    private readonly WriteableBitmap _waveformBitmap;
    private readonly int[] _waveformPixels;
    private readonly WriteableBitmap _spectrumBitmap;
    private readonly int[] _spectrumPixels;

    private bool _isRunning;
    private string _statusText = "已停止";
    private string _appVersion = string.Empty;
    private AudioBackendItem? _selectedInputBackend;
    private AudioBackendItem? _selectedOutputBackend;
    private AudioDeviceItem? _selectedInputDevice;
    private AudioDeviceItem? _selectedOutputDevice;
    private double _frameMs;
    private double _inferMs;
    private double _avgMs;
    private double _rtf;
    private double _latencyMs;
    private double _inRms;
    private double _outRms;
    private double _fps;
    private double _denoiseStrengthDb = 100;
    private double _postFilterBeta = 0;
    private int _actualInputSampleRate;
    private int _actualOutputSampleRate;
    private int _actualBufferSamples;
    private string _processingChannelMode = "未运行";

    public MainViewModel(Action startAction, Action stopAction)
    {
        Backends = new ObservableCollection<AudioBackendItem>();
        InputDevices = new ObservableCollection<AudioDeviceItem>();
        OutputDevices = new ObservableCollection<AudioDeviceItem>();
        StartCommand = new RelayCommand(startAction, () => !IsRunning);
        StopCommand = new RelayCommand(stopAction, () => IsRunning);
        ToggleCommand = new RelayCommand(() =>
        {
            if (IsRunning)
            {
                stopAction();
            }
            else
            {
                startAction();
            }
        });

        _waveformBitmap = new WriteableBitmap(WaveformWidth, WaveformHeight, 96, 96, PixelFormats.Bgra32, null);
        _waveformPixels = new int[WaveformWidth * WaveformHeight];
        _spectrumBitmap = new WriteableBitmap(SpectrumWidth, SpectrumHeight, 96, 96, PixelFormats.Bgra32, null);
        _spectrumPixels = new int[SpectrumWidth * SpectrumHeight];
    }

    public ObservableCollection<AudioBackendItem> Backends { get; }
    public ObservableCollection<AudioDeviceItem> InputDevices { get; }
    public ObservableCollection<AudioDeviceItem> OutputDevices { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ToggleCommand { get; }

    public AudioBackendItem? SelectedInputBackend
    {
        get => _selectedInputBackend;
        set => SetField(ref _selectedInputBackend, value);
    }

    public AudioBackendItem? SelectedOutputBackend
    {
        get => _selectedOutputBackend;
        set => SetField(ref _selectedOutputBackend, value);
    }

    public AudioDeviceItem? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set => SetField(ref _selectedInputDevice, value);
    }

    public AudioDeviceItem? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set => SetField(ref _selectedOutputDevice, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetField(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsNotRunning));
                OnPropertyChanged(nameof(InputSampleRateDisplay));
                OnPropertyChanged(nameof(OutputSampleRateDisplay));
                (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (ToggleCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotRunning => !IsRunning;

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string AppVersion
    {
        get => _appVersion;
        set
        {
            if (SetField(ref _appVersion, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string WindowTitle
        => string.IsNullOrWhiteSpace(_appVersion) ? "DeepFilterNet3 实时降噪" : $"DeepFilterNet3 实时降噪 v{_appVersion}";

    public ImageSource WaveformImage => _waveformBitmap;
    public ImageSource SpectrumImage => _spectrumBitmap;

    public double FrameMs { get => _frameMs; set => SetField(ref _frameMs, value); }
    public double InferMs { get => _inferMs; set => SetField(ref _inferMs, value); }
    public double AvgMs { get => _avgMs; set => SetField(ref _avgMs, value); }
    public double Rtf { get => _rtf; set => SetField(ref _rtf, value); }
    public double LatencyMs { get => _latencyMs; set => SetField(ref _latencyMs, value); }
    public double InRms { get => _inRms; set => SetField(ref _inRms, value); }
    public double OutRms { get => _outRms; set => SetField(ref _outRms, value); }
    public double Fps { get => _fps; set => SetField(ref _fps, value); }
    public double DenoiseStrengthDb { get => _denoiseStrengthDb; set => SetField(ref _denoiseStrengthDb, value); }
    public double PostFilterBeta { get => _postFilterBeta; set => SetField(ref _postFilterBeta, value); }
    public int ActualInputSampleRate
    {
        get => _actualInputSampleRate;
        set
        {
            if (SetField(ref _actualInputSampleRate, value))
            {
                OnPropertyChanged(nameof(InputSampleRateDisplay));
            }
        }
    }

    public int ActualOutputSampleRate
    {
        get => _actualOutputSampleRate;
        set
        {
            if (SetField(ref _actualOutputSampleRate, value))
            {
                OnPropertyChanged(nameof(OutputSampleRateDisplay));
            }
        }
    }

    public int ActualBufferSamples
    {
        get => _actualBufferSamples;
        set
        {
            SetField(ref _actualBufferSamples, value);
        }
    }

    public string ProcessingChannelMode
    {
        get => _processingChannelMode;
        set => SetField(ref _processingChannelMode, value);
    }

    public string InputSampleRateDisplay
    {
        get
        {
            if (!IsRunning)
                return "未运行";
            if (_actualInputSampleRate > 0)
                return $"{_actualInputSampleRate} Hz";
            return "未知";
        }
    }

    public string OutputSampleRateDisplay
    {
        get
        {
            if (!IsRunning)
                return "未运行";
            if (_actualOutputSampleRate > 0)
                return $"{_actualOutputSampleRate} Hz";
            return "未知";
        }
    }

    public void UpdateMetrics(Metrics metrics)
    {
        InferMs = metrics.InferMs;
        FrameMs = metrics.FrameMs;
        AvgMs = metrics.AvgMs;
        Rtf = metrics.Rtf;
        LatencyMs = metrics.LatencyMs;
        InRms = metrics.InRms;
        OutRms = metrics.OutRms;
        Fps = metrics.Fps;
    }

    public void UpdateWaveform(float[] samples)
    {
        Array.Fill(_waveformPixels, unchecked((int)0xFF111111));
        int mid = WaveformHeight / 2;
        int maxY = WaveformHeight - 1;
        for (int x = 0; x < WaveformWidth; x++)
        {
            int idx = x * samples.Length / WaveformWidth;
            float sample = samples[idx];
            int y = mid - (int)(sample * (mid - 1));
            y = Math.Clamp(y, 0, maxY);
            int y0 = Math.Min(mid, y);
            int y1 = Math.Max(mid, y);
            for (int yy = y0; yy <= y1; yy++)
            {
                _waveformPixels[yy * WaveformWidth + x] = unchecked((int)0xFF00FF6A);
            }
        }

        _waveformBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, WaveformWidth, WaveformHeight),
            _waveformPixels, WaveformWidth * 4, 0);
    }

    public void UpdateSpectrum(float[] magnitudes)
    {
        Array.Fill(_spectrumPixels, unchecked((int)0xFF111111));
        int bins = magnitudes.Length;
        for (int x = 0; x < SpectrumWidth; x++)
        {
            int idx = x * bins / SpectrumWidth;
            float mag = magnitudes[idx];
            double db = 20 * Math.Log10(mag + 1e-6);
            double norm = (db + 80) / 80;
            norm = Math.Clamp(norm, 0, 1);
            int bar = (int)(norm * (SpectrumHeight - 1));
            for (int y = SpectrumHeight - 1; y >= SpectrumHeight - 1 - bar; y--)
            {
                int r = (int)(norm * 255);
                int g = (int)(Math.Min(1, norm * 1.2) * 255);
                int b = 64;
                int color = (255 << 24) | (r << 16) | (g << 8) | b;
                _spectrumPixels[y * SpectrumWidth + x] = color;
            }
        }

        _spectrumBitmap.WritePixels(new System.Windows.Int32Rect(0, 0, SpectrumWidth, SpectrumHeight),
            _spectrumPixels, SpectrumWidth * 4, 0);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

