using System.Buffers.Binary;
using System.IO.Compression;

namespace Realism.Rendering;

/// <summary>Decodes non-interlaced PNGs (any bit depth: RGBA, RGB, grey, grey+alpha, palette) to RGBA. Covers skins and
/// Minecraft's own textures (many mob textures are 4-bit palette images).</summary>
public static class PngDecoder
{
    public static (byte[] rgba, int width, int height) Decode(byte[] png)
    {
        if (png.Length < 8 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') throw new InvalidDataException("Not a PNG");
        int width = 0, height = 0, colorType = 0, bitDepth = 0, interlace = 0;
        byte[]? palette = null, paletteAlpha = null;
        using var idat = new MemoryStream();
        for (int pos = 8; pos + 8 <= png.Length;)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var data = png.AsSpan(pos + 8, len);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    bitDepth = data[8];
                    colorType = data[9];
                    interlace = data[12];
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": paletteAlpha = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            if (type == "IEND") break;
            pos += 12 + len;
        }
        if (interlace != 0 || bitDepth is not (1 or 2 or 4 or 8 or 16)) throw new InvalidDataException($"Unsupported PNG (depth {bitDepth}, interlace {interlace})");
        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException($"Color type {colorType}") };

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        int bitsPerPixel = channels * bitDepth;
        int stride = (width * bitsPerPixel + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8); // filter step in bytes
        var raw = new byte[height * (stride + 1)];
        z.ReadExactly(raw);

        var pixels = new byte[height * stride];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            var line = raw.AsSpan(y * (stride + 1) + 1, stride);
            var cur = pixels.AsSpan(y * stride, stride);
            var prev = y > 0 ? pixels.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? cur[x - bpp] : 0;
                int b = y > 0 ? prev[x] : 0;
                int c = x >= bpp && y > 0 ? prev[x - bpp] : 0;
                int v = line[x];
                cur[x] = (byte)(filter switch
                {
                    1 => v + a,
                    2 => v + b,
                    3 => v + (a + b) / 2,
                    4 => v + Paeth(a, b, c),
                    _ => v,
                });
            }
        }

        // One sample (channel value) at sample index s of row y, scaled to 0..255 (palette indices are left as is).
        int Sample(int y, int s)
        {
            var row = pixels.AsSpan(y * stride, stride);
            switch (bitDepth)
            {
                case 8: return row[s];
                case 16: return row[s * 2];
                default:
                    int bit = s * bitDepth;
                    int value = (row[bit >> 3] >> (8 - bitDepth - (bit & 7))) & ((1 << bitDepth) - 1);
                    return colorType == 3 ? value : value * 255 / ((1 << bitDepth) - 1);
            }
        }

        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int s = x * channels, d = (y * width + x) * 4;
            switch (colorType)
            {
                case 6: for (int k = 0; k < 4; k++) rgba[d + k] = (byte)Sample(y, s + k); break;
                case 2: for (int k = 0; k < 3; k++) rgba[d + k] = (byte)Sample(y, s + k); rgba[d + 3] = 255; break;
                case 0: rgba[d] = rgba[d + 1] = rgba[d + 2] = (byte)Sample(y, s); rgba[d + 3] = 255; break;
                case 4: rgba[d] = rgba[d + 1] = rgba[d + 2] = (byte)Sample(y, s); rgba[d + 3] = (byte)Sample(y, s + 1); break;
                case 3:
                    int idx = Sample(y, s);
                    rgba[d] = palette![idx * 3]; rgba[d + 1] = palette[idx * 3 + 1]; rgba[d + 2] = palette[idx * 3 + 2];
                    rgba[d + 3] = paletteAlpha != null && idx < paletteAlpha.Length ? paletteAlpha[idx] : (byte)255;
                    break;
            }
        }
        return (rgba, width, height);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
