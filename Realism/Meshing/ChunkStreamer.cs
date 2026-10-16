using System.Collections.Concurrent;
using System.Numerics;
using Realism.Minecraft;

namespace Realism.Meshing;

/// <summary>
/// Background loader: keeps the chunks around the camera loaded and meshed, nearest first.
/// Finished meshes are queued for the render thread to upload (GL calls must stay on that thread).
/// </summary>
public sealed class ChunkStreamer : IDisposable
{
    // One chunk per meshing thread per round: rounds stay short, so a block you just broke or placed (re-meshed
    // nearest first) never waits behind a long batch of distant terrain.
    static readonly int BatchSize = Math.Max(2, Environment.ProcessorCount / 2 - 1);

    readonly World _world;
    readonly int _radius;
    readonly Thread _thread;
    readonly ConcurrentQueue<ChunkMeshes> _ready = new();
    readonly HashSet<long> _done = new(); // meshed or skipped; worker thread only
    readonly object _camLock = new();
    Vector3 _camera;
    volatile bool _stop;
    volatile bool _idle;

    public ChunkStreamer(World world, int radius, Vector3 camera)
    {
        _world = world;
        _radius = radius;
        _camera = camera;
        _thread = new Thread(Run) { IsBackground = true, Name = "Chunk streamer", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Called on a mesher thread for every finished chunk (live mode: sends smooth collision).</summary>
    public Action<ChunkMeshes>? Meshed;

    public int Radius => _radius;

    public World World => _world;

    /// <summary>True when everything in range is meshed and handed over.</summary>
    public bool Idle => _idle && _ready.IsEmpty;

    public void SetCamera(Vector3 p)
    {
        lock (_camLock) _camera = p;
    }

    readonly ConcurrentQueue<long> _dirty = new();
    CaveReveal? _reveal;                         // the cave system the camera is in (null above ground)
    Dictionary<long, ulong> _revealPrints = new();
    Vector3 _revealCam = new(float.NaN);
    int _revealLoaded;
    readonly System.Diagnostics.Stopwatch _revealAge = System.Diagnostics.Stopwatch.StartNew();
    volatile bool _reset;
    int _generation;

    /// <summary>Bumped when everything was thrown away (new live session); the renderer drops all its chunks.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>A chunk changed (or arrived): re-mesh it and its neighbours, whose padding overlaps it.</summary>
    public void MarkDirty(int cx, int cz) => _dirty.Enqueue(World.Key(cx, cz));

    /// <summary>A chunk was unloaded: tell the renderer to drop its meshes.</summary>
    public void Remove(int cx, int cz) => _ready.Enqueue(new ChunkMeshes { X = cx, Z = cz });

    /// <summary>Forget everything (the live world changed).</summary>
    public void ResetAll()
    {
        _reset = true;
        Interlocked.Increment(ref _generation);
        while (_ready.TryDequeue(out _)) { }
    }

    public bool TryTake(out ChunkMeshes mesh) => _ready.TryDequeue(out mesh!);

    void Run()
    {
        while (!_stop)
        {
            Vector3 cam;
            lock (_camLock) cam = _camera;
            int ccx = (int)MathF.Floor(cam.X) >> 4, ccz = (int)MathF.Floor(cam.Z) >> 4;

            if (_reset) { _reset = false; _done.Clear(); _dirty.Clear(); }
            UpdateCaveReveal(cam);

            while (_dirty.TryDequeue(out long dk))
            {
                _idle = false;
                var (dcx, dcz) = World.Unkey(dk);
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    _done.Remove(World.Key(dcx + dx, dcz + dz));
            }

            // Forget what's far away; it will be re-meshed if the camera comes back.
            _done.RemoveWhere(k =>
            {
                var (cx, cz) = World.Unkey(k);
                return (cx - ccx) * (cx - ccx) + (cz - ccz) * (cz - ccz) > (_radius + 2) * (_radius + 2);
            });
            _world.UnloadFarFrom(ccx, ccz, _radius + 4);

            var batch = new List<(int cx, int cz)>();
            for (int r = 0; r <= _radius && batch.Count < BatchSize; r++)
            for (int dz = -r; dz <= r && batch.Count < BatchSize; dz++)
            for (int dx = -r; dx <= r && batch.Count < BatchSize; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r || dx * dx + dz * dz > _radius * _radius) continue;
                if (!_done.Contains(World.Key(ccx + dx, ccz + dz))) batch.Add((ccx + dx, ccz + dz));
            }

            if (batch.Count == 0)
            {
                _idle = true;
                Thread.Sleep(50);
                continue;
            }
            _idle = false;

            var meshable = new List<Chunk>();
            foreach (var (cx, cz) in batch)
            {
                _done.Add(World.Key(cx, cz));
                bool ok = true;
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    ok &= _world.EnsureLoaded(cx + dx, cz + dz);
                if (ok) meshable.Add(_world.GetChunk(cx, cz)!);
            }
            // Half the CPU threads, below normal priority: Minecraft (game, integrated server) always comes first, so
            // streaming new terrain while you move never makes the game itself stutter.
            Parallel.ForEach(meshable, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2 - 1) },
                c =>
                {
                    var priority = Thread.CurrentThread.Priority;
                    Thread.CurrentThread.Priority = ThreadPriority.Normal;
                    try
                    {
                        if (Realism.Live.MinecraftBridge.TraceTimes) Console.WriteLine($"[trace] {Environment.TickCount64 % 100000} meshing {c.X},{c.Z} (batch {meshable.Count})");
                        var m = Mesher.Build(_world, c, _reveal);
                        if (_world.GetChunk(c.X, c.Z) == null) return; // unloaded meanwhile (live chunks can vanish)
                        _ready.Enqueue(m);
                        Meshed?.Invoke(m);
                    }
                    catch (NullReferenceException) { } // a neighbour was unloaded mid-mesh; it will be re-marked dirty
                    finally { Thread.CurrentThread.Priority = priority; }
                });

            // Don't run far ahead of the uploader.
            while (_ready.Count > 64 && !_stop) Thread.Sleep(10);
        }
    }

    /// <summary>
    /// Underground: flood-fill the cave the camera is in (again after moving a few blocks, or every couple of
    /// seconds as chunks stream in) and re-mesh the chunks whose part of it changed. Walking around inside one
    /// cave system re-meshes nothing; leaving it (or the surface) fills its caves back in.
    /// </summary>
    void UpdateCaveReveal(Vector3 cam)
    {
        int bx = (int)MathF.Floor(cam.X), bz = (int)MathF.Floor(cam.Z);
        var here = _world.GetChunk(bx >> 4, bz >> 4);
        bool underground = here != null && cam.Y < here.TopTerrain[(bz & 15) * 16 + (bx & 15)] - 1;
        if (!underground && _reveal == null) return;
        bool moved = !(Vector3.Distance(cam, _revealCam) < 4);
        // New chunks loaded (the cave may continue into them): fill again, or caves stay sealed until you move.
        bool grew = _world.LoadedCount != _revealLoaded && _revealAge.Elapsed.TotalSeconds >= 1; // at most once a second
        if (underground && !moved && !grew && _revealAge.Elapsed.TotalSeconds < 2) return;
        _revealLoaded = _world.LoadedCount;
        if (!underground && _reveal == null) return;

        _idle = false; // busy from here: re-meshing follows (nobody may think the view has settled meanwhile)
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var reveal = underground ? CaveReveal.Compute(_world, cam, _radius * 16) : null;
        _revealCam = cam;
        _revealAge.Restart();
        var prints = new Dictionary<long, ulong>();
        if (reveal != null) foreach (long k in reveal.Chunks) prints[k] = reveal.Fingerprint(k);

        var changed = new List<long>();
        foreach (var (k, f) in prints) if (!_revealPrints.TryGetValue(k, out var old) || old != f) changed.Add(k);
        foreach (var k in _revealPrints.Keys) if (!prints.ContainsKey(k)) changed.Add(k);
        _reveal = reveal;
        _revealPrints = prints;
        foreach (long k in changed)
        {
            var (cx, cz) = World.Unkey(k);
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                _done.Remove(World.Key(cx + dx, cz + dz));
        }
        if (changed.Count > 0)
            Console.WriteLine($"Cave: {reveal?.CellCount ?? 0} open cells revealed (reaching {reveal?.Reach ?? 0} blocks) in {timer.ElapsedMilliseconds} ms, re-meshing {changed.Count} chunks");
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join();
    }
}
