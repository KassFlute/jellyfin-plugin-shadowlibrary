using System.Buffers.Binary;
using System.Numerics;
using Jellyfin.MediaEncoding.Keyframes;

namespace Jellyfin.Plugin.ShadowLibrary.Remote;

/// <summary>
/// Reads the keyframes of a remote Matroska file from its Cues, with a few range reads
/// instead of the whole file.
/// </summary>
/// <remarks>
/// Same walk as Jellyfin's MatroskaKeyframeExtractor (SeekHead, Info, Tracks, Cues), which
/// only opens local files.
/// </remarks>
public static class MatroskaCueReader
{
    private const uint EbmlHeaderId = 0x1A45DFA3;
    private const uint SegmentId = 0x18538067;
    private const uint SeekHeadId = 0x114D9B74;
    private const uint SeekId = 0x4DBB;
    private const uint SeekIdId = 0x53AB;
    private const uint SeekPositionId = 0x53AC;
    private const uint InfoId = 0x1549A966;
    private const uint TimestampScaleId = 0x2AD7B1;
    private const uint DurationId = 0x4489;
    private const uint TracksId = 0x1654AE6B;
    private const uint TrackEntryId = 0xAE;
    private const uint TrackNumberId = 0xD7;
    private const uint TrackTypeId = 0x83;
    private const uint CuesId = 0x1C53BB6B;
    private const uint CuePointId = 0xBB;
    private const uint CueTimeId = 0xB3;
    private const uint CueTrackPositionsId = 0xB7;
    private const uint CueTrackId = 0xF7;
    private const uint ClusterId = 0x1F43B675;

    private const ulong TrackTypeVideo = 1;
    private const long DefaultTimestampScale = 1_000_000;

    // SeekHead, Info and Tracks sit in the first few KB of any muxer's output
    private const int HeadLength = 64 * 1024;

    // 4 bytes of id and 8 of size at most
    private const int MaxElementHeaderLength = 12;

    // a cue per keyframe over a three hour film stays far below this
    private const long MaxCuesLength = 32L * 1024 * 1024;

