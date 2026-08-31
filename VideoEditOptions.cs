namespace ForgeFlow
{
    internal sealed class VideoEditOptions
    {
        public double? TrimStartSeconds { get; set; }
        public double? TrimEndSeconds { get; set; }
        public double? DeleteStartSeconds { get; set; }
        public double? DeleteEndSeconds { get; set; }
        public string LogoPath { get; set; } = "";
        public string OverlayText { get; set; } = "";
        public bool ExportEditOnly { get; set; }

        public bool HasEdits => TrimStartSeconds.HasValue || TrimEndSeconds.HasValue ||
                                (DeleteStartSeconds.HasValue && DeleteEndSeconds.HasValue) ||
                                !string.IsNullOrWhiteSpace(LogoPath) || !string.IsNullOrWhiteSpace(OverlayText);
    }
}
