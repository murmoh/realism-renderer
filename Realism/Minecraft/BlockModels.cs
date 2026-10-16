using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Numerics;

namespace Realism.Minecraft;

/// <summary>One face of a Minecraft block model, in block units, with atlas UVs.</summary>
public readonly record struct ModelQuad(Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3, Vector2 Uv0, Vector2 Uv1, Vector2 Uv2, Vector2 Uv3,
    int CullFace, bool Shade, int Emission, uint Tint)
{
    /// <summary>Minecraft's Direction order: down, up, north (-z), south (+z), west (-x), east (+x).</summary>
    public static readonly (int x, int y, int z)[] Directions = [(0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1), (-1, 0, 0), (1, 0, 0)];
}

/// <summary>Minecraft's block texture atlas (RGBA, mip level 0).</summary>
public sealed record AtlasImage(byte[] Rgba, int Width, int Height);

/// <summary>A block state's model as Minecraft bakes it (see MinecraftMod/.../BlockModelExport.java).</summary>
public sealed class BlockModel
{
    public required ModelQuad[] Quads;
    public bool Translucent, OpaqueCube;
    public int Luminance;

    /// <summary>Atlas corner and width of the texture on a side face (columns like deepslate differ on top).</summary>
    public (Vector2 corner, float size) SideTexture()
    {
        var q = Quads.FirstOrDefault(q => q.CullFace >= 2, Quads[0]);
        var min = Vector2.Min(Vector2.Min(q.Uv0, q.Uv1), Vector2.Min(q.Uv2, q.Uv3));
        var max = Vector2.Max(Vector2.Max(q.Uv0, q.Uv1), Vector2.Max(q.Uv2, q.Uv3));
        return (min, max.X - min.X);
    }
}

/// <summary>
/// Minecraft's block models and texture atlas, sent by the mod in live mode. Everything that isn't landscape is
/// drawn with these, so it looks exactly as in Minecraft. Filled on the bridge thread, read by mesher threads.
/// </summary>
public sealed class BlockModels
{
    readonly ConcurrentDictionary<int, BlockModel> _models = new();
    AtlasImage? _atlas;
    int _atlasVersion;

    public BlockModel? Get(int state) => _models.GetValueOrDefault(state);

    public AtlasImage? Atlas => Volatile.Read(ref _atlas);
    public int AtlasVersion => Volatile.Read(ref _atlasVersion);

    public void Clear() => _models.Clear();

    public void ReadModel(ReadOnlySpan<byte> d)
    {
        int p = 0;
        int Int(ReadOnlySpan<byte> s) { int v = BinaryPrimitives.ReadInt32LittleEndian(s[p..]); p += 4; return v; }
        float F(ReadOnlySpan<byte> s) { float v = BinaryPrimitives.ReadSingleLittleEndian(s[p..]); p += 4; return v; }
        int id = Int(d), flags = Int(d), luminance = Int(d), count = Int(d);
        var quads = new ModelQuad[count];
        Span<Vector3> pos = stackalloc Vector3[4];
        Span<Vector2> uv = stackalloc Vector2[4];
        for (int q = 0; q < count; q++)
        {
            int cull = d[p] == 255 ? -1 : d[p];
            bool shade = d[p + 1] != 0;
            int emission = d[p + 2];
            p += 4;
            uint tint = (uint)Int(d);
            for (int k = 0; k < 4; k++)
            {
                pos[k] = new Vector3(F(d), F(d), F(d));
                uv[k] = new Vector2(F(d), F(d));
            }
            quads[q] = new ModelQuad(pos[0], pos[1], pos[2], pos[3], uv[0], uv[1], uv[2], uv[3], cull, shade, emission, tint);
        }
        _models[id] = new BlockModel { Quads = quads, Translucent = (flags & 1) != 0, OpaqueCube = (flags & 2) != 0, Luminance = luminance };
    }

    public void ReadAtlas(ReadOnlySpan<byte> d)
    {
        int w = BinaryPrimitives.ReadInt32LittleEndian(d), h = BinaryPrimitives.ReadInt32LittleEndian(d[4..]);
        Volatile.Write(ref _atlas, new AtlasImage(d.Slice(8, w * h * 4).ToArray(), w, h));
        Interlocked.Increment(ref _atlasVersion);
    }
}
