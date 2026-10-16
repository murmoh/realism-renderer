using System.Globalization;
using System.Numerics;
using Realism.Meshing;
using Realism.Minecraft;
using Realism.Rendering;

if (args.Contains("--compare")) return Comparison.Run(args); // comparison video from recordings (see Rendering/Comparison.cs)

// Usage: dotnet run -- [world name or path | --live] [--radius N] [--time 0..1] [--cam x,y,z,yaw,pitch] [--screenshot file.png]
string? worldArg = null, screenshot = null, camArg = null, filmScript = null, filmOut = null, filmPreview = null;
int radius = 12, weather = 0;
float timeOfDay = 0.3f;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--radius": radius = int.Parse(args[++i]); break;
        case "--time": timeOfDay = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--cam": camArg = args[++i]; break;
        case "--screenshot": screenshot = args[++i]; break;
        case "--cinematic": filmScript = args[++i]; break;
        case "--out": filmOut = args[++i]; break;
        case "--preview": filmPreview = args[++i]; break;
        case "--blocks": break;
        case "--live": break;
        case "--no-launch": break;
        case "--control-test": break;
        case "--climb-test": break;
        case "--find-gaps": break;
        case "--gameplay": i++; break;
        case "--find-crystals": break;
        case "--find-ores": break;
        case "--map": break;
        case "--bench-mesh": break;
        case "--probe": break;
        case "--build-test": break;
        case "--third-person": break;
        case "--weather": weather = Array.IndexOf(new[] { "clear", "overcast", "rain" }, args[++i]); break;
        default: worldArg = args[i]; break;
    }
}

