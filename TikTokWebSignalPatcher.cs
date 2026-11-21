using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace VideoProcessing;

/// <summary>
/// Adds the low-risk MP4 signal used by the newer TikTok Web upload experiments:
/// a neutral video edit list plus a small private metadata marker.  It does not
/// duplicate frames, alter timestamps, or re-encode pixels.  The patch is kept
/// separate from the legacy sample-table inflation experiment because that
/// experiment can confuse local players and is not the same ingest signal.
/// </summary>
public static class TikTokWebSignalPatcher
{
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "stbl", "edts", "udta"
    };

    public sealed record PatchResult(bool Applied, int VideoTracks, int EditLists, string Message);

    public static PatchResult Patch(string inputPath, string outputPath)
    {
        if (!File.Exists(inputPath)) throw new FileNotFoundException("TikTok Web Signal input was not found.", inputPath);
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        string partial = outputPath + ".partial";
        if (File.Exists(partial)) File.Delete(partial);

        try
        {
            byte[] input = File.ReadAllBytes(inputPath);
            List<TopBox> top = ReadTopLevel(input);
            TopBox? moovInfo = top.FirstOrDefault(b => b.Type == "moov");
            TopBox? mdatInfo = top.FirstOrDefault(b => b.Type == "mdat");
            if (moovInfo is null || mdatInfo is null)
                return Copy(inputPath, outputPath, "MP4 moov/mdat boxes were not found; Web Signal was skipped.");

            Mp4Box moov = ParseSingle(input.AsSpan(checked((int)moovInfo.Offset), checked((int)moovInfo.Size)).ToArray());
            Mp4Box? mvhd = moov.Child("mvhd");
            if (mvhd is null) return Copy(inputPath, outputPath, "MP4 mvhd box was not found; Web Signal was skipped.");
            ulong movieDuration = ReadMovieDuration(mvhd);
            int videoTracks = 0;
            int editLists = 0;
            foreach (Mp4Box trak in moov.Children.Where(c => c.Type == "trak"))
            {
                if (!IsVideoTrack(trak)) continue;
                videoTracks++;
                Mp4Box? edts = trak.Child("edts");
                if (edts is null)
                {
                    edts = new Mp4Box("edts");
                    trak.InsertAfter("tkhd", edts);
                }
                Mp4Box? oldElst = edts.Child("elst");
                Mp4Box elst = MakeNeutralEditList(movieDuration);
                if (oldElst is null) edts.Children.Add(elst);
                else edts.ReplaceChild(oldElst, elst);
                editLists++;
            }
            if (videoTracks == 0)
                return Copy(inputPath, outputPath, "No video track was found; Web Signal was skipped.");

            Mp4Box? udta = moov.Child("udta");
            if (udta is null)
            {
                udta = new Mp4Box("udta");
                moov.Children.Add(udta);
            }
            udta.Children.RemoveAll(c => c.Type == "u120");
            udta.Children.Add(new Mp4Box("u120", Encoding.UTF8.GetBytes("ForgeFlow TikTok Web Signal; method=extension-signal; divider=1; local=true")));

            byte[] patchedMoov = moov.Serialize();
            long delta = checked((long)patchedMoov.Length - moovInfo.Size);
            // Adding bytes before mdat moves every media chunk.  Update chunk
            // offsets only in that case; a moov after mdat needs no offset work.
            if (moovInfo.Offset < mdatInfo.Offset)
                AdjustChunkOffsets(patchedMoov, mdatInfo.Offset, delta);

            using (FileStream dst = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long cursor = 0;
                foreach (TopBox box in top)
                {
                    if (box.Offset > cursor)
                        dst.Write(input.AsSpan(checked((int)cursor), checked((int)(box.Offset - cursor))));
                    if (box.Type == "moov") dst.Write(patchedMoov);
                    else dst.Write(input.AsSpan(checked((int)box.Offset), checked((int)box.Size)));
                    cursor = box.Offset + box.Size;
                }
                if (cursor < input.Length) dst.Write(input.AsSpan(checked((int)cursor)));
                dst.Flush(true);
            }
            File.Move(partial, outputPath, true);
            return new PatchResult(true, videoTracks, editLists,
                $"TikTok Web Signal applied: {editLists} neutral edit-list signal(s), timing and pixels unchanged.");
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }

    private static PatchResult Copy(string input, string output, string message)
    {
        File.Copy(input, output, true);
        return new PatchResult(false, 0, 0, message);
    }

    private static Mp4Box MakeNeutralEditList(ulong duration)
    {
        if (duration <= uint.MaxValue)
        {
            byte[] p = new byte[20]; // full-box header + count + one entry
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(8, 4), (uint)duration);
            BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(12, 4), 0);
            BinaryPrimitives.WriteInt16BigEndian(p.AsSpan(16, 2), 1);
            return new Mp4Box("elst", p);
        }
        byte[] large = new byte[32];
        large[0] = 1; // version 1
        BinaryPrimitives.WriteUInt32BigEndian(large.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(large.AsSpan(8, 8), duration);
        BinaryPrimitives.WriteInt64BigEndian(large.AsSpan(16, 8), 0);
        BinaryPrimitives.WriteInt16BigEndian(large.AsSpan(24, 2), 1);
        return new Mp4Box("elst", large);
    }

    private static ulong ReadMovieDuration(Mp4Box mvhd)
    {
        if (mvhd.Payload.Length < 20) throw new InvalidDataException("Invalid mvhd box.");
        return mvhd.Payload[0] == 1
            ? BinaryPrimitives.ReadUInt64BigEndian(mvhd.Payload.AsSpan(24, 8))
            : BinaryPrimitives.ReadUInt32BigEndian(mvhd.Payload.AsSpan(16, 4));
    }

    private static bool IsVideoTrack(Mp4Box trak)
    {
        Mp4Box? mdia = trak.Child("mdia");
        Mp4Box? hdlr = mdia?.Child("hdlr");
        return hdlr is not null && hdlr.Payload.Length >= 12 && Encoding.ASCII.GetString(hdlr.Payload, 8, 4) == "vide";
    }

    private static void AdjustChunkOffsets(byte[] moov, long oldMdatOffset, long delta)
    {
        if (delta == 0) return;
        Mp4Box root = ParseSingle(moov);
        foreach (Mp4Box box in root.Descendants())
        {
            if (box.Type == "stco")
            {
                if (box.Payload.Length < 8) continue;
                uint count = BinaryPrimitives.ReadUInt32BigEndian(box.Payload.AsSpan(4, 4));
                for (int i = 0; i < count && 8 + i * 4 + 4 <= box.Payload.Length; i++)
                {
                    int at = 8 + i * 4;
                    uint value = BinaryPrimitives.ReadUInt32BigEndian(box.Payload.AsSpan(at, 4));
                    if (value >= oldMdatOffset) BinaryPrimitives.WriteUInt32BigEndian(box.Payload.AsSpan(at, 4), checked((uint)(value + delta)));
                }
            }
            else if (box.Type == "co64")
            {
                if (box.Payload.Length < 8) continue;
                uint count = BinaryPrimitives.ReadUInt32BigEndian(box.Payload.AsSpan(4, 4));
                for (int i = 0; i < count && 8 + i * 8 + 8 <= box.Payload.Length; i++)
                {
                    int at = 8 + i * 8;
                    ulong value = BinaryPrimitives.ReadUInt64BigEndian(box.Payload.AsSpan(at, 8));
                    if (value >= (ulong)oldMdatOffset) BinaryPrimitives.WriteUInt64BigEndian(box.Payload.AsSpan(at, 8), checked((ulong)((long)value + delta)));
                }
            }
        }
        byte[] updated = root.Serialize();
        updated.CopyTo(moov, 0);
    }

    private static List<TopBox> ReadTopLevel(byte[] data)
    {
        var result = new List<TopBox>();
        long offset = 0;
        while (offset + 8 <= data.Length)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(checked((int)offset), 4));
            string type = Encoding.ASCII.GetString(data, checked((int)offset + 4), 4);
            int header = size32 == 1 ? 16 : 8;
            ulong size = size32 == 1 ? BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(checked((int)offset + 8), 8)) : size32 == 0 ? (ulong)(data.Length - offset) : size32;
            if (size < (ulong)header || size > (ulong)(data.Length - offset)) throw new InvalidDataException($"Invalid MP4 box {type}.");
            result.Add(new TopBox(type, offset, checked((long)size)));
            offset += checked((long)size);
        }
        if (offset != data.Length) throw new InvalidDataException("MP4 ended with incomplete top-level box.");
        return result;
    }

    private static Mp4Box ParseSingle(byte[] data)
    {
        int offset = 0;
        Mp4Box box = Mp4Box.Parse(data, ref offset, data.Length);
        if (offset != data.Length) throw new InvalidDataException("Unexpected bytes after MP4 box.");
        return box;
    }

    private sealed record TopBox(string Type, long Offset, long Size);

    private sealed class Mp4Box
    {
        public string Type { get; }
        public byte[] Payload { get; set; }
        public List<Mp4Box> Children { get; } = new();
        public Mp4Box(string type, byte[]? payload = null) { Type = type; Payload = payload ?? Array.Empty<byte>(); }
        public Mp4Box? Child(string type) => Children.FirstOrDefault(c => c.Type == type);
        public void InsertAfter(string type, Mp4Box child) { int i = Children.FindIndex(c => c.Type == type); if (i < 0) Children.Add(child); else Children.Insert(i + 1, child); }
        public void ReplaceChild(Mp4Box oldChild, Mp4Box newChild) { int i = Children.IndexOf(oldChild); if (i >= 0) Children[i] = newChild; }
        public IEnumerable<Mp4Box> Descendants() { foreach (Mp4Box child in Children) { yield return child; foreach (Mp4Box nested in child.Descendants()) yield return nested; } }
        public static Mp4Box Parse(byte[] data, ref int offset, int limit)
        {
            if (offset + 8 > limit) throw new InvalidDataException("Truncated MP4 box.");
            int start = offset;
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            offset += 8;
            int header = 8;
            ulong size = size32;
            if (size32 == 1) { size = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8)); offset += 8; header = 16; }
            else if (size32 == 0) size = (ulong)(limit - start);
            if (size < (ulong)header || size > (ulong)(limit - start)) throw new InvalidDataException($"Invalid MP4 box {type}.");
            int end = checked(start + (int)size);
            var box = new Mp4Box(type);
            if (!Containers.Contains(type)) { box.Payload = data.AsSpan(offset, end - offset).ToArray(); offset = end; return box; }
            // udta is normally a sequence of child boxes.  If a legacy file has
            // opaque bytes there, fail safely rather than corrupting the output.
            while (offset < end) box.Children.Add(Parse(data, ref offset, end));
            if (offset != end) throw new InvalidDataException($"Invalid {type} child boundary.");
            return box;
        }
        public byte[] Serialize()
        {
            byte[] payload;
            if (Containers.Contains(Type)) { using var s = new MemoryStream(); foreach (Mp4Box c in Children) s.Write(c.Serialize()); payload = s.ToArray(); }
            else payload = Payload;
            ulong size = checked((ulong)payload.Length + 8);
            if (size > uint.MaxValue) throw new InvalidDataException("MP4 box became too large.");
            byte[] result = new byte[(int)size];
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)size);
            Encoding.ASCII.GetBytes(Type, result.AsSpan(4, 4));
            payload.CopyTo(result, 8);
            return result;
        }
    }
}
