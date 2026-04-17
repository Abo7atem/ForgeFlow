using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace ForgeFlow.Security
{
    public static class HardwareIdHelper
    {
        public static string GetHardwareId()
        {
            string machineGuid = ReadMachineGuid();
            string machineName = Environment.MachineName;
            string user = Environment.UserName;
            string os = Environment.OSVersion.VersionString;
            string raw = $"{machineGuid}|{machineName}|{user}|{os}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        }

        public static string GetLicenseHardwareToken()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(GetHardwareId()));
            var result = new char[16];
            for (int i = 0; i < result.Length; i++)
                result[i] = chars[hash[i] % chars.Length];
            return new string(result);
        }

        private static string ReadMachineGuid()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid")?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
