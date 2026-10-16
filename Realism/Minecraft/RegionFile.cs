using System.Buffers.Binary;
using System.IO.Compression;

namespace Realism.Minecraft;

/// <summary>Reads chunks out of an Anvil (.mca) region file: 32x32 chunks, each zlib/gzip compressed NBT.</summary>
public static class RegionFile
{
    public static NbtCompound? ReadChunk(byte[] file, int localX, int localZ)
    {
        int header = 4 * ((localX & 31) + (localZ & 31) * 32);
        if (header + 4 > file.Length) return null;
        int offset = (file[header] << 16) | (file[header + 1] << 8) | file[header + 2];
        if (offset == 0 || file[header + 3] == 0) return null;

        int pos = offset * 4096;
        if (pos + 5 > file.Length) return null;
        int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
        byte compression = file[pos + 4];
        if ((compression & 0x80) != 0 || length <= 1 || pos + 4 + length > file.Length) return null; // external .mcc chunk

        using var raw = new MemoryStream(file, pos + 5, length - 1, writable: false);
        Stream? stream = compression switch
        {
            1 => new GZipStream(raw, CompressionMode.Decompress),
            2 => new ZLibStream(raw, CompressionMode.Decompress),
            3 => raw,
            _ => null, // 4 = LZ4, not supported
        };
        if (stream == null) return null;

        using var outStream = new MemoryStream();
        stream.CopyTo(outStream);
        if (stream != raw) stream.Dispose();
        return Nbt.Parse(outStream.GetBuffer().AsSpan(0, (int)outStream.Length));
    }
}