if (args.Contains("--live"))
{
    // Live sync: the world, player, time and weather come from a running Minecraft with the bridge mod.
    var liveWorld = new World(null);
    using var bridge = new Realism.Live.MinecraftBridge(liveWorld);
    var start = bridge.ReadPlayer();
    var liveCam = start.InWorld ? start.Eye : new Vector3(0, 100, 0);
    Console.WriteLine($"Live mode: reading {Realism.Live.BridgeProtocol.FilePath}");
    // The diagnostics drive the player, so they refuse to run against a game that was already open, unless
    // PTR_BRIDGE_DIR points at the isolated test client (its own world copy).
    bool alreadyRunning = bridge.MinecraftRunning && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PTR_BRIDGE_DIR"));
    if (alreadyRunning) Console.WriteLine("Minecraft is already running.");
    else if (!args.Contains("--no-launch")) LaunchMinecraft(worldArg);
    else Console.WriteLine("Waiting for Minecraft (start it with the PassThrough Realism Bridge mod)...");
    using var liveStreamer = new ChunkStreamer(liveWorld, radius, liveCam);
    liveStreamer.Meshed = bridge.SendCollision;
    bridge.Start(liveStreamer);
    if (args.Contains("--build-test"))
    {
        // Diagnostic: dig into the ground ahead, then place blocks from hotbar slot 1, like a player would.
        if (alreadyRunning) { Console.WriteLine("Build test refused: Minecraft is already running. Close it first; the test starts its own."); return 1; }
        var drain = new Thread(() => { while (true) { while (liveStreamer.TryTake(out _)) { } Thread.Sleep(20); } }) { IsBackground = true };
        drain.Start();
        for (int i = 0; i < 600 && !bridge.ReadPlayer().InWorld; i++) Thread.Sleep(500);
        Thread.Sleep(6000);
        var s0 = bridge.ReadPlayer();
        var input = new Realism.Live.ControlInput { Active = true, Yaw = s0.Yaw, Pitch = 55 };
        void Hold(int ms) { for (int t = 0; t < ms; t += 50) { bridge.WriteInput(input); Thread.Sleep(50); } }
        Hold(500);
        var tgt = bridge.Target;
        Console.WriteLine($"Build test: aiming at {(tgt.has ? $"{tgt.x},{tgt.y},{tgt.z}" : "nothing")}");
        input.Keys = Realism.Live.BridgeProtocol.KeyAttack; input.AttackClicks = 1;
        int maxStage = -1;
        for (int t = 0; t < 4000; t += 50) { bridge.WriteInput(input); Thread.Sleep(50); maxStage = Math.Max(maxStage, bridge.Target.stage); }
        input.Keys = 0;
        Hold(400);
        bridge.SendInputEvent(0, 49, 0, 1, 0); bridge.SendInputEvent(0, 49, 0, 0, 0); // key "1": hotbar slot 1
        Hold(400);
        for (int n = 0; n < 3; n++)
        {
            input.Pitch = 40 + n * 8;
            Hold(300);
            input.UseClicks++;
            Hold(400);
        }
        input.Active = false;
        bridge.WriteInput(input);
        Thread.Sleep(1500);
        var s1 = bridge.ReadPlayer();
        int ex = (int)MathF.Floor(s1.Eye.X), ez = (int)MathF.Floor(s1.Eye.Z);
        var near = liveWorld.EditsNear(ex - 6, ez - 6, ex + 6, ez + 6);
        Console.WriteLine($"Build test: highest mining stage {maxStage}, {bridge.BlocksReceived} block changes, edits recorded near player: {near.Count} " +
                          string.Join(" ", near.Take(8).Select(e => $"({e.x},{e.y},{e.z})")));
        return 0;
    }

    if (args.Contains("--climb-test"))
    {
        // Diagnostic: walk straight at the highest ground nearby for 8 s and log progress (finds where you get stuck).
        if (alreadyRunning) { Console.WriteLine("Climb test refused: Minecraft is already running. Close it first; the test starts its own."); return 1; }
        var drain = new Thread(() => { while (true) { while (liveStreamer.TryTake(out _)) { } Thread.Sleep(20); } }) { IsBackground = true };
        drain.Start();
        for (int i = 0; i < 600 && !bridge.ReadPlayer().InWorld; i++) Thread.Sleep(500);
        Thread.Sleep(6000);
        var origin = bridge.ReadPlayer();
        int px = (int)MathF.Floor(origin.Eye.X), pz = (int)MathF.Floor(origin.Eye.Z);
        (int x, int z, int h) best = (px, pz, int.MinValue);
        // REALISM_CLIMB_CAVE: aim at the highest cave floor 2..6 blocks above the feet (a slope to walk up), not the surface.
        bool cave = Environment.GetEnvironmentVariable("REALISM_CLIMB_CAVE") != null;
        int feet = (int)MathF.Floor(origin.FeetY);
        for (int dz = -24; dz <= 24; dz++)
        for (int dx = -24; dx <= 24; dx++)
        {
            var c = liveWorld.GetChunk((px + dx) >> 4, (pz + dz) >> 4);
            if (c == null || dx * dx + dz * dz < 36) continue;
            int lx = (px + dx) & 15, lz = (pz + dz) & 15;
            int h = c.TopTerrain[lz * 16 + lx];
            if (cave)
            {
                h = int.MinValue;
                for (int y = feet + 6; y >= feet - 6; y--)
                    if (Mat.IsTerrain(c.Get(lx, y, lz)) && !Mat.IsTerrain(c.Get(lx, y + 1, lz)) && !Mat.IsTerrain(c.Get(lx, y + 2, lz))) { h = y; break; }
                if (h < feet + 1 || h > feet + 5 || dx * dx + dz * dz > 14 * 14) continue;
            }
            if (h > best.h && h < origin.FeetY + 14) best = (px + dx, pz + dz, h);
        }
        float climbYaw = MathF.Atan2(-(best.x + 0.5f - origin.Eye.X), best.z + 0.5f - origin.Eye.Z) * 180 / MathF.PI;
        Console.WriteLine($"Climb test: from {origin.Eye.X:0},{origin.FeetY:0.0},{origin.Eye.Z:0} toward {best.x},{best.h},{best.z} (climbYaw {climbYaw:0})");
        {
            var sm = Realism.Meshing.Mesher.Build(liveWorld, liveWorld.GetChunk(px >> 4, pz >> 4)!);
            int scol = (pz & 15) * 16 + (px & 15);
            Console.WriteLine($"  start chunk: {sm.CollisionFloors.Count} cave floors; at the start column: "
                + string.Join(" | ", sm.CollisionFloors.Where(f => f.col == scol).Select(f => $"{f.top}: {f.heights.Min() / 16f:0.00}..{f.heights.Max() / 16f:0.00}")));
        }
        var walk = new Realism.Live.ControlInput { Active = true, Keys = Realism.Live.BridgeProtocol.KeyForward, Yaw = climbYaw, Pitch = 0 };
        // REALISM_CLIMB_YAWS="0,90,180": walk 4 s in each of these directions in turn instead (repeatable A/B runs).
        var yaws = Environment.GetEnvironmentVariable("REALISM_CLIMB_YAWS")?.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        int steps = yaws != null ? yaws.Length * 80 : 160;
        float highest = origin.FeetY;
        var last = origin;
        int stuck = 0;
        var log = new System.Text.StringBuilder();
        for (int i = 0; i < steps; i++)
        {
            if (yaws != null) walk.Yaw = yaws[i / 80];
            bridge.WriteInput(walk);
            Thread.Sleep(50);
            if (i % 5 != 4) continue;
            var s = bridge.ReadPlayer();
            float moved = MathF.Sqrt((s.Eye.X - last.Eye.X) * (s.Eye.X - last.Eye.X) + (s.Eye.Z - last.Eye.Z) * (s.Eye.Z - last.Eye.Z));
            float? g = Realism.Meshing.TerrainProbe.SurfaceY(liveWorld, s.Eye.X, s.Eye.Z, s.FeetY);
            bool isStuck = moved < 0.3f;
            highest = Math.Max(highest, s.FeetY);
            if (isStuck) stuck++;
            log.Append($"\n  {s.Eye.X:0.00},{s.FeetY:0.00},{s.Eye.Z:0.00} smooth {(g is float gv ? gv.ToString("0.00") : "-")} moved {moved:0.00}{(isStuck ? "  STUCK" : "")}");
            last = s;
        }
        walk.Active = false;
        bridge.WriteInput(walk);
        Console.WriteLine($"Climb: {stuck} of {steps / 5} quarter-seconds stuck, climbed {last.FeetY - origin.FeetY:0.0} blocks (highest {highest - origin.FeetY:0.0})" + log);
        {
            // What collision the renderer sent for the columns around where the player ended up.
            int ex = (int)MathF.Floor(last.Eye.X), ez = (int)MathF.Floor(last.Eye.Z);
            var meshes = new Dictionary<long, Realism.Meshing.ChunkMeshes>();
            for (int dz = -1; dz <= 2; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int x = ex + dx, z = ez + dz;
                long key = World.Key(x >> 4, z >> 4);
                if (!meshes.TryGetValue(key, out var m)) meshes[key] = m = Realism.Meshing.Mesher.Build(liveWorld, liveWorld.GetChunk(x >> 4, z >> 4)!);
                int col = (z & 15) * 16 + (x & 15);
                var c = liveWorld.GetChunk(x >> 4, z >> 4)!;
                string blocks = string.Join(" ", Enumerable.Range((int)MathF.Floor(last.FeetY) - 2, 7).Select(y => $"{y}:{c.Get(x & 15, y, z & 15)}"));
                string hs = string.Join(",", Enumerable.Range(0, 16).Select(i => (m.CollisionHeights[col * 16 + i] / 16f).ToString("0.00")));
                Console.WriteLine($"  col {x},{z} top {m.CollisionTops[col]} [{blocks}] h {hs}");
                foreach (var f in m.CollisionFloors.Where(f => f.col == col && Math.Abs(f.top - last.FeetY) < 8))
                    Console.WriteLine($"      floor {f.top}: " + string.Join(",", f.heights.Select(v => (v / 16f).ToString("0.00"))));
            }
        }
        return 0;
    }

    if (args.Contains("--control-test"))
    {
        // Diagnostic: drive the player from here (hold W, turn 90°) and report what Minecraft did.
        // It moves the player and digs, so it never runs against a game that was already open (someone may be playing).
        // Consume finished meshes like the renderer does, so meshing (and collision sending) keeps going.
        var drain = new Thread(() => { while (true) { while (liveStreamer.TryTake(out _)) { } Thread.Sleep(20); } }) { IsBackground = true };
        drain.Start();
        if (alreadyRunning)
        {
            Console.WriteLine("Control test refused: Minecraft is already running. Close it first; the test starts its own.");
            return 1;
        }
        for (int i = 0; i < 600 && !(bridge.ReadPlayer().InWorld); i++) Thread.Sleep(500);
        Thread.Sleep(3000);
        var before = bridge.ReadPlayer();
        float targetYaw = Environment.GetEnvironmentVariable("REALISM_TEST_YAW") is { } ty ? float.Parse(ty, CultureInfo.InvariantCulture) : before.Yaw + 90;
        var input = new Realism.Live.ControlInput { Active = true, Keys = Realism.Live.BridgeProtocol.KeyForward, Yaw = targetYaw, Pitch = 0 };
        var trace = new System.Text.StringBuilder();
        for (int i = 0; i < 60; i++)
        {
            bridge.WriteInput(input);
            Thread.Sleep(50);
            if (i % 5 == 4)
            {
                var s = bridge.ReadPlayer();
                float? g = Realism.Meshing.TerrainProbe.SurfaceY(liveWorld, s.Eye.X, s.Eye.Z, s.FeetY);
                trace.Append($" feet {s.FeetY:0.00}/smooth {(g is float gv ? gv.ToString("0.00") : "-")}{(s.OnGround ? "" : " air")} |");
            }
        }
        Console.WriteLine("Walk trace:" + trace);
        input.Active = false;
        bridge.WriteInput(input);
        Thread.Sleep(300);
        var after = bridge.ReadPlayer();
        Console.WriteLine($"Control test: moved {Vector3.Distance(before.Eye, after.Eye):0.0} blocks, yaw {before.Yaw:0} -> {after.Yaw:0} (wanted {targetYaw:0})");
        // Mining: look straight down and hold attack; the block underneath should break and the player drop.
        var dig = new Realism.Live.ControlInput { Active = true, Keys = Realism.Live.BridgeProtocol.KeyAttack, Yaw = after.Yaw, Pitch = 90, AttackClicks = 1 };
        for (int i = 0; i < 200; i++) { bridge.WriteInput(dig); Thread.Sleep(50); }
        dig.Active = false;
        bridge.WriteInput(dig);
        Thread.Sleep(500);
        var dug = bridge.ReadPlayer();
        Console.WriteLine($"Mining test: player height {after.Eye.Y:0.0} -> {dug.Eye.Y:0.0} ({(dug.Eye.Y < after.Eye.Y - 0.5f ? "dug down" : "no change")})");
        {
            int fx = (int)MathF.Floor(dug.Eye.X), fz = (int)MathF.Floor(dug.Eye.Z);
            var col = liveWorld.GetChunk(fx >> 4, fz >> 4);
            Console.WriteLine($"Renderer column {fx},{fz}: " + string.Join(" ", Enumerable.Range((int)dug.FeetY - 5, 7).Select(y => $"{y}:{col?.Get(fx & 15, y, fz & 15)}")) + $"  blocks received {bridge.BlocksReceived}");
        }
        return 0;
    }
    // --gameplay script.txt --out film.mp4: record scripted live play (test client) to a video.
    var play = args.SkipWhile(a => a != "--gameplay").Skip(1).FirstOrDefault() is { } gp ? Gameplay.Load(gp) : null;
    new App(liveStreamer, new AppOptions(liveCam, 0, 0, start.InWorld ? start.TimeOfDay : timeOfDay, (radius - 1) * 16f, screenshot,
        "PassThrough Realism", 0, bridge, args.Contains("--third-person"), Play: play,
        FilmOut: args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault() ?? "gameplay.mp4")).Run();
    return 0;
}

string savesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "saves");
string? worldDir = worldArg switch
{
    null => Directory.Exists(savesDir)
        ? Directory.GetDirectories(savesDir).Where(d => Directory.Exists(Path.Combine(d, "region")))
            .OrderByDescending(Directory.GetLastWriteTime).FirstOrDefault()
        : null,
    _ when Directory.Exists(worldArg) => worldArg,
    _ => Path.Combine(savesDir, worldArg),
};
if (worldDir == null || !Directory.Exists(Path.Combine(worldDir, "region")))
{
    Console.WriteLine($"No Minecraft world found. Pass a save name (from {savesDir}) or a path to a world folder.");
    return 1;
}

var level = LevelInfo.Load(worldDir);
var center = level.PlayerPos ?? level.Spawn ?? Vector3.Zero;
Console.WriteLine($"World '{level.Name}' ({worldDir})");
Console.WriteLine($"Starting at {center.X:0},{center.Y:0},{center.Z:0}, view radius {radius} chunks (streams in as you fly)");

// Start a little above the player, looking out over the land.
var cam = center + new Vector3(0, 25, 0);
if (camArg != null)
{
    var cv = camArg.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
    cam = new Vector3(cv[0], cv[1], cv[2]); // the diagnostics below use it too
}
float yaw = 0.6f, pitch = -0.25f;
if (camArg != null && camArg.Split(',') is { Length: >= 5 } cp)
    (yaw, pitch) = (float.Parse(cp[3], CultureInfo.InvariantCulture), float.Parse(cp[4], CultureInfo.InvariantCulture));
