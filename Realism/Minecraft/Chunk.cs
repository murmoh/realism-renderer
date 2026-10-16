using System.Numerics;

namespace Realism.Minecraft;

/// <summary>A 16-wide column of blocks, stored as material classes (see <see cref="Mat"/>).</summary>
public sealed class Chunk
{
    public readonly int X, Z;
    readonly int _minSection;
    readonly byte[]?[] _sections; // 4096 materials each, index y*256 + z*16 + x; null = all air
    readonly ushort[]?[]? _states; // live mode: Minecraft block state ids, same layout (null = no data)

    /// <summary>Highest block that isn't air or a small plant, per column (index z*16 + x).</summary>
    public readonly int[] TopAny = new int[256];

    /// <summary>Highest terrain-solid block per column (index z*16 + x).</summary>
    public readonly int[] TopTerrain = new int[256];

    public int MinY => _minSection * 16;
    public int MaxY => (_minSection + _sections.Length) * 16;

    static readonly byte[][] Uniform = Enumerable.Range(0, 256).Select(m => Enumerable.Repeat((byte)m, 4096).ToArray()).ToArray();

    Chunk(int x, int z, int minSection, byte[]?[] sections, ushort[]?[]? states = null)
    {
        X = x;
        Z = z;
        _minSection = minSection;
        _sections = sections;
        _states = states;
        ComputeHeights();
    }

    public byte Get(int lx, int y, int lz)
    {
        int s = (y >> 4) - _minSection;
        if ((uint)s >= (uint)_sections.Length) return y < MinY ? Mat.Stone : Mat.Air;
        var sec = _sections[s];
        return sec == null ? Mat.Air : sec[((y & 15) << 8) | (lz << 4) | lx];
    }

    /// <summary>Minecraft's block state id at a position (live mode), 0 when unknown.</summary>
    public int GetState(int lx, int y, int lz)
    {
        int s = (y >> 4) - _minSection;
        if (_states == null || (uint)s >= (uint)_states.Length) return 0;
        var sec = _states[s];
        return sec == null ? 0 : sec[((y & 15) << 8) | (lz << 4) | lx];
    }

    /// <summary>A chunk built from live data (sections of 4096 materials, null = all air; optional state ids).</summary>
    public static Chunk FromSections(int x, int z, int minSection, byte[]?[] sections, ushort[]?[]? states = null) => new(x, z, minSection, sections, states);

    /// <summary>A copy of this chunk with one block changed. Only the touched section is copied, so it is
    /// safe to swap in while mesher threads are still reading the old one.</summary>
    public Chunk WithBlock(int lx, int y, int lz, byte material, int state = 0)
    {
        int s = (y >> 4) - _minSection;
        if ((uint)s >= (uint)_sections.Length) return this;
        int i = ((y & 15) << 8) | (lz << 4) | lx;
        var sections = (byte[]?[])_sections.Clone();
        var section = sections[s] == null ? new byte[4096] : (byte[])sections[s]!.Clone();
        section[i] = material;
        sections[s] = section;
        var states = _states;
        if (states != null)
        {
            states = (ushort[]?[])states.Clone();
            var st = states[s] == null ? new ushort[4096] : (ushort[])states[s]!.Clone();
            st[i] = (ushort)state;
            states[s] = st;
        }
        return new Chunk(X, Z, _minSection, sections, states);
    }

    void ComputeHeights()
    {
        for (int lz = 0; lz < 16; lz++)
        for (int lx = 0; lx < 16; lx++)
        {
            int any = int.MinValue, terrain = MinY - 1;
            for (int y = MaxY - 1; y >= MinY; y--)
            {
                // Skip empty sections quickly.
                if ((y & 15) == 15 && _sections[(y >> 4) - _minSection] == null) { y -= 15; continue; }
                byte m = Get(lx, y, lz);
                if (m is Mat.Air or Mat.Plant or Mat.TallGrass or Mat.Flower) continue;
                if (any == int.MinValue) any = y;
                if (Mat.IsTerrain(m)) { terrain = y; break; }
            }
            TopAny[lz * 16 + lx] = Math.Max(any, terrain);
            TopTerrain[lz * 16 + lx] = terrain;
        }
    }

    /// <summary>Parses chunk NBT. Supports 1.16+ layouts (both the pre-1.18 "Level" wrapper and the modern flat form).</summary>
    public static Chunk? FromNbt(NbtCompound root)
    {
        var level = root.GetCompound("Level") ?? root;
        var status = level.GetString("Status") ?? root.GetString("Status");
        if (status != null && !status.EndsWith("full")) return null; // still generating; terrain may be incomplete

        if (level.GetNumber("xPos") is not long cx || level.GetNumber("zPos") is not long cz) return null;
        var sectionList = level.GetList("sections") ?? level.GetList("Sections");
        if (sectionList == null || sectionList.Count == 0) return null;

        var parsed = new List<(int y, byte[] data)>();
        foreach (NbtCompound sec in sectionList.Cast<NbtCompound>())
        {
            if (sec.GetNumber("Y") is not long y) continue;
            var states = sec.GetCompound("block_states");
            var palette = states?.GetList("palette") ?? sec.GetList("Palette");
            var data = states?.GetLongArray("data") ?? sec.GetLongArray("BlockStates");
            if (palette == null || palette.Count == 0) continue;
            parsed.Add(((int)y, Decode(palette, data)));
        }
        if (parsed.Count == 0) return null;

        int min = parsed.Min(p => p.y), max = parsed.Max(p => p.y);
        var sections = new byte[]?[max - min + 1];
        foreach (var (y, data) in parsed)
            sections[y - min] = ReferenceEquals(data, Uniform[Mat.Air]) ? null : data;
        return new Chunk((int)cx, (int)cz, min, sections);
    }

    static byte[] Decode(List<object> palette, long[]? data)
    {
        var mats = new byte[palette.Count];
        for (int i = 0; i < mats.Length; i++)
        {
            var entry = (NbtCompound)palette[i];
            string name = entry.GetString("Name") ?? "minecraft:air";
            string? level = entry.GetCompound("Properties")?.GetString("level");
            mats[i] = name == "minecraft:water" && level != null ? Mat.WaterLevel(int.Parse(level))
                : name == "minecraft:lava" && level != null ? Mat.LavaLevel(int.Parse(level))
                : Blocks.Classify(name);
        }

        if (mats.Length == 1 || data == null || data.Length == 0) return Uniform[mats[0]];

        int bits = Math.Max(4, 32 - BitOperations.LeadingZeroCount((uint)(mats.Length - 1)));
        int perLong = 64 / bits;
        ulong mask = (1UL << bits) - 1;
        var result = new byte[4096];
        for (int i = 0; i < 4096; i++)
        {
            int li = i / perLong;
            if (li >= data.Length) break;
            int idx = (int)(((ulong)data[li] >> (i % perLong * bits)) & mask);
            result[i] = idx < mats.Length ? mats[idx] : Mat.Air;
        }
        return result;
    }
}
