using System.Buffers.Binary;
using System.Text;

namespace SIRIAUTOPOST.Domain.Services;

/// <summary>
/// Puts an MP4 or MOV file in the shape Facebook's upload accepts without a hitch. A video exported with its index
/// (<c>moov</c>) after the picture data (<c>mdat</c>) and a large cover picture inside <c>udta</c> (what an
/// <c>ffmpeg</c> remux with an attached poster leaves) played everywhere but never finished processing in the Facebook
/// composer (2026-10-06, SIRI_Promo_Voice.mp4); the same video with the index first and no cover went through. The
/// rewrite is lossless: the sample bytes are copied as they are and only the chunk offsets move.
/// </summary>
public static class Mp4Layout
{
    /// <summary>A <c>udta</c> box this big is cover art or similar ballast, not a title or an encoder tag.</summary>
    public const int LargeUdta = 64 * 1024;

    private readonly record struct Box(string Type, int Start, int Size, int Header)
    {
        public int End => Start + Size;
        public int BodyStart => Start + Header;
    }

    /// <summary>
    /// The file with its index first and without a large <c>udta</c>, or null when nothing has to change (already in
    /// shape) or the file is not one this can rewrite safely (not a plain MP4/MOV, fragmented, damaged). Never throws.
    /// </summary>
    public static byte[]? ForFacebook(byte[] data)
    {
        try
        {
            return Rewrite(data);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[]? Rewrite(byte[] d)
    {
        var top = Boxes(d, 0, d.Length);
        if (top is null || top.Count < 2 || top[0].Type != "ftyp") return null;
        var moovs = top.FindAll(b => b.Type == "moov");
        if (moovs.Count != 1 || top.Exists(b => b.Type == "moof") || !top.Exists(b => b.Type == "mdat")) return null;
        var moov = moovs[0];

        var kids = Boxes(d, moov.BodyStart, moov.End);
        if (kids is null || kids.Exists(k => k.Type == "mvex")) return null;
        var largeUdta = kids.Exists(k => k.Type == "udta" && k.Size > LargeUdta);

        var moovLast = top.FindIndex(b => b.Type == "moov") > top.FindIndex(b => b.Type == "mdat");
        if (!moovLast && !largeUdta) return null;

        // The new index: the old one without the large udta, header and size redone.
        var body = new List<byte>(moov.Size);
        foreach (var k in kids)
            if (k.Type != "udta" || k.Size <= LargeUdta) body.AddRange(new ReadOnlySpan<byte>(d, k.Start, k.Size).ToArray());
        var newMoov = new byte[8 + body.Count];
        BinaryPrimitives.WriteUInt32BigEndian(newMoov, (uint)newMoov.Length);
        Encoding.ASCII.GetBytes("moov").CopyTo(newMoov, 4);
        body.CopyTo(newMoov, 8);

        // New order: ftyp, moov, then every other box as it was. Each box's move decides where its chunks end up.
        var ftyp = top[0];
        var rest = top.Where(b => b.Type != "moov" && b.Start != ftyp.Start).ToList();
        var newStart = new Dictionary<int, long>();
        long pos = ftyp.Size + newMoov.Length;
        foreach (var b in rest)
        {
            newStart[b.Start] = pos;
            pos += b.Size;
        }
        if (!PatchOffsets(newMoov, 8, newMoov.Length, rest, newStart)) return null;

        var output = new byte[pos];
        Buffer.BlockCopy(d, ftyp.Start, output, 0, ftyp.Size);
        Buffer.BlockCopy(newMoov, 0, output, ftyp.Size, newMoov.Length);
        foreach (var b in rest) Buffer.BlockCopy(d, b.Start, output, (int)newStart[b.Start], b.Size);
        return output;
    }

    /// <summary>Walks the track boxes of the new index and moves every chunk offset to where its picture-data box went.</summary>
    private static bool PatchOffsets(byte[] moov, int from, int to, List<Box> rest, Dictionary<int, long> newStart)
    {
        var boxes = Boxes(moov, from, to);
        if (boxes is null) return false;
        foreach (var b in boxes)
        {
            switch (b.Type)
            {
                case "trak" or "mdia" or "minf" or "stbl":
                    if (!PatchOffsets(moov, b.BodyStart, b.End, rest, newStart)) return false;
                    break;
                case "stco" or "co64":
                    var wide = b.Type == "co64";
                    var step = wide ? 8 : 4;
                    var count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(b.BodyStart + 4, 4));
                    if (b.BodyStart + 8 + (long)count * step > b.End) return false;
                    for (var i = 0; i < count; i++)
                    {
                        var at = b.BodyStart + 8 + i * step;
                        var old = wide ? (long)BinaryPrimitives.ReadUInt64BigEndian(moov.AsSpan(at, 8)) : BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(at, 4));
                        var home = rest.FindIndex(r => old >= r.Start && old < r.End);
                        if (home < 0 || rest[home].Type != "mdat") return false; // a chunk that is not in picture data: leave the file alone
                        var moved = old - rest[home].Start + newStart[rest[home].Start];
                        if (wide) BinaryPrimitives.WriteUInt64BigEndian(moov.AsSpan(at, 8), (ulong)moved);
                        else if (moved > uint.MaxValue) return false;
                        else BinaryPrimitives.WriteUInt32BigEndian(moov.AsSpan(at, 4), (uint)moved);
                    }
                    break;
            }
        }
        return true;
    }

    /// <summary>The boxes between two positions, or null when a size does not fit (damaged or not an MP4).</summary>
    private static List<Box>? Boxes(byte[] d, int from, int to)
    {
        var list = new List<Box>();
        var pos = from;
        while (pos < to)
        {
            if (to - pos < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos, 4));
            var type = Encoding.ASCII.GetString(d, pos + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (to - pos < 16) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(pos + 8, 8));
                header = 16;
            }
            else if (size == 0) size = to - pos;
            if (size < header || size > to - pos || size > int.MaxValue) return null;
            list.Add(new Box(type, pos, (int)size, header));
            pos += (int)size;
        }
        return list;
    }
}
