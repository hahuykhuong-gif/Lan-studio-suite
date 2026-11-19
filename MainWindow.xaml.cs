using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LanMasterStudio.Models;
using LanMasterStudio.Services;
using Microsoft.Win32;

namespace LanMasterStudio
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<SatelliteDevice> _satellites = new();
        private readonly ObservableCollection<AudioSourceItem> _audioSources = new();

        private readonly StudioSecurityService _security = new();
        private readonly AudioHotPlugService _audioService;
        private readonly LanDiscoveryAndTransportService _lanService;
        private readonly SceneRendererAndRecorder _renderer = new();
        private readonly DispatcherTimer _uiTimer;

        private SceneCompositionState _previewState = new();
        private SceneCompositionState _programState = new();

        public MainWindow()
        {
            InitializeComponent();

            SatellitesList.ItemsSource = _satellites;
            AudioSourcesList.ItemsSource = _audioSources;

            var savedRtmp = _security.LoadRtmpCredentials();
            TxtRtmpUrl.Text = savedRtmp.RtmpUrl;
            PwdStreamKey.Password = savedRtmp.StreamKey;

            _audioService = new AudioHotPlugService(_audioSources);
            _audioService.LogMessage += AppendLog;
            _audioService.ProgramPcmAudioCaptured += OnProgramAudioCaptured;

            _lanService = new LanDiscoveryAndTransportService(_satellites, _audioService, _security);
            _lanService.LogMessage += AppendLog;
            _lanService.Start();

            _renderer.LogMessage += AppendLog;

            // Giảm nhịp render xuống ~20fps khi idle để tiết kiệm CPU/nhiệt, tự tăng khi ghi hình/RTMP
            _uiTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();

            Closed += OnWindowClosed;
        }

        private void OnProgramAudioCaptured(byte[] pcm, int length)
        {
            _renderer.AppendProgramAudioPcm(pcm, length);

            bool talkAll = BtnTalkAll.IsChecked == true;
            bool anyPrivate = _satellites.Any(s => s.IsTalkingPrivate);
            if (talkAll || anyPrivate)
            {
                _ = _lanService.SendTalkbackPcmAsync(null, talkAll, pcm, length);
            }
        }

        private void OnUiTick(object? sender, EventArgs e)
        {
            _renderer.RenderToCanvas(PreviewCanvas, _previewState, _satellites);
            _renderer.RenderToCanvas(ProgramCanvas, _programState, _satellites);
            _renderer.ProcessProgramFrameTick(ProgramCanvas);
        }

        private void BtnToggleRtmp_Click(object sender, RoutedEventArgs e)
        {
            if (!_renderer.IsStreamingRtmp)
            {
                string url = TxtRtmpUrl.Text.Trim();
                string key = PwdStreamKey.Password.Trim();
                _security.SaveRtmpCredentials(url, key);

                bool started = _renderer.StartRtmpStream(url, key);
                if (started)
                {
                    BtnToggleRtmp.IsChecked = true;
                    BtnToggleRtmp.Content = "■ DỪNG LIVE";
                    BtnToggleRtmp.Background = System.Windows.Media.Brushes.Crimson;
                }
                else
                {
                    BtnToggleRtmp.IsChecked = false;
                }
            }
            else
            {
                _renderer.StopRtmpStream();
                BtnToggleRtmp.IsChecked = false;
                BtnToggleRtmp.Content = "LIVE MXH";
                BtnToggleRtmp.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 83, 9));
            }
        }

        private void SatelliteCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SatelliteDevice dev)
            {
                if (_previewState.Layout == LayoutMode.Single)
                {
                    _previewState.PrimaryCameraId = dev.DeviceId;
                    _previewState.ActiveCameraIds.Clear();
                    _previewState.ActiveCameraIds.Add(dev.DeviceId);
                }
                else
                {
                    if (!_previewState.ActiveCameraIds.Contains(dev.DeviceId))
                    {
                        _previewState.ActiveCameraIds.Add(dev.DeviceId);
                    }
                    _previewState.PrimaryCameraId = dev.DeviceId;
                }

                foreach (var s in _satellites)
                {
                    s.IsOnPreview = _previewState.ActiveCameraIds.Contains(s.DeviceId);
                }

                _ = _lanService.SendTallyAndTalkbackAsync(null, BtnTalkAll.IsChecked == true, BtnTalkAll.IsChecked == true);
                AppendLog($"Đã đưa '{dev.Name}' vào Màn Phụ (Demo).");
            }
        }

        private void BtnSendToLive_Click(object sender, RoutedEventArgs e)
        {
            _programState = _previewState.Clone();

            foreach (var s in _satellites)
            {
                s.IsOnProgram = _programState.ActiveCameraIds.Contains(s.DeviceId);
            }

            TxtActiveLiveSummary.Text = $"Chế độ: {_programState.Layout} | {_programState.ActiveCameraIds.Count} cam";
            _ = _lanService.SendTallyAndTalkbackAsync(null, BtnTalkAll.IsChecked == true, BtnTalkAll.IsChecked == true);
            AppendLog("Đã SEND bố cục từ Màn Phụ sang Màn Live Chính!");
        }

        private void BtnSyncFromLive_Click(object sender, RoutedEventArgs e)
        {
            _previewState = _programState.Clone();
            TxtOverlayInput.Text = _previewState.OverlayText;
            AppendLog("Đã đồng bộ Màn Phụ theo Màn Live Chính hiện tại.");
        }

        private void BtnLayoutSingle_Click(object sender, RoutedEventArgs e) => SetLayout(LayoutMode.Single);
        private void BtnLayoutSplit2_Click(object sender, RoutedEventArgs e) => SetLayout(LayoutMode.Split2);
        private void BtnLayoutGrid4_Click(object sender, RoutedEventArgs e) => SetLayout(LayoutMode.Grid4);
        private void BtnLayoutPip_Click(object sender, RoutedEventArgs e) => SetLayout(LayoutMode.Pip);

        private void SetLayout(LayoutMode mode)
        {
            _previewState.Layout = mode;
            if (mode == LayoutMode.Single && _previewState.ActiveCameraIds.Count > 1)
            {
                var first = _previewState.PrimaryCameraId ?? _previewState.ActiveCameraIds.First();
                _previewState.ActiveCameraIds.Clear();
                _previewState.ActiveCameraIds.Add(first);
            }
            AppendLog($"Màn Phụ chuyển sang bố cục: {mode}");
        }

        private void OverlayControls_Changed(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _previewState.OverlayText = TxtOverlayInput.Text;
            if (double.TryParse(TxtTextX.Text, out double tx)) _previewState.TextX = tx;
            if (double.TryParse(TxtTextY.Text, out double ty)) _previewState.TextY = ty;
            if (double.TryParse(TxtImgX.Text, out double ix)) _previewState.ImageX = ix;
            if (double.TryParse(TxtImgY.Text, out double iy)) _previewState.ImageY = iy;
        }

        private void BtnPickImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Ảnh (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
            };
            if (dlg.ShowDialog() == true)
            {
                _previewState.OverlayImagePath = dlg.FileName;
                TxtSelectedImagePath.Text = Path.GetFileName(dlg.FileName);
                AppendLog($"Đã chọn ảnh đè: {dlg.FileName}");
            }
        }

        private void AudioSourceRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.DataContext is AudioSourceItem item && item.IsSelected)
            {
                _audioService.SelectSource(item);
            }
        }

        private void ChkAutoAudio_Changed(object sender, RoutedEventArgs e)
        {
            if (_audioService != null)
            {
                _audioService.AutoMode = ChkAutoAudio.IsChecked == true;
                AppendLog($"Chế độ tự động chọn nguồn âm thanh: {(_audioService.AutoMode ? "BẬT" : "TẮT")}");
            }
        }

        private void BtnRescanAudio_Click(object sender, RoutedEventArgs e)
        {
            _audioService.RefreshWindowsCaptureDevices();
            AppendLog("Đã quét lại danh sách thiết bị âm thanh.");
        }

        private void BtnTalkAll_Click(object sender, RoutedEventArgs e)
        {
            bool active = BtnTalkAll.IsChecked == true;
            _ = _lanService.SendTallyAndTalkbackAsync(null, active, active);
            AppendLog(active ? "ĐANG ĐÀM THOẠI VỚI TOÀN BỘ THIẾT BỊ VỆ TINH." : "Đã tắt đàm thoại toàn bộ.");
        }

        private void BtnTalkPrivate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb && tb.Tag is SatelliteDevice dev)
            {
                bool active = tb.IsChecked == true;
                dev.IsTalkingPrivate = active;
                _ = _lanService.SendTallyAndTalkbackAsync(dev, false, active);
                AppendLog(active ? $"Đang thoại riêng với '{dev.Name}'." : $"Đã tắt thoại riêng với '{dev.Name}'.");
            }
        }

        private void BtnRecordToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!_renderer.IsRecording)
            {
                string outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LanMasterStudio");
                var folder = _renderer.StartRecording(outDir);
                BtnRecordToggle.Content = "■ DỪNG GHI";
                BtnRecordToggle.Background = System.Windows.Media.Brushes.DarkRed;
                AppendLog($"Đang ghi hình + ghi âm (WAV) tại: {folder}");
            }
            else
            {
                var folder = _renderer.StopRecording();
                BtnRecordToggle.Content = "● GHI HÌNH";
                BtnRecordToggle.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(153, 27, 27));
                AppendLog($"Đã dừng ghi hình tại: {folder}");
            }
        }

        private void AppendLog(string msg)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                TxtStatusLog.Text = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            });
        }

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _uiTimer.Stop();
            _renderer.Dispose();
            _lanService.Dispose();
            _audioService.Dispose();
        }
    }
}
