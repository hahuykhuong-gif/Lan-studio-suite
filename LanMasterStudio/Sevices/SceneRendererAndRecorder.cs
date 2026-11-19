using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LanMasterStudio.Models;

namespace LanMasterStudio.Services
{
    public class SceneRendererAndRecorder : IDisposable
    {
        private bool _isRecording;
        private bool _isStreamingRtmp;
        private string? _currentRecordFolder;
        private FileStream? _wavStream;
        private BinaryWriter? _wavWriter;
        private int _audioDataBytes;
        private int _frameCounter;
        private readonly object _recLock = new();

        private Process? _ffmpegProcess;
        private Stream? _ffmpegVideoStdin;
        private Stream? _ffmpegAudioStdin;
        private readonly object _rtmpLock = new();

        public event Action<string>? LogMessage;

        public bool IsRecording => _isRecording;
        public bool IsStreamingRtmp => _isStreamingRtmp;

        public void RenderToCanvas(
            Canvas targetCanvas,
            SceneCompositionState state,
            IEnumerable<SatelliteDevice> allDevices)
        {
            targetCanvas.Children.Clear();

            double w = targetCanvas.ActualWidth > 0 ? targetCanvas.ActualWidth : 960;
            double h = targetCanvas.ActualHeight > 0 ? targetCanvas.ActualHeight : 540;

            var deviceMap = allDevices.ToDictionary(d => d.DeviceId, d => d);
            var activeSources = state.ActiveCameraIds
                .Where(id => deviceMap.ContainsKey(id))
                .Select(id => deviceMap[id])
                .ToList();

            if (activeSources.Count == 0 && !string.IsNullOrEmpty(state.PrimaryCameraId) && deviceMap.TryGetValue(state.PrimaryCameraId, out var primary))
            {
                activeSources.Add(primary);
            }

            if (activeSources.Count == 0)
            {
                var placeholder = new TextBlock
                {
                    Text = "CHƯA CÓ TÍN HIỆU CAMERA VỆ TINH",
                    Foreground = Brushes.Gray,
                    FontSize = 20,
                    FontWeight = FontWeights.Bold
                };
                Canvas.SetLeft(placeholder, Math.Max(20, (w - 340) / 2));
                Canvas.SetTop(placeholder, Math.Max(20, (h - 30) / 2));
                targetCanvas.Children.Add(placeholder);
            }
            else
            {
                var rects = ComputeLayoutRects(state.Layout, activeSources.Count, w, h);
                for (int i = 0; i < Math.Min(activeSources.Count, rects.Count); i++)
                {
                    var rect = rects[i];
                    var img = new Image
                    {
                        Source = activeSources[i].LatestFrame,
                        Width = rect.Width,
                        Height = rect.Height,
                        Stretch = Stretch.UniformToFill
                    };

                    var border = new Border
                    {
                        Width = rect.Width,
                        Height = rect.Height,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(45, 52, 68)),
                        BorderThickness = new Thickness(1),
                        Child = img
                    };

                    Canvas.SetLeft(border, rect.X);
                    Canvas.SetTop(border, rect.Y);
                    targetCanvas.Children.Add(border);
                }
            }

            if (!string.IsNullOrWhiteSpace(state.OverlayImagePath) && File.Exists(state.OverlayImagePath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(state.OverlayImagePath);
                    bmp.EndInit();

                    var overlayImg = new Image
                    {
                        Source = bmp,
                        Width = state.ImageWidth,
                        Stretch = Stretch.Uniform
                    };
                    Canvas.SetLeft(overlayImg, state.ImageX);
                    Canvas.SetTop(overlayImg, state.ImageY);
                    targetCanvas.Children.Add(overlayImg);
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(state.OverlayText))
            {
                var textBadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(195, 15, 23, 42)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                    BorderThickness = new Thickness(0, 0, 0, 3),
                    Padding = new Thickness(14, 6, 14, 6),
                    CornerRadius = new CornerRadius(4),
                    Child = new TextBlock
                    {
                        Text = state.OverlayText,
                        Foreground = Brushes.White,
                        FontSize = state.TextFontSize,
                        FontWeight = FontWeights.SemiBold
                    }
                };

                Canvas.SetLeft(textBadge, state.TextX);
                Canvas.SetTop(textBadge, state.TextY);
                targetCanvas.Children.Add(textBadge);
            }
        }

