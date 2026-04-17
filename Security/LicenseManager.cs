using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForgeFlow.Security
{
    public sealed class LicenseManager
    {
        private const char SignedKeySeparator = '.';
        private const int SignedRandomLength = 8;
        private const string PublicSigningKeyXml = "<RSAKeyValue><Modulus>0WpB+pMrqdXW2Tq9pVBZB5LLuDQWzb7RHJCDLN/pPplz3J0XX5f+poYo7f5Ot1/vw8Z2+bVBkuQrutT1loByRtUi53YQwm7fImtW/eN5TbzWd9va2PThEEjwshhCFrL8tz0qC2qBsDAecq4iV75hvDhK73LeLx6JoT70XC3o7RFnIpZxkxXAgo3L582t+uPMYq/L8E9Mc7fcVT8O1nTz5DMC4Q37ueGhGPdviE93NKVCUkAAR5A9UG9nRuxmJIVEnflSVI6CxqQ944QaVq/8Esj7rRAWw2ZHQxfcsbXjFvOUzgzeKpCMvAgb3TcArTVlmavW5UpBkVD3g0VqvmuZ4wZcGi1VaubTOrHNEOIuA1b/Duob3Upv9ttgspIwWaO6cTlSAyx6fDa/dqLg0g2yH7cevXoXraNqmQTdPZSTO5SZwEic12gtB9VpfQ2asaKPJnXSRtau4fN5nJNISZwPFhspmu/44SC4HUwl/G4PAL0MDn3pOJw/ZNyadnBjg/sSRfRSrBO2e93tHXpl8aCko5ZNCM6yEEPCIihAOeEsGIXAySFYBc1FrvnErG3cwnlxD6KByamyTDZrwFnCCQemoZiaBBU2YcuBD0CzlaVjiqWL3bBGut+3KYQTyDZ2kxSqLWVQWsfkAEmVz9gyHg6fkgWGThf6UkekC1MN/t3U+LE=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        private static readonly byte[] MasterKey = DeriveKey("ForgeFlow-Lite-License-AES-2026", "ForgeFlow-Lite-AES-Salt");
        private static readonly byte[] HmacKey = DeriveKey("ForgeFlow-Lite-License-HMAC-2026", "ForgeFlow-Lite-HMAC-Salt");
        private readonly string _licenseFilePath;

        public LicenseInfo CurrentLicense { get; private set; }

        public LicenseManager()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeFlow");
            Directory.CreateDirectory(dir);
            _licenseFilePath = Path.Combine(dir, "license.dat");
            CurrentLicense = LoadLicense();
        }

        public bool IsLicenseValid()
        {
            if (CurrentLicense.Type == LicenseType.None || CurrentLicense.Status != LicenseStatus.Active)
                return false;

            if (!TryValidateSignedKey(CurrentLicense.LicenseKey.Trim().ToUpperInvariant(), out LicenseType signedType, out _))
                return false;

            if (signedType != CurrentLicense.Type)
                return false;

            string currentHwid = HardwareIdHelper.GetHardwareId();
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(CurrentLicense.HardwareId),
                Encoding.UTF8.GetBytes(currentHwid));
        }

        public string? CheckForDateTamper()
        {
            DateTime now = DateTime.UtcNow;
            if (!CurrentLicense.LastRunTime.HasValue)
            {
                CurrentLicense.LastRunTime = now;
                CurrentLicense.MaxRunTime = now;
                SaveLicense();
                return null;
            }

            DateTime lastRun = CurrentLicense.LastRunTime.Value;
            if (now < lastRun && (lastRun - now).TotalHours > 1)
            {
                CurrentLicense.TamperDetected = true;
                SaveLicense();
                return "تم اكتشاف تلاعب في تاريخ النظام. يرجى تصحيح التاريخ أو تفعيل التطبيق من جديد.";
            }

            if (!CurrentLicense.MaxRunTime.HasValue || now > CurrentLicense.MaxRunTime.Value)
                CurrentLicense.MaxRunTime = now;

            CurrentLicense.LastRunTime = now;
            SaveLicense();
            return null;
        }

        public (bool Success, string Message) ActivateLicense(string licenseKey)
        {
            var (valid, type, message) = ValidateKey(licenseKey);
            if (!valid) return (false, message);

            string currentToken = HardwareIdHelper.GetLicenseHardwareToken();
            string keyToken = ExtractKeyHardwareToken(licenseKey);
            if (!ConstantTimeEquals(currentToken, keyToken))
                return (false, $"هذا المفتاح ليس لهذا الجهاز.\nMachine ID: {currentToken}");

            CurrentLicense = new LicenseInfo
            {
                LicenseKey = licenseKey.Trim().ToUpperInvariant(),
                Type = type,
                HardwareId = HardwareIdHelper.GetHardwareId(),
                ActivatedOn = DateTime.UtcNow,
                LastRunTime = DateTime.UtcNow,
                MaxRunTime = DateTime.UtcNow,
                ExpiresOn = type switch
                {
                    LicenseType.Monthly => DateTime.UtcNow.AddMonths(1),
                    LicenseType.SemiAnnual => DateTime.UtcNow.AddMonths(6),
                    LicenseType.Yearly => DateTime.UtcNow.AddYears(1),
                    _ => null
                }
            };
            SaveLicense();
            return (true, "تم تفعيل ForgeFlow بنجاح.");
        }

        public (bool IsValid, LicenseType Type, string Message) ValidateKey(string licenseKey)
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
                return (false, LicenseType.None, "أدخل مفتاح الترخيص.");

            if (TryValidateSignedKey(licenseKey.Trim().ToUpperInvariant(), out var type, out var message))
                return (true, type, message ?? "المفتاح صالح.");

            return (false, LicenseType.None, message ?? "مفتاح الترخيص غير صالح.");
        }

        private LicenseInfo LoadLicense()
        {
            try
            {
                if (!File.Exists(_licenseFilePath)) return new LicenseInfo();
                byte[] data = File.ReadAllBytes(_licenseFilePath);
                string? json = DecryptStringWithHmac(data);
                return string.IsNullOrWhiteSpace(json) ? new LicenseInfo() : JsonSerializer.Deserialize<LicenseInfo>(json) ?? new LicenseInfo();
            }
            catch { return new LicenseInfo(); }
        }

        private void SaveLicense()
        {
            string json = JsonSerializer.Serialize(CurrentLicense);
            File.WriteAllBytes(_licenseFilePath, EncryptStringWithHmac(json));
        }

        private static bool TryValidateSignedKey(string licenseKey, out LicenseType type, out string? message)
        {
            type = LicenseType.None;
            message = null;
            string[] parts = licenseKey.Split(SignedKeySeparator);
            if (parts.Length != 4) { message = "صيغة المفتاح غير صحيحة."; return false; }

            type = parts[0] switch
            {
                "MON" => LicenseType.Monthly,
                "6MO" => LicenseType.SemiAnnual,
                "YER" => LicenseType.Yearly,
                "LFT" => LicenseType.Lifetime,
                _ => LicenseType.None
            };
            if (type == LicenseType.None) { message = "نوع المفتاح غير معروف."; return false; }

            string hardwareToken = NormalizeHardwareToken(parts[1]);
            string randomPart = parts[2];
            string signaturePart = parts[3];
            if (hardwareToken.Length != 16 || randomPart.Length != SignedRandomLength || !IsAlphaNumeric(hardwareToken + randomPart))
            {
                message = "أجزاء المفتاح غير صحيحة.";
                return false;
            }

            string payload = $"{parts[0]}.{hardwareToken}.{randomPart}";
            if (!VerifySignedPayload(payload, signaturePart))
            {
                message = "التوقيع الرقمي للمفتاح غير صالح.";
                return false;
            }

            return true;
        }

        private static string ExtractKeyHardwareToken(string licenseKey)
        {
            string[] parts = (licenseKey ?? string.Empty).Trim().ToUpperInvariant().Split(SignedKeySeparator);
            return parts.Length >= 2 ? NormalizeHardwareToken(parts[1]) : string.Empty;
        }

        private static string NormalizeHardwareToken(string hardwareToken)
        {
            var builder = new StringBuilder(16);
            foreach (char c in (hardwareToken ?? string.Empty).Trim().ToUpperInvariant())
                if (char.IsLetterOrDigit(c)) builder.Append(c);
            string normalized = builder.ToString();
            return normalized.Length > 16 ? normalized[..16] : normalized;
        }

        private static bool VerifySignedPayload(string payload, string signaturePart)
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.FromXmlString(PublicSigningKeyXml);
                return rsa.VerifyData(Encoding.UTF8.GetBytes(payload), Base64UrlDecode(signaturePart), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return false;
            }
        }

        private static bool IsAlphaNumeric(string value) => value.All(char.IsLetterOrDigit);

        private static byte[] Base64UrlDecode(string value)
        {
            string normalized = value.Replace('-', '+').Replace('_', '/');
            int padding = 4 - normalized.Length % 4;
            if (padding is > 0 and < 4) normalized = normalized.PadRight(normalized.Length + padding, '=');
            return Convert.FromBase64String(normalized);
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            byte[] left = Encoding.UTF8.GetBytes(a ?? string.Empty);
            byte[] right = Encoding.UTF8.GetBytes(b ?? string.Empty);
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }

        private static byte[] DeriveKey(string password, string salt)
        {
            using var pbkdf2 = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(salt), 100_000, HashAlgorithmName.SHA256);
            return pbkdf2.GetBytes(32);
        }

        private static byte[] EncryptStringWithHmac(string plainText)
        {
            using var aes = Aes.Create();
            aes.Key = MasterKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.GenerateIV();
            byte[] encrypted = aes.CreateEncryptor().TransformFinalBlock(Encoding.UTF8.GetBytes(plainText), 0, Encoding.UTF8.GetByteCount(plainText));
            byte[] body = aes.IV.Concat(encrypted).ToArray();
            byte[] hmac = HMACSHA256.HashData(HmacKey, body);
            return body.Concat(hmac).ToArray();
        }

        private static string? DecryptStringWithHmac(byte[] data)
        {
            const int ivLength = 16;
            const int hmacLength = 32;
            if (data.Length <= ivLength + hmacLength) return null;
            byte[] body = data[..^hmacLength];
            byte[] expected = HMACSHA256.HashData(HmacKey, body);
            if (!CryptographicOperations.FixedTimeEquals(expected, data[^hmacLength..])) return null;
            using var aes = Aes.Create();
            aes.Key = MasterKey;
            aes.IV = body[..ivLength];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            byte[] encrypted = body[ivLength..];
            return Encoding.UTF8.GetString(aes.CreateDecryptor().TransformFinalBlock(encrypted, 0, encrypted.Length));
        }
    }
}
