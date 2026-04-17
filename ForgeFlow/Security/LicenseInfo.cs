using System.Text.Json.Serialization;

namespace ForgeFlow.Security
{
    public enum LicenseType
    {
        None = 0,
        Monthly = 2,
        SemiAnnual = 5,
        Yearly = 3,
        Lifetime = 4
    }

    public enum LicenseStatus
    {
        NotActivated,
        Active,
        Expired,
        Tampered
    }

    public sealed class LicenseInfo
    {
        [JsonPropertyName("key")]
        public string LicenseKey { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public LicenseType Type { get; set; }

        [JsonPropertyName("hwid")]
        public string HardwareId { get; set; } = string.Empty;

        [JsonPropertyName("activatedOn")]
        public DateTime ActivatedOn { get; set; }

        [JsonPropertyName("expiresOn")]
        public DateTime? ExpiresOn { get; set; }

        [JsonPropertyName("lastRunTime")]
        public DateTime? LastRunTime { get; set; }

        [JsonPropertyName("maxRunTime")]
        public DateTime? MaxRunTime { get; set; }

        [JsonPropertyName("tamperDetected")]
        public bool TamperDetected { get; set; }

        [JsonIgnore]
        public LicenseStatus Status
        {
            get
            {
                if (TamperDetected) return LicenseStatus.Tampered;
                if (Type == LicenseType.None) return LicenseStatus.NotActivated;
                if (Type == LicenseType.Lifetime) return LicenseStatus.Active;
                if (ExpiresOn.HasValue && DateTime.UtcNow > ExpiresOn.Value) return LicenseStatus.Expired;
                return LicenseStatus.Active;
            }
        }
    }
}
