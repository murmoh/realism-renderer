using System.Numerics;
using Realism.Minecraft;

namespace Realism.Meshing;

/// <summary>
/// The cave system the camera is in: every open (non-terrain) cell under the ground surface that connects to the
/// camera's cell, flood-filled out to the view radius. The mesher hides deep caves everywhere else (they can't be
/// seen and would cost a lot of geometry), but shows all of this one, however far it reaches.
/// </summary>
public sealed class CaveReveal
{
    const int MaxCells = 8_000_000;

    /// <summary>Open cells of one chunk column, one bit per block from <see cref="MinY"/> up.</summary>
    sealed class Bits(int minY, int height)
    {
        public readonly int MinY = minY, Height = height;
        public readonly ulong[] Words = new ulong[(16 * 16 * height + 63) / 64];
        public int Lowest = int.MaxValue;

        public int Index(int lx, int y, int lz) => ((y - MinY) * 16 + lz) * 16 + lx;
        public bool Get(int i) => (Words[i >> 6] & (1UL << (i & 63))) != 0;
        public void Set(int i) => Words[i >> 6] |= 1UL << (i & 63);
    }

    readonly Dictionary<long, Bits> _chunks = new();

    public int CellCount { get; private set; }

    /// <summary>How far (horizontally, in blocks) the revealed cave reaches from the camera.</summary>
    public int Reach { get; private set; }

    public bool Contains(int x, int y, int z)
    {
        if (!_chunks.TryGetValue(World.Key(x >> 4, z >> 4), out var b) || y < b.MinY || y >= b.MinY + b.Height) return false;
        return b.Get(b.Index(x & 15, y, z & 15));
    }

    /// <summary>Lowest revealed block in the chunks overlapping the block range (int.MaxValue if none).</summary>
    public int LowestIn(int minX, int minZ, int maxX, int maxZ)
    {
        int lowest = int.MaxValue;
        for (int cz = minZ >> 4; cz <= maxZ >> 4; cz++)
        for (int cx = minX >> 4; cx <= maxX >> 4; cx++)
            if (_chunks.TryGetValue(World.Key(cx, cz), out var b)) lowest = Math.Min(lowest, b.Lowest);
        return lowest;
    }

    /// <summary>Fingerprint of one chunk's revealed cells (0 = none): a chunk whose fingerprint changes needs re-meshing.</summary>
    public ulong Fingerprint(long chunkKey)
    {
        if (!_chunks.TryGetValue(chunkKey, out var b)) return 0;
        ulong h = 14695981039346656037UL ^ (ulong)b.MinY;
        foreach (ulong w in b.Words) h = (h ^ w) * 1099511628211UL;
        return h | 1;
    }

    public IEnumerable<long> Chunks => _chunks.Keys;

    /// <summary>
    /// Flood-fills from the camera. Open = loaded, not terrain (air, water, lava, plants, builds) and below the
    /// column's ground surface, so the fill stops at cave mouths instead of spilling over the landscape.
    /// Returns null when the camera isn't in a cave.
    /// </summary>
    public static CaveReveal? Compute(World world, Vector3 camera, int radius)
    {
        int sx = (int)MathF.Floor(camera.X), sy = (int)MathF.Floor(camera.Y), sz = (int)MathF.Floor(camera.Z);
        var reveal = new CaveReveal();
        Chunk? lastChunk = null;
        long lastKey = long.MinValue;
        Bits? lastBits = null;

        bool Open(int x, int y, int z, out Bits? bits, out int index)
        {
            bits = null;
            index = 0;
            int dx = x - sx, dz = z - sz;
            if (dx * dx + dz * dz > radius * radius) return false;
            long key = World.Key(x >> 4, z >> 4);
            if (key != lastKey)
            {
                lastKey = key;
                lastChunk = world.GetChunk(x >> 4, z >> 4);
                lastBits = null;
                if (lastChunk != null && !reveal._chunks.TryGetValue(key, out lastBits))
                    reveal._chunks[key] = lastBits = new Bits(lastChunk.MinY, lastChunk.MaxY - lastChunk.MinY);
            }
            if (lastChunk == null || y < lastChunk.MinY || y >= lastChunk.MaxY) return false;
            int lx = x & 15, lz = z & 15;
            if (y >= lastChunk.TopTerrain[lz * 16 + lx] || Mat.IsTerrain(lastChunk.Get(lx, y, lz))) return false;
            bits = lastBits!;
            index = bits.Index(lx, y, lz);
            return !bits.Get(index);
        }

        // Start in the camera's cell, or the nearest open cell (the eye can sit inside smoothed-away rock).
        (int x, int y, int z)? start = null;
        for (int r = 0; r <= 2 && start == null; r++)
        for (int dy = -r; dy <= r && start == null; dy++)
        for (int dz = -r; dz <= r && start == null; dz++)
        for (int dx = -r; dx <= r && start == null; dx++)
            if (Open(sx + dx, sy + dy, sz + dz, out _, out _)) start = (sx + dx, sy + dy, sz + dz);
        if (start is not { } s) return null;

        var queue = new Queue<(int x, int y, int z)>();
        Open(s.x, s.y, s.z, out var b0, out int i0);
        b0!.Set(i0);
        b0.Lowest = Math.Min(b0.Lowest, s.y);
        queue.Enqueue(s);
        int count = 1, reach2 = 0;
        ReadOnlySpan<(int, int, int)> steps = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];
        while (queue.Count > 0 && count < MaxCells)
        {
            var (x, y, z) = queue.Dequeue();
            foreach (var (dx, dy, dz) in steps)
            {
                int nx = x + dx, ny = y + dy, nz = z + dz;
                if (!Open(nx, ny, nz, out var bits, out int index)) continue;
                bits!.Set(index);
                if (ny < bits.Lowest) bits.Lowest = ny;
                queue.Enqueue((nx, ny, nz));
                count++;
                reach2 = Math.Max(reach2, (nx - sx) * (nx - sx) + (nz - sz) * (nz - sz));
            }
        }
        // Columns the fill only peeked at hold nothing.
        foreach (var key in reveal._chunks.Where(kv => kv.Value.Lowest == int.MaxValue).Select(kv => kv.Key).ToList())
            reveal._chunks.Remove(key);
        reveal.CellCount = count;
        reveal.Reach = (int)MathF.Sqrt(reach2);
        return reveal;
    }
}
