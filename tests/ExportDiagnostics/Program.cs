using System.Diagnostics;
using ForgeFlow;
using VideoProcessing;

string root = Path.Combine(Path.GetTempPath(), "ForgeFlowExportDiagnostics");
Directory.CreateDirectory(root);
string ffmpeg = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
string ffprobe = Path.Combine(AppContext.BaseDirectory, "Tools", "ffprobe.exe");
string source = Path.Combine(root, "source.mp4");
string logo = Path.Combine(root, "logo.png");
string output = Path.Combine(root, "editor-smoke.mp4");
string customOutput = Path.Combine(root, "editor-custom-export.mp4");
string decoyBase = Path.Combine(root, "tiktok-decoy-base.mp4");
string decoyOutput = Path.Combine(root, "tiktok-decoy-patched.mp4");

Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=60:duration=2", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source);
Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=orange:s=160x90", "-frames:v", "1", logo);

var model = new EditTimelineModel();
model.Load(2, source);
EditTimelineModel.Clip text = model.AddText("ForgeFlow تجربة", 0.15, 1.5);
text.X = 0.12; text.Y = 0.14; text.FontSize = 46; text.Color = "#FFFFFF";
EditTimelineModel.Clip image = model.AddImage(logo, 0.2, 1.4);
image.X = 0.65; image.Y = 0.1; image.Width = 0.22; image.Height = 0.16;
EditTimelineModel.Clip blur = model.AddBlur(0.35, 0.35);
model.UpdateBlur(blur.Id, 4);
EditTimelineModel.Clip lens = model.AddLens(0.9, 0.6);
model.UpdateLens(lens.Id, 0.5, 0.55, 0.24, 2.5);
EditTimelineModel.Clip music = model.AddAudio(source, 0.4, 1.2);
model.UpdateAudio(music.Id, 0.2, 1.25, false);

await EditorExportService.ExportAsync(model, output, 1080, 1920, 60, 1, 1, 0, 0, false, 0.5, 0.5, 4, 0.28);
string probe = Capture(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate", "-of", "csv=p=0", output).Trim();
if (!probe.StartsWith("1080,1920,60/1", StringComparison.Ordinal))
    throw new InvalidOperationException("Unexpected export stream: " + probe);
if (new FileInfo(output).Length < 50_000)
    throw new InvalidOperationException("Export output is unexpectedly small.");
Console.WriteLine("PASS  MP4 export with 60fps, portrait canvas, audio, text, image, lens and blur");
Console.WriteLine(output);

var customSettings = new EditorExportSettings
{
    Width = 720,
    Height = 1280,
    FrameRate = 30,
    VideoEncoder = "libx264",
    RateControl = "bitrate",
    VideoBitrateMbps = 5,
    MaxBitrateMbps = 7,
    BufferSizeMbps = 10,
    Preset = "fast",
    PixelFormat = "yuv420p",
    KeyframeIntervalSeconds = 2,
    IncludeAudio = false,
    FastStart = true
};
var settingsModel = new EditTimelineModel();
settingsModel.Load(2, source);
await EditorExportService.ExportAsync(settingsModel, customOutput, customSettings, 1, 1, 0, 0, false, 0.5, 0.5, 4, 0.28);
string customProbe = Capture(ffprobe, "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate,codec_name,pix_fmt", "-of", "default=nw=1", customOutput);
if (!customProbe.Contains("codec_name=h264", StringComparison.Ordinal) ||
    !customProbe.Contains("width=720", StringComparison.Ordinal) ||
    !customProbe.Contains("height=1280", StringComparison.Ordinal) ||
    !customProbe.Contains("avg_frame_rate=30/1", StringComparison.Ordinal) ||
    !customProbe.Contains("pix_fmt=yuv420p", StringComparison.Ordinal))
    throw new InvalidOperationException("Custom export settings were not applied:\n" + customProbe);
string audioProbe = Capture(ffprobe, "-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_type", "-of", "csv=p=0", customOutput).Trim();
if (audioProbe.Length != 0) throw new InvalidOperationException("Audio was present although IncludeAudio=false.");
Console.WriteLine("PASS  custom bitrate/resolution/fps/codec/pixel format/no-audio export settings");
Console.WriteLine(customOutput);

Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-y", "-i", source,
    "-map", "0:v:0", "-map", "0:a:0", "-map", "0:a:0",
    "-c:v", "copy", "-c:a", "aac", "-b:a", "128k", decoyBase);
TikTokAudioDecoyPatcher.PatchResult decoyResult = TikTokAudioDecoyPatcher.Patch(decoyBase, decoyOutput, 10);
if (!decoyResult.Applied || decoyResult.DecoySamples != decoyResult.OriginalSamples * 9)
    throw new InvalidOperationException("TikTok audio decoy was not applied: " + decoyResult.Message);
string decoyStreams = Capture(ffprobe, "-v", "error", "-show_entries", "stream=index,codec_type", "-of", "csv=p=0", decoyOutput);
if (decoyStreams.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.Contains("audio", StringComparison.Ordinal)) != 2)
    throw new InvalidOperationException("TikTok decoy output did not retain two audio tracks:\n" + decoyStreams);
string decoyPackets = Capture(ffprobe, "-v", "error", "-select_streams", "a:1", "-show_entries", "stream=nb_frames", "-of", "csv=p=0", decoyOutput).Trim();
if (!int.TryParse(decoyPackets, out int packetCount) || packetCount != decoyResult.OriginalSamples + decoyResult.DecoySamples)
    throw new InvalidOperationException($"Unexpected TikTok decoy sample-table count: {decoyPackets}");
Run(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", decoyOutput, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "NUL");
Console.WriteLine("PASS  upload-only TikTok audio compatibility track with intact primary A/V");
Console.WriteLine(decoyOutput);

static void Run(string file, params string[] args)
{
    using Process process = Start(file, args);
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(error);
}

static string Capture(string file, params string[] args)
{
    using Process process = Start(file, args);
    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(error);
    return output;
}

static Process Start(string file, IEnumerable<string> args)
{
    var info = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string arg in args) info.ArgumentList.Add(arg);
    return Process.Start(info) ?? throw new InvalidOperationException("Could not start " + file);
}
