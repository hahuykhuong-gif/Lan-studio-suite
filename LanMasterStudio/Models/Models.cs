using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace LanMasterStudio.Models
{
    public enum LayoutMode
    {
        Single,
        Split2,
        Grid4,
        Pip
    }

    public enum AudioSourceKind
    {
        LocalAudioDevice,
        LanAudioNode,
        SatelliteCameraAudio
    }

    public class SatelliteDevice : INotifyPropertyChanged
    {
        private string _name = "Cam Vệ Tinh";
        private string _ipAddress = "0.0.0.0";
        private int _port = 9001;
        private bool _isOnline = true;
        private int _batteryLevel = 100;
        private float _temperatureC = 32.0f;
        private int _latencyMs = 10;
        private bool _isOnProgram;
        private bool _isOnPreview;
        private bool _isTalkingPrivate;
        private ImageSource? _latestFrame;

        public string DeviceId { get; set; } = Guid.NewGuid().ToString("N")[..8];

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string IpAddress
        {
            get => _ipAddress;
            set { _ipAddress = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusSummary)); }
        }

        public int Port
        {
            get => _port;
            set { _port = value; OnPropertyChanged(); }
        }

        public bool IsOnline
        {
            get => _isOnline;
            set { _isOnline = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusSummary)); }
        }

        public int BatteryLevel
        {
            get => _batteryLevel;
            set { _batteryLevel = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusSummary)); }
        }

        public float TemperatureC
        {
            get => _temperatureC;
            set { _temperatureC = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusSummary)); }
        }

        public int LatencyMs
        {
            get => _latencyMs;
            set { _latencyMs = value; OnPropertyChanged(); }
        }

        public bool IsOnProgram
        {
            get => _isOnProgram;
            set { _isOnProgram = value; OnPropertyChanged(); OnPropertyChanged(nameof(TallyStatusText)); }
        }

        public bool IsOnPreview
        {
            get => _isOnPreview;
            set { _isOnPreview = value; OnPropertyChanged(); OnPropertyChanged(nameof(TallyStatusText)); }
        }

        public bool IsTalkingPrivate
        {
            get => _isTalkingPrivate;
            set { _isTalkingPrivate = value; OnPropertyChanged(); }
        }

        public ImageSource? LatestFrame
        {
            get => _latestFrame;
            set { _latestFrame = value; OnPropertyChanged(); }
        }

        public string TallyStatusText => IsOnProgram ? "PGM (LIVE)" : (IsOnPreview ? "PVW (DEMO)" : "IDLE");
        public string StatusSummary => IsOnline ? $"{IpAddress} | Pin {BatteryLevel}% | {TemperatureC:0.0}°C" : $"{IpAddress} | OFFLINE";
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class AudioSourceItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private float _peakLevel;
        private int _delayMs;

        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public AudioSourceKind Kind { get; set; }
        public int Priority { get; set; }
        public string? NativeDeviceId { get; set; }
        public string? IpAddress { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public float PeakLevel
        {
            get => _peakLevel;
            set { _peakLevel = value; OnPropertyChanged(); }
        }

        public int DelayMs
        {
            get => _delayMs;
            set { _delayMs = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SceneCompositionState
    {
        public LayoutMode Layout { get; set; } = LayoutMode.Single;
        public string? PrimaryCameraId { get; set; }
        public ObservableCollection<string> ActiveCameraIds { get; set; } = new();
        public string OverlayText { get; set; } = string.Empty;
        public double TextX { get; set; } = 30;
        public double TextY { get; set; } = 360;
        public double TextFontSize { get; set; } = 26;
        public string? OverlayImagePath { get; set; }
        public double ImageX { get; set; } = 20;
        public double ImageY { get; set; } = 20;
        public double ImageWidth { get; set; } = 130;

        public SceneCompositionState Clone()
        {
            return new SceneCompositionState
            {
                Layout = Layout,
                PrimaryCameraId = PrimaryCameraId,
                ActiveCameraIds = new ObservableCollection<string>(ActiveCameraIds),
                OverlayText = OverlayText,
                TextX = TextX,
                TextY = TextY,
                TextFontSize = TextFontSize,
                OverlayImagePath = OverlayImagePath,
                ImageX = ImageX,
                ImageY = ImageY,
                ImageWidth = ImageWidth
            };
        }
    }
}
