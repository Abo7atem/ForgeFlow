using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace VideoProcessing;

/// <summary>
/// Experimental TikTok upload compatibility patch. It does not re-encode pixels:
/// it appends tiny filler NAL samples and expands the video sample tables so the
/// web upload pipeline sees a dense 60-fps stream. The original file is untouched.
/// </summary>
public static class TikTokVideoInflatePatcher
{
    private static readonly HashSet<string> ContainerTypes = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "stbl", "edts"
    };

    private static readonly Dictionary<string, int> DummySizes = new(StringComparer.Ordinal)
    {
        ["avc1"] = 8, ["avc3"] = 8, ["hvc1"] = 16, ["hev1"] = 16,
        ["vp09"] = 4, ["av01"] = 4, ["mp4v"] = 8
    };

    private static readonly byte[] PaddingNal = { 0, 0, 0, 4, 0, 0, 0, 0 };

    public sealed record PatchResult(bool Applied, int OriginalSamples, int DeclaredSamples, string Message);

    public static PatchResult Patch(string inputPath, string outputPath, int multiplier = 10)
    {
        if (multiplier is < 2 or > 20)
            throw new ArgumentOutOfRangeException(nameof(multiplier), "Video sample multiplier must be between 2 and 20.");
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("TikTok video patch input was not found.", inputPath);

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory)) Directory.CreateDirectory(outputDirectory);
        string partialPath = outputPath + ".partial";
        if (File.Exists(partialPath)) File.Delete(partialPath);

        try
        {
            byte[] input = File.ReadAllBytes(inputPath);
            List<TopLevelBox> top = ReadTopLevelBoxes(input);
            TopLevelBox? moovInfo = top.FirstOrDefault(box => box.Type == "moov");
            TopLevelBox? mdatInfo = top.FirstOrDefault(box => box.Type == "mdat");
            if (moovInfo is null || mdatInfo is null)
                return CopyUnchanged(inputPath, outputPath, "MP4 moov/mdat boxes were not found; video patch skipped.");

            // Rebuilding a moov before mdat requires rewriting every existing chunk
            // offset. Generated ForgeFlow Studio outputs place moov after mdat, which
            // lets us patch safely without touching the original media offsets.
            if (moovInfo.Offset < mdatInfo.Offset + mdatInfo.Size)
                return CopyUnchanged(inputPath, outputPath, "Fast-start MP4 was left unchanged; safe offset patch requires moov after mdat.");

            byte[] moovBytes = input.AsSpan(checked((int)moovInfo.Offset), checked((int)moovInfo.Size)).ToArray();
            Mp4Box moov = ParseSingleBox(moovBytes);
            Mp4Box? videoTrack = moov.Children.FirstOrDefault(box => box.Type == "trak" && IsVideoTrack(box));
            Mp4Box? stbl = videoTrack is null ? null : FindPath(videoTrack, "mdia", "minf", "stbl");
            Mp4Box? stts = stbl?.Child("stts");
            Mp4Box? stsz = stbl?.Child("stsz");
            Mp4Box? stsc = stbl?.Child("stsc");
            Mp4Box? offsets = stbl?.Child("stco") ?? stbl?.Child("co64");
            if (stbl is null || stts is null || stsz is null || stsc is null || offsets is null)
                return CopyUnchanged(inputPath, outputPath, "Video sample tables were incomplete; video patch skipped.");

            int originalSamples = checked((int)ReadUInt32(stsz.Payload, 8));
            if (originalSamples <= 0)
                return CopyUnchanged(inputPath, outputPath, "Video track had no samples; video patch skipped.");

            uint sampleDelta = ReadFirstSampleDelta(stts.Payload);
            int fakeSamples = checked(originalSamples * (multiplier - 1));
            int declaredSamples = checked(originalSamples + fakeSamples);
            int dummySize = GetDummySize(stbl);
            ulong safeOffset = checked((ulong)(mdatInfo.Offset + mdatInfo.Size));
            ulong maxOffset = safeOffset;
            if (offsets.Type == "stco" && maxOffset > uint.MaxValue)
                return CopyUnchanged(inputPath, outputPath, "MP4 exceeded 32-bit chunk offsets; video patch skipped safely.");

            PatchTimeToSample(stts, fakeSamples, sampleDelta);
            PatchSampleSizes(stsz, originalSamples, fakeSamples, dummySize);
            uint originalChunkCount = ReadUInt32(offsets.Payload, 4);
            PatchSampleToChunk(stsc, originalChunkCount);
            PatchChunkOffsets(offsets, fakeSamples, safeOffset);
            byte[] patchedMoov = moov.Serialize();

            byte[] oldMdat = input.AsSpan(checked((int)mdatInfo.Offset), checked((int)mdatInfo.Size)).ToArray();
            byte[] newMdat = AppendPadding(oldMdat, PaddingNal);

            using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long cursor = 0;
                foreach (TopLevelBox box in top)
                {
                    if (box.Offset > cursor)
                        destination.Write(input.AsSpan(checked((int)cursor), checked((int)(box.Offset - cursor))));

                    if (box.Type == "mdat") destination.Write(newMdat);
                    else if (box.Type == "moov") destination.Write(patchedMoov);
                    else destination.Write(input.AsSpan(checked((int)box.Offset), checked((int)box.Size)));
                    cursor = box.Offset + box.Size;
                }
                if (cursor < input.Length)
                    destination.Write(input.AsSpan(checked((int)cursor)));
                destination.Flush(true);
            }

            File.Move(partialPath, outputPath, true);
            return new PatchResult(true, originalSamples, declaredSamples,
                $"TikTok video quality patch: {originalSamples} real + {fakeSamples} compatibility samples (x{multiplier}), pixels unchanged.");
        }
        catch
        {
            if (File.Exists(partialPath)) File.Delete(partialPath);
            throw;
        }
    }

    private static PatchResult CopyUnchanged(string inputPath, string outputPath, string message)
    {
        File.Copy(inputPath, outputPath, true);
        return new PatchResult(false, 0, 0, message);
    }

    private static byte[] AppendPadding(byte[] oldMdat, byte[] padding)
    {
        int headerSize = oldMdat.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(oldMdat.AsSpan(0, 4)) == 1 ? 16 : 8;
        ulong newSize = checked((ulong)oldMdat.Length + (ulong)padding.Length);
        if (headerSize == 8 && newSize > uint.MaxValue)
            throw new InvalidDataException("MP4 mdat became larger than 32-bit size.");

        byte[] result = new byte[checked(oldMdat.Length + padding.Length)];
        oldMdat.CopyTo(result, 0);
        padding.CopyTo(result, oldMdat.Length);
        if (headerSize == 8)
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)newSize);
        else
            BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(8, 8), newSize);
        return result;
    }

    private static void PatchTimeToSample(Mp4Box stts, int fakeSamples, uint sampleDelta)
    {
        if (stts.Payload.Length < 8) throw new InvalidDataException("Invalid stts box.");
        uint entries = ReadUInt32(stts.Payload, 4);
        int expected = checked(8 + ((int)entries * 8));
        if (stts.Payload.Length < expected) throw new InvalidDataException("Truncated stts box.");
        if (entries > 0)
        {
            int last = 8 + (((int)entries - 1) * 8);
            if (ReadUInt32(stts.Payload, last + 4) == sampleDelta)
            {
                WriteUInt32(stts.Payload, last, checked(ReadUInt32(stts.Payload, last) + (uint)fakeSamples));
                return;
            }
        }

        byte[] payload = new byte[checked(expected + 8)];
        stts.Payload.AsSpan(0, expected).CopyTo(payload);
        WriteUInt32(payload, 4, entries + 1);
        WriteUInt32(payload, expected, checked((uint)fakeSamples));
        WriteUInt32(payload, expected + 4, sampleDelta);
        stts.Payload = payload;
    }

    private static void PatchSampleSizes(Mp4Box stsz, int originalSamples, int fakeSamples, int dummySize)
    {
        if (stsz.Payload.Length < 12) throw new InvalidDataException("Invalid stsz box.");
        uint fixedSize = ReadUInt32(stsz.Payload, 4);
        int tableBytes = fixedSize == 0 ? checked(originalSamples * 4) : 0;
        if (stsz.Payload.Length < 12 + tableBytes) throw new InvalidDataException("Truncated stsz box.");

        byte[] payload = new byte[checked(12 + ((originalSamples + fakeSamples) * 4))];
        stsz.Payload.AsSpan(0, 4).CopyTo(payload);
        WriteUInt32(payload, 4, 0);
        WriteUInt32(payload, 8, checked((uint)(originalSamples + fakeSamples)));
        int cursor = 12;
        if (fixedSize == 0)
        {
            stsz.Payload.AsSpan(12, tableBytes).CopyTo(payload.AsSpan(cursor));
            cursor += tableBytes;
        }
        else
        {
            for (int i = 0; i < originalSamples; i++, cursor += 4) WriteUInt32(payload, cursor, fixedSize);
        }
        for (int i = 0; i < fakeSamples; i++, cursor += 4) WriteUInt32(payload, cursor, checked((uint)dummySize));
        stsz.Payload = payload;
    }

    private static void PatchSampleToChunk(Mp4Box stsc, uint originalChunkCount)
    {
        if (stsc.Payload.Length < 8) throw new InvalidDataException("Invalid stsc box.");
        uint entries = ReadUInt32(stsc.Payload, 4);
        int expected = checked(8 + ((int)entries * 12));
        if (entries == 0 || stsc.Payload.Length < expected) throw new InvalidDataException("Truncated stsc box.");
        uint descriptionIndex = ReadUInt32(stsc.Payload, expected - 4);
        byte[] payload = new byte[checked(expected + 12)];
        stsc.Payload.AsSpan(0, expected).CopyTo(payload);
        WriteUInt32(payload, 4, entries + 1);
        WriteUInt32(payload, expected, checked(originalChunkCount + 1));
        WriteUInt32(payload, expected + 4, 1);
        WriteUInt32(payload, expected + 8, descriptionIndex);
        stsc.Payload = payload;
    }

    private static void PatchChunkOffsets(Mp4Box offsets, int fakeSamples, ulong safeOffset)
    {
        if (offsets.Payload.Length < 8) throw new InvalidDataException("Invalid chunk offset box.");
        uint count = ReadUInt32(offsets.Payload, 4);
        int stride = offsets.Type == "co64" ? 8 : 4;
        int expected = checked(8 + ((int)count * stride));
        if (offsets.Payload.Length < expected) throw new InvalidDataException("Truncated chunk offset box.");
        byte[] payload = new byte[checked(expected + (fakeSamples * stride))];
        offsets.Payload.AsSpan(0, expected).CopyTo(payload);
        WriteUInt32(payload, 4, checked(count + (uint)fakeSamples));
        int cursor = expected;
        for (int i = 0; i < fakeSamples; i++, cursor += stride)
        {
            if (stride == 8) WriteUInt64(payload, cursor, safeOffset);
            else WriteUInt32(payload, cursor, checked((uint)safeOffset));
        }
        offsets.Payload = payload;
    }

    private static uint ReadFirstSampleDelta(byte[] payload)
    {
        if (payload.Length < 16 || ReadUInt32(payload, 4) == 0) throw new InvalidDataException("Invalid stts entries.");
        return ReadUInt32(payload, 12);
    }

    private static int GetDummySize(Mp4Box stbl)
    {
        Mp4Box? stsd = stbl.Child("stsd");
        string codec = stsd?.Payload.Length >= 16 ? Encoding.ASCII.GetString(stsd.Payload, 12, 4) : "";
        return DummySizes.TryGetValue(codec, out int size) ? size : 8;
    }

    private static bool IsVideoTrack(Mp4Box track)
    {
        Mp4Box? handler = FindPath(track, "mdia", "hdlr");
        return handler is not null && handler.Payload.Length >= 12 && Encoding.ASCII.GetString(handler.Payload, 8, 4) == "vide";
    }

    private static Mp4Box? FindPath(Mp4Box root, params string[] path)
    {
        Mp4Box? current = root;
        foreach (string type in path)
        {
            current = current?.Child(type);
            if (current is null) return null;
        }
        return current;
    }

    private static List<TopLevelBox> ReadTopLevelBoxes(byte[] data)
    {
        var boxes = new List<TopLevelBox>();
        long offset = 0;
        while (offset + 8 <= data.Length)
        {
            uint rawSize = ReadUInt32(data, checked((int)offset));
            string type = Encoding.ASCII.GetString(data, checked((int)offset + 4), 4);
            int headerSize = 8;
            ulong size = rawSize;
            if (rawSize == 1)
            {
                if (offset + 16 > data.Length) throw new InvalidDataException("Truncated MP4 large box header.");
                size = ReadUInt64(data, checked((int)offset + 8));
                headerSize = 16;
            }
            else if (rawSize == 0) size = checked((ulong)(data.Length - offset));
            if (size < (ulong)headerSize || size > (ulong)(data.Length - offset)) throw new InvalidDataException($"Invalid MP4 box {type}.");
            boxes.Add(new TopLevelBox(type, offset, checked((long)size), headerSize));
            offset += checked((long)size);
        }
        if (offset != data.Length) throw new InvalidDataException("MP4 ended with incomplete top-level box.");
        return boxes;
    }

    private static Mp4Box ParseSingleBox(byte[] data)
    {
        int offset = 0;
        Mp4Box box = Mp4Box.Parse(data, ref offset, data.Length);
        if (offset != data.Length) throw new InvalidDataException("Unexpected bytes after moov box.");
        return box;
    }

    private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static ulong ReadUInt64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
    private static void WriteUInt32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    private static void WriteUInt64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset, 8), value);

    private sealed record TopLevelBox(string Type, long Offset, long Size, int HeaderSize);

    private sealed class Mp4Box
    {
        public string Type { get; }
        public byte[] Payload { get; set; }
        public List<Mp4Box> Children { get; }

        private Mp4Box(string type, byte[] payload, List<Mp4Box>? children = null)
        {
            Type = type;
            Payload = payload;
            Children = children ?? new List<Mp4Box>();
        }

        public Mp4Box? Child(string type) => Children.FirstOrDefault(child => child.Type == type);

        public static Mp4Box Parse(byte[] data, ref int offset, int limit)
        {
            if (offset + 8 > limit) throw new InvalidDataException("Truncated MP4 box header.");
            int start = offset;
            uint size32 = ReadUInt32(data, offset);
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            offset += 8;
            ulong size = size32;
            int headerSize = 8;
            if (size32 == 1)
            {
                if (offset + 8 > limit) throw new InvalidDataException("Truncated MP4 large box header.");
                size = ReadUInt64(data, offset);
                offset += 8;
                headerSize = 16;
            }
            else if (size32 == 0) size = checked((ulong)(limit - start));
            if (size < (ulong)headerSize || size > (ulong)(limit - start)) throw new InvalidDataException($"Invalid MP4 box {type}.");
            int end = checked(start + (int)size);
            if (!ContainerTypes.Contains(type))
            {
                byte[] payload = data.AsSpan(offset, end - offset).ToArray();
                offset = end;
                return new Mp4Box(type, payload);
            }
            var children = new List<Mp4Box>();
            while (offset < end) children.Add(Parse(data, ref offset, end));
            if (offset != end) throw new InvalidDataException($"Invalid {type} child boundary.");
            return new Mp4Box(type, Array.Empty<byte>(), children);
        }

        public byte[] Serialize()
        {
            byte[] payload;
            if (ContainerTypes.Contains(Type))
            {
                using var stream = new MemoryStream();
                foreach (Mp4Box child in Children) stream.Write(child.Serialize());
                payload = stream.ToArray();
            }
            else payload = Payload;

            ulong size = checked((ulong)payload.Length + 8);
            if (size > uint.MaxValue) throw new InvalidDataException("MP4 metadata box became too large.");
            byte[] output = new byte[checked((int)size)];
            WriteUInt32(output, 0, (uint)size);
            Encoding.ASCII.GetBytes(Type, output.AsSpan(4, 4));
            payload.CopyTo(output, 8);
            return output;
        }
    }
}
