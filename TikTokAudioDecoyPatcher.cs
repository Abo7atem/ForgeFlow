using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace VideoProcessing;

/// <summary>
/// Adds a deliberately malformed, non-default AAC compatibility track to an MP4.
/// The clean primary audio and video tracks are left untouched. This output is
/// intended only for direct TikTok upload; editors and archive workflows should
/// use the normal ForgeFlow output instead.
/// </summary>
public static class TikTokAudioDecoyPatcher
{
    private static readonly HashSet<string> ContainerTypes = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "stbl", "edts"
    };

    private static readonly byte[] DecoySample = { 0, 0, 0, 4, 0, 0, 0, 0 };

    public sealed record PatchResult(bool Applied, int OriginalSamples, int DecoySamples, string Message);

    public static PatchResult Patch(string inputPath, string outputPath, int packetMultiplier = 10)
    {
        if (packetMultiplier < 2 || packetMultiplier > 20)
            throw new ArgumentOutOfRangeException(nameof(packetMultiplier), "Packet multiplier must be between 2 and 20.");
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("TikTok compatibility input was not found.", inputPath);

        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        string partialPath = outputPath + ".partial";
        if (File.Exists(partialPath)) File.Delete(partialPath);

        try
        {
            using var source = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            List<TopLevelBox> topLevel = ReadTopLevelBoxes(source);
            TopLevelBox? moovInfo = topLevel.SingleOrDefault(box => box.Type == "moov");
            if (moovInfo is null)
                return CopyUnchanged(inputPath, outputPath, "MP4 movie box was not found; compatibility track was skipped.");

            long lastMediaEnd = topLevel.Where(box => box.Type == "mdat").Select(box => box.Offset + box.Size).DefaultIfEmpty(0).Max();
            if (moovInfo.Offset < lastMediaEnd || moovInfo.Offset + moovInfo.Size != source.Length)
                return CopyUnchanged(inputPath, outputPath, "MP4 is already fast-start/interleaved; compatibility track was skipped safely.");

            source.Position = moovInfo.Offset;
            byte[] moovBytes = ReadExact(source, checked((int)moovInfo.Size));
            Mp4Box moov = ParseSingleBox(moovBytes);
            List<Mp4Box> audioTracks = moov.Children
                .Where(box => box.Type == "trak" && IsAudioTrack(box))
                .ToList();
            if (audioTracks.Count < 2)
                return CopyUnchanged(inputPath, outputPath, "A secondary AAC track was not available; compatibility track was skipped.");

            Mp4Box targetTrack = audioTracks[1];
            Mp4Box? stbl = FindPath(targetTrack, "mdia", "minf", "stbl");
            Mp4Box? stsz = stbl?.Child("stsz");
            Mp4Box? stts = stbl?.Child("stts");
            Mp4Box? stsc = stbl?.Child("stsc");
            Mp4Box? chunkOffsets = stbl?.Child("stco") ?? stbl?.Child("co64");
            if (stbl is null || stsz is null || stts is null || stsc is null || chunkOffsets is null)
                return CopyUnchanged(inputPath, outputPath, "AAC sample tables were incomplete; compatibility track was skipped.");

            int originalSamples = checked((int)ReadUInt32(stsz.Payload, 8));
            if (originalSamples <= 0)
                return CopyUnchanged(inputPath, outputPath, "Secondary AAC track had no samples; compatibility track was skipped.");

            int decoySamples = checked(originalSamples * (packetMultiplier - 1));
            long decoyPayloadBytes = checked((long)decoySamples * DecoySample.Length);
            long newMdatPayloadOffset = checked(moovInfo.Offset + 8);
            if (newMdatPayloadOffset > uint.MaxValue && chunkOffsets.Type == "stco")
                return CopyUnchanged(inputPath, outputPath, "MP4 exceeded 32-bit chunk offsets; compatibility track was skipped.");

            ulong originalMediaDuration = SumTimeToSampleDurations(stts.Payload);
            AppendSampleSizes(stsz, originalSamples, decoySamples);
            AppendTimeToSampleEntry(stts, checked((uint)decoySamples), 1);
            AppendChunk(stsc, chunkOffsets, checked((ulong)newMdatPayloadOffset), checked((uint)decoySamples));

            ulong newMediaDuration = checked(originalMediaDuration + (ulong)decoySamples);
            UpdateDurations(moov, targetTrack, newMediaDuration);
            byte[] patchedMoov = moov.Serialize();

            source.Position = 0;
            using (var destination = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CopyBytes(source, destination, moovInfo.Offset);
                WriteUInt32(destination, checked((uint)(8 + decoyPayloadBytes)));
                destination.Write(Encoding.ASCII.GetBytes("mdat"));
                for (int i = 0; i < decoySamples; i++) destination.Write(DecoySample);
                destination.Write(patchedMoov);
                destination.Flush(true);
            }

            File.Move(partialPath, outputPath, true);
            return new PatchResult(true, originalSamples, decoySamples,
                $"TikTok audio compatibility track: {originalSamples} clean + {decoySamples} decoy AAC packets.");
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

    private static List<TopLevelBox> ReadTopLevelBoxes(Stream stream)
    {
        var boxes = new List<TopLevelBox>();
        long offset = 0;
        Span<byte> header = stackalloc byte[16];
        while (offset + 8 <= stream.Length)
        {
            stream.Position = offset;
            stream.ReadExactly(header[..8]);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            int headerSize = 8;
            if (size == 1)
            {
                stream.ReadExactly(header.Slice(8, 8));
                size = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = checked((ulong)(stream.Length - offset));
            }

            if (size < (ulong)headerSize || size > (ulong)(stream.Length - offset))
                throw new InvalidDataException($"Invalid MP4 box {type} at {offset}.");
            boxes.Add(new TopLevelBox(type, offset, checked((long)size), headerSize));
            offset = checked(offset + (long)size);
        }
        if (offset != stream.Length) throw new InvalidDataException("MP4 ended with an incomplete box.");
        return boxes;
    }

    private static Mp4Box ParseSingleBox(byte[] data)
    {
        int offset = 0;
        Mp4Box box = Mp4Box.Parse(data, ref offset, data.Length);
        if (offset != data.Length) throw new InvalidDataException("Unexpected bytes after MP4 movie box.");
        return box;
    }

    private static bool IsAudioTrack(Mp4Box track)
    {
        Mp4Box? handler = FindPath(track, "mdia", "hdlr");
        return handler is not null && handler.Payload.Length >= 12 &&
               Encoding.ASCII.GetString(handler.Payload, 8, 4) == "soun";
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

    private static void AppendSampleSizes(Mp4Box stsz, int originalSamples, int decoySamples)
    {
        if (stsz.Payload.Length < 12) throw new InvalidDataException("Invalid stsz box.");
        uint fixedSize = ReadUInt32(stsz.Payload, 4);
        int oldTableBytes = fixedSize == 0 ? checked(originalSamples * 4) : 0;
        if (stsz.Payload.Length < 12 + oldTableBytes) throw new InvalidDataException("Truncated stsz box.");

        byte[] payload = new byte[checked(12 + ((originalSamples + decoySamples) * 4))];
        stsz.Payload.AsSpan(0, 4).CopyTo(payload);
        WriteUInt32(payload, 4, 0);
        WriteUInt32(payload, 8, checked((uint)(originalSamples + decoySamples)));
        int cursor = 12;
        if (fixedSize == 0)
        {
            stsz.Payload.AsSpan(12, oldTableBytes).CopyTo(payload.AsSpan(cursor));
            cursor += oldTableBytes;
        }
        else
        {
            for (int i = 0; i < originalSamples; i++, cursor += 4) WriteUInt32(payload, cursor, fixedSize);
        }
        for (int i = 0; i < decoySamples; i++, cursor += 4) WriteUInt32(payload, cursor, (uint)DecoySample.Length);
        stsz.Payload = payload;
    }

    private static ulong SumTimeToSampleDurations(byte[] payload)
    {
        if (payload.Length < 8) throw new InvalidDataException("Invalid stts box.");
        uint entries = ReadUInt32(payload, 4);
        if (payload.Length < 8 + (entries * 8L)) throw new InvalidDataException("Truncated stts box.");
        ulong total = 0;
        for (int i = 0; i < entries; i++)
        {
            int cursor = 8 + (i * 8);
            total = checked(total + ((ulong)ReadUInt32(payload, cursor) * ReadUInt32(payload, cursor + 4)));
        }
        return total;
    }

    private static void AppendTimeToSampleEntry(Mp4Box stts, uint sampleCount, uint sampleDelta)
    {
        uint entries = ReadUInt32(stts.Payload, 4);
        int expected = checked(8 + ((int)entries * 8));
        if (stts.Payload.Length < expected) throw new InvalidDataException("Truncated stts box.");

        if (entries > 0)
        {
            int last = 8 + (((int)entries - 1) * 8);
            if (ReadUInt32(stts.Payload, last + 4) == sampleDelta)
            {
                WriteUInt32(stts.Payload, last, checked(ReadUInt32(stts.Payload, last) + sampleCount));
                return;
            }
        }

        byte[] payload = new byte[checked(expected + 8)];
        stts.Payload.AsSpan(0, expected).CopyTo(payload);
        WriteUInt32(payload, 4, entries + 1);
        WriteUInt32(payload, expected, sampleCount);
        WriteUInt32(payload, expected + 4, sampleDelta);
        stts.Payload = payload;
    }

    private static void AppendChunk(Mp4Box stsc, Mp4Box offsets, ulong newOffset, uint samplesPerChunk)
    {
        uint chunkCount = ReadUInt32(offsets.Payload, 4);
        uint stscEntries = ReadUInt32(stsc.Payload, 4);
        int stscExpected = checked(8 + ((int)stscEntries * 12));
        if (stsc.Payload.Length < stscExpected || stscEntries == 0) throw new InvalidDataException("Invalid stsc box.");
        uint descriptionIndex = ReadUInt32(stsc.Payload, stscExpected - 4);

        byte[] stscPayload = new byte[checked(stscExpected + 12)];
        stsc.Payload.AsSpan(0, stscExpected).CopyTo(stscPayload);
        WriteUInt32(stscPayload, 4, stscEntries + 1);
        WriteUInt32(stscPayload, stscExpected, chunkCount + 1);
        WriteUInt32(stscPayload, stscExpected + 4, samplesPerChunk);
        WriteUInt32(stscPayload, stscExpected + 8, descriptionIndex);
        stsc.Payload = stscPayload;

        int stride = offsets.Type == "co64" ? 8 : 4;
        int offsetsExpected = checked(8 + ((int)chunkCount * stride));
        if (offsets.Payload.Length < offsetsExpected) throw new InvalidDataException("Invalid chunk offset box.");
        byte[] offsetPayload = new byte[checked(offsetsExpected + stride)];
        offsets.Payload.AsSpan(0, offsetsExpected).CopyTo(offsetPayload);
        WriteUInt32(offsetPayload, 4, chunkCount + 1);
        if (stride == 8) WriteUInt64(offsetPayload, offsetsExpected, newOffset);
        else WriteUInt32(offsetPayload, offsetsExpected, checked((uint)newOffset));
        offsets.Payload = offsetPayload;
    }

    private static void UpdateDurations(Mp4Box moov, Mp4Box track, ulong mediaDuration)
    {
        Mp4Box? mvhd = moov.Child("mvhd");
        Mp4Box? tkhd = track.Child("tkhd");
        Mp4Box? mdhd = FindPath(track, "mdia", "mdhd");
        if (mvhd is null || tkhd is null || mdhd is null) return;

        uint movieTimescale = ReadFullBoxTimescale(mvhd.Payload);
        uint mediaTimescale = ReadFullBoxTimescale(mdhd.Payload);
        if (movieTimescale == 0 || mediaTimescale == 0) return;

        ulong oldMediaDuration = ReadFullBoxDuration(mdhd.Payload);
        ulong addedMediaDuration = mediaDuration > oldMediaDuration ? mediaDuration - oldMediaDuration : 0;
        ulong addedMovieDuration = checked((ulong)Math.Ceiling(addedMediaDuration * (double)movieTimescale / mediaTimescale));
        WriteFullBoxDuration(mdhd.Payload, mediaDuration);
        ulong oldTrackDuration = ReadTrackHeaderDuration(tkhd.Payload);
        ulong newTrackDuration = checked(oldTrackDuration + addedMovieDuration);
        WriteTrackHeaderDuration(tkhd.Payload, newTrackDuration);
        ExtendEditList(track, addedMovieDuration);
        if (ReadFullBoxDuration(mvhd.Payload) < newTrackDuration) WriteFullBoxDuration(mvhd.Payload, newTrackDuration);
    }

    private static void ExtendEditList(Mp4Box track, ulong addedMovieDuration)
    {
        Mp4Box? elst = FindPath(track, "edts", "elst");
        if (elst is null || elst.Payload.Length < 8 || addedMovieDuration == 0) return;
        bool version1 = elst.Payload[0] == 1;
        uint count = ReadUInt32(elst.Payload, 4);
        int stride = version1 ? 20 : 12;
        if (elst.Payload.Length < 8 + (count * (long)stride)) return;

        for (int index = checked((int)count - 1); index >= 0; index--)
        {
            int cursor = 8 + (index * stride);
            long mediaTime = version1
                ? unchecked((long)ReadUInt64(elst.Payload, cursor + 8))
                : unchecked((int)ReadUInt32(elst.Payload, cursor + 4));
            if (mediaTime < 0) continue;
            if (version1)
                WriteUInt64(elst.Payload, cursor, checked(ReadUInt64(elst.Payload, cursor) + addedMovieDuration));
            else
                WriteUInt32(elst.Payload, cursor, checked(ReadUInt32(elst.Payload, cursor) + (uint)addedMovieDuration));
            return;
        }
    }

    private static uint ReadFullBoxTimescale(byte[] payload)
    {
        bool version1 = payload.Length > 0 && payload[0] == 1;
        int offset = version1 ? 20 : 12;
        return payload.Length >= offset + 4 ? ReadUInt32(payload, offset) : 0;
    }

    private static ulong ReadFullBoxDuration(byte[] payload)
    {
        bool version1 = payload.Length > 0 && payload[0] == 1;
        int offset = version1 ? 24 : 16;
        if (version1) return payload.Length >= offset + 8 ? ReadUInt64(payload, offset) : 0;
        return payload.Length >= offset + 4 ? ReadUInt32(payload, offset) : 0;
    }

    private static void WriteFullBoxDuration(byte[] payload, ulong duration)
    {
        bool version1 = payload.Length > 0 && payload[0] == 1;
        int offset = version1 ? 24 : 16;
        if (version1)
        {
            if (payload.Length >= offset + 8) WriteUInt64(payload, offset, duration);
        }
        else if (payload.Length >= offset + 4)
        {
            WriteUInt32(payload, offset, checked((uint)Math.Min(duration, uint.MaxValue)));
        }
    }

    private static void WriteTrackHeaderDuration(byte[] payload, ulong duration)
    {
        bool version1 = payload.Length > 0 && payload[0] == 1;
        int offset = version1 ? 28 : 20;
        if (version1)
        {
            if (payload.Length >= offset + 8) WriteUInt64(payload, offset, duration);
        }
        else if (payload.Length >= offset + 4)
        {
            WriteUInt32(payload, offset, checked((uint)Math.Min(duration, uint.MaxValue)));
        }
    }

    private static ulong ReadTrackHeaderDuration(byte[] payload)
    {
        bool version1 = payload.Length > 0 && payload[0] == 1;
        int offset = version1 ? 28 : 20;
        if (version1) return payload.Length >= offset + 8 ? ReadUInt64(payload, offset) : 0;
        return payload.Length >= offset + 4 ? ReadUInt32(payload, offset) : 0;
    }

    private static void CopyBytes(Stream source, Stream destination, long count)
    {
        byte[] buffer = new byte[1024 * 1024];
        while (count > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException();
            destination.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        byte[] data = new byte[count];
        stream.ReadExactly(data);
        return data;
    }

    private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static ulong ReadUInt64(byte[] data, int offset) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
    private static void WriteUInt32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    private static void WriteUInt64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(offset, 8), value);

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

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
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            offset += 8;
            ulong size = size32;
            int headerSize = 8;
            if (size32 == 1)
            {
                if (offset + 8 > limit) throw new InvalidDataException("Truncated large MP4 box header.");
                size = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
                offset += 8;
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = checked((ulong)(limit - start));
            }

            if (size < (ulong)headerSize || size > (ulong)(limit - start))
                throw new InvalidDataException($"Invalid MP4 box {type}.");
            int end = checked(start + (int)size);
            int payloadLength = end - offset;
            if (!ContainerTypes.Contains(type))
            {
                byte[] payload = data.AsSpan(offset, payloadLength).ToArray();
                offset = end;
                return new Mp4Box(type, payload);
            }

            var children = new List<Mp4Box>();
            while (offset < end) children.Add(Parse(data, ref offset, end));
            if (offset != end) throw new InvalidDataException($"Invalid {type} child box boundary.");
            return new Mp4Box(type, Array.Empty<byte>(), children);
        }

        public byte[] Serialize()
        {
            byte[] payload;
            if (ContainerTypes.Contains(Type))
            {
                using var childrenStream = new MemoryStream();
                foreach (Mp4Box child in Children)
                {
                    byte[] bytes = child.Serialize();
                    childrenStream.Write(bytes);
                }
                payload = childrenStream.ToArray();
            }
            else
            {
                payload = Payload;
            }

            ulong size = checked((ulong)payload.Length + 8);
            if (size > uint.MaxValue) throw new InvalidDataException("MP4 metadata box became too large.");
            byte[] output = new byte[checked((int)size)];
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)size);
            Encoding.ASCII.GetBytes(Type, output.AsSpan(4, 4));
            payload.CopyTo(output, 8);
            return output;
        }
    }
}
