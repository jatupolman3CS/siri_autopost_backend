using System.Buffers.Binary;
using System.Text;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Domain.Tests;

public class Mp4LayoutTests
{
    private static byte[] Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        body.CopyTo(b, 8);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] U64(ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, v);
        return b;
    }

    // version/flags, entry count, then the offsets
    private static byte[] Stco(params uint[] offsets) =>
        Box("stco", U32(0), U32((uint)offsets.Length), offsets.SelectMany(U32).ToArray());

    private static byte[] Co64(params ulong[] offsets) =>
        Box("co64", U32(0), U32((uint)offsets.Length), offsets.SelectMany(U64).ToArray());

    private static byte[] Moov(byte[] chunkTable, byte[]? udta) =>
        Box("moov", Box("mvhd", new byte[100]), Box("trak", Box("mdia", Box("minf", Box("stbl", chunkTable)))), udta ?? []);

    private static readonly byte[] Ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomiso2avc1mp41"));
    private static readonly byte[] Free = Box("free");
    private static readonly byte[] Payload = Enumerable.Range(1, 200).Select(i => (byte)i).ToArray();
    private static byte[] CoverArt => Box("udta", Box("meta", new byte[Mp4Layout.LargeUdta + 10]));

    // ftyp, free, mdat, moov: the index at the end, one chunk at the first byte of the payload
    private static byte[] IndexAtEnd(byte[]? udta = null, bool wide = false)
    {
        var mdat = Box("mdat", Payload);
        var firstPayloadByte = (uint)(Ftyp.Length + Free.Length + 8);
        var table = wide ? Co64(firstPayloadByte, firstPayloadByte + 100) : Stco(firstPayloadByte, firstPayloadByte + 100);
        return [.. Ftyp, .. Free, .. mdat, .. Moov(table, udta)];
    }

    private static List<(string Type, int Start, int Size)> Top(byte[] d)
    {
        var list = new List<(string, int, int)>();
        for (var pos = 0; pos < d.Length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos, 4));
            list.Add((Encoding.ASCII.GetString(d, pos + 4, 4), pos, size));
            pos += size;
        }
        return list;
    }

    /// <summary>The offsets of the first moov's stco/co64 chunk table and the file position they point at, in order.</summary>
    private static List<long> ChunkOffsets(byte[] d)
    {
        var moov = Top(d).Single(b => b.Type == "moov");
        var text = d.AsSpan(moov.Start, moov.Size);
        var wide = text.IndexOf("co64"u8);
        var at = wide >= 0 ? wide : text.IndexOf("stco"u8);
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(text.Slice(at + 8, 4));
        var offsets = new List<long>();
        for (var i = 0; i < count; i++)
            offsets.Add(wide >= 0
                ? (long)BinaryPrimitives.ReadUInt64BigEndian(text.Slice(at + 12 + 8 * i, 8))
                : BinaryPrimitives.ReadUInt32BigEndian(text.Slice(at + 12 + 4 * i, 4)));
        return offsets;
    }

    [Fact]
    public void Index_at_the_end_moves_to_the_front_and_the_chunks_still_point_at_the_same_bytes()
    {
        var input = IndexAtEnd();
        var output = Mp4Layout.ForFacebook(input);

        Assert.NotNull(output);
        Assert.Equal(new[] { "ftyp", "moov", "free", "mdat" }, Top(output).Select(b => b.Type));
        var offsets = ChunkOffsets(output);
        Assert.Equal(Payload[0], output[(int)offsets[0]]);
        Assert.Equal(Payload[100], output[(int)offsets[1]]);
        Assert.Equal(input.Length, output.Length); // nothing dropped, nothing added
    }

    [Fact]
    public void Wide_chunk_offsets_are_moved_too()
    {
        var output = Mp4Layout.ForFacebook(IndexAtEnd(wide: true));

        Assert.NotNull(output);
        var offsets = ChunkOffsets(output);
        Assert.Equal(Payload[0], output[(int)offsets[0]]);
        Assert.Equal(Payload[100], output[(int)offsets[1]]);
    }

    [Fact]
    public void A_large_udta_cover_is_dropped_and_the_file_gets_smaller()
    {
        var input = IndexAtEnd(CoverArt);
        var output = Mp4Layout.ForFacebook(input);

        Assert.NotNull(output);
        Assert.True(output.Length < input.Length - Mp4Layout.LargeUdta);
        Assert.False(output.AsSpan().IndexOf("udta"u8) >= 0);
        var offsets = ChunkOffsets(output);
        Assert.Equal(Payload[0], output[(int)offsets[0]]);
        Assert.Equal(Payload[100], output[(int)offsets[1]]);
    }

    [Fact]
    public void An_index_that_is_already_first_with_no_cover_is_left_alone()
    {
        var mdat = Box("mdat", Payload);
        var moov = Moov(Stco(0), null);
        byte[] input = [.. Ftyp, .. moov, .. Free, .. mdat];

        Assert.Null(Mp4Layout.ForFacebook(input));
    }

    [Fact]
    public void An_index_first_file_with_a_cover_loses_the_cover_and_the_chunks_follow()
    {
        var mdat = Box("mdat", Payload);
        var oldMoovLength = Moov(Stco(0, 0), CoverArt).Length;
        var firstPayloadByte = (uint)(Ftyp.Length + oldMoovLength + Free.Length + 8);
        byte[] input = [.. Ftyp, .. Moov(Stco(firstPayloadByte, firstPayloadByte + 100), CoverArt), .. Free, .. mdat];

        var output = Mp4Layout.ForFacebook(input);

        Assert.NotNull(output);
        Assert.Equal(new[] { "ftyp", "moov", "free", "mdat" }, Top(output).Select(b => b.Type));
        var offsets = ChunkOffsets(output);
        Assert.Equal(Payload[0], output[(int)offsets[0]]);
        Assert.Equal(Payload[100], output[(int)offsets[1]]);
    }

    [Fact]
    public void A_small_udta_such_as_an_encoder_tag_is_kept()
    {
        var small = Box("udta", Box("meta", new byte[40]));
        var output = Mp4Layout.ForFacebook(IndexAtEnd(small));

        Assert.NotNull(output);
        Assert.True(output.AsSpan().IndexOf("udta"u8) >= 0);
    }

    [Fact]
    public void Files_that_are_not_plain_mp4_are_left_alone()
    {
        Assert.Null(Mp4Layout.ForFacebook([]));
        Assert.Null(Mp4Layout.ForFacebook([1, 2, 3, 4, 5, 6, 7, 8, 9]));
        Assert.Null(Mp4Layout.ForFacebook(Encoding.ASCII.GetBytes("RIFFxxxxWEBMVP8 not an mp4 at all")));

        var cut = IndexAtEnd();
        Assert.Null(Mp4Layout.ForFacebook(cut[..(cut.Length - 20)])); // cut off in the middle of the index

        var fragmented = (byte[])IndexAtEnd().Clone();
        var at = fragmented.AsSpan().IndexOf("free"u8);
        Encoding.ASCII.GetBytes("moof").CopyTo(fragmented, at);
        Assert.Null(Mp4Layout.ForFacebook(fragmented));
    }

    [Fact]
    public void A_chunk_pointing_outside_the_picture_data_leaves_the_file_alone()
    {
        var mdat = Box("mdat", Payload);
        byte[] input = [.. Ftyp, .. mdat, .. Moov(Stco(5), null)]; // offset 5 is inside ftyp
        Assert.Null(Mp4Layout.ForFacebook(input));
    }
}
