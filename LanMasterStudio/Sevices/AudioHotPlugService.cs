using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LanMasterStudio.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LanMasterStudio.Services
{
    public class AudioHotPlugService : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator _enumerator;
        private readonly ObservableCollection<AudioSourceItem> _sources;
        private WaveInEvent? _activeCapture;
        private BufferedWaveProvider? _bufferedWave;
        private readonly object _lock = new();
        private bool _autoMode = true;
        private AudioSourceItem? _currentSource;
        private CancellationTokenSource? _levelPollCts;

        public event Action<AudioSourceItem?>? ActiveAudioSourceChanged;
        public event Action<byte[], int>? ProgramPcmAudioCaptured;
        public event Action<string>? LogMessage;

        public AudioHotPlugService(ObservableCollection<AudioSourceItem> sources)
        {
            _sources = sources;
            _enumerator = new MMDeviceEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
            RefreshWindowsCaptureDevices();
            StartLevelMonitor();
        }

        public bool AutoMode
        {
            get => _autoMode;
            set
            {
                _autoMode = value;
                if (_autoMode)
                {
                    EvaluatePriorityFailover();
                }
            }
        }

        public AudioSourceItem? CurrentSource => _currentSource;

        public void RefreshWindowsCaptureDevices()
        {
            try
            {
                var endpoints = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var existingLocal = _sources.Where(s => s.Kind == AudioSourceKind.LocalAudioDevice).ToList();
                    foreach (var old in existingLocal)
                    {
                        if (!endpoints.Any(e => e.ID == old.NativeDeviceId))
                        {
                            _sources.Remove(old);
                        }
                    }

                    foreach (var ep in endpoints)
                    {
                        if (_sources.Any(s => s.NativeDeviceId == ep.ID)) continue;

                        string friendly = ep.FriendlyName;
                        bool isUsbOrSoundcard = friendly.Contains("USB", StringComparison.OrdinalIgnoreCase)
                            || friendly.Contains("Sound", StringComparison.OrdinalIgnoreCase)
                            || friendly.Contains("Mixer", StringComparison.OrdinalIgnoreCase)
                            || friendly.Contains("Audio", StringComparison.OrdinalIgnoreCase)
                            || friendly.Contains("Line", StringComparison.OrdinalIgnoreCase);

                        int priority = isUsbOrSoundcard ? 1 : 2;
                        _sources.Add(new AudioSourceItem
                        {
                            Id = $"win:{ep.ID}",
                            NativeDeviceId = ep.ID,
                            DisplayName = $"{friendly} ({(priority == 1 ? "Ưu tiên cao" : "Line/Mic")})",
                            Kind = AudioSourceKind.LocalAudioDevice,
                            Priority = priority
                        });
                    }

                    SortSources();
                });

                EvaluatePriorityFailover();
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Lỗi dò thiết bị âm thanh: {ex.Message}");
            }
        }

        public void RegisterOrUpdateLanAudioNode(string nodeId, string label, string ip)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var item = _sources.FirstOrDefault(s => s.Id == $"lan-audio:{nodeId}");
                if (item == null)
                {
                    _sources.Add(new AudioSourceItem
                    {
                        Id = $"lan-audio:{nodeId}",
                        DisplayName = $"[LAN Audio] {label} ({ip})",
                        Kind = AudioSourceKind.LanAudioNode,
                        IpAddress = ip,
                        Priority = 3
                    });
                    SortSources();
                    LogMessage?.Invoke($"Tự động phát hiện Audio Node qua LAN: {label} ({ip})");
                }
                else
                {
                    item.IpAddress = ip;
                }
            });

            EvaluatePriorityFailover();
        }

        public void FeedLanAudioPcm(string nodeId, byte[] pcmBytes, int length)
        {
            if (_currentSource?.Id != $"lan-audio:{nodeId}") return;

            float maxPeak = 0f;
            for (int i = 0; i < length - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(pcmBytes, i);
                float abs = Math.Abs(sample / 32768f);
                if (abs > maxPeak) maxPeak = abs;
            }
            _currentSource.PeakLevel = maxPeak;
            ProgramPcmAudioCaptured?.Invoke(pcmBytes, length);
        }

        public void SelectSource(AudioSourceItem? source)
        {
            lock (_lock)
            {
                StopCaptureInternal();
                _currentSource = source;

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    foreach (var s in _sources)
                        s.IsSelected = (s == source);
                });

                if (source == null)
                {
                    ActiveAudioSourceChanged?.Invoke(null);
                    return;
                }

                if (source.Kind == AudioSourceKind.LocalAudioDevice && !string.IsNullOrEmpty(source.NativeDeviceId))
                {
                    try
                    {
                        int matchedIndex = -1;
                        int waveInCapsCount = WaveIn.DeviceCount;
                        for (int i = 0; i < waveInCapsCount; i++)
                        {
                            var caps = WaveIn.GetCapabilities(i);
                            if (source.DisplayName.StartsWith(caps.ProductName, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedIndex = i;
                                break;
                            }
                        }

                        if (matchedIndex < 0 && waveInCapsCount > 0)
                            matchedIndex = 0;

                        if (matchedIndex >= 0)
                        {
                            _bufferedWave = new BufferedWaveProvider(new WaveFormat(48000, 16, 2))
                            {
                                DiscardOnBufferOverflow = true,
                                BufferDuration = TimeSpan.FromSeconds(2)
                            };

                            _activeCapture = new WaveInEvent
                            {
                                DeviceNumber = matchedIndex,
                                WaveFormat = new WaveFormat(48000, 16, 2),
                                BufferMilliseconds = 25
                            };
                            _activeCapture.DataAvailable += OnCaptureDataAvailable;
                            _activeCapture.StartRecording();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogMessage?.Invoke($"Không thể mở luồng âm thanh '{source.DisplayName}': {ex.Message}");
                    }
                }

                ActiveAudioSourceChanged?.Invoke(source);
                LogMessage?.Invoke($"Đang dùng nguồn âm thanh: {source.DisplayName}");
            }
        }

        private void OnCaptureDataAvailable(object? sender, WaveInEventArgs e)
        {
            _bufferedWave?.AddSamples(e.Buffer, 0, e.BytesRecorded);

            float maxPeak = 0f;
            for (int i = 0; i < e.BytesRecorded - 1; i += 2)
            {
                short sample = BitConverter.ToInt16(e.Buffer, i);
                float abs = Math.Abs(sample / 32768f);
                if (abs > maxPeak) maxPeak = abs;
            }

            if (_currentSource != null)
            {
                _currentSource.PeakLevel = maxPeak;
            }

            ProgramPcmAudioCaptured?.Invoke(e.Buffer, e.BytesRecorded);
        }

        public void EvaluatePriorityFailover()
        {
            if (!_autoMode) return;

            AudioSourceItem? best;
            lock (_lock)
            {
                best = _sources
                    .Where(s => s.Kind != AudioSourceKind.SatelliteCameraAudio)
                    .OrderBy(s => s.Priority)
                    .FirstOrDefault()
                    ?? _sources.FirstOrDefault();
            }

            if (best != null && best != _currentSource)
            {
                SelectSource(best);
            }
        }

        private void SortSources()
        {
            var ordered = _sources.OrderBy(s => s.Priority).ThenBy(s => s.DisplayName).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int currentIdx = _sources.IndexOf(ordered[i]);
                if (currentIdx != i && currentIdx >= 0)
                {
                    _sources.Move(currentIdx, i);
                }
            }
        }

        private void StartLevelMonitor()
        {
            _levelPollCts = new CancellationTokenSource();
            var token = _levelPollCts.Token;
            Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(140, token);
                    foreach (var s in _sources.ToList())
                    {
                        if (s != _currentSource && s.PeakLevel > 0.01f)
                        {
                            s.PeakLevel *= 0.7f;
                        }
                    }
                }
            }, token);
        }

        private void StopCaptureInternal()
        {
            if (_activeCapture != null)
            {
                _activeCapture.DataAvailable -= OnCaptureDataAvailable;
                try { _activeCapture.StopRecording(); } catch { }
                _activeCapture.Dispose();
                _activeCapture = null;
            }
            _bufferedWave = null;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => RefreshWindowsCaptureDevices();
        public void OnDeviceAdded(string pwstrDeviceId)
        {
            LogMessage?.Invoke("Phát hiện thiết bị âm thanh mới được cắm vào...");
            RefreshWindowsCaptureDevices();
        }
        public void OnDeviceRemoved(string deviceId)
        {
            LogMessage?.Invoke("Thiết bị âm thanh vừa bị rút ra, đang kiểm tra nguồn dự phòng...");
            bool wasCurrent = _currentSource?.NativeDeviceId == deviceId;
            RefreshWindowsCaptureDevices();
            if (wasCurrent)
            {
                EvaluatePriorityFailover();
            }
        }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void Dispose()
        {
            _levelPollCts?.Cancel();
            StopCaptureInternal();
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _enumerator.Dispose();
        }
    }
}