if (args.Contains("--probe"))
{
    // Diagnostic: tree trunks near the camera (base block, height) and the ground height under the camera.
    var pw = new World(worldDir);
    int px = (int)MathF.Floor(cam.X), pz = (int)MathF.Floor(cam.Z);
    for (int dz = -2; dz <= 2; dz++) for (int dx = -2; dx <= 2; dx++) pw.EnsureLoaded((px >> 4) + dx, (pz >> 4) + dz);
    int Top(int x, int z) => pw.GetChunk(x >> 4, z >> 4)!.TopTerrain[(z & 15) * 16 + (x & 15)];
    Console.WriteLine($"Ground under camera: {Top(px, pz)}");
    if (Environment.GetEnvironmentVariable("REALISM_LAVA_FALLS") != null)
    {
        // Lava with open air on a side: falls and curtains. Grouped per column: x z, y range, levels.
        var cols = new SortedDictionary<(int, int), List<string>>();
        for (int z = pz - 4; z <= pz + 28; z++)
        for (int x = px - 16; x <= px + 8; x++)
        {
            pw.EnsureLoaded(x >> 4, z >> 4);
            var ch = pw.GetChunk(x >> 4, z >> 4)!;
            for (int y = (int)cam.Y - 8; y <= (int)cam.Y + 30; y++)
            {
                byte m = ch.Get(x & 15, y, z & 15);
                if (!Mat.IsLava(m)) continue;
                int open = 0;
                foreach (var (ox, oz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    pw.EnsureLoaded((x + ox) >> 4, (z + oz) >> 4);
                    if (pw.GetChunk((x + ox) >> 4, (z + oz) >> 4)!.Get((x + ox) & 15, y, (z + oz) & 15) == Mat.Air) open++;
                }
                if (open == 0) continue;
                if (!cols.TryGetValue((x, z), out var l)) cols[(x, z)] = l = new();
                l.Add($"{y}:{m}/{open}");
            }
        }
        foreach (var ((x, z), l) in cols) Console.WriteLine($"  lava col {x},{z}: {string.Join(" ", l)}");
    }
    if (Environment.GetEnvironmentVariable("REALISM_DUMP_Y") is { } dy)
        for (int y = int.Parse(dy) + 3; y >= int.Parse(dy) - 2; y--)
        {
            Console.WriteLine($"  y {y}:");
            for (int z = pz - 3; z <= pz + 5; z++)
                Console.WriteLine($"    z {z,4}: " + string.Join(" ", Enumerable.Range(px - 3, 8).Select(x => $"{pw.GetChunk(x >> 4, z >> 4)!.Get(x & 15, y, z & 15),3}")) + $"   (x {px - 3}..{px + 4})");
        }
    var trunks = new List<(int x, int y, int z, int h, float d)>();
    for (int z = pz - 14; z <= pz + 14; z++)
    for (int x = px - 14; x <= px + 14; x++)
    {
        var ch = pw.GetChunk(x >> 4, z >> 4)!;
        int g = Top(x, z);
        if (!Mat.IsWood(ch.Get(x & 15, g + 1, z & 15))) continue;
        int h = 0;
        while (Mat.IsWood(ch.Get(x & 15, g + 1 + h, z & 15))) h++;
        trunks.Add((x, g + 1, z, h, MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz))));
    }
    // Canopy map, one block per character: '#' leaves/logs above the ground (head height up to 5 above), '.' open sky.
    Console.WriteLine($"Canopy map: x {px - 20}..{px + 20} left to right, z {pz - 14}..{pz + 14} top to bottom");
    for (int z = pz - 14; z <= pz + 14; z++)
    {
        var sb = new System.Text.StringBuilder($"{z,5} ");
        for (int x = px - 20; x <= px + 20; x++)
        {
            var ch = pw.GetChunk(x >> 4, z >> 4)!;
            int g = Top(x, z);
            bool cover = false;
            for (int y = g + 1; y <= g + 6 && !cover; y++) { byte m = ch.Get(x & 15, y, z & 15); cover = Mat.IsLeaves(m) || Mat.IsWood(m) || Mat.IsBuilt(m); }
            sb.Append(x == px && z == pz ? '@' : cover ? '#' : Mat.IsWater(ch.Get(x & 15, g + 1, z & 15)) ? '~' : '.');
        }
        Console.WriteLine(sb);
    }
    foreach (var t in trunks.OrderBy(t => t.d).Take(8))
        Console.WriteLine($"Trunk at {t.x},{t.y},{t.z} height {t.h} (distance {t.d:0.0}); ground around: " +
            string.Join(" ", new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Select(o => Top(t.x + o.Item1 * 3, t.z + o.Item2 * 3))));
    return 0;
}

