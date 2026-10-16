using System.Collections.Concurrent;
using System.IO.Compression;
using System.Numerics;

namespace Realism.Minecraft;

/// <summary>
/// Chunks from a Minecraft save, loaded on demand and dropped when far away (or, in live mode with no save
/// folder, pushed in by the Minecraft bridge). Region files are cached so
/// neighbouring chunks don't re-read them. Safe for concurrent reads while one thread loads and unloads.
/// </summary>
public sealed class World(string? worldDir)
{
    readonly ConcurrentDictionary<long, Chunk> _chunks = new();
    readonly ConcurrentDictionary<long, byte> _missing = new(); // not generated / unreadable: don't retry
    readonly ConcurrentDictionary<long, byte[]?> _regions = new();
    readonly string? _regionDir = worldDir == null ? null : Path.Combine(worldDir, "region");

    /// <summary>Live mode: chunks come from a running Minecraft instead of a save.</summary>
    public bool IsLive => _regionDir == null;

    /// <summary>Live mode: Minecraft's block models and atlas (blocks that aren't landscape are drawn with them).</summary>
    public BlockModels? Models { get; set; }

    public void Put(Chunk c) => _chunks[Key(c.X, c.Z)] = c;
    public void Remove(int cx, int cz) => _chunks.TryRemove(Key(cx, cz), out _);
    public void Clear()
    {
        _chunks.Clear();
        _edits.Clear();
        _editsByChunk.Clear();
    }

    public static long Key(int cx, int cz) => ((long)cx << 32) | (uint)cz;
    public static (int cx, int cz) Unkey(long key) => ((int)(key >> 32), (int)key);

    public int LoadedCount => _chunks.Count;

    public Chunk? GetChunk(int cx, int cz) => _chunks.GetValueOrDefault(Key(cx, cz));

    /// <summary>True when the chunk and all 8 neighbours are loaded, so it can be meshed without seams.</summary>
    public bool HasNeighbours(Chunk c)
    {
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
            if (!_chunks.ContainsKey(Key(c.X + dx, c.Z + dz))) return false;
        return true;
    }

    // ---- live edits ----

    readonly ConcurrentDictionary<long, byte> _edits = new();          // block positions changed in play this session
    readonly ConcurrentDictionary<long, List<(int x, int y, int z)>> _editsByChunk = new();

    static long PosKey(int x, int y, int z) => ((long)(x & 0x3FFFFFF) << 38) | ((long)(z & 0x3FFFFFF) << 12) | (long)(y & 0xFFF);

    /// <summary>
    /// Records a block placed or dug in play (solid ground &lt;-&gt; open space). Edited blocks are drawn crisp and
    /// dug space is never smoothed over or hidden, so tunnels and builds look like what you actually did.
    /// </summary>
    public void MarkEdit(int x, int y, int z)
    {
        if (!_edits.TryAdd(PosKey(x, y, z), 0)) return;
        var list = _editsByChunk.GetOrAdd(Key(x >> 4, z >> 4), _ => new List<(int, int, int)>());
        lock (list) list.Add((x, y, z));
    }

    public bool IsEdited(int x, int y, int z) => !_edits.IsEmpty && _edits.ContainsKey(PosKey(x, y, z));

    /// <summary>Edits in the chunks overlapping the given block rectangle (copy; safe to use on any thread).</summary>
    public List<(int x, int y, int z)> EditsNear(int minX, int minZ, int maxX, int maxZ)
    {
        var result = new List<(int, int, int)>();
        if (_edits.IsEmpty) return result;
        for (int cz = minZ >> 4; cz <= maxZ >> 4; cz++)
        for (int cx = minX >> 4; cx <= maxX >> 4; cx++)
            if (_editsByChunk.TryGetValue(Key(cx, cz), out var list))
                lock (list) result.AddRange(list);
        return result;
    }

