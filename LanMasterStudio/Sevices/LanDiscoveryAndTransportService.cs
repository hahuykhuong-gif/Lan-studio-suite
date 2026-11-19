using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using LanMasterStudio.Models;

namespace LanMasterStudio.Services
{
    public class LanDiscoveryAndTransportService : IDisposable
    {
        public const int DiscoveryPort = 45888;
        public const int FrameIngressPort = 45889;
        public const int ControlPort = 45890;
        public const int TalkbackAudioPort = 45891;
        public const int LanAudioNodePort = 45892;

        private readonly ObservableCollection<SatelliteDevice> _devices;
        private readonly AudioHotPlugService _audioService;
        private readonly StudioSecurityService _security;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentDictionary<string, long> _lastUiFrameTicks = new();

        private UdpClient? _discoveryClient;
        private UdpClient? _frameClient;
        private UdpClient? _controlClient;
        private UdpClient? _talkbackClient;
        private UdpClient? _lanAudioClient;

        public event Action<string>? LogMessage;

        public LanDiscoveryAndTransportService(
            ObservableCollection<SatelliteDevice> devices,
            AudioHotPlugService audioService,
            StudioSecurityService security)
        {
            _devices = devices;
            _audioService = audioService;
            _security = security;
        }

        public void Start()
        {
            _discoveryClient = new UdpClient();
            _discoveryClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _discoveryClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _discoveryClient.EnableBroadcast = true;

            _frameClient = new UdpClient();
            _frameClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _frameClient.Client.Bind(new IPEndPoint(IPAddress.Any, FrameIngressPort));

            _lanAudioClient = new UdpClient();
            _lanAudioClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _lanAudioClient.Client.Bind(new IPEndPoint(IPAddress.Any, LanAudioNodePort));

            _controlClient = new UdpClient();
            _controlClient.EnableBroadcast = true;

            _talkbackClient = new UdpClient();

            _ = Task.Run(() => ListenDiscoveryLoop(_cts.Token));
            _ = Task.Run(() => ListenFramesLoop(_cts.Token));
            _ = Task.Run(() => ListenLanAudioLoop(_cts.Token));
            _ = Task.Run(() => AnnounceMasterLoop(_cts.Token));
            _ = Task.Run(() => WatchdogLoop(_cts.Token));

            LogMessage?.Invoke($"LAN Studio sẵn sàng | Token ghép cặp: {_security.LanPairingToken}");
        }