if (args.Contains("--bench-mesh"))
{
    // Diagnostic: how long meshing takes per chunk around the camera (what streaming costs while you move).
    var bw = new World(worldDir);
    int bcx = (int)cam.X >> 4, bcz = (int)cam.Z >> 4;
    for (int dz = -4; dz <= 4; dz++) for (int dx = -4; dx <= 4; dx++) bw.EnsureLoaded(bcx + dx, bcz + dz);
    var reveal = Environment.GetEnvironmentVariable("REALISM_BENCH_CAVE") != null ? Realism.Meshing.CaveReveal.Compute(bw, cam, 160) : null;
    var times = new List<double>();
    for (int dz = -3; dz <= 3; dz++) for (int dx = -3; dx <= 3; dx++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Realism.Meshing.Mesher.Build(bw, bw.GetChunk(bcx + dx, bcz + dz)!, reveal);
        times.Add(sw.Elapsed.TotalMilliseconds);
    }
    times.Sort();
    Console.WriteLine($"Mesh: {times.Count} chunks, median {times[times.Count / 2]:0} ms, worst {times[^1]:0} ms, total {times.Sum():0} ms (single thread)");
    return 0;
}

if (args.Contains("--map"))
{
    // Diagnostic: a top-down map around the camera, 4 blocks per character: surface height (0-9 = 60..105 in 5s),
    // ~ water, T trees, L lava at the surface.
    var mw = new World(worldDir);
    int mx = (int)cam.X, mz = (int)cam.Z;
    Console.WriteLine($"Map around {mx},{mz} (x right, z down; 4 blocks per char)");
    for (int z = mz - 128; z < mz + 128; z += 4)
    {
        var sb = new System.Text.StringBuilder();
        for (int x = mx - 160; x < mx + 160; x += 4)
        {
            if (!mw.EnsureLoaded(x >> 4, z >> 4)) { sb.Append(' '); continue; }
            var ch = mw.GetChunk(x >> 4, z >> 4)!;
            int top = ch.TopAny[(z & 15) * 16 + (x & 15)];
            byte m = ch.Get(x & 15, top, z & 15);
            sb.Append(Mat.IsWater(m) ? '~' : Mat.IsLava(m) ? 'L' : Mat.IsLeaves(m) || Mat.IsWood(m) ? 'T'
                : (char)('0' + Math.Clamp((ch.TopTerrain[(z & 15) * 16 + (x & 15)] - 60) / 5, 0, 9)));
        }
        Console.WriteLine($"{z,5} {sb}");
    }
    return 0;
}

