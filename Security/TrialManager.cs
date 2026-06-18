using Microsoft.Win32;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ForgeFlow.Security
{
    public static class TrialManager
    {
        public const int MaxTrialVideos = 2;
        private const string RegistryPath = @"SOFTWARE\ForgeFlow\Lite";
        private const string MarkerRegistryPath = @"SOFTWARE\ForgeFlow\LiteMarker";
        private const string ValueData = "TrialData";
        private const string ValueMarker = "InstallMarker";
        private static readonly byte[] MasterKey = DeriveKey("ForgeFlow-Lite-Trial-AES-2026", "ForgeFlow-Lite-Trial-AES-Salt");
        private static readonly byte[] HmacKey = DeriveKey("ForgeFlow-Lite-Trial-HMAC-2026", "ForgeFlow-Lite-Trial-HMAC-Salt");
        private static readonly string FallbackPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForgeFlow", "trial.dat");
        private static readonly string MarkerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForgeFlow", "install.idx");
        private static readonly string CommonMarkerPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ForgeFlow", "Lite", "install.idx");

        public static (bool Allowed, int Remaining, bool Tampered) CheckTrial()
        {
            var data = LoadData();
            if (data.Tampered) return (false, 0, true);

            DateTime now = DateTime.UtcNow;
            if (now < data.LastRunUtc.AddHours(-1))
            {
                data.Tampered = true;
                data.Count = MaxTrialVideos;
                SaveData(data);
                return (false, 0, true);
            }

            data.LastRunUtc = now;
            SaveData(data);
            int remaining = Math.Max(0, MaxTrialVideos - data.Count);
            return (data.Count < MaxTrialVideos, remaining, false);
        }

        public static void IncrementTrial()
        {
            var data = LoadData();
            if (data.Tampered) return;
            data.Count = Math.Min(MaxTrialVideos, data.Count + 1);
            data.LastRunUtc = DateTime.UtcNow;
            SaveData(data);
        }

        public static int GetTrialCount() => LoadData().Count;

        private static TrialData LoadData()
        {
            string? raw = ReadRaw();
            if (!string.IsNullOrWhiteSpace(raw) && TryVerify(raw, out string payload))
            {
                var parsed = TrialData.Deserialize(payload);
                if (parsed != null && parsed.HardwareId == HardwareIdHelper.GetHardwareId())
                    return parsed;
            }

            if (InstallMarkerExists())
            {
                var tampered = new TrialData
                {
                    Count = MaxTrialVideos,
                    HardwareId = HardwareIdHelper.GetHardwareId(),
                    FirstRunUtc = DateTime.UtcNow,
                    LastRunUtc = DateTime.UtcNow,
                    Tampered = true
                };
                SaveData(tampered);
                return tampered;
            }

            var data = new TrialData
            {
                Count = 0,
                HardwareId = HardwareIdHelper.GetHardwareId(),
                FirstRunUtc = DateTime.UtcNow,
                LastRunUtc = DateTime.UtcNow,
                Tampered = false
            };
            SaveData(data);
            SaveInstallMarker();
            return data;
        }

        private static string? ReadRaw()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                string? value = key?.GetValue(ValueData)?.ToString();
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { }

            try
            {
                return File.Exists(FallbackPath) ? File.ReadAllText(FallbackPath) : null;
            }
            catch { return null; }
        }

        private static void SaveData(TrialData data)
        {
            string raw = Protect(data.Serialize());
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                key?.SetValue(ValueData, raw, RegistryValueKind.String);
            }
            catch { }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
                File.WriteAllText(FallbackPath, raw);
            }
            catch { }
        }

        private static bool InstallMarkerExists()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(MarkerRegistryPath);
                if (!string.IsNullOrWhiteSpace(key?.GetValue(ValueMarker)?.ToString()))
                    return true;
            }
            catch { }

            try
            {
                if (File.Exists(MarkerPath)) return true;
                if (File.Exists(CommonMarkerPath)) return true;
            }
            catch { return false; }

            return false;
        }

        private static void SaveInstallMarker()
        {
            string marker = Protect(HardwareIdHelper.GetHardwareId() + "|" + DateTime.UtcNow.Ticks);
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(MarkerRegistryPath);
                key?.SetValue(ValueMarker, marker, RegistryValueKind.String);
            }
            catch { }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
                File.WriteAllText(MarkerPath, marker);
            }
            catch { }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CommonMarkerPath)!);
                File.WriteAllText(CommonMarkerPath, marker);
            }
            catch { }
        }

        private static string Protect(string payload)
        {
            using var aes = Aes.Create();
            aes.Key = MasterKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();

            byte[] plainBytes = Encoding.UTF8.GetBytes(payload);
            byte[] encrypted = aes.CreateEncryptor().TransformFinalBlock(plainBytes, 0, plainBytes.Length);
            byte[] body = aes.IV.Concat(encrypted).ToArray();
            byte[] hmac = HMACSHA256.HashData(HmacKey, body);
            return Convert.ToBase64String(body.Concat(hmac).ToArray());
        }

        private static bool TryVerify(string raw, out string payload)
        {
            payload = string.Empty;
            try
            {
                const int ivLength = 16;
                const int hmacLength = 32;
                byte[] data = Convert.FromBase64String(raw);
                if (data.Length <= ivLength + hmacLength) return false;

                byte[] body = data[..^hmacLength];
                byte[] actualHmac = data[^hmacLength..];
                byte[] expectedHmac = HMACSHA256.HashData(HmacKey, body);
                if (!CryptographicOperations.FixedTimeEquals(expectedHmac, actualHmac))
                    return false;

                using var aes = Aes.Create();
                aes.Key = MasterKey;
                aes.IV = body[..ivLength];
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                byte[] encrypted = body[ivLength..];
                payload = Encoding.UTF8.GetString(aes.CreateDecryptor().TransformFinalBlock(encrypted, 0, encrypted.Length));
                return true;
            }
            catch { return false; }
        }

        private static byte[] DeriveKey(string password, string salt)
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), 100_000, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(32);
        }

        private sealed class TrialData
        {
            public int Count { get; set; }
            public string HardwareId { get; set; } = string.Empty;
            public DateTime FirstRunUtc { get; set; }
            public DateTime LastRunUtc { get; set; }
            public bool Tampered { get; set; }

            public string Serialize() => $"{Count}|{HardwareId}|{FirstRunUtc.Ticks}|{LastRunUtc.Ticks}|{(Tampered ? 1 : 0)}";

            public static TrialData? Deserialize(string raw)
            {
                try
                {
                    string[] p = raw.Split('|');
                    if (p.Length != 5) return null;
                    return new TrialData
                    {
                        Count = int.Parse(p[0]),
                        HardwareId = p[1],
                        FirstRunUtc = new DateTime(long.Parse(p[2]), DateTimeKind.Utc),
                        LastRunUtc = new DateTime(long.Parse(p[3]), DateTimeKind.Utc),
                        Tampered = p[4] == "1"
                    };
                }
                catch { return null; }
            }
        }
    }
}