        private async Task AnnounceMasterLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        type = "MASTER_ANNOUNCE",
                        masterName = Environment.MachineName,
                        token = _security.LanPairingToken,
                        framePort = FrameIngressPort,
                        controlPort = ControlPort,
                        talkbackPort = TalkbackAudioPort,
                        lanAudioPort = LanAudioNodePort
                    });
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    await _controlClient!.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                }
                catch { }

                await Task.Delay(2500, token);
            }
        }

        private async Task ListenDiscoveryLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await _discoveryClient!.ReceiveAsync(token);
                    string json = Encoding.UTF8.GetString(res.Buffer);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "SATELLITE_HELLO")
                    {
                        string? incomingToken = root.TryGetProperty("token", out var tk) ? tk.GetString() : null;
                        if (!string.Equals(incomingToken, _security.LanPairingToken, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string id = root.GetProperty("deviceId").GetString() ?? res.RemoteEndPoint.Address.ToString();
                        string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Cam Vệ Tinh" : "Cam Vệ Tinh";
                        int battery = root.TryGetProperty("battery", out var b) ? b.GetInt32() : 100;
                        float temp = root.TryGetProperty("temp", out var t) ? (float)t.GetDouble() : 32.0f;
                        bool isAudioNode = root.TryGetProperty("audioOnly", out var ao) && ao.GetBoolean();
                        string ip = res.RemoteEndPoint.Address.ToString();

                        if (isAudioNode)
                        {
                            _audioService.RegisterOrUpdateLanAudioNode(id, name, ip);
                        }
                        else
                        {
                            UpsertSatellite(id, name, ip, battery, temp);
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private async Task ListenFramesLoop(CancellationToken token)
        {
            // Packet format: [4 bytes DeviceIdLen][DeviceId UTF8][JPEG bytes]
            // Throttle UI bitmap updates per device (~20fps max) to avoid CPU/thermal spike
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await _frameClient!.ReceiveAsync(token);
                    byte[] data = res.Buffer;
                    if (data.Length < 8 || data.Length > 262144) continue;

                    int idLen = BitConverter.ToInt32(data, 0);
                    if (idLen <= 0 || idLen > 64 || 4 + idLen >= data.Length) continue;

                    string deviceId = Encoding.UTF8.GetString(data, 4, idLen);
                    long nowMs = Environment.TickCount64;
                    long prevMs = _lastUiFrameTicks.GetOrAdd(deviceId, 0);
                    if (nowMs - prevMs < 48) continue;
                    _lastUiFrameTicks[deviceId] = nowMs;

                    int jpegOffset = 4 + idLen;
                    int jpegLength = data.Length - jpegOffset;

                    var dev = _devices.FirstOrDefault(d => d.DeviceId == deviceId);
                    if (dev == null)
                    {
                        dev = UpsertSatellite(deviceId, $"Cam-{deviceId}", res.RemoteEndPoint.Address.ToString(), 100, 30f);
                    }

                    dev.LastSeenUtc = DateTime.UtcNow;
                    dev.IsOnline = true;
                    dev.LatencyMs = 10;

                    using var ms = new MemoryStream(data, jpegOffset, jpegLength, writable: false);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 640;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();

                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        dev.LatestFrame = bmp;
                    });
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private async Task ListenLanAudioLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await _lanAudioClient!.ReceiveAsync(token);
                    byte[] data = res.Buffer;
                    if (data.Length < 8 || data.Length > 32768) continue;

                    int idLen = BitConverter.ToInt32(data, 0);
                    if (idLen <= 0 || idLen > 64 || 4 + idLen >= data.Length) continue;

                    string nodeId = Encoding.UTF8.GetString(data, 4, idLen);
                    int pcmOffset = 4 + idLen;
                    int pcmLength = data.Length - pcmOffset;

                    byte[] pcm = new byte[pcmLength];
                    Buffer.BlockCopy(data, pcmOffset, pcm, 0, pcmLength);
                    _audioService.FeedLanAudioPcm(nodeId, pcm, pcmLength);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        public async Task SendTalkbackPcmAsync(SatelliteDevice? specificDevice, bool allCall, byte[] pcmBytes, int length)
        {
            if (_talkbackClient == null || length <= 0) return;
            foreach (var dev in _devices.ToList())
            {
                if (!dev.IsOnline || dev.IpAddress == "0.0.0.0") continue;
                bool target = allCall || (specificDevice != null && dev.DeviceId == specificDevice.DeviceId) || dev.IsTalkingPrivate;
                if (!target) continue;

                try
                {
                    await _talkbackClient.SendAsync(pcmBytes, length, new IPEndPoint(IPAddress.Parse(dev.IpAddress), TalkbackAudioPort));
                }
                catch { }
            }
        }

        private async Task WatchdogLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2500, token);
                var now = DateTime.UtcNow;
                foreach (var d in _devices.ToList())
                {
                    if ((now - d.LastSeenUtc).TotalSeconds > 6 && d.IsOnline)
                    {
                        d.IsOnline = false;
                        LogMessage?.Invoke($"Thiết bị vệ tinh '{d.Name}' mất tín hiệu LAN.");
                    }
                }
            }
        }

        private SatelliteDevice UpsertSatellite(string id, string name, string ip, int battery, float temp)
        {
            SatelliteDevice? target = null;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                target = _devices.FirstOrDefault(d => d.DeviceId == id);
                if (target == null)
                {
                    target = new SatelliteDevice
                    {
                        DeviceId = id,
                        Name = name,
                        IpAddress = ip,
                        BatteryLevel = battery,
                        TemperatureC = temp,
                        IsOnline = true,
                        LastSeenUtc = DateTime.UtcNow
                    };
                    _devices.Add(target);
                    LogMessage?.Invoke($"Đã kết nối thiết bị vệ tinh: {name} ({ip})");
                }
                else
                {
                    target.Name = name;
                    target.IpAddress = ip;
                    target.BatteryLevel = battery;
                    target.TemperatureC = temp;
                    target.IsOnline = true;
                    target.LastSeenUtc = DateTime.UtcNow;
                }
            });
            return target!;
        }

        public async Task SendTallyAndTalkbackAsync(SatelliteDevice? specificDevice, bool allCall, bool talkOn)
        {
            if (_controlClient == null) return;

            foreach (var dev in _devices.ToList())
            {
                if (!dev.IsOnline || dev.IpAddress == "0.0.0.0") continue;

                bool isTargetedTalk = talkOn && (allCall || (specificDevice != null && dev.DeviceId == specificDevice.DeviceId) || dev.IsTalkingPrivate);
                var cmd = JsonSerializer.Serialize(new
                {
                    type = "STUDIO_STATE",
                    token = _security.LanPairingToken,
                    targetDeviceId = dev.DeviceId,
                    tally = dev.IsOnProgram ? "PGM" : (dev.IsOnPreview ? "PVW" : "OFF"),
                    talkback = isTargetedTalk
                });

                byte[] bytes = Encoding.UTF8.GetBytes(cmd);
                try
                {
                    await _controlClient.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Parse(dev.IpAddress), ControlPort));
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _discoveryClient?.Dispose();
            _frameClient?.Dispose();
            _controlClient?.Dispose();
            _talkbackClient?.Dispose();
            _lanAudioClient?.Dispose();
        }
    }
}