    /// <summary>Loads the chunk if needed. Returns false if it doesn't exist in the save.</summary>
    public bool EnsureLoaded(int cx, int cz)
    {
        long key = Key(cx, cz);
        if (_chunks.ContainsKey(key)) return true;
        if (_regionDir == null) return false; // live: only what Minecraft has sent
        if (_missing.ContainsKey(key)) return false;

        var file = _regions.GetOrAdd(Key(cx >> 5, cz >> 5), rk =>
        {
            var (rx, rz) = Unkey(rk);
            string path = Path.Combine(_regionDir, $"r.{rx}.{rz}.mca");
            if (!File.Exists(path)) return null;
            try
            {
                // Minecraft may have the file open (playing this world): share it rather than fail.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
                return bytes;
            }
            catch (IOException) { return null; }
        });
        Chunk? chunk = null;
        if (file != null)
        {
            try
            {
                var nbt = RegionFile.ReadChunk(file, cx & 31, cz & 31);
                chunk = nbt == null ? null : Chunk.FromNbt(nbt);
            }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            {
                Console.WriteLine($"  skipped corrupt chunk {cx},{cz}: {e.Message}");
            }
        }
        if (chunk == null)
        {
            _missing[key] = 0;
            return false;
        }
        _chunks[key] = chunk;
        return true;
    }

    /// <summary>Forget chunks (and region files) further than <paramref name="keepRadius"/> chunks from the centre.</summary>
    public void UnloadFarFrom(int centreCx, int centreCz, int keepRadius)
    {
        if (IsLive) return; // Minecraft decides what is loaded
        UnloadFar(centreCx, centreCz, keepRadius);
    }

    void UnloadFar(int centreCx, int centreCz, int keepRadius)
    {
        foreach (var key in _chunks.Keys)
        {
            var (cx, cz) = Unkey(key);
            if (Math.Max(Math.Abs(cx - centreCx), Math.Abs(cz - centreCz)) > keepRadius) _chunks.TryRemove(key, out _);
        }
        foreach (var key in _missing.Keys)
        {
            var (cx, cz) = Unkey(key);
            if (Math.Max(Math.Abs(cx - centreCx), Math.Abs(cz - centreCz)) > keepRadius) _missing.TryRemove(key, out _);
        }
        foreach (var key in _regions.Keys)
        {
            var (rx, rz) = Unkey(key);
            int dx = Math.Abs(rx * 32 + 16 - centreCx), dz = Math.Abs(rz * 32 + 16 - centreCz);
            if (Math.Max(dx, dz) > keepRadius + 48) _regions.TryRemove(key, out _);
        }
    }
}

/// <summary>What we need from level.dat: the world name and where the player is standing.</summary>
public sealed record LevelInfo(string Name, Vector3? PlayerPos, Vector3? Spawn)
{
    public static LevelInfo Load(string worldDir)
    {
        string path = Path.Combine(worldDir, "level.dat");
        if (!File.Exists(path)) return new LevelInfo(Path.GetFileName(worldDir), null, null);

        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var data = Nbt.Parse(ms.ToArray()).GetCompound("Data");
        if (data == null) return new LevelInfo(Path.GetFileName(worldDir), null, null);

        string name = data.GetString("LevelName") ?? Path.GetFileName(worldDir);
        Vector3? player = null;
        if (data.GetCompound("Player")?.GetList("Pos") is { Count: 3 } pos)
            player = new Vector3((float)NbtCompound.ToDouble(pos[0])!, (float)NbtCompound.ToDouble(pos[1])!, (float)NbtCompound.ToDouble(pos[2])!);

        Vector3? spawn = null;
        if (data.GetNumber("SpawnX") is long sx && data.GetNumber("SpawnZ") is long sz)
            spawn = new Vector3(sx, data.GetNumber("SpawnY") ?? 64, sz);
        else if (data.GetCompound("spawn")?.GetIntArray("pos") is { Length: 3 } sp)
            spawn = new Vector3(sp[0], sp[1], sp[2]);

        return new LevelInfo(name, player, spawn);
    }
}