if (args.Contains("--find-crystals"))
{
    // Diagnostic: amethyst clusters near the player, with a camera position inside the geode looking at one.
    var gw = new World(worldDir);
    int px = (int)cam.X, pz = (int)cam.Z, shown = 0;
    for (int r = 0; r <= 12 && shown < 4; r++)
    for (int cz = -r; cz <= r && shown < 4; cz++)
    for (int cx = -r; cx <= r && shown < 4; cx++)
    {
        if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != r || !gw.EnsureLoaded((px >> 4) + cx, (pz >> 4) + cz)) continue;
        var ch = gw.GetChunk((px >> 4) + cx, (pz >> 4) + cz)!;
        for (int y = ch.MinY + 1; y < 60 && shown < 4; y++)
        for (int lz = 0; lz < 16 && shown < 4; lz++)
        for (int lx = 0; lx < 16 && shown < 4; lx++)
        {
            if (ch.Get(lx, y, lz) != Mat.Crystal + 3) continue;
            int x = ch.X * 16 + lx, z = ch.Z * 16 + lz;
            // Step away from it through the open space of the geode.
            foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0) })
            {
                bool open = true;
                for (int d = 1; d <= 3 && open; d++)
                    open = gw.EnsureLoaded((x + dx * d) >> 4, (z + dz * d) >> 4) && gw.GetChunk((x + dx * d) >> 4, (z + dz * d) >> 4)!.Get((x + dx * d) & 15, y + dy * d, (z + dz * d) & 15) == Mat.Air;
                if (!open) continue;
                float lookYaw = MathF.Atan2(-dz, -dx);
                Console.WriteLine($"Cluster at {x},{y},{z}: --cam {x + 0.5f + dx * 3:0.0},{y + 0.5f + dy * 3:0.0},{z + 0.5f + dz * 3:0.0},{lookYaw:0.00},{(dy > 0 ? -1.2f : -0.2f):0.0}");
                shown++;
                break;
            }
        }
    }
    if (shown == 0) Console.WriteLine("No amethyst clusters found nearby.");
    return 0;
}