    /// <summary>
    /// Reads a byte range of the file. May return fewer bytes only at the end of the file.
    /// </summary>
    /// <param name="offset">First byte.</param>
    /// <param name="length">Byte count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bytes.</returns>
    public delegate Task<byte[]> RangeReader(long offset, int length, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the keyframes of the first video track.
    /// </summary>
    /// <param name="read">Range reader over the file.</param>
    /// <param name="runtimeTicks">Runtime to fall back on when the file states no duration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The keyframes, in ticks.</returns>
    /// <exception cref="InvalidDataException">The file is not Matroska or lacks the elements needed.</exception>
    public static async Task<KeyframeData> ReadAsync(RangeReader read, long runtimeTicks, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        var head = await read(0, HeadLength, cancellationToken).ConfigureAwait(false);
        var position = 0;

        if (!TryReadHeader(head, ref position, out var id, out var size) || id != EbmlHeaderId || size < 0)
        {
            throw new InvalidDataException("Not a Matroska file.");
        }

        position += (int)size;
        if (!TryReadHeader(head, ref position, out id, out _) || id != SegmentId)
        {
            throw new InvalidDataException("No Segment after the EBML header.");
        }

        long segmentStart = position;
        long? infoPosition = null, tracksPosition = null, cuesPosition = null;
        byte[]? info = null, tracks = null;

        // top level elements that fit in the head, up to the first cluster
        while (TryReadHeader(head, ref position, out id, out size) && size >= 0 && position + size <= head.Length && id != ClusterId)
        {
            var body = head.AsMemory(position, (int)size);
            switch (id)
            {
                case SeekHeadId:
                    foreach (var (target, offset) in ReadSeeks(body))
                    {
                        switch (target)
                        {
                            case InfoId: infoPosition ??= offset; break;
                            case TracksId: tracksPosition ??= offset; break;
                            case CuesId: cuesPosition ??= offset; break;
                        }
                    }

                    break;
                case InfoId:
                    info = body.ToArray();
                    break;
                case TracksId:
                    tracks = body.ToArray();
                    break;
            }

            position += (int)size;
        }

        info ??= await ReadElementAsync(read, segmentStart, infoPosition, InfoId, HeadLength, cancellationToken).ConfigureAwait(false);
        tracks ??= await ReadElementAsync(read, segmentStart, tracksPosition, TracksId, HeadLength, cancellationToken).ConfigureAwait(false);
        var cues = await ReadElementAsync(read, segmentStart, cuesPosition, CuesId, MaxCuesLength, cancellationToken).ConfigureAwait(false);

        var (timestampScale, duration) = ReadInfo(info);
        var videoTrack = FindVideoTrack(tracks);

        var keyframes = new List<long>();
        foreach (var (pointId, point) in Children(cues))
        {
            if (pointId != CuePointId)
            {
                continue;
            }

            ulong? time = null;
            var onVideo = false;
            foreach (var (childId, child) in Children(point))
            {
                if (childId == CueTimeId)
                {
                    time = ReadUInt(child.Span);
                }
                else if (childId == CueTrackPositionsId)
                {
                    onVideo |= Children(child).Any(c => c.Id == CueTrackId && ReadUInt(c.Body.Span) == videoTrack);
                }
            }

            if (time is { } t && onVideo)
            {
                keyframes.Add(ToTicks(t, timestampScale));
            }
        }

        keyframes.Sort();

        // Jellyfin rejects keyframe data that ends past its duration
        var total = duration is { } d ? (long)(d * timestampScale / 100) : runtimeTicks;
        if (keyframes.Count > 0)
        {
            total = Math.Max(total, keyframes[^1]);
        }

        return new KeyframeData(total, keyframes.Distinct().ToList());
    }

    private static async Task<byte[]> ReadElementAsync(
        RangeReader read,
        long segmentStart,
        long? relativePosition,
        uint expectedId,
        long maxLength,
        CancellationToken cancellationToken)
    {
        if (relativePosition is not { } relative)
        {
            throw new InvalidDataException(FormattableString.Invariant($"No SeekHead entry for element 0x{expectedId:X}."));
        }

        var start = segmentStart + relative;
        var header = await read(start, MaxElementHeaderLength, cancellationToken).ConfigureAwait(false);
        var position = 0;
        if (!TryReadHeader(header, ref position, out var id, out var size) || id != expectedId)
        {
            throw new InvalidDataException(FormattableString.Invariant($"Element 0x{expectedId:X} is not where the SeekHead says."));
        }

        if (size < 0 || size > maxLength)
        {
            throw new InvalidDataException(FormattableString.Invariant($"Element 0x{expectedId:X} has an unusable size of {size}."));
        }

        var body = await read(start + position, (int)size, cancellationToken).ConfigureAwait(false);
        if (body.Length != size)
        {
            throw new InvalidDataException(FormattableString.Invariant($"Element 0x{expectedId:X} is cut short."));
        }

        return body;
    }

    private static IEnumerable<(uint Target, long Position)> ReadSeeks(ReadOnlyMemory<byte> seekHead)
    {
        foreach (var (id, seek) in Children(seekHead))
        {
            if (id != SeekId)
            {
                continue;
            }

            uint? target = null;
            long? position = null;
            foreach (var (childId, child) in Children(seek))
            {
                if (childId == SeekIdId)
                {
                    target = (uint)ReadUInt(child.Span);
                }
                else if (childId == SeekPositionId)
                {
                    position = (long)ReadUInt(child.Span);
                }
            }

            if (target is { } t && position is { } p)
            {
                yield return (t, p);
            }
        }
    }

    private static (long Scale, double? Duration) ReadInfo(byte[] info)
    {
        var scale = DefaultTimestampScale;
        double? duration = null;
        foreach (var (id, body) in Children(info))
        {
            if (id == TimestampScaleId)
            {
                scale = (long)ReadUInt(body.Span);
            }
            else if (id == DurationId)
            {
                duration = body.Length == 4
                    ? BinaryPrimitives.ReadSingleBigEndian(body.Span)
                    : BinaryPrimitives.ReadDoubleBigEndian(body.Span);
            }
        }

        return (scale, duration);
    }

    private static ulong FindVideoTrack(byte[] tracks)
    {
        foreach (var (id, entry) in Children(tracks))
        {
            if (id != TrackEntryId)
            {
                continue;
            }

            ulong? number = null, type = null;
            foreach (var (childId, child) in Children(entry))
            {
                if (childId == TrackNumberId)
                {
                    number = ReadUInt(child.Span);
                }
                else if (childId == TrackTypeId)
                {
                    type = ReadUInt(child.Span);
                }
            }

            if (type == TrackTypeVideo && number is { } n)
            {
                return n;
            }
        }

        throw new InvalidDataException("No video track.");
    }

    private static IEnumerable<(uint Id, ReadOnlyMemory<byte> Body)> Children(ReadOnlyMemory<byte> parent)
    {
        var position = 0;
        while (true)
        {
            var span = parent.Span;
            if (!TryReadHeader(span, ref position, out var id, out var size) || size < 0 || position + size > span.Length)
            {
                // unknown sizes only appear on clusters and segments, never in what is read here
                yield break;
            }

            yield return (id, parent.Slice(position, (int)size));
            position += (int)size;
        }
    }

    /// <summary>
    /// Reads an element id and size. A size of -1 means unknown.
    /// </summary>
    private static bool TryReadHeader(ReadOnlySpan<byte> data, ref int position, out uint id, out long size)
    {
        id = 0;
        size = 0;
        var start = position;
        if (!TryReadVint(data, ref position, 4, true, out var rawId)
            || !TryReadVint(data, ref position, 8, false, out var rawSize))
        {
            position = start;
            return false;
        }

        id = (uint)rawId;
        size = rawSize == ulong.MaxValue ? -1 : (long)rawSize;
        return true;
    }

    // EBML variable length integer. Ids keep their length marker, sizes drop it, and a size
    // with every value bit set means unknown, returned as ulong.MaxValue
    private static bool TryReadVint(ReadOnlySpan<byte> data, ref int position, int maxLength, bool keepMarker, out ulong value)
    {
        value = 0;
        if (position >= data.Length || data[position] == 0)
        {
            return false;
        }

        var first = data[position];
        var length = BitOperations.LeadingZeroCount((uint)first) - 23;
        if (length > maxLength || position + length > data.Length)
        {
            return false;
        }

        value = keepMarker ? first : (ulong)(first & (0xFF >> length));
        for (var i = 1; i < length; i++)
        {
            value = (value << 8) | data[position + i];
        }

        if (!keepMarker && value == (1UL << (7 * length)) - 1)
        {
            value = ulong.MaxValue;
        }

        position += length;
        return true;
    }

    private static ulong ReadUInt(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        foreach (var b in data[..Math.Min(data.Length, 8)])
        {
            value = (value << 8) | b;
        }

        return value;
    }

    // TimestampScale is in nanoseconds, a tick is 100 ns
    private static long ToTicks(ulong value, long timestampScale) => (long)value * timestampScale / 100;
}
