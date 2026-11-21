using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ForgeFlow
{
    internal sealed record SubtitleCue(double Start, double End, string Text);

    internal static class SubtitleService
    {
        private static readonly Regex TimestampLine = new(
            @"(?<start>\d{1,2}:\d{2}(?::\d{2})?[\.,]\d{3})\s*-->\s*(?<end>\d{1,2}:\d{2}(?::\d{2})?[\.,]\d{3})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static bool TryParseFile(string path, out List<SubtitleCue> cues, out string error)
        {
            cues = new List<SubtitleCue>();
            error = "";
            try
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is not ".srt" and not ".vtt")
                {
                    error = "اختر ملف ترجمة بصيغة SRT أو VTT.";
                    return false;
                }

                string text = ReadSubtitleText(path).Replace("\r\n", "\n").Replace('\r', '\n');
                cues = Parse(text);
                if (cues.Count == 0)
                {
                    error = "لم أجد أي مقاطع ترجمة صالحة في الملف.";
                    return false;
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                error = $"تعذر قراءة ملف الترجمة: {ex.Message}";
                return false;
            }
        }

        private static string ReadSubtitleText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        internal static List<SubtitleCue> Parse(string text)
        {
            var result = new List<SubtitleCue>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            string normalized = text.TrimStart('\uFEFF');
            string[] blocks = Regex.Split(normalized, @"\n\s*\n");
            foreach (string rawBlock in blocks)
            {
                if (result.Count >= 10_000) break;
                string[] lines = rawBlock.Split('\n');
                int timestampIndex = Array.FindIndex(lines, line => TimestampLine.IsMatch(line));
                if (timestampIndex < 0) continue;

                Match match = TimestampLine.Match(lines[timestampIndex]);
                if (!TryParseTimestamp(match.Groups["start"].Value, out double start) ||
                    !TryParseTimestamp(match.Groups["end"].Value, out double end) ||
                    end <= start || end - start < 0.04 || start > 7 * 24 * 60 * 60)
                    continue;

                string cueText = string.Join("\n", lines.Skip(timestampIndex + 1)).Trim();
                cueText = Regex.Replace(cueText, @"<[^>]+>", "");
                cueText = cueText.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
                                 .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
                                 .Trim();
                if (string.IsNullOrWhiteSpace(cueText) || cueText.Length > 20_000) continue;

                result.Add(new SubtitleCue(start, Math.Min(end, 7 * 24 * 60 * 60), cueText));
            }

            return result.OrderBy(cue => cue.Start).ThenBy(cue => cue.End).ToList();
        }

        private static bool TryParseTimestamp(string value, out double seconds)
        {
            seconds = 0;
            string normalized = value.Replace(',', '.');
            string[] parts = normalized.Split(':');
            if (parts.Length is not 2 and not 3) return false;
            if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double withMilliseconds) ||
                !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)) return false;

            int hours = 0;
            if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) return false;
            if (minutes is < 0 or > 59 || hours < 0) return false;
            if (withMilliseconds < 0 || withMilliseconds >= 60) return false;
            seconds = hours * 3600 + minutes * 60 + withMilliseconds;
            return double.IsFinite(seconds) && seconds >= 0;
        }
    }
}