if (args.Contains("--find-ores"))
{
    // Diagnostic: exposed ores near the player, each with a spot to stand on 3 blocks away and the Minecraft tp command
    // that looks at it.
    var gw = new World(worldDir);
    int px = (int)cam.X, pz = (int)cam.Z, shown = 0;
    byte At(int x, int y, int z) => gw.EnsureLoaded(x >> 4, z >> 4) ? gw.GetChunk(x >> 4, z >> 4)!.Get(x & 15, y, z & 15) : Mat.Stone;
    for (int r = 0; r <= 8 && shown < 12; r++)
    for (int cz = -r; cz <= r && shown < 12; cz++)
    for (int cx = -r; cx <= r && shown < 12; cx++)
    {
        if (Math.Max(Math.Abs(cx), Math.Abs(cz)) != r || !gw.EnsureLoaded((px >> 4) + cx, (pz >> 4) + cz)) continue;
        var ch = gw.GetChunk((px >> 4) + cx, (pz >> 4) + cz)!;
        for (int y = ch.MinY + 2; y < 100 && shown < 12; y++)
        for (int lz = 0; lz < 16 && shown < 12; lz++)
        for (int lx = 0; lx < 16 && shown < 12; lx++)
        {
            if (!Mat.IsOre(ch.Get(lx, y, lz))) continue;
            int x = ch.X * 16 + lx, z = ch.Z * 16 + lz;
            foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                bool open = true;
                for (int d = 1; d <= 3 && open; d++) open = At(x + dx * d, y, z + dz * d) == Mat.Air && At(x + dx * d, y + 1, z + dz * d) == Mat.Air;
                if (!open || !Mat.IsTerrain(At(x + dx * 3, y - 1, z + dz * 3))) continue;
                float mcYaw = MathF.Atan2(dx, -dz) * 180 / MathF.PI;
                Console.WriteLine($"{(ch.Get(lx, y, lz) == Mat.DeepslateOre ? "deepslate ore" : ch.Get(lx, y, lz) == Mat.NetherOre ? "nether ore" : "ore")} at {x},{y},{z}: tp @s {x + 0.5f + dx * 3:0.0} {y} {z + 0.5f + dz * 3:0.0} {mcYaw:0} 20");
                shown++;
                break;
            }
        }
    }
    if (shown == 0) Console.WriteLine("No exposed ores found nearby.");
    return 0;
}

if (args.Contains("--find-gaps"))
{
    // Diagnostic: natural holes in walls (2-3 tall, ground below, rock above, open outside) near the player, with a
    // camera position looking into each (use with --cam).
    var gw = new World(worldDir);
    var gc = cam;
    int found = 0;
    for (int r = 0; r <= 64 && found < 12; r++)
    for (int dz = -r; dz <= r && found < 12; dz++)
    for (int dx = -r; dx <= r && found < 12; dx++)
    {
        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
        int x = (int)gc.X + dx, z = (int)gc.Z + dz;
        if (!gw.EnsureLoaded(x >> 4, z >> 4)) continue;
        var ch = gw.GetChunk(x >> 4, z >> 4)!;
        byte At(int ax, int ay, int az) => gw.EnsureLoaded(ax >> 4, az >> 4) ? gw.GetChunk(ax >> 4, az >> 4)!.Get(ax & 15, ay, az & 15) : Mat.Air;
        bool Open(byte m) => m is Mat.Air or Mat.Plant or Mat.TallGrass or Mat.Flower;
        bool Gap(int ax, int ay, int az) => Mat.IsTerrain(At(ax, ay - 1, az)) && Open(At(ax, ay, az)) && Open(At(ax, ay + 1, az)) && Mat.IsTerrain(At(ax, ay + 2, az));
        int top = ch.TopTerrain[(z & 15) * 16 + (x & 15)];
        for (int y = top - 60; y <= top + 1 && found < 12; y++)
        {
            if (!Gap(x, y, z)) continue;
            foreach (var (ox, oz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                // Outside is open sky-ish (no rock just above), and the hole goes at least 2 deep.
                bool outside = Open(At(x + ox, y, z + oz)) && Open(At(x + ox, y + 1, z + oz)) && Open(At(x + ox, y + 2, z + oz));
                if (!outside || !Gap(x - ox, y, z - oz)) continue;
                var camPos = new Vector3(x + 0.5f + ox * 4, y + 1.6f, z + 0.5f + oz * 4);
                float yawTo = MathF.Atan2(-oz, -ox);
                // Is the opening open in the smooth mesh? A ray from outside through its middle, 1.5 blocks in.
                var reveal = Realism.Meshing.CaveReveal.Compute(gw, new Vector3(x + 0.5f + ox * 1.5f, y + 1f, z + 0.5f + oz * 1.5f), 64);
                var mouth = new Vector3(x + 0.5f + ox * 1.5f, y + 1.2f, z + 0.5f + oz * 1.5f);
                var inside = new Vector3(x + 0.5f - ox * 1.5f, y + 1.2f, z + 0.5f - oz * 1.5f);
                bool blocked = false;
                foreach (var key in new[] { (x >> 4, z >> 4), ((x - ox * 2) >> 4, (z - oz * 2) >> 4), ((x + ox * 3) >> 4, (z + oz * 3) >> 4) }.Distinct())
                {
                    bool loaded = true;
                    for (int nz = -1; nz <= 1; nz++) for (int nx = -1; nx <= 1; nx++) loaded &= gw.EnsureLoaded(key.Item1 + nx, key.Item2 + nz);
                    if (!loaded) continue;
                    var mesh = Realism.Meshing.Mesher.Build(gw, gw.GetChunk(key.Item1, key.Item2)!, reveal).Terrain;
                    for (int t = 0; t + 2 < mesh.Indices.Count && !blocked; t += 3)
                        blocked = RayHits(mouth, inside, mesh.Vertices[(int)mesh.Indices[t]].Pos, mesh.Vertices[(int)mesh.Indices[t + 1]].Pos, mesh.Vertices[(int)mesh.Indices[t + 2]].Pos);
                }
                Console.WriteLine($"Hole at {x},{y},{z} facing {ox},{oz}: {(blocked ? "BLOCKED by the smooth surface" : "open")}  (--cam {camPos.X:0.0},{camPos.Y:0.0},{camPos.Z:0.0},{yawTo:0.00},-0.1)");
                if (blocked && Environment.GetEnvironmentVariable("REALISM_GAP_DEBUG") != null)
                    for (int yy = y + 3; yy >= y - 1; yy--)
                        Console.WriteLine($"    y {yy}: " + string.Join(" ", Enumerable.Range(-3, 7).Select(a => $"{At(x + ox * a, yy, z + oz * a),3}")) + "   (outside -> inside, left to right reversed)");
                found++;
                break;
            }
        }
    }
    if (found == 0) Console.WriteLine("No wall holes found nearby.");
    return 0;

    // Segment a->b against triangle (Moller-Trumbore).
    static bool RayHits(Vector3 a, Vector3 b, Vector3 p0, Vector3 p1, Vector3 p2)
    {
        var d = b - a;
        Vector3 e1 = p1 - p0, e2 = p2 - p0, h = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, h);
        if (MathF.Abs(det) < 1e-8f) return false;
        float f = 1 / det;
        var sv = a - p0;
        float u = f * Vector3.Dot(sv, h);
        if (u < 0 || u > 1) return false;
        var q = Vector3.Cross(sv, e1);
        float v = f * Vector3.Dot(d, q);
        if (v < 0 || u + v > 1) return false;
        float t = f * Vector3.Dot(e2, q);
        return t > 0 && t < 1;
    }
}


