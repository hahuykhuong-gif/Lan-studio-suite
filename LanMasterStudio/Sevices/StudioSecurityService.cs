using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LanMasterStudio.Services
{
    public sealed class StudioSecurityService
    {
        private readonly string _secretFilePath;
        private string _lanKey = "DEFAULT-LAN-KEY";

        public StudioSecurityService()
        {
            string appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LanMasterStudio");
            Directory.CreateDirectory(appDir);
            _secretFilePath = Path.Combine(appDir, "studio_rtmp.dat");
            LoadOrCreateLanKey();
        }

        public string LanPairingToken => ComputeSha256Hex(_lanKey)[..16];

        private void LoadOrCreateLanKey()
        {
            string envKey = Environment.GetEnvironmentVariable("LAN_STUDIO_KEY") ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                _lanKey = envKey.Trim();
                return;
            }

            // Gắn cố định theo tên máy + người dùng cục bộ để tránh lộ khóa mặc định công khai
            _lanKey = $"{Environment.MachineName}:{Environment.UserName}:LAN-STUDIO-V1";
        }

        public string SignLanPayload(string payloadJson)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(_lanKey);
            byte[] msgBytes = Encoding.UTF8.GetBytes(payloadJson);
            using var hmac = new HMACSHA256(keyBytes);
            return Convert.ToHexString(hmac.ComputeHash(msgBytes));
        }

        public bool VerifyLanSignature(string payloadJson, string? signatureHex)
        {
            if (string.IsNullOrWhiteSpace(signatureHex)) return false;
            string expected = SignLanPayload(payloadJson);
            byte[] a = Encoding.UTF8.GetBytes(expected);
            byte[] b = Encoding.UTF8.GetBytes(signatureHex.Trim().ToUpperInvariant());
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        public void SaveRtmpCredentials(string rtmpUrl, string streamKey)
        {
            string combined = $"{rtmpUrl.Trim()}
{streamKey.Trim()}";
            byte[] plain = Encoding.UTF8.GetBytes(combined);
            byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_secretFilePath, protectedBytes);
        }

        public (string RtmpUrl, string StreamKey) LoadRtmpCredentials()
        {
            if (!File.Exists(_secretFilePath)) return (string.Empty, string.Empty);
            try
            {
                byte[] protectedBytes = File.ReadAllBytes(_secretFilePath);
                byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                string text = Encoding.UTF8.GetString(plain);
                var parts = text.Split('
', 2);
                return (parts.Length > 0 ? parts[0] : string.Empty, parts.Length > 1 ? parts[1] : string.Empty);
            }
            catch
            {
                return (string.Empty, string.Empty);
            }
        }

        private static string ComputeSha256Hex(string input)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash);
        }
    }
}