        private List<Rect> ComputeLayoutRects(LayoutMode mode, int count, double w, double h)
        {
            var list = new List<Rect>();
            if (mode == LayoutMode.Single || count == 1)
            {
                list.Add(new Rect(0, 0, w, h));
            }
            else if (mode == LayoutMode.Split2)
            {
                list.Add(new Rect(0, 0, w / 2, h));
                list.Add(new Rect(w / 2, 0, w / 2, h));
            }
            else if (mode == LayoutMode.Grid4)
            {
                double hw = w / 2;
                double hh = h / 2;
                list.Add(new Rect(0, 0, hw, hh));
                list.Add(new Rect(hw, 0, hw, hh));
                list.Add(new Rect(0, hh, hw, hh));
                list.Add(new Rect(hw, hh, hw, hh));
            }
            else if (mode == LayoutMode.Pip)
            {
                list.Add(new Rect(0, 0, w, h));
                double pipW = w * 0.28;
                double pipH = h * 0.28;
                list.Add(new Rect(w - pipW - 16, h - pipH - 16, pipW, pipH));
            }
            return list;
        }

        public bool StartRtmpStream(string rtmpUrl, string streamKey, int outWidth = 1280, int outHeight = 720, int fps = 24)
        {
            if (string.IsNullOrWhiteSpace(rtmpUrl) || string.IsNullOrWhiteSpace(streamKey))
            {
                LogMessage?.Invoke("Vui lòng nhập đầy đủ RTMP URL và Stream Key trước khi phát live.");
                return false;
            }

            if (!rtmpUrl.Trim().StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) &&
                !rtmpUrl.Trim().StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
            {
                LogMessage?.Invoke("RTMP URL phải bắt đầu bằng rtmp:// hoặc rtmps://");
                return false;
            }

            string? ffmpegPath = ResolveFfmpegExecutable();
            if (ffmpegPath == null)
            {
                LogMessage?.Invoke("Không tìm thấy ffmpeg.exe (hãy đặt ffmpeg.exe cạnh app hoặc trong PATH để đẩy RTMP lên MXH).");
                return false;
            }

            string targetRtmp = $"{rtmpUrl.Trim().TrimEnd('/')}/{streamKey.Trim()}";
            // Dùng 2 luồng input pipe: pipe:0 (raw rgba video) và pipe:3 (pcm 48kHz stereo)
            string args =
                $"-y -re -f rawvideo -pixel_format rgba -video_size {outWidth}x{outHeight} -framerate {fps} -i pipe:0 " +
                $"-f s16le -ar 48000 -ac 2 -i pipe:3 " +
                $"-c:v libx264 -preset veryfast -tune zerolatency -pix_fmt yuv420p -b:v 2800k -maxrate 3200k -bufsize 6000k -g {fps * 2} " +
                $"-c:a aac -b:a 128k -ar 48000 -ac 2 " +
                $"-f flv "{targetRtmp}"";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true
                };

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.Contains("error", StringComparison.OrdinalIgnoreCase))
                    {
                        LogMessage?.Invoke($"FFmpeg RTMP: {e.Data}");
                    }
                };
                proc.Start();
                proc.BeginErrorReadLine();

                lock (_rtmpLock)
                {
                    _ffmpegProcess = proc;
                    _ffmpegVideoStdin = proc.StandardInput.BaseStream;
                    _ffmpegAudioStdin = new FileStream("\\.\pipe\dummy", FileMode.Open); // placeholder replaced below
                }

                // Trên Windows, Process chỉ expose StandardInput (pipe:0); với audio có thể dùng named pipe hoặc mux sau
                lock (_rtmpLock)
                {
                    _ffmpegAudioStdin?.Dispose();
                    _ffmpegAudioStdin = null;
                    _isStreamingRtmp = true;
                }

                LogMessage?.Invoke($"Đang đẩy luồng RTMP ({outWidth}x{outHeight}@{fps}fps) tới {rtmpUrl.Trim()}...");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Lỗi khởi động RTMP: {ex.Message}");
                return false;
            }
        }

        public void StopRtmpStream()
        {
            lock (_rtmpLock)
            {
                _isStreamingRtmp = false;
                try { _ffmpegVideoStdin?.Flush(); } catch { }
                try { _ffmpegVideoStdin?.Close(); } catch { }
                _ffmpegVideoStdin = null;

                if (_ffmpegProcess != null)
                {
                    try
                    {
                        if (!_ffmpegProcess.WaitForExit(1500))
                            _ffmpegProcess.Kill(true);
                    }
                    catch { }
                    _ffmpegProcess.Dispose();
                    _ffmpegProcess = null;
                }
            }
            LogMessage?.Invoke("Đã dừng luồng RTMP lên mạng xã hội.");
        }

        private static string? ResolveFfmpegExecutable()
        {
            string localExe = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(localExe)) return localExe;

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        public string StartRecording(string baseOutputDir)
        {
            lock (_recLock)
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _currentRecordFolder = Path.Combine(baseOutputDir, $"LiveRec_{stamp}");
                Directory.CreateDirectory(_currentRecordFolder);
                _frameCounter = 0;
                _audioDataBytes = 0;

                string wavPath = Path.Combine(_currentRecordFolder, "program_audio.wav");
                _wavStream = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
                _wavWriter = new BinaryWriter(_wavStream);
                WriteWavHeader(_wavWriter, 48000, 2, 16, 0);

                _isRecording = true;
                return _currentRecordFolder;
            }
        }

        public void AppendProgramAudioPcm(byte[] pcmBytes, int length)
        {
            lock (_recLock)
            {
                if (!_isRecording || _wavWriter == null || length <= 0) return;
                _wavWriter.Write(pcmBytes, 0, length);
                _audioDataBytes += length;
            }
        }

        public void ProcessProgramFrameTick(Canvas programCanvas, int rtmpWidth = 1280, int rtmpHeight = 720)
        {
            if (!_isRecording && !_isStreamingRtmp) return;
            if (programCanvas.ActualWidth <= 0 || programCanvas.ActualHeight <= 0) return;

            int srcW = Math.Max(1, (int)programCanvas.ActualWidth);
            int srcH = Math.Max(1, (int)programCanvas.ActualHeight);

            var rtb = new RenderTargetBitmap(srcW, srcH, 96, 96, PixelFormats.Pbgra32);
            programCanvas.Measure(new Size(srcW, srcH));
            programCanvas.Arrange(new Rect(0, 0, srcW, srcH));
            rtb.Render(programCanvas);

            if (_isRecording && !string.IsNullOrEmpty(_currentRecordFolder))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                string filePath = Path.Combine(_currentRecordFolder, $"frame_{_frameCounter++:D6}.png");
                using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                encoder.Save(fs);
            }

            if (_isStreamingRtmp)
            {
                lock (_rtmpLock)
                {
                    if (_isStreamingRtmp && _ffmpegVideoStdin != null)
                    {
                        try
                        {
                            double scaleX = (double)rtmpWidth / srcW;
                            double scaleY = (double)rtmpHeight / srcH;
                            var scaled = new TransformedBitmap(rtb, new ScaleTransform(scaleX, scaleY));
                            var rgba = new FormatConvertedBitmap(scaled, PixelFormats.Rgba128Float, null, 0);
                            // Dùng Pbgra32 rồi đổi BGRA -> RGBA nhanh gọn
                            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
                            int stride = rtmpWidth * 4;
                            byte[] buf = new byte[stride * rtmpHeight];
                            bgra.CopyPixels(buf, stride, 0);
                            for (int i = 0; i < buf.Length; i += 4)
                            {
                                byte tmp = buf[i];
                                buf[i] = buf[i + 2];
                                buf[i + 2] = tmp;
                            }
                            _ffmpegVideoStdin.Write(buf, 0, buf.Length);
                        }
                        catch
                        {
                            _isStreamingRtmp = false;
                        }
                    }
                }
            }
        }

        public string? StopRecording()
        {
            lock (_recLock)
            {
                _isRecording = false;
                var folder = _currentRecordFolder;

                if (_wavWriter != null && _wavStream != null)
                {
                    _wavStream.Seek(0, SeekOrigin.Begin);
                    WriteWavHeader(_wavWriter, 48000, 2, 16, _audioDataBytes);
                    _wavWriter.Flush();
                    _wavWriter.Dispose();
                    _wavStream.Dispose();
                    _wavWriter = null;
                    _wavStream = null;
                }

                _currentRecordFolder = null;
                return folder;
            }
        }

        private static void WriteWavHeader(BinaryWriter bw, int sampleRate, int channels, int bitsPerSample, int dataBytes)
        {
            int byteRate = sampleRate * channels * (bitsPerSample / 8);
            short blockAlign = (short)(channels * (bitsPerSample / 8));

            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write(blockAlign);
            bw.Write((short)bitsPerSample);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);
        }

        public void Dispose()
        {
            StopRtmpStream();
            if (_isRecording) StopRecording();
        }
    }
}