using var streamer = new ChunkStreamer(new World(worldDir), radius, cam);
// --cinematic script.txt --out film.mp4 (or --preview dir): render a scripted camera film from this world.
var film = filmScript != null ? Cinematic.Load(filmScript) : null;
if (film != null) { var f0 = film.At(0); cam = f0.pos; }
if (filmPreview != null) Directory.CreateDirectory(filmPreview);
var app = new App(streamer, new AppOptions(cam, yaw, pitch, timeOfDay, (radius - 1) * 16f, screenshot,
    $"PassThrough Realism - {level.Name}", Math.Max(weather, 0), Film: film, FilmOut: filmOut ?? "film.mp4", FilmPreviewDir: filmPreview));
app.Run();

if (args.Contains("--blocks"))
    foreach (var group in Blocks.Classified.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
        Console.WriteLine($"  [{group.Key}] {string.Join(", ", group.Select(kv => kv.Key.Replace("minecraft:", "")).Order())}");
return 0;

/// <summary>
/// Starts Minecraft 1.21.5 + Fabric with the bridge mod (the MinecraftMod project's client run, offline
/// singleplayer, saves linked to %APPDATA%\.minecraft\saves). With a world name it opens that world directly.
/// </summary>
static void LaunchMinecraft(string? world)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MinecraftMod", "gradlew.bat"))) dir = dir.Parent;
    if (dir == null)
    {
        Console.WriteLine("Couldn't find the MinecraftMod folder; start Minecraft yourself (Fabric 1.21.5 profile).");
        return;
    }
    string args = world == null ? "runClient" : $"runClient \"-Pworld={world}\"";
    Console.WriteLine(world == null
        ? "Starting Minecraft (title screen; open any world). The first start takes about a minute..."
        : $"Starting Minecraft straight into '{world}'. The first start takes about a minute...");
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = Path.Combine(dir.FullName, "MinecraftMod", "gradlew.bat"), // full path: bare names can be blocked by NoDefaultCurrentDirectoryInExePath
        Arguments = args,
        WorkingDirectory = Path.Combine(dir.FullName, "MinecraftMod"),
        UseShellExecute = true, // its own console window, so the Gradle/Minecraft log is visible
    });
}
