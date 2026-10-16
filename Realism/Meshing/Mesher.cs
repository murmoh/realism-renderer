using System.Numerics;
using System.Runtime.InteropServices;
using Realism.Minecraft;

namespace Realism.Meshing;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Vertex
{
    public Vector3 Pos;
    public Vector3 Normal;
    public uint Data0; // 4 x unorm8: per-kind data (terrain: grass, dirt, stone, sand weights)
    public uint Data1; // 4 x unorm8: (terrain: snow, gravel, clay weights) + ambient occlusion in .w

    public const int Size = 32;
}

/// <summary>Per-instance data for instanced draws (grass tufts, leaf cards): two vec4s.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Instance
{
    public Vector4 A; // xyz = world position, w = random seed
    public Vector4 B; // grass: height, kind (0 grass, 1 tall grass, 2 flower), colour seed, AO | leaves: normal xyz, AO | rocks: scale xyz, AO

    public const int Size = 32;
}

public sealed class MeshData
{
    public readonly List<Vertex> Vertices = new();
    public readonly List<uint> Indices = new();
    public bool IsEmpty => Indices.Count == 0;
}

public sealed class ChunkMeshes
{
    public required int X, Z;
    public readonly MeshData Terrain = new(), Leaves = new(), Wood = new(), Water = new(), Buildings = new(), Glass = new(),
        ShadowCanopy = new(), // solid tree crowns, drawn only into shadow maps
        Blocks = new(), BlocksTranslucent = new(), // Minecraft's own block models (live mode); see Mesher.AddModel
        Ores = new(); // Minecraft's ore textures over the smooth rock around exposed ores (live mode)
    public readonly List<Instance> Grass = new(), LeafCards = new(), Rocks = new(), Pebbles = new();
    /// <summary>Point lights from torches and glowing blocks: xyz position, w intensity.</summary>
    public readonly List<Vector4> Lights = new();
    /// <summary>Particle emitters: xyz position, w &gt; 0 waterfall splash (strength = fall height), w &lt; 0 lava embers.</summary>
    public readonly List<Vector4> Emitters = new();
    /// <summary>Smooth collision for Minecraft: top natural block per column (short.MinValue = none) and the
    /// surface height at 4x4 points per column, in 1/16 blocks.</summary>
    public readonly short[] CollisionTops = new short[256], CollisionHeights = new short[4096];
    /// <summary>Smooth collision for the other floors under a column's top (cave floors, ledges under overhangs):
    /// column index, floor block Y, and the surface height at 4x4 points (1/16 blocks), as for the top.</summary>
    public readonly List<(short col, short top, short[] heights)> CollisionFloors = new();
    public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
}

/// <summary>
/// Turns a chunk of blocks into smooth geometry. Block occupancy becomes a density field that is blurred,
/// then polygonised with Surface Nets so cubes become rolling hills, rounded cliffs and soft canopies.
/// </summary>
public static class Mesher
{
    const int Pad = 8;               // neighbour blocks sampled around the chunk (blur + gradients + AO reach)
    const int W = 16 + 2 * Pad;
    const int CaveHideDepth = 6;     // air deeper than this below the local surface is filled in (hides caves)
    const float ThinTerrain = 0.33f;
    /// <summary>Diagnostics (REALISM_NO_GAPS): let smoothing close walkable gaps, as before they were kept open.</summary>
    static readonly bool KeepGapsClosed = Environment.GetEnvironmentVariable("REALISM_NO_GAPS") != null;
    /// <summary>Space you can walk through: air and things without collision worth smoothing (plants, torches).</summary>
    static bool IsOpen(byte m) => m is Mat.Air or Mat.Plant or Mat.TallGrass or Mat.Flower or Mat.Torch or Mat.SnowLayer;  // smoothed density below this at a solid block means smoothing erased it

    static readonly (int x, int y, int z)[] Axis = { (1, 0, 0), (0, 1, 0), (0, 0, 1) };

    /// <param name="reveal">The cave system an underground camera is in: shown in full, not filled in.</param>
    public static ChunkMeshes Build(World world, Chunk chunk, CaveReveal? reveal = null)
    {
        var stageClock = System.Diagnostics.Stopwatch.StartNew();
        var stages = new System.Text.StringBuilder();
        double lastStage = 0;
        void Stage(string name)
        {
            double now = stageClock.Elapsed.TotalMilliseconds;
            stages.Append($" {name} {now - lastStage:0}");
            lastStage = now;
        }
        var result = new ChunkMeshes { X = chunk.X, Z = chunk.Z };
        int bx = chunk.X * 16 - Pad, bz = chunk.Z * 16 - Pad; // world coordinate of array index 0

        // --- Column heights over the padded area (+1 ring for the cave-hiding min filter) ---
        const int HW = W + 2;
        var topTerrain = new int[HW * HW];
        var topAny = new int[HW * HW];
        for (int k = 0; k < HW; k++)
        for (int i = 0; i < HW; i++)
        {
            int wx = bx - 1 + i, wz = bz - 1 + k;
            var c = world.GetChunk(wx >> 4, wz >> 4)!;
            int col = (wz & 15) * 16 + (wx & 15);
            topTerrain[k * HW + i] = c.TopTerrain[col];
            topAny[k * HW + i] = c.TopAny[col];
        }

        var hideBelow = new int[W * W];
        int y0 = int.MaxValue, y1 = int.MinValue;
        for (int k = 0; k < W; k++)
        for (int i = 0; i < W; i++)
        {
            int m = int.MaxValue;
            for (int dk = 0; dk < 3; dk++)
            for (int di = 0; di < 3; di++)
                m = Math.Min(m, topTerrain[(k + dk) * HW + i + di]);
            hideBelow[k * W + i] = m - CaveHideDepth;
            y0 = Math.Min(y0, m - CaveHideDepth - 3);
            y1 = Math.Max(y1, topAny[(k + 1) * HW + i + 1] + 5);
        }

        // Blocks dug or placed in play: caves within RevealRadius of them are shown instead of filled in (you can
        // see into the cave your tunnel broke into), and the volume reaches down to them.
        const int RevealRadius = 12;
        var edits = world.EditsNear(bx - RevealRadius, bz - RevealRadius, bx + W + RevealRadius, bz + W + RevealRadius);
        var revealLo = new int[W * W];
        var revealHi = new int[W * W];
        Array.Fill(revealLo, int.MaxValue);
        Array.Fill(revealHi, int.MinValue);
        foreach (var (ex, ey, ez) in edits)
        {
            for (int k = Math.Max(0, ez - RevealRadius - bz); k <= Math.Min(W - 1, ez + RevealRadius - bz); k++)
            for (int i = Math.Max(0, ex - RevealRadius - bx); i <= Math.Min(W - 1, ex + RevealRadius - bx); i++)
            {
                revealLo[k * W + i] = Math.Min(revealLo[k * W + i], ey - RevealRadius);
                revealHi[k * W + i] = Math.Max(revealHi[k * W + i], ey + RevealRadius);
            }
            y0 = Math.Min(y0, ey - 3);
            y1 = Math.Max(y1, ey + 3);
        }
        if (reveal != null && reveal.LowestIn(bx, bz, bx + W - 1, bz + W - 1) is var lowest and < int.MaxValue)
            y0 = Math.Min(y0, lowest - 2); // reach down to the cave floor (and the rock under it)
        int H = y1 - y0 + 1;
        int Idx(int i, int j, int k) => (j * W + k) * W + i;

        Stage("volume-setup");
        // --- Material volume ---
        var mat = new byte[W * H * W];
        var edited = new bool[W * H * W];
        for (int k = 0; k < W; k++)
        for (int i = 0; i < W; i++)
        {
            int wx = bx + i, wz = bz + k;
            var c = world.GetChunk(wx >> 4, wz >> 4)!;
            int lx = wx & 15, lz = wz & 15;
            int top = c.TopAny[lz * 16 + lx], hb = hideBelow[k * W + i];
            for (int j = 0; j < H; j++)
            {
                int y = y0 + j;
                byte m = y > top + 1 ? Mat.Air : c.Get(lx, y, lz);
                if (y < hb && !Mat.IsTerrain(m) && !(y >= revealLo[k * W + i] && y <= revealHi[k * W + i]) && reveal?.Contains(wx, y, wz) != true)
                    m = Mat.Stone;
                if (edits.Count > 0 && world.IsEdited(wx, y, wz)) edited[Idx(i, j, k)] = true;
                mat[Idx(i, j, k)] = m;
            }
        }

        // Nether portal frames stay crisp blocks (obsidian is natural rock, which would melt into the smooth ground):
        // the rock around each portal block in the portal's own plane (its neighbours across the portal say which).
        for (int j = 1; j < H - 1; j++)
        for (int k = 1; k < W - 1; k++)
        for (int i = 1; i < W - 1; i++)
        {
            if (mat[Idx(i, j, k)] != Mat.Portal) continue;
            bool alongX = mat[Idx(i - 1, j, k)] == Mat.Portal || mat[Idx(i + 1, j, k)] == Mat.Portal || mat[Idx(i - 1, j, k)] != Mat.Air && mat[Idx(i + 1, j, k)] != Mat.Air;
            for (int dy = -1; dy <= 1; dy++)
            for (int d = -1; d <= 1; d++)
            {
                int n = alongX ? Idx(i + d, j + dy, k) : Idx(i, j + dy, k + d);
                if (Mat.IsTerrain(mat[n])) edited[n] = true;
            }
        }

        Stage("volume");
        // --- Density fields ---
        var terrain = new float[mat.Length];
        var leaves = new float[mat.Length];
        var leafCore = new float[mat.Length]; // broadleaf crowns only: conifers get their shape from needle cards
        for (int n = 0; n < mat.Length; n++)
        {
            // Placed blocks are drawn crisp, not melted into the smooth ground.
            terrain[n] = Mat.IsTerrain(mat[n]) && !edited[n] ? 1f : 0f;
            leaves[n] = Mat.IsLeaves(mat[n]) ? 1f : 0f;
            leafCore[n] = Mat.IsLeaves(mat[n]) && Mat.Species(mat[n]) != Mat.Spruce ? 1f : 0f;
        }
        var tmp = new float[mat.Length];
        // Terrain: 3 blur passes shape the surface, 2 more give extra-smooth normals so Minecraft's
        // staircase slopes light like continuous hillsides. Leaves keep more detail.
        for (int pass = 0; pass < 3; pass++) Blur(terrain, tmp, H);
        for (int pass = 0; pass < 2; pass++) { Blur(leaves, tmp, H); Blur(leafCore, tmp, H); }
        // Caves: rock that touches covered space (air with ground above it) is never smoothed away, so thin cave
        // roofs and walls stay solid instead of opening holes to the sky. Outdoor hills stay smooth.
        // Sky light: 1 in open air under the sky, 0 under ground, spread a few blocks into cave mouths; baked into
        // the terrain shading so caves are dark (torches are the light down there), as in Minecraft.
        var sky = new float[mat.Length];
        {
            int ColumnTop(int i, int k) => topTerrain[(k + 1) * HW + i + 1];
            bool CaveAir(int i, int j, int k) =>
                (uint)i < W && (uint)j < (uint)H && (uint)k < W && !Mat.IsTerrain(mat[Idx(i, j, k)]) && y0 + j < ColumnTop(i, k);
            for (int j = 0; j < H; j++)
            for (int k = 0; k < W; k++)
            for (int i = 0; i < W; i++)
            {
                int n = Idx(i, j, k);
                if (Mat.IsTerrain(mat[n]))
                {
                    if (!edited[n] && terrain[n] < 0.62f
                        && (CaveAir(i, j - 1, k) || CaveAir(i, j + 1, k) || CaveAir(i - 1, j, k) || CaveAir(i + 1, j, k) || CaveAir(i, j, k - 1) || CaveAir(i, j, k + 1)))
                        terrain[n] = 0.62f;
                }
                else if (y0 + j > ColumnTop(i, k)) sky[n] = 1f;
            }
            for (int pass = 0; pass < 4; pass++) Blur(sky, tmp, H);
        }

        // Dug space stays open: smoothing would otherwise fill narrow tunnels and shafts.
        for (int n = 0; n < mat.Length; n++)
            if (edited[n] && !Mat.IsTerrain(mat[n])) terrain[n] = 0f;
        // Walkable gaps stay open too: open space 2-3 blocks tall with ground below and rock above (tunnels, holes in
        // a wall, crawl spaces under overhangs). Blurring would close them, leaving solid-looking rock where Minecraft
        // lets you walk in, so you'd clip into the wall.
        if (!KeepGapsClosed)
            for (int k = 1; k < W - 1; k++)
            for (int i = 1; i < W - 1; i++)
            for (int j = 1; j < H - 3; j++)
            {
                if (!Mat.IsTerrain(mat[Idx(i, j - 1, k)]) || !IsOpen(mat[Idx(i, j, k)]) || !IsOpen(mat[Idx(i, j + 1, k)])) continue;
                int top = Mat.IsTerrain(mat[Idx(i, j + 2, k)]) ? j + 1 : IsOpen(mat[Idx(i, j + 2, k)]) && Mat.IsTerrain(mat[Idx(i, j + 3, k)]) ? j + 2 : -1;
                for (int y = j; y <= top; y++)
                {
                    terrain[Idx(i, y, k)] = 0f;
                    // ...and its mouth: the open cells beside it, which the blur would otherwise seal like a lid.
                    foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        if (IsOpen(mat[Idx(i + dx, y, k + dz)])) terrain[Idx(i + dx, y, k + dz)] = MathF.Min(terrain[Idx(i + dx, y, k + dz)], 0.3f);
                }
            }
        var terrainNormals = (float[])terrain.Clone();
        for (int pass = 0; pass < 2; pass++) Blur(terrainNormals, tmp, H);

        var field = new Field(terrain, leaves, H, sky);
        Vector3 origin = new(bx + 0.5f, y0 + 0.5f, bz + 0.5f);

        Stage("density");
        var oreAt = new Dictionary<int, List<int>>(); // terrain vertex -> the ore blocks near it
        // The ore blocks within reach of a terrain vertex at p (cell i, j, k), for the ore overlay below.
        void OreNear(int i, int j, int k, Vector3 p)
        {
            List<int>? found = null;
            for (int dj = -1; dj <= 2; dj++)
            for (int dk = -1; dk <= 2; dk++)
            for (int di = -1; di <= 2; di++)
            {
                int ii = i + di, jj = j + dj, kk = k + dk;
                if ((uint)jj >= (uint)H || (uint)ii >= W || (uint)kk >= W || !Mat.IsOre(mat[Idx(ii, jj, kk)])) continue;
                if (Vector3.Distance(p, new Vector3(ii, jj, kk)) > 1.6f) continue;
                (found ??= new()).Add(Idx(ii, jj, kk));
            }
            if (found != null) oreAt[result.Terrain.Vertices.Count] = found;
        }
        // Terrain surface: materials from the blocks around each vertex, AO from the density field.
        var weights = new float[Mat.ChannelCount];
        Polygonize(terrain, terrainNormals, H, 0.5f, origin, result.Terrain, (i, j, k, p, n) =>
        {
            if (world.Models != null) OreNear(i, j, k, p);
            // Blend the materials of nearby *exposed* blocks (terrain with open space above), weighted by
            // distance. A wide window stops Minecraft's per-block patterns (snow on every stair tread,
            // stone on every riser) from printing onto the smooth surface; the shader re-derives rock from slope.
            Array.Clear(weights);
            float total = 0;
            for (int dj = -3; dj <= 3; dj++)
            for (int dk = -2; dk <= 3; dk++)
            for (int di = -2; di <= 3; di++)
            {
                int ii = i + di, jj = Math.Clamp(j + dj, 0, H - 2), kk = k + dk;
                byte m = mat[Idx(ii, jj, kk)];
                if (!Mat.IsTerrain(m)) continue;
                byte above = mat[Idx(ii, jj + 1, kk)];
                if (Mat.IsTerrain(above)) continue;
                var block = new Vector3(ii, jj, kk);
                float w = 1f / (1f + Vector3.DistanceSquared(block, p) * 0.35f);
                int ch = above == Mat.SnowLayer ? Mat.Channel(Mat.Snow) : Mat.Channel(m);
                weights[ch] += w;
                total += w;
            }
            if (total == 0)
            {
                // Vertical faces with nothing exposed nearby: use the block itself.
                for (int n8 = 0; n8 < 8; n8++)
                {
                    byte m = mat[Idx(i + (n8 & 1), j + ((n8 >> 1) & 1), k + ((n8 >> 2) & 1))];
                    if (Mat.IsTerrain(m)) { weights[Mat.Channel(m)] += 1; total += 1; }
                }
                if (total == 0) { weights[Mat.Channel(Mat.Stone)] = 1; total = 1; }
            }

            float occ = 0.5f * field.Terrain(p + n * 1.5f) + 0.3f * field.Terrain(p + n * 3f) + 0.2f * field.Terrain(p + n * 5f);
            occ += 0.4f * (field.Leaves(p + n * 1.5f) + field.Leaves(p + n * 3f));
            float canopy = 0;
            for (int s = 2; s <= 12; s += 2) canopy += field.Leaves(p + new Vector3(0, s, 0));
            float ao = (1 - 0.85f * SmoothStep(0.05f, 0.55f, occ)) * (1 - 0.55f * Math.Clamp(canopy * 0.6f, 0, 1));
            // Darkness under ground: almost no sky light reaches covered ground (the shader also cuts the sun there).
            ao *= 0.10f + 0.90f * SmoothStep(0.02f, 0.5f, field.Sky(p + n * 1.2f));

            byte W8(int c) => (byte)MathF.Round(weights[c] / total * 255);
            return (Pack(W8(0), W8(1), W8(2), W8(3)), Pack(W8(4), W8(5), W8(6), ToByte(ao)));
        });

        // Ore overlay: the terrain triangles near exposed ores again, carrying the ore's texture (Minecraft's own, from
        // the block atlas) and the box of the ore blocks it may cover: the shader draws the ore's pixels only on the
        // part of the surface inside those blocks (with a small margin, as the smooth surface wanders a little off
        // the block faces), so two ore blocks show as two blocks' worth of ore, no more.
        var near = new List<int>();
        for (int t = 0; t + 2 < result.Terrain.Indices.Count; t += 3)
        {
            near.Clear();
            for (int c = 0; c < 3; c++)
                if (oreAt.TryGetValue((int)result.Terrain.Indices[t + c], out var list))
                    foreach (int o in list) if (!near.Contains(o)) near.Add(o);
            if (near.Count == 0) continue;
            var va = result.Terrain.Vertices[(int)result.Terrain.Indices[t]];
            var vb = result.Terrain.Vertices[(int)result.Terrain.Indices[t + 1]];
            var vc = result.Terrain.Vertices[(int)result.Terrain.Indices[t + 2]];
            var centre = (va.Pos + vb.Pos + vc.Pos) / 3 - origin;
            Vector3 Cell(int n) => new(n % W, n / (W * W), n / W % W);
            int main = near.MinBy(n => Vector3.DistanceSquared(Cell(n), centre));
            var lo = Cell(main);
            var hi = lo;
            foreach (int n in near)
                if (mat[n] == mat[main] && Vector3.Distance(Cell(n), Cell(main)) < 3.5f) { lo = Vector3.Min(lo, Cell(n)); hi = Vector3.Max(hi, Cell(n)); }
            int wx = bx + (int)lo.X, wz = bz + (int)lo.Z;
            int st = world.GetChunk(bx + main % W >> 4, bz + main / W % W >> 4)?.GetState(bx + main % W & 15, y0 + main / (W * W), bz + main / W % W & 15) ?? 0;
            if (st == 0 || world.Models!.Get(st) is not { Quads.Length: > 0 } bm) continue;
            var boxMin = new Vector3(wx, y0 + lo.Y, wz); // world corner of the first ore block
            var size = hi - lo; // 0..3 (blocks - 1)
            uint rock = (uint)(mat[main] - Mat.Ore);
            uint oreSky = (uint)MathF.Round(Math.Clamp(field.Sky(centre + va.Normal * 1.2f), 0, 1) * 63);
            uint d0 = PackUv(bm.SideTexture().corner);
            uint baseIndex = (uint)result.Ores.Vertices.Count;
            bool fits = true;
            Span<uint> d1 = stackalloc uint[3];
            for (int c = 0; c < 3 && fits; c++)
            {
                var v = c == 0 ? va : c == 1 ? vb : vc;
                // Box corner relative to the vertex's own block (each vertex says the same box, whichever the GPU uses).
                var off = boxMin - new Vector3(MathF.Floor(v.Pos.X), MathF.Floor(v.Pos.Y), MathF.Floor(v.Pos.Z)) + new Vector3(4);
                fits = off.X is >= 0 and <= 7 && off.Y is >= 0 and <= 7 && off.Z is >= 0 and <= 7;
                d1[c] = (uint)off.X | (uint)off.Y << 3 | (uint)off.Z << 6 | (uint)size.X << 9 | (uint)size.Y << 11 | (uint)size.Z << 13
                        | (oreSky << 2 | rock) << 24;
            }
            if (!fits) continue;
            for (int c = 0; c < 3; c++)
            {
                var v = c == 0 ? va : c == 1 ? vb : vc;
                result.Ores.Vertices.Add(new Vertex { Pos = v.Pos, Normal = v.Normal, Data0 = d0, Data1 = d1[c] });
            }
            result.Ores.Indices.AddRange([baseIndex, baseIndex + 1, baseIndex + 2]);
        }

        // Inner canopy core: a dense dark mass inside the leaf cards so crowns never look hollow.
        // Shadow crowns: every species (conifers included), a little inside the leaf cards so crowns cast dense
        // shade without darkening their own sunlit side. Only depth matters, so no vertex attributes.
        Polygonize(leaves, leaves, H, 0.45f, origin, result.ShadowCanopy, (_, _, _, _, _) => (0u, 0u));

        Polygonize(leafCore, leafCore, H, 0.5f, origin, result.Leaves, (i, j, k, p, n) =>
        {
            float inside = field.Leaves(p - n * 1.5f);
            float above = 0;
            for (int s = 1; s <= 6; s++) above += field.Leaves(p + new Vector3(0, s, 0));
            float ao = (1 - 0.5f * Math.Clamp(above * 0.4f, 0, 1)) * (0.75f + 0.25f * Math.Clamp(1 - inside, 0, 1));
            ao *= 1 - 0.5f * SmoothStep(0.1f, 0.6f, field.Terrain(p + n * 1.5f));
            int species = 0;
            for (int c8 = 0; c8 < 8; c8++)
            {
                byte cm = mat[Idx(i + (c8 & 1), j + ((c8 >> 1) & 1), k + ((c8 >> 2) & 1))];
                if (Mat.IsLeaves(cm)) { species = Mat.Species(cm); break; }
            }
            return (0u, Pack((byte)species, 0, 0, ToByte(ao)));
        });

        // Water depth below the surface block at (i, j, k); 0 where it isn't water (shore).
        float ColumnDepth(int i, int j, int k)
        {
            if (!Mat.IsWater(mat[Idx(i, j, k)])) return 0;
            int d = 1;
            while (j - d > 0 && Mat.IsWater(mat[Idx(i, j - d, k)])) d++;
            return d;
        }
        // Each quad corner averages the four columns that share it.
        float[] CornerDepths(int i, int j, int k)
        {
            var r = new float[4];
            ReadOnlySpan<(int x, int z)> corners = [(0, 0), (1, 0), (1, 1), (0, 1)];
            for (int c = 0; c < 4; c++)
                r[c] = (ColumnDepth(i + corners[c].x - 1, j, k + corners[c].z - 1) + ColumnDepth(i + corners[c].x, j, k + corners[c].z - 1)
                      + ColumnDepth(i + corners[c].x - 1, j, k + corners[c].z) + ColumnDepth(i + corners[c].x, j, k + corners[c].z)) / 4;
            return r;
        }

        // Height of the water surface at a block corner: the average of the water columns around it,
        // or full height if any of them has water directly above (it's part of a falling column).
        float CornerHeight(int i, int j, int k, int cx, int cz)
        {
            float sum = 0;
            int n = 0;
            for (int dz = cz - 1; dz <= cz; dz++)
            for (int dx = cx - 1; dx <= cx; dx++)
            {
                byte w = mat[Idx(i + dx, j, k + dz)];
                if (!Mat.IsWater(w)) continue;
                if (Mat.IsWater(mat[Idx(i + dx, j + 1, k + dz)])) return 1f;
                sum += Mat.WaterHeight(w);
                n++;
            }
            return n > 0 ? sum / n : 0.889f;
        }

        bool IsThin(int i, int j, int k) => Mat.IsTerrain(mat[Idx(i, j, k)]) && terrain[Idx(i, j, k)] < ThinTerrain;
        int ThinRun(int i, int j, int k)
        {
            int run = 1;
            for (int y = j + 1; y < H - 1 && IsThin(i, y, k); y++) run++;
            for (int y = j - 1; y > 0 && IsThin(i, y, k); y--) run++;
            return run;
        }

        // A log belongs to a tree if there are leaves around or above it (trunks run up into the canopy).
        bool NearLeaves(int i, int j, int k)
        {
            for (int dy = -1; dy <= 8; dy++)
            for (int dz = -2; dz <= 2; dz++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int y = j + dy;
                if (y < 0 || y >= H) continue;
                if (Mat.IsLeaves(mat[Idx(Math.Clamp(i + dx, 0, W - 1), y, Math.Clamp(k + dz, 0, W - 1))])) return true;
            }
            return false;
        }

        Stage("terrain-surface");
        // Live mode: everything that isn't landscape (torches, placed and building blocks, rails, ladders, signs, ...)
        // is drawn with Minecraft's own block model and textures, exactly as in Minecraft. Only terrain, natural
        // trees, grass and water are regenerated. modelAt holds the model for such a block (null: landscape, or no
        // model, e.g. offline worlds, where the procedural look below is the fallback).
        var models = world.Models;
        var modelAt = models == null ? null : new BlockModel?[mat.Length];
        BlockModel? ModelOf(int i, int j, int k)
        {
            int wx = bx + i, wz = bz + k;
            int st = world.GetChunk(wx >> 4, wz >> 4)?.GetState(wx & 15, y0 + j, wz & 15) ?? 0;
            return st != 0 && models!.Get(st) is { Quads.Length: > 0 } bm ? bm : null;
        }
        if (modelAt != null)
        {
            for (int j = 1; j < H - 1; j++)
            for (int k = Pad - 1; k <= Pad + 16; k++)
            for (int i = Pad - 1; i <= Pad + 16; i++)
            {
                int n = Idx(i, j, k);
                byte m = mat[n];
                bool wants = m == Mat.Torch || m == Mat.Plant || m == Mat.Portal || Mat.IsBuilt(m)
                    || Mat.IsWood(m) && (edited[n] || !NearLeaves(i, j, k))
                    || Mat.IsTerrain(m) && (edited[n] || IsThin(i, j, k) && ThinRun(i, j, k) >= 3);
                if (wants) modelAt[n] = ModelOf(i, j, k);
            }
            // A natural block something hangs on (a torch on a lone rock, a ladder on a ledge) stays a block where
            // the smoothing would melt it away, so nothing floats.
            for (int j = 1; j < H - 1; j++)
            for (int k = Pad - 1; k <= Pad + 16; k++)
            for (int i = Pad - 1; i <= Pad + 16; i++)
            {
                int n = Idx(i, j, k);
                if (!Mat.IsTerrain(mat[n]) || modelAt[n] != null || terrain[n] >= 0.6f) continue;
                foreach (var (dx, dy, dz) in ModelQuad.Directions)
                {
                    if (dy == 1) continue; // things resting on the ground (plants, carpets) sit on the smooth surface
                    int nb = Idx(i + dx, j + dy, k + dz);
                    if (modelAt[nb] != null && !Mat.IsTerrain(mat[nb])) { modelAt[n] = ModelOf(i, j, k); break; }
                }
            }
        }

        // A model face against this cell is hidden (Minecraft's face culling): a full opaque neighbour, the same
        // block (glass next to glass), or natural ground buried deep in the smooth surface.
        bool Hides(int x, int y, int z, BlockModel self)
        {
            int n = Idx(x, y, z);
            if (modelAt![n] is { } nm) return nm.OpaqueCube || nm == self;
            byte m = mat[n];
            return Mat.IsTerrain(m) ? !edited[n] && terrain[n] > 0.8f : Mat.IsBuilt(m) && Mat.IsOpaqueCube(m);
        }

        void AddModel(BlockModel bm, Vector3 basePos, int i, int j, int k)
        {
            foreach (var q in bm.Quads)
            {
                if (q.CullFace >= 0)
                {
                    var (dx, dy, dz) = ModelQuad.Directions[q.CullFace];
                    if (j + dy >= 0 && j + dy < H && Hides(i + dx, j + dy, k + dz, bm)) continue;
                }
                var target = bm.Translucent ? result.BlocksTranslucent : result.Blocks;
                var nrm = Vector3.Cross(q.P1 - q.P0, q.P2 - q.P0);
                nrm = nrm.LengthSquared() > 1e-12f ? Vector3.Normalize(nrm) : Vector3.UnitY;
                // Sky light on the open side of the face (dark in caves), tint as RGB565, shade flag, light emission.
                byte skyLight = ToByte(field.Sky(new Vector3(i, j, k) + nrm * 0.8f));
                int lum = Math.Max(bm.Luminance, q.Emission) & 15;
                uint tint = (((q.Tint >> 19) & 31) << 11) | (((q.Tint >> 10) & 63) << 5) | ((q.Tint >> 3) & 31);
                int portal = mat[Idx(i, j, k)] == Mat.Portal ? 1 << 5 : 0; // the shader draws its own swirling surface
                uint data1 = tint | ((uint)((q.Shade ? 1 : 0) | (lum << 1) | portal) << 16) | ((uint)skyLight << 24);
                uint b = (uint)target.Vertices.Count;
                target.Vertices.Add(new Vertex { Pos = basePos + q.P0, Normal = nrm, Data0 = PackUv(q.Uv0), Data1 = data1 });
                target.Vertices.Add(new Vertex { Pos = basePos + q.P1, Normal = nrm, Data0 = PackUv(q.Uv1), Data1 = data1 });
                target.Vertices.Add(new Vertex { Pos = basePos + q.P2, Normal = nrm, Data0 = PackUv(q.Uv2), Data1 = data1 });
                target.Vertices.Add(new Vertex { Pos = basePos + q.P3, Normal = nrm, Data0 = PackUv(q.Uv3), Data1 = data1 });
                target.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            }
        }

        // Lava pools light the cave around them: one warm light per 4x4x4 cell of exposed lava.
        var lavaGlow = new Dictionary<(int, int, int), (Vector3 sum, int n)>();
        Vector3 portalSum = Vector3.Zero; // nether portal blocks in this chunk: one violet light at their middle
        int portalCount = 0;
        var flowCache = new Dictionary<int, Vector2>();

        // Trunks, water surfaces: only for this chunk's own 16x16 columns.
        for (int k = Pad; k < Pad + 16; k++)
        for (int i = Pad; i < Pad + 16; i++)
        for (int j = 1; j < H - 1; j++)
        {
            byte m = mat[Idx(i, j, k)];
            var basePos = new Vector3(bx + i, y0 + j, bz + k);
            if (modelAt?[Idx(i, j, k)] is { } model)
            {
                AddModel(model, basePos, i, j, k);
                if (m == Mat.Portal) { portalSum += basePos + new Vector3(0.5f); portalCount++; continue; }
                if (model.Luminance > 0 && result.Lights.Count < 96)
                    result.Lights.Add(new Vector4(basePos + new Vector3(0.5f, 0.6f, 0.5f), model.Luminance / 15f * 3f));
                continue;
            }
            if (Mat.IsLava(m))
            {
                AddLava(result, mat, i, j, k, basePos, lavaGlow, geometry: false);
                continue;
            }
            if (Mat.IsCrystal(m))
            {
                AddCrystals(result.Buildings, mat, i, j, k, basePos, m - Mat.Crystal);
                continue;
            }
            if (m == Mat.Torch)
            {
                // A wooden stick with a glowing flame on top; it also lights its surroundings.
                AddPlainBox(result.Buildings, basePos + new Vector3(0.4375f, 0, 0.4375f), basePos + new Vector3(0.5625f, 0.55f, 0.5625f), Mat.OakPlanks);
                AddPlainBox(result.Buildings, basePos + new Vector3(0.44f, 0.55f, 0.44f), basePos + new Vector3(0.56f, 0.7f, 0.56f), Mat.Glow);
                if (result.Lights.Count < 96) result.Lights.Add(new Vector4(basePos + new Vector3(0.5f, 0.75f, 0.5f), 4f));
                continue;
            }
            if (Mat.IsTerrain(m) && (edited[Idx(i, j, k)] || (IsThin(i, j, k) && ThinRun(i, j, k) >= 3)))
            {
                // A pillar or 1-wide wall at least 3 blocks tall (usually player-built) is too thin for the smooth
                // surface. Lone thin blocks on natural cliffs still melt away; these are kept as crisp blocks.
                // Drawn with the same natural material.
                byte top = Mat.IsTerrain(mat[Idx(i, j + 1, k)]) ? Mat.Dirt : m;
                // Faces are hidden only against other crisp blocks or ground buried deep in the smooth surface.
                AddBuiltBlock(result, mat, H, i, j, k, basePos, Mat.TerrainLook + Mat.Channel(m == Mat.Grass ? top : m),
                    (x, y, z) => y >= 0 && y < H && (Mat.IsBuilt(mat[Idx(x, y, z)]) && Mat.IsOpaqueCube(mat[Idx(x, y, z)])
                        || Mat.IsTerrain(mat[Idx(x, y, z)]) && (edited[Idx(x, y, z)] || terrain[Idx(x, y, z)] > 0.8f)));
                continue;
            }
            if (Mat.IsBuilt(m))
            {
                AddBuiltBlock(result, mat, H, i, j, k, basePos);
                if (Mat.Appearance(m) == Mat.Glow && result.Lights.Count < 96)
                    result.Lights.Add(new Vector4(basePos + new Vector3(0.5f), 6f));
                continue;
            }
            if (Mat.IsWood(m) && (edited[Idx(i, j, k)] || !NearLeaves(i, j, k)))
            {
                // Logs used for building (placed, or no canopy above): a crisp log block, not a round trunk.
                AddBuiltBlock(result, mat, H, i, j, k, basePos, Mat.LogLook + Mat.Species(m));
                continue;
            }
            if (Mat.IsWood(m))
            {
                float ao = 1 - 0.6f * Math.Clamp(field.Leaves(basePos - origin + new Vector3(0.5f, 3, 0.5f)) * 1.5f, 0, 1);
                // Trunk base: if smoothing wore the ground slightly away beneath it, run the trunk down to the surface.
                // Only nearby ground counts: never stretch a trunk through an arch or over a drop.
                float extend = 0;
                if (!Mat.IsWood(mat[Idx(i, j - 1, k)]))
                {
                    float y = j - 0.5f;
                    while (y > j - 3.5f && field.Terrain(new Vector3(i, y, k)) < 0.5f) y -= 0.25f;
                    bool found = field.Terrain(new Vector3(i, y, k)) >= 0.5f;
                    if (found && j - 0.5f - y > 0.05f) extend = j - 0.5f - y + 0.3f;
                }
                AddTrunk(result.Wood, basePos + new Vector3(0.5f, 0, 0.5f), ao, Mat.Species(m), extend);
            }
            else if (Mat.IsWater(m))
            {
                bool frozen = m == Mat.FrozenWater;
                byte above = mat[Idx(i, j + 1, k)];
                // Open sky over this water (0 in a cave): cave pools reflect darkness and torchlight, not the sky.
                float waterSky = SmoothStep(0.05f, 0.6f, field.Sky(new Vector3(i, j + 1, k)));
                bool covered = Mat.IsWater(above);
                // Corner heights as Minecraft computes them: flowing water slopes between its neighbours.
                Span<float> h = stackalloc float[4];
                ReadOnlySpan<(int x, int z)> corners = [(0, 0), (1, 0), (1, 1), (0, 1)];
                for (int c = 0; c < 4; c++) h[c] = covered ? 1f : CornerHeight(i, j, k, corners[c].x, corners[c].z);

                if (frozen && !covered && !Mat.IsTerrain(above)) // liquid water: the smooth fluid surface below
                {
                    // Rushing water (falling, or a surface sloping down a cascade) foams white.
                    float slope = MathF.Max(MathF.Max(h[0], h[1]), MathF.Max(h[2], h[3])) - MathF.Min(MathF.Min(h[0], h[1]), MathF.Min(h[2], h[3]));
                    float rush = Mat.IsFalling(m) || Mat.IsFalling(mat[Idx(i, j - 1, k)]) ? 1 : Math.Clamp(slope * 4, 0, 1);
                    Span<Vector2> flow = stackalloc Vector2[4];
                    for (int c = 0; c < 4; c++) flow[c] = CornerFlow(mat, i, j, k, corners[c].x, corners[c].z, lava: false);
                    AddWaterQuad(result.Water, basePos, h, CornerDepths(i, j, k), frozen, rush: rush, sky: waterSky, flow: flow);
                }

                // Where a waterfall lands: spray and mist (strength grows with the height of the fall).
                if (Mat.IsFalling(m) && !Mat.IsFalling(mat[Idx(i, j - 1, k)]) && result.Emitters.Count < 64)
                {
                    byte landing = mat[Idx(i, j - 1, k)];
                    int fall = 1;
                    while (fall < 24 && j + fall < H - 1 && Mat.IsWater(mat[Idx(i, j + fall, k)])) fall++;
                    float y = Mat.IsWater(landing) ? basePos.Y - 1 + Mat.WaterHeight(landing) : basePos.Y;
                    if (fall >= 2) result.Emitters.Add(new Vector4(basePos.X + 0.5f, y, basePos.Z + 0.5f, fall));
                }

                // Sides and underside wherever ice meets open air.
                foreach (var (dx, dz, c0, c1) in new[] { (1, 0, 1, 2), (-1, 0, 3, 0), (0, 1, 2, 3), (0, -1, 0, 1) })
                {
                    if (!frozen) break;
                    byte nb = mat[Idx(i + dx, j, k + dz)];
                    if (Mat.IsWater(nb) || Mat.IsOpaqueCube(nb)) continue;
                    var p0 = new Vector3(corners[c0].x, 0, corners[c0].z);
                    var p1 = new Vector3(corners[c1].x, 0, corners[c1].z);
                    AddWaterSide(result.Water, basePos + p0, basePos + p1, h[c0], h[c1], new Vector3(dx, 0, dz), frozen, waterSky,
                        rush: Mat.IsFalling(m) ? 1 : 0.35f);
                }
                byte below = mat[Idx(i, j - 1, k)];
                if (frozen && !Mat.IsWater(below) && !Mat.IsOpaqueCube(below))
                    AddWaterQuad(result.Water, basePos, stackalloc float[4], stackalloc float[4], frozen, facingDown: true, sky: waterSky);
            }
            else if (Mat.IsTerrain(m) && !Mat.IsTerrain(mat[Idx(i, j + 1, k)]) && !Mat.IsWater(mat[Idx(i, j + 1, k)]))
            {
                // Shore block: extend a neighbouring water surface underneath it. The smoothed terrain dips
                // below the old block top, so the waterline becomes a natural curve instead of block steps.
                for (int dk = -3; dk <= 3; dk++)
                for (int di = -3; di <= 3; di++)
                {
                    byte nm = mat[Idx(i + di, j, k + dk)];
                    if (nm != Mat.FrozenWater || Mat.IsWater(mat[Idx(i + di, j + 1, k + dk)])) continue; // ice; liquid shores are part of the fluid surface
                    bool frozen = nm == Mat.FrozenWater;
                    float sh = Mat.WaterHeight(nm);
                    AddWaterQuad(result.Water, basePos, [sh, sh, sh, sh], stackalloc float[4], frozen,
                        sky: SmoothStep(0.05f, 0.6f, field.Sky(new Vector3(i + di, j + 1, k + dk))));
                    goto nextBlock;
                }
                nextBlock:;
            }
        }

        Stage("blocks");
        AddFluidSurface(lava: false);
        AddFluidSurface(lava: true);

        // Water and lava as one smooth volume each, like the terrain: every fluid block fills its column up to its
        // Minecraft level (falling columns full), the fill reaches a little into the rock beside and under it (so
        // the smooth terrain cuts a clean waterline), and the result is blurred and meshed with Surface Nets at 4
        // samples per block. Streams slope smoothly, waterfalls become rounded flowing columns curving over the lip,
        // and whatever Minecraft's water or lava does, the surface follows it.
        // Fluid at different heights (a river at the surface, lava far below) is built band by band: one tall
        // block of samples spanning both would be mostly empty and cost hundreds of milliseconds.
        void AddFluidSurface(bool lava)
        {
            int bi0 = Pad - 2, bi1 = Pad + 18;
            var has = new bool[H];
            for (int j = 1; j < H - 1; j++)
                for (int k = bi0; k < bi1 && !has[j]; k++)
                for (int i = bi0; i < bi1 && !has[j]; i++)
                    has[j] = IsFluid(mat[Idx(i, j, k)], lava);
            for (int j = 1; j < H - 1; j++)
            {
                if (!has[j]) continue;
                int end = j;
                while (end + 1 < H - 1 && (has[end + 1] || (end + 2 < H - 1 && has[end + 2]) || (end + 3 < H - 1 && has[end + 3]))) end++;
                AddFluidBand(lava, j, end);
                j = end;
            }
        }

        void AddFluidBand(bool lava, int jmin, int jmax)
        {
            const int R = 4;
            int bi0 = Pad - 2, bi1 = Pad + 18; // blocks sampled (the core plus 2 each side, for seamless chunk borders)
            int j0 = Math.Max(1, jmin - 3), j1 = Math.Min(H - 2, jmax + 1); // 3 below: the bed the fill reaches into
            Stage($"band{j1 - j0 + 1}");
            int nb = bi1 - bi0, nby = j1 - j0 + 1;

            // Fluid surface height within each block (-1: not fluid).
            var top = new float[nb * nby * nb];
            int B(int i, int j, int k) => ((j - j0) * nb + (k - bi0)) * nb + (i - bi0);
            for (int k = bi0; k < bi1; k++)
            for (int i = bi0; i < bi1; i++)
            for (int j = j0; j <= j1; j++)
            {
                byte m = mat[Idx(i, j, k)];
                top[B(i, j, k)] = !IsFluid(m, lava) ? -1 : IsFluid(mat[Idx(i, j + 1, k)], lava) ? 1f : FluidHeight(m, lava);
            }
            float TopAt(int i, int j, int k) => i < bi0 || i >= bi1 || k < bi0 || k >= bi1 || j < j0 || j > j1 ? -1 : top[B(i, j, k)];

            // Thin flowing layers resting on the ground follow the smooth terrain, not Minecraft's block steps: for each
            // such block, the smoothed ground height under each fill sample. (Otherwise a stream running down a hill
            // is a staircase of sheets the smooth slope cuts through, showing only their edges.)
            // Within a block the surface height is interpolated from Minecraft's corner heights (shared with the
            // neighbours, as Minecraft's own fluid renderer slopes between them): one continuous surface, no per-block
            // pillows or creases.
            var corner = new float[nb * nby * nb * 4];
            var sheet = new HashSet<int>(); // blocks drawn as a layer lying on the smooth ground
            for (int k = bi0; k < bi1; k++)
            for (int i = bi0; i < bi1; i++)
            for (int j = j0; j <= j1; j++)
            {
                float t = top[B(i, j, k)];
                if (t < 0) continue;
                bool covered = IsFluid(mat[Idx(i, j + 1, k)], lava);
                int c0 = B(i, j, k) * 4;
                corner[c0] = covered ? 1 : CornerFluidHeight(mat, i, j, k, 0, 0, lava);
                corner[c0 + 1] = covered ? 1 : CornerFluidHeight(mat, i, j, k, 1, 0, lava);
                corner[c0 + 2] = covered ? 1 : CornerFluidHeight(mat, i, j, k, 0, 1, lava);
                corner[c0 + 3] = covered ? 1 : CornerFluidHeight(mat, i, j, k, 1, 1, lava);
                // Thin flowing layers and pours onto the next step, resting on the ground: they lie on the smooth slope
                // instead of making Minecraft's staircase of terraces and curtains. Pools and lakes keep their level.
                if (!covered && Mat.IsTerrain(mat[Idx(i, j - 1, k)]) && (t <= 0.7f || t >= 0.99f)) sheet.Add(B(i, j, k));
            }
            float SurfaceAt(int i, int j, int k, float fx, float fz)
            {
                int c0 = B(i, j, k) * 4;
                return (corner[c0] * (1 - fx) + corner[c0 + 1] * fx) * (1 - fz) + (corner[c0 + 2] * (1 - fx) + corner[c0 + 3] * fx) * fz;
            }
            // The smoothed ground under a sheet, at a fill sample's exact position (cached per sample column and block).
            var groundCache = new Dictionary<long, float>();
            float GroundAt(int sx, int sz, int j, float px, float pz)
            {
                long key = ((long)sx * 4096 + sz) * 4096 + j;
                if (!groundCache.TryGetValue(key, out float g)) groundCache[key] = g = FloorY(field, px, pz, j - 1);
                return g;
            }
            const float SheetThickness = 0.16f;

            Stage("fl-prep");
            // Fill samples: 1 inside the fluid (or the rock right beside / under it), 0 outside.
            int nx = nb * R + 1, ny = nby * R + 1;
            var f = new float[nx * ny * nx];
            int S(int x, int y, int z) => (y * nx + z) * nx + x;
            for (int sz = 0; sz < nx; sz++)
            for (int sy = 0; sy < ny; sy++)
            for (int sx = 0; sx < nx; sx++)
            {
                float px = bi0 - 0.5f + (float)sx / R, py = j0 - 0.5f + (float)sy / R, pz = bi0 - 0.5f + (float)sz / R;
                int i = (int)MathF.Floor(px + 0.5f), j = (int)MathF.Floor(py + 0.5f), k = (int)MathF.Floor(pz + 0.5f);
                float local = py - (j - 0.5f);
                float t = TopAt(i, j, k), v = 0;
                if (t >= 0)
                {
                    if (sheet.Contains(B(i, j, k)))
                        v = py <= GroundAt(sx, sz, j, px, pz) + SheetThickness ? 1 : 0; // a layer on the smooth ground
                    else
                        v = local <= SurfaceAt(i, j, k, Math.Clamp(px - (i - 0.5f), 0, 1), Math.Clamp(pz - (k - 0.5f), 0, 1)) ? 1 : 0;
                }
                else
                {
                    // Around the fluid, the fill reaches into the ground so its hidden faces always end up inside the
                    // smooth terrain you see (never as shells or ribbons in the open): under a fluid block (up to 3
                    // blocks down) and beside it, every point inside the smoothed ground is filled.
                    bool solid = Mat.IsTerrain(mat[Idx(Math.Clamp(i, 0, W - 1), Math.Clamp(j, 0, H - 1), Math.Clamp(k, 0, W - 1))]);
                    float above = TopAt(i, j + 1, k);
                    bool under = above >= 0 || TopAt(i, j + 2, k) >= 0 || TopAt(i, j + 3, k) >= 0;
                    float besideTop = -1;
                    foreach (var (dx, dz) in Dirs4) besideTop = MathF.Max(besideTop, TopAt(i + dx, j, k + dz));
                    bool beside = solid && besideTop >= 0 && local <= besideTop;
                    if (under || beside)
                    {
                        if (field.Terrain(new Vector3(px, py, pz)) >= 0.45f) v = 1;
                        else if (above >= 0 && sheet.Contains(B(i, j + 1, k))) v = py <= GroundAt(sx, sz, j + 1, px, pz) + SheetThickness ? 1 : 0;
                        else if (above >= 0 && solid) v = 1; // the bed of a pool: water fills down to the smooth ground
                        else if (beside) v = 1;              // the waterline reaches into the bank
                    }
                }
                f[S(sx, sy, sz)] = v;
            }
            Stage("fl-fill");
            // Blur ([1 2 1] passes): strongly across (5 passes, ~0.6 block), so a one-block step from one fluid level
            // down to the next becomes a spill slope instead of a wall; lightly up and down (2), so thin layers survive.
            var tmp = new float[f.Length];
            foreach (int axis in Axes)
            {
                // Layout (y * nx + z) * nx + x: x stride 1, z stride nx, y stride nx*nx; edges clamp.
                int stride = axis == 0 ? 1 : axis == 2 ? nx : nx * nx, len = axis == 1 ? ny : nx;
                for (int a = 0; a < f.Length; a++)
                {
                    int pos = axis == 0 ? a % nx : axis == 2 ? (a / nx) % nx : a / (nx * nx);
                    float lo = pos > 0 ? f[a - stride] : f[a], hi = pos < len - 1 ? f[a + stride] : f[a];
                    tmp[a] = (lo + 2 * f[a] + hi) * 0.25f;
                }
                (f, tmp) = (tmp, f);
            }

            // Falling columns (fluid with fluid above) keep a solid rounded core through the blur: a lone column stays
            // one unbroken pour, side-by-side columns merge into one curtain instead of thinning into torn strips.
            for (int sz = 0; sz < nx; sz++)
            for (int sy = 0; sy < ny; sy++)
            for (int sx = 0; sx < nx; sx++)
            {
                float px = bi0 - 0.5f + (float)sx / R, py = j0 - 0.5f + (float)sy / R, pz = bi0 - 0.5f + (float)sz / R;
                int i = (int)MathF.Floor(px + 0.5f), j = (int)MathF.Floor(py + 0.5f), k = (int)MathF.Floor(pz + 0.5f);
                if (TopAt(i, j, k) < 0 || TopAt(i, j + 1, k) < 0) continue;
                float dx = MathF.Abs(px - i), dz = MathF.Abs(pz - k);
                if (dx * dx * dx * dx + dz * dz * dz * dz < 0.52f * 0.52f * 0.52f * 0.52f) f[S(sx, sy, sz)] = 1;
            }

            float Sample(Vector3 s) // trilinear, in sample coordinates
            {
                int x = Math.Clamp((int)MathF.Floor(s.X), 0, nx - 2), y = Math.Clamp((int)MathF.Floor(s.Y), 0, ny - 2), z = Math.Clamp((int)MathF.Floor(s.Z), 0, nx - 2);
                float fx = Math.Clamp(s.X - x, 0, 1), fy = Math.Clamp(s.Y - y, 0, 1), fz = Math.Clamp(s.Z - z, 0, 1);
                float L(float a, float b, float tt) => a + (b - a) * tt;
                return L(L(L(f[S(x, y, z)], f[S(x + 1, y, z)], fx), L(f[S(x, y + 1, z)], f[S(x + 1, y + 1, z)], fx), fy),
                         L(L(f[S(x, y, z + 1)], f[S(x + 1, y, z + 1)], fx), L(f[S(x, y + 1, z + 1)], f[S(x + 1, y + 1, z + 1)], fx), fy), fz);
            }

            Stage("fl-blur");
            // Surface Nets: one vertex per cell the surface passes through, quads across sign changes.
            var target = lava ? result.Buildings : result.Water;
            var cellVertex = new int[(nx - 1) * (ny - 1) * (nx - 1)];
            Array.Fill(cellVertex, -1);
            int C(int x, int y, int z) => (y * (nx - 1) + z) * (nx - 1) + x;
            Span<float> c8 = stackalloc float[8];
            int need0 = Math.Max((Pad - bi0) * R - 1, 0), need1 = Math.Min((Pad + 16 - bi0) * R, nx - 1); // cells used by core quads
            for (int cz = need0; cz < need1; cz++)
            for (int cy = 0; cy < ny - 1; cy++)
            for (int cx = need0; cx < need1; cx++)
            {
                int inside = 0;
                for (int c = 0; c < 8; c++) { c8[c] = f[S(cx + (c & 1), cy + ((c >> 1) & 1), cz + ((c >> 2) & 1))]; if (c8[c] > 0.5f) inside++; }
                if (inside == 0 || inside == 8) continue;
                var sum = Vector3.Zero;
                int cnt = 0;
                for (int c = 0; c < 8; c++)
                for (int a = 0; a < 3; a++)
                {
                    int o = c ^ (1 << a);
                    if (o < c || (c8[c] > 0.5f) == (c8[o] > 0.5f)) continue;
                    float tt = (0.5f - c8[c]) / (c8[o] - c8[c]);
                    var pc = new Vector3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
                    var po = new Vector3(o & 1, (o >> 1) & 1, (o >> 2) & 1);
                    sum += pc + (po - pc) * tt;
                    cnt++;
                }
                var sp = new Vector3(cx, cy, cz) + sum / cnt;                 // sample coordinates
                var p = new Vector3(bi0 - 0.5f, j0 - 0.5f, bi0 - 0.5f) + sp / R; // block index coordinates
                const float e = 0.5f;
                var g = new Vector3(Sample(sp + new Vector3(e, 0, 0)) - Sample(sp - new Vector3(e, 0, 0)),
                                    Sample(sp + new Vector3(0, e, 0)) - Sample(sp - new Vector3(0, e, 0)),
                                    Sample(sp + new Vector3(0, 0, e)) - Sample(sp - new Vector3(0, 0, e)));
                var normal = g.LengthSquared() > 1e-10f ? -Vector3.Normalize(g) : Vector3.UnitY;
                cellVertex[C(cx, cy, cz)] = target.Vertices.Count;
                target.Vertices.Add(FluidVertex(p, normal, lava));
            }
            void Quad(int a, int b, int c, int d)
            {
                if (a < 0 || b < 0 || c < 0 || d < 0) return;
                target.Indices.AddRange([(uint)a, (uint)b, (uint)c, (uint)a, (uint)c, (uint)d]);
            }
            Stage("fl-verts");
            // Only edges owned by this chunk's own columns (the neighbours mesh theirs: no seams, no doubles).
            int core0 = (Pad - bi0) * R, core1 = (Pad + 16 - bi0) * R;
            for (int sz = core0; sz < core1; sz++)
            for (int sy = 1; sy < ny - 1; sy++)
            for (int sx = core0; sx < core1; sx++)
            {
                bool here = f[S(sx, sy, sz)] > 0.5f;
                if (here != (f[S(sx + 1, sy, sz)] > 0.5f))
                    Quad(cellVertex[C(sx, sy - 1, sz - 1)], cellVertex[C(sx, sy, sz - 1)], cellVertex[C(sx, sy, sz)], cellVertex[C(sx, sy - 1, sz)]);
                if (here != (f[S(sx, sy + 1, sz)] > 0.5f))
                    Quad(cellVertex[C(sx - 1, sy, sz - 1)], cellVertex[C(sx - 1, sy, sz)], cellVertex[C(sx, sy, sz)], cellVertex[C(sx, sy, sz - 1)]);
                if (here != (f[S(sx, sy, sz + 1)] > 0.5f))
                    Quad(cellVertex[C(sx - 1, sy - 1, sz)], cellVertex[C(sx, sy - 1, sz)], cellVertex[C(sx, sy, sz)], cellVertex[C(sx - 1, sy, sz)]);
            }
        }

        // Flow per fluid block, computed once per chunk (every surface vertex averages several blocks' flow).
        Vector2 CachedFlow(int i, int j, int k, bool lava)
        {
            int key = Idx(i, j, k) * 2 + (lava ? 1 : 0);
            if (!flowCache.TryGetValue(key, out var v)) flowCache[key] = v = BlockFlow(mat, i, j, k, lava);
            return v;
        }

        // A fluid surface vertex: world position, smooth normal, and what the shaders need from the fluid there.
        Vertex FluidVertex(Vector3 p, Vector3 normal, bool lava)
        {
            int i = (int)MathF.Floor(p.X + 0.5f), j = (int)MathF.Floor(p.Y + 0.5f), k = (int)MathF.Floor(p.Z + 0.5f);
            // The fluid block this vertex belongs to: its own, the one below (surfaces sit inside the top block),
            // or a neighbour (the fill reaches into the rock beside).
            (int i, int j, int k) b = (i, j, k);
            foreach (var c in new[] { (i, j, k), (i, j - 1, k), (i + 1, j, k), (i - 1, j, k), (i, j, k + 1), (i, j, k - 1), (i, j + 1, k) })
                if ((uint)c.Item1 < W && (uint)c.Item2 < (uint)H && (uint)c.Item3 < W && IsFluid(mat[Idx(c.Item1, c.Item2, c.Item3)], lava)) { b = c; break; }
            // Flow: a smooth average over the blocks around the vertex (tent weights over ~2 blocks), so the flow field
            // changes gradually; abrupt changes from block to block would show as seams in the flow-mapped ripples.
            var flow = Vector2.Zero;
            float fw = 0;
            for (int dz = -1; dz <= 2; dz++)
            for (int dx = -1; dx <= 2; dx++)
            {
                int ci = (int)MathF.Floor(p.X) + dx, ck = (int)MathF.Floor(p.Z) + dz;
                if (ci < 1 || ci >= W - 1 || ck < 1 || ck >= W - 1) continue;
                float w = MathF.Max(0, 2 - MathF.Abs(ci - p.X)) * MathF.Max(0, 2 - MathF.Abs(ck - p.Z));
                if (w <= 0) continue;
                for (int dy = 0; dy >= -1; dy--) // the surface block, or the one under it
                {
                    if (b.j + dy < 1 || !IsFluid(mat[Idx(ci, b.j + dy, ck)], lava)) continue;
                    flow += CachedFlow(ci, b.j + dy, ck, lava) * w;
                    fw += w;
                    break;
                }
            }
            if (fw > 0) flow /= fw;
            var world = origin + p;
            if (lava)
                return new Vertex { Pos = world, Normal = normal,
                    Data0 = Pack(29, ToByte(flow.X * 0.5f + 0.5f), ToByte(flow.Y * 0.5f + 0.5f), (byte)(normal.Y < 0.5f ? 255 : 0)), Data1 = Pack(0, 0, 0, 255) };
            byte m = mat[Idx(b.i, b.j, b.k)];
            float rush = Mat.IsFalling(m) || Mat.IsFalling(mat[Idx(b.i, Math.Max(b.j - 1, 0), b.k)]) ? 1 : SmoothStep(0.45f, 1f, flow.Length());
            float sky = SmoothStep(0.05f, 0.6f, field.Sky(new Vector3(b.i, b.j + 1, b.k)));
            return new Vertex { Pos = world, Normal = normal,
                Data0 = Pack((byte)Math.Clamp(ColumnDepth(b.i, b.j, b.k) * 8, 0, 255), 0, ToByte(rush), ToByte(sky)), Data1 = PackFlow(flow) };
        }

        foreach (var (sum, n) in lavaGlow.Values)
            if (result.Lights.Count < 96) result.Lights.Add(new Vector4(sum / n, -Math.Min(2.5f + n * 0.3f, 6f))); // negative w: lava
        if (portalCount > 0 && result.Lights.Count < 96)
            result.Lights.Add(new Vector4(portalSum / portalCount, PortalLight + Math.Min(2f + portalCount * 0.25f, 4.5f)));

        Stage("fluids");
        // Leaf cards: every leaf block becomes a cluster of alpha-cut leaf cards. Their lighting normal points
        // out of the canopy (density gradient), so the crown shades as a volume while the edges stay ragged.
        // Ground cover: grass tufts (and tall grass / flowers from Minecraft's plants) sit on the smoothed surface.
        for (int k = Pad; k < Pad + 16; k++)
        for (int i = Pad; i < Pad + 16; i++)
        {
            int wx = bx + i, wz = bz + k;
            // Snow lying on the treetop (snowy biomes) means every tier of this tree carries snow.
            bool snowyColumn = false;
            for (int j = H - 2; j > 0; j--)
            {
                byte t = mat[Idx(i, j, k)];
                if (t is Mat.Air or Mat.Plant or Mat.TallGrass or Mat.Flower) continue;
                snowyColumn = t == Mat.SnowLayer;
                break;
            }
            for (int j = 1; j < H - 1; j++)
            {
                byte leaf = mat[Idx(i, j, k)];
                if (!Mat.IsLeaves(leaf)) continue;
                var local = new Vector3(i, j, k);
                var g = -Gradient(leaves, H, local);
                var n = g.LengthSquared() > 1e-4f ? Vector3.Normalize(g) : Vector3.UnitY;
                float exposure = Math.Clamp(1 - field.Leaves(local - n * 1.2f), 0, 1);
                float above = 0;
                for (int s = 1; s <= 6; s++) above += field.Leaves(local + new Vector3(0, s, 0));
                float ao = (1 - 0.55f * Math.Clamp(above * 0.4f, 0, 1)) * (0.55f + 0.45f * exposure);
                var jitter = new Vector3(Hash(wx, j, wz, 1), Hash(wx, j, wz, 2), Hash(wx, j, wz, 3)) - new Vector3(0.5f);
                var pos = origin + local + jitter * 0.4f;
                // Species in the integer part (+10 when snow lies on this tree), random seed in the fraction.
                int code = Mat.Species(leaf) + (snowyColumn ? 10 : 0);
                result.LeafCards.Add(new Instance { A = new Vector4(pos, code + Hash(wx, j, wz, 4) * 0.999f), B = new Vector4(n, ao) });
                if (Mat.Species(leaf) == Mat.Spruce) // conifers need denser foliage: a second, offset sprig cluster
                    result.LeafCards.Add(new Instance { A = new Vector4(pos - jitter * 0.6f + new Vector3(0, 0.3f, 0), code + Hash(wx, j, wz, 5) * 0.999f), B = new Vector4(n, ao) });
            }

            int top = -1;
            for (int j = H - 2; j > 0; j--)
                if (Mat.IsTerrain(mat[Idx(i, j, k)])) { top = j; break; }
            bool topEdited = top >= 0 && edited[Idx(i, top, k)];
            if (top >= 0 && !topEdited) AddRocks(result, field, mat[Idx(i, top, k)], mat[Idx(i, top + 1, k)], i, k, top, wx, wz, origin);
            // Smooth collision for Minecraft: the surface height at 4x4 points per column, from the same field
            // that is drawn. Only open natural ground; water, builds, thin pillars and far-off surfaces stay vanilla.
            {
                int col = (k - Pad) * 16 + (i - Pad);
                result.CollisionTops[col] = short.MinValue;
                bool editedNear = false; // around your edits Minecraft keeps its exact blocks
                for (int j = Math.Max(0, top - 4); top >= 0 && j <= Math.Min(H - 1, top + 2); j++) editedNear |= edited[Idx(i, j, k)];
                if (top >= 0 && !IsThin(i, top, k) && !editedNear
                    && mat[Idx(i, top + 1, k)] is Mat.Air or Mat.Plant or Mat.TallGrass or Mat.Flower or Mat.SnowLayer)
                {
                    int topY = y0 + top;
                    bool ok = true;
                    for (int b = 0; b < 4 && ok; b++)
                    for (int a = 0; a < 4 && ok; a++)
                    {
                        float ly = SurfaceY(field, i - 0.5f + (a + 0.5f) / 4, k - 0.5f + (b + 0.5f) / 4, top);
                        // Never above the real block top: rounding down smooths hills; raising into air would fill
                        // dug holes and dips with invisible floor.
                        float worldY = MathF.Min(y0 + 0.5f + ly, topY + 1);
                        ok = worldY >= topY - 3f && worldY <= topY + 3f; // within the T-4 .. T+2 blocks the mod reshapes
                        result.CollisionHeights[col * 16 + b * 4 + a] = (short)MathF.Round(worldY * 16);
                    }
                    if (ok) result.CollisionTops[col] = (short)topY;
                }
                // Floors further down the column (caves the camera has revealed, ledges under overhangs): the same
                // smooth surface heights, so you can walk up the slopes you see underground too.
                for (int j = (top >= 0 ? top : H - 1) - 1; j >= 2 && result.CollisionFloors.Count < 4096; j--)
                {
                    int n = Idx(i, j, k);
                    if (!Mat.IsTerrain(mat[n]) || edited[n] || IsThin(i, j, k)) continue;
                    if (mat[Idx(i, j + 1, k)] is not (Mat.Air or Mat.Plant or Mat.TallGrass or Mat.Flower or Mat.SnowLayer)) continue;
                    if (field.Terrain(new Vector3(i, j + 1, k)) >= 0.5f) continue; // smoothing closed this gap: stays vanilla
                    bool editedClose = false;
                    for (int y = Math.Max(0, j - 4); y <= Math.Min(H - 1, j + 2); y++) editedClose |= edited[Idx(i, y, k)];
                    if (editedClose) continue;
                    int floorY = y0 + j;
                    var heights = new short[16];
                    bool good = true;
                    for (int b = 0; b < 4 && good; b++)
                    for (int a = 0; a < 4 && good; a++)
                    {
                        float ly = FloorY(field, i - 0.5f + (a + 0.5f) / 4, k - 0.5f + (b + 0.5f) / 4, j);
                        float worldY = MathF.Min(y0 + 0.5f + ly, floorY + 1);
                        good = worldY >= floorY - 3f && worldY <= floorY + 3f;
                        heights[b * 4 + a] = (short)MathF.Round(worldY * 16);
                    }
                    if (good) result.CollisionFloors.Add(((short)((k - Pad) * 16 + (i - Pad)), (short)floorY, heights));
                }
            }
            // Cave ceilings: moss and vines hang from the rock in patches (grass instances of kind 3, growing down).
            for (int j = 2; j < Math.Min(top, H - 1); j++)
            {
                int n = Idx(i, j, k);
                if (!Mat.IsTerrain(mat[n]) || edited[n] || mat[Idx(i, j - 1, k)] != Mat.Air) continue;
                if (field.Sky(new Vector3(i, j - 1, k)) > 0.1f) continue; // only under cover, deep in caves
                // Patches: clusters of a few blocks, most of the ceiling bare.
                float patch = Hash(wx >> 2, y0 + j, wz >> 2, 21);
                if (patch < 0.55f || Hash(wx, y0 + j, wz, 22) < 0.35f) continue;
                // The smoothed ceiling surface above this air cell.
                float y = j - 1;
                while (y < j + 1 && field.Terrain(new Vector3(i, y, k)) < 0.5f) y += 0.125f;
                if (y >= j + 1) continue;
                int strands = 3 + (int)(Hash(wx, y0 + j, wz, 23) * 5);
                for (int t = 0; t < strands; t++)
                {
                    float u = Hash(wx, wz, t + y0 + j, 24), v = Hash(wx, wz, t + y0 + j, 25);
                    float len = (0.5f + 1.8f * Hash(wx, wz, t + y0 + j, 26)) * (0.6f + 0.6f * patch);
                    var pos = origin + new Vector3(i - 0.5f + u, y + 0.1f, k - 0.5f + v);
                    result.Grass.Add(new Instance { A = new Vector4(pos, Hash(wx, wz, t, 27)), B = new Vector4(-len, 3, Hash(wx, wz, t, 28), 1) });
                }
            }

            if (top < 0 || topEdited || mat[Idx(i, top, k)] != Mat.Grass) continue;
            byte cover = mat[Idx(i, top + 1, k)];
            if (cover is not (Mat.Air or Mat.Plant or Mat.TallGrass or Mat.Flower)) continue;

            float h00 = SurfaceY(field, i - 0.5f, k - 0.5f, top), h10 = SurfaceY(field, i + 0.5f, k - 0.5f, top);
            float h01 = SurfaceY(field, i - 0.5f, k + 0.5f, top), h11 = SurfaceY(field, i + 0.5f, k + 0.5f, top);
            float slope = MathF.Sqrt(MathF.Pow((h10 + h11 - h00 - h01) / 2, 2) + MathF.Pow((h01 + h11 - h00 - h10) / 2, 2));
            if (slope > 1.0f) continue; // too steep: bare soil and rock

            var centre = new Vector3(i, (h00 + h10 + h01 + h11) / 4, k);
            float occ = 0.5f * field.Terrain(centre + Vector3.UnitY * 1.5f) + 0.3f * field.Terrain(centre + Vector3.UnitY * 3f);
            float canopy = 0;
            for (int s = 2; s <= 12; s += 2) canopy += field.Leaves(centre + new Vector3(0, s, 0));
            float groundAo = (1 - 0.85f * SmoothStep(0.05f, 0.55f, occ)) * (1 - 0.55f * Math.Clamp(canopy * 0.6f, 0, 1));
            float colorSeed = Hash(wx, top, wz, 9);

            int tufts = cover == Mat.TallGrass ? 12 : 9;
            for (int t = 0; t < tufts; t++)
            {
                float u = Hash(wx, wz, t, 5), v = Hash(wx, wz, t, 6);
                float h = (h00 * (1 - u) + h10 * u) * (1 - v) + (h01 * (1 - u) + h11 * u) * v;
                // On a rounded cliff lip the true surface falls away from the block average: skip tufts that would float.
                float exact = SurfaceY(field, i - 0.5f + u, k - 0.5f + v, top);
                if (MathF.Abs(exact - h) > 0.2f) continue;
                h = exact;
                var pos = origin + new Vector3(i - 0.5f + u, h - 0.05f, k - 0.5f + v);
                float kind = 0, height = 0.38f * (1 - slope * 0.5f) * (0.7f + 0.6f * Hash(wx, wz, t, 7));
                if (cover == Mat.TallGrass && t < 5) { kind = 1; height = 0.75f + 0.45f * Hash(wx, wz, t, 8); }
                if (cover == Mat.Flower && t < 1) { kind = 2; height = 0.45f + 0.15f * Hash(wx, wz, t, 8); }
                result.Grass.Add(new Instance { A = new Vector4(pos, Hash(wx, wz, t, 4)), B = new Vector4(height, kind, colorSeed, groundAo) });
            }
        }

        Stage("foliage");
        if (Realism.Live.MinecraftBridge.TraceTimes && stageClock.Elapsed.TotalMilliseconds > 150)
            Console.WriteLine($"[trace] slow mesh {chunk.X},{chunk.Z} H={H}:{stages}");
        foreach (var mesh in new[] { result.Terrain, result.Leaves, result.Wood, result.Water, result.Buildings, result.Glass, result.ShadowCanopy, result.Blocks, result.BlocksTranslucent, result.Ores })
        foreach (var v in mesh.Vertices)
        {
            result.Min = Vector3.Min(result.Min, v.Pos);
            result.Max = Vector3.Max(result.Max, v.Pos);
        }
        foreach (var inst in result.Grass.Concat(result.LeafCards).Concat(result.Rocks).Concat(result.Pebbles))
        {
            var p = new Vector3(inst.A.X, inst.A.Y, inst.A.Z);
            result.Min = Vector3.Min(result.Min, p - new Vector3(2.6f));
            result.Max = Vector3.Max(result.Max, p + new Vector3(2.6f));
        }
        return result;
    }

    delegate (uint d0, uint d1) VertexAttrib(int i, int j, int k, Vector3 localPos, Vector3 normal);

    /// <summary>
    /// Surface Nets: one vertex per cell the iso-surface passes through (average of its edge crossings),
    /// one quad per sample edge that crosses the surface, joining the four cells around that edge.
    /// Only edges starting inside this chunk's 16x16 footprint are emitted, so neighbouring chunks tile seamlessly.
    /// </summary>
    static void Polygonize(float[] d, float[] normalField, int H, float iso, Vector3 origin, MeshData mesh, VertexAttrib attrib)
    {
        int Idx(int i, int j, int k) => (j * W + k) * W + i;
        var cellVertex = new int[W * H * W];
        Array.Fill(cellVertex, -1);
        Span<float> c = stackalloc float[8];

        for (int j = 0; j < H - 1; j++)
        for (int k = Pad - 1; k < Pad + 16; k++)
        for (int i = Pad - 1; i < Pad + 16; i++)
        {
            int mask = 0;
            for (int n = 0; n < 8; n++)
            {
                c[n] = d[Idx(i + (n & 1), j + ((n >> 1) & 1), k + ((n >> 2) & 1))];
                if (c[n] >= iso) mask |= 1 << n;
            }
            if (mask == 0 || mask == 255) continue;

            Vector3 sum = Vector3.Zero;
            int count = 0;
            for (int n = 0; n < 8; n++)
            for (int bit = 1; bit <= 4; bit <<= 1)
            {
                if ((n & bit) != 0) continue;
                int m = n | bit;
                if (((mask >> n) & 1) == ((mask >> m) & 1)) continue;
                float t = (iso - c[n]) / (c[m] - c[n]);
                var a = new Vector3(n & 1, (n >> 1) & 1, (n >> 2) & 1);
                var b = new Vector3(m & 1, (m >> 1) & 1, (m >> 2) & 1);
                sum += a + (b - a) * t;
                count++;
            }
            var local = new Vector3(i, j, k) + sum / count;
            var normal = -Gradient(normalField, H, local);
            normal = normal.LengthSquared() > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitY;
            var (d0, d1) = attrib(i, j, k, local, normal);

            cellVertex[Idx(i, j, k)] = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vertex { Pos = origin + local, Normal = normal, Data0 = d0, Data1 = d1 });
        }

        Span<int> quad = stackalloc int[4];
        for (int j = 1; j < H - 1; j++)
        for (int k = Pad; k < Pad + 16; k++)
        for (int i = Pad; i < Pad + 16; i++)
        {
            bool inside0 = d[Idx(i, j, k)] >= iso;
            for (int a = 0; a < 3; a++)
            {
                var e = Axis[a];
                if (j + e.y >= H) continue;
                bool inside1 = d[Idx(i + e.x, j + e.y, k + e.z)] >= iso;
                if (inside0 == inside1) continue;

                var eb = Axis[(a + 1) % 3];
                var ec = Axis[(a + 2) % 3];
                bool ok = true;
                for (int q = 0; q < 4 && ok; q++)
                {
                    int ub = q == 1 || q == 2 ? 1 : 0, uc = q >= 2 ? 1 : 0;
                    quad[q] = cellVertex[Idx(i - ub * eb.x - uc * ec.x, j - ub * eb.y - uc * ec.y, k - ub * eb.z - uc * ec.z)];
                    ok = quad[q] >= 0;
                }
                if (!ok) continue;

                // The cyclic order (0,0),(1,0),(1,1),(0,1) over (b,c) winds counter-clockwise around +a
                // (since b x c = a). Facing must point from solid to air, so flip when the solid side is +a.
                // This is purely topological; deriving it from vertex positions fails on skewed quads.
                bool flip = !inside0;

                // Split along the shorter diagonal for smoother shading.
                var v = mesh.Vertices;
                bool diag02 = Vector3.DistanceSquared(v[quad[0]].Pos, v[quad[2]].Pos) <= Vector3.DistanceSquared(v[quad[1]].Pos, v[quad[3]].Pos);
                if (diag02) { AddTri(mesh, quad[0], quad[1], quad[2], flip); AddTri(mesh, quad[0], quad[2], quad[3], flip); }
                else { AddTri(mesh, quad[0], quad[1], quad[3], flip); AddTri(mesh, quad[1], quad[2], quad[3], flip); }
            }
        }
    }

    static void AddTri(MeshData m, int a, int b, int c, bool flip)
    {
        m.Indices.Add((uint)a);
        m.Indices.Add((uint)(flip ? c : b));
        m.Indices.Add((uint)(flip ? b : c));
    }

    static Vector3 Gradient(float[] d, int H, Vector3 p)
    {
        // Trilinear blend of central-difference gradients at the 8 surrounding samples.
        int i = (int)MathF.Floor(p.X), j = (int)MathF.Floor(p.Y), k = (int)MathF.Floor(p.Z);
        float fx = p.X - i, fy = p.Y - j, fz = p.Z - k;
        Vector3 g = Vector3.Zero;
        for (int n = 0; n < 8; n++)
        {
            int x = i + (n & 1), y = j + ((n >> 1) & 1), z = k + ((n >> 2) & 1);
            float w = ((n & 1) != 0 ? fx : 1 - fx) * (((n >> 1) & 1) != 0 ? fy : 1 - fy) * (((n >> 2) & 1) != 0 ? fz : 1 - fz);
            g += w * new Vector3(
                At(d, H, x + 1, y, z) - At(d, H, x - 1, y, z),
                At(d, H, x, y + 1, z) - At(d, H, x, y - 1, z),
                At(d, H, x, y, z + 1) - At(d, H, x, y, z - 1));
        }
        return g;
    }

    static float At(float[] d, int H, int i, int j, int k)
    {
        i = Math.Clamp(i, 0, W - 1); j = Math.Clamp(j, 0, H - 1); k = Math.Clamp(k, 0, W - 1);
        return d[(j * W + k) * W + i];
    }

    /// <summary>Trilinear lookups into the blurred density volumes, in array-local coordinates.</summary>
    sealed class Field(float[] terrain, float[] leaves, int h, float[] sky)
    {
        public float Terrain(Vector3 p) => Sample(terrain, p);
        public float Leaves(Vector3 p) => Sample(leaves, p);
        public float Sky(Vector3 p) => Sample(sky, p);

        float Sample(float[] d, Vector3 p)
        {
            int i = (int)MathF.Floor(p.X), j = (int)MathF.Floor(p.Y), k = (int)MathF.Floor(p.Z);
            float fx = p.X - i, fy = p.Y - j, fz = p.Z - k;
            float r = 0;
            for (int n = 0; n < 8; n++)
            {
                float w = ((n & 1) != 0 ? fx : 1 - fx) * (((n >> 1) & 1) != 0 ? fy : 1 - fy) * (((n >> 2) & 1) != 0 ? fz : 1 - fz);
                r += w * At(d, h, i + (n & 1), j + ((n >> 1) & 1), k + ((n >> 2) & 1));
            }
            return r;
        }
    }

    /// <summary>Separable [1 2 1]/4 blur along all three axes (edges clamp).</summary>
    static void Blur(float[] d, float[] tmp, int H)
    {
        int Idx(int i, int j, int k) => (j * W + k) * W + i;
        for (int j = 0; j < H; j++)
        for (int k = 0; k < W; k++)
        for (int i = 0; i < W; i++)
            tmp[Idx(i, j, k)] = 0.25f * d[Idx(Math.Max(i - 1, 0), j, k)] + 0.5f * d[Idx(i, j, k)] + 0.25f * d[Idx(Math.Min(i + 1, W - 1), j, k)];
        for (int j = 0; j < H; j++)
        for (int k = 0; k < W; k++)
        for (int i = 0; i < W; i++)
            d[Idx(i, j, k)] = 0.25f * tmp[Idx(i, j, Math.Max(k - 1, 0))] + 0.5f * tmp[Idx(i, j, k)] + 0.25f * tmp[Idx(i, j, Math.Min(k + 1, W - 1))];
        for (int j = 0; j < H; j++)
        for (int k = 0; k < W; k++)
        for (int i = 0; i < W; i++)
            tmp[Idx(i, j, k)] = 0.25f * d[Idx(i, Math.Max(j - 1, 0), k)] + 0.5f * d[Idx(i, j, k)] + 0.25f * d[Idx(i, Math.Min(j + 1, H - 1), k)];
        Array.Copy(tmp, d, d.Length);
    }

    static void AddTrunk(MeshData m, Vector3 bottom, float ao, int species, float extendDown = 0)
    {
        const int Sides = 8;
        float Radius = species == Mat.Birch ? 0.22f : species is Mat.Jungle or Mat.DarkOak ? 0.36f : 0.3f;
        uint baseIdx = (uint)m.Vertices.Count;
        uint data = Pack((byte)species, 0, 0, ToByte(ao));
        for (int s = 0; s <= Sides; s++)
        {
            float a = s * MathF.Tau / Sides;
            var n = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            m.Vertices.Add(new Vertex { Pos = bottom + n * Radius - new Vector3(0, extendDown, 0), Normal = n, Data1 = data });
            m.Vertices.Add(new Vertex { Pos = bottom + n * Radius + Vector3.UnitY, Normal = n, Data1 = data });
        }
        for (uint s = 0; s < Sides; s++)
        {
            uint a = baseIdx + s * 2;
            m.Indices.AddRange([a, a + 1, a + 2, a + 2, a + 1, a + 3]);
        }
    }

    /// <summary>
    /// Horizontal water quad on top of the block at <paramref name="p"/>. Heights and depths are per corner
    /// (x0z0, x1z0, x1z1, x0z1) so flowing water slopes and shallows grade smoothly. Data0: depth, frozen flag, rush, open sky.
    /// </summary>
    static void AddWaterQuad(MeshData m, Vector3 p, ReadOnlySpan<float> cornerHeight, ReadOnlySpan<float> cornerDepth, bool frozen, bool facingDown = false, float rush = 0, float sky = 1,
        ReadOnlySpan<Vector2> flow = default)
    {
        uint b = (uint)m.Vertices.Count;
        ReadOnlySpan<Vector3> offsets = [new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)];
        for (int c = 0; c < 4; c++)
        {
            uint data = Pack((byte)Math.Clamp(cornerDepth[c] * 8, 0, 255), (byte)(frozen ? 255 : 0), ToByte(rush), ToByte(sky));
            var f = flow.IsEmpty ? Vector2.Zero : flow[c];
            m.Vertices.Add(new Vertex
            {
                Pos = p + offsets[c] + new Vector3(0, cornerHeight[c], 0),
                Normal = facingDown ? -Vector3.UnitY : Vector3.UnitY, Data0 = data, Data1 = PackFlow(f),
            });
        }
        if (facingDown) m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        else m.Indices.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
    }

    /// <summary>Vertical water face from the block bottom up to the surface heights at its two top corners.</summary>
    static void AddWaterSide(MeshData m, Vector3 p0, Vector3 p1, float h0, float h1, Vector3 normal, bool frozen, float sky, float rush = 1)
    {
        uint b = (uint)m.Vertices.Count;
        uint data = Pack(0, (byte)(frozen ? 255 : 0), ToByte(rush), ToByte(sky));
        foreach (var p in new[] { p0, p1, p1 + new Vector3(0, h1, 0), p0 + new Vector3(0, h0, 0) })
            m.Vertices.Add(new Vertex { Pos = p, Normal = normal, Data0 = data, Data1 = PackFlow(Vector2.Zero) });
        // Wind so the face points along the normal.
        var n = Vector3.Cross(p1 - p0, Vector3.UnitY);
        if (Vector3.Dot(n, normal) > 0) m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        else m.Indices.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
    }

    /// <summary>
    /// Scatters rocks on the smoothed surface: pebbles on gravel, scree and boulders on exposed stone,
    /// the odd rock in grass and dirt, a few on the seabed. Rocks sink partly into the ground.
    /// </summary>
    static void AddRocks(ChunkMeshes result, Field field, byte ground, byte above, int i, int k, int top, int wx, int wz, Vector3 origin)
    {
        if (above is Mat.SnowLayer || Mat.IsLeaves(above) || Mat.IsWood(above)) return;
        (float chance, float minSize, float maxSize) = Mat.Channel(ground) switch
        {
            5 => (0.9f, 0.06f, 0.22f),   // gravel: pebbles
            2 => (0.22f, 0.2f, 0.9f),    // stone: scree and rocks
            1 => (0.06f, 0.1f, 0.45f),   // dirt
            0 => (0.025f, 0.15f, 0.6f),  // grass
            3 => (0.01f, 0.08f, 0.25f),  // sand
            _ => (0f, 0f, 0f),
        };
        if (Mat.IsWater(above)) { chance = Math.Min(chance, 0.02f); maxSize = Math.Max(maxSize, 0.6f); } // a few on the seabed
        int count = chance >= 0.5f ? 3 : 1;
        for (int n = 0; n < count; n++)
        {
            if (Hash(wx, wz, n, 20) >= chance) continue;
            float u = Hash(wx, wz, n, 21), v = Hash(wx, wz, n, 22);
            float x = i - 0.5f + u, z = k - 0.5f + v;
            float y = SurfaceY(field, x, z, top);
            float size = minSize + (maxSize - minSize) * MathF.Pow(Hash(wx, wz, n, 23), 2.5f);
            // Rare large boulders on rocky ground.
            if (Mat.Channel(ground) == 2 && Hash(wx, wz, n, 24) < 0.08f) size = 1.2f + 1.3f * Hash(wx, wz, n, 25);
            var scale = new Vector3(size * (0.8f + 0.5f * Hash(wx, wz, n, 26)), size * (0.45f + 0.35f * Hash(wx, wz, n, 27)), size * (0.8f + 0.5f * Hash(wx, wz, n, 28)));
            var local = new Vector3(x, y, z);
            float occ = 0.5f * field.Terrain(local + Vector3.UnitY * 1.5f) + 0.3f * field.Terrain(local + Vector3.UnitY * 3f);
            float ao = 1 - 0.7f * SmoothStep(0.05f, 0.55f, occ);
            var pos = origin + local - new Vector3(0, scale.Y * 0.35f, 0);
            var rock = new Instance { A = new Vector4(pos, Hash(wx, wz, n, 29)), B = new Vector4(scale, ao) };
            (size < 0.3f ? result.Pebbles : result.Rocks).Add(rock);
        }
    }

    /// <summary>
    /// A man-made block as crisp boxes: full cube, bottom slab, or a post with rails/panels joining its neighbours.
    /// Faces against opaque neighbours are culled; boundary faces get classic per-corner voxel AO.
    /// </summary>
    static void AddBuiltBlock(ChunkMeshes result, byte[] mat, int H, int i, int j, int k, Vector3 basePos, int appearanceOverride = -1,
        Func<int, int, int, bool>? isSolid = null)
    {
        byte m = mat[(j * W + k) * W + i];
        int appearance = appearanceOverride >= 0 ? appearanceOverride : Mat.Appearance(m);
        int shape = appearanceOverride >= 0 ? Mat.ShapeFull : Mat.Shape(m);
        var mesh = appearance == Mat.Glass ? result.Glass : result.Buildings;
        bool Solid(int x, int y, int z) => isSolid?.Invoke(x, y, z) ?? (y >= 0 && y < H && Mat.IsOpaqueCube(mat[(y * W + z) * W + x]));

        if (shape == Mat.ShapeFull) { AddBox(mesh, Vector3.Zero, Vector3.One); return; }
        if (shape == Mat.ShapeSlab) { AddBox(mesh, Vector3.Zero, new Vector3(1, 0.5f, 1)); return; }

        // Post: centre column plus a connection toward each horizontal neighbour that is a post or a solid block.
        bool panel = appearance is Mat.Glass or Mat.Metal; // glass panes and iron bars are full-height panels
        float r = panel ? 0.0625f : 0.125f;
        AddBox(mesh, new Vector3(0.5f - r, 0, 0.5f - r), new Vector3(0.5f + r, 1, 0.5f + r));
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            byte n = mat[(j * W + k + dz) * W + i + dx];
            if (!(Mat.IsBuilt(n) && Mat.Shape(n) == Mat.ShapePost) && !Mat.IsOpaqueCube(n)) continue;
            var lo = new Vector3(dx > 0 ? 0.5f : dx < 0 ? 0 : 0.5f - r * 0.7f, 0, dz > 0 ? 0.5f : dz < 0 ? 0 : 0.5f - r * 0.7f);
            var hi = new Vector3(dx > 0 ? 1 : dx < 0 ? 0.5f : 0.5f + r * 0.7f, 1, dz > 0 ? 1 : dz < 0 ? 0.5f : 0.5f + r * 0.7f);
            if (panel) AddBox(mesh, lo, hi);
            else
            {
                AddBox(mesh, lo with { Y = 0.38f }, hi with { Y = 0.55f });
                AddBox(mesh, lo with { Y = 0.75f }, hi with { Y = 0.92f });
            }
        }

        void AddBox(MeshData target, Vector3 lo, Vector3 hi)
        {
            for (int a = 0; a < 3; a++)
            for (int s = -1; s <= 1; s += 2)
            {
                int u = (a + 1) % 3, v = (a + 2) % 3;
                var na = Axis3(a) * s;
                bool onBoundary = s > 0 ? hi[a] >= 1 : lo[a] <= 0;
                int ni = i + (int)na.X, nj = j + (int)na.Y, nk = k + (int)na.Z;
                if (onBoundary && Solid(ni, nj, nk)) continue;

                uint b = (uint)target.Vertices.Count;
                ReadOnlySpan<(int cu, int cv)> corners = s > 0 ? [(0, 0), (1, 0), (1, 1), (0, 1)] : [(0, 0), (0, 1), (1, 1), (1, 0)];
                foreach (var (cu, cv) in corners)
                {
                    var p = Vector3.Zero;
                    p[a] = s > 0 ? hi[a] : lo[a];
                    p[u] = cu == 1 ? hi[u] : lo[u];
                    p[v] = cv == 1 ? hi[v] : lo[v];

                    float ao = 1;
                    if (onBoundary)
                    {
                        var du = Axis3(u) * (cu * 2 - 1);
                        var dv = Axis3(v) * (cv * 2 - 1);
                        bool s1 = Solid(ni + (int)du.X, nj + (int)du.Y, nk + (int)du.Z);
                        bool s2 = Solid(ni + (int)dv.X, nj + (int)dv.Y, nk + (int)dv.Z);
                        bool c = Solid(ni + (int)(du.X + dv.X), nj + (int)(du.Y + dv.Y), nk + (int)(du.Z + dv.Z));
                        int occ = s1 && s2 ? 3 : (s1 ? 1 : 0) + (s2 ? 1 : 0) + (c ? 1 : 0);
                        ao = 1 - occ * 0.18f;
                    }
                    target.Vertices.Add(new Vertex
                    {
                        Pos = basePos + p, Normal = na,
                        Data0 = Pack((byte)appearance, 0, 0, 0), Data1 = Pack(0, 0, 0, ToByte(ao)),
                    });
                }
                // Split along the brighter diagonal so AO gradients don't crease.
                var vs = target.Vertices;
                int bi = (int)b;
                bool flipDiag = vs[bi].Data1 >> 24 < vs[bi + 1].Data1 >> 24 && vs[bi + 2].Data1 >> 24 < vs[bi + 3].Data1 >> 24
                    || (vs[bi].Data1 >> 24) + (vs[bi + 2].Data1 >> 24) < (vs[bi + 1].Data1 >> 24) + (vs[bi + 3].Data1 >> 24);
                if (flipDiag) target.Indices.AddRange([b + 1, b + 2, b + 3, b + 1, b + 3, b]);
                else target.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            }
        }
    }

    /// <summary>An axis-aligned box with all six faces, no culling or AO (small props like torches).</summary>
    static void AddPlainBox(MeshData m, Vector3 lo, Vector3 hi, int appearance)
    {
        for (int a = 0; a < 3; a++)
        for (int s = -1; s <= 1; s += 2)
        {
            int u = (a + 1) % 3, v = (a + 2) % 3;
            uint b = (uint)m.Vertices.Count;
            ReadOnlySpan<(int cu, int cv)> corners = s > 0 ? [(0, 0), (1, 0), (1, 1), (0, 1)] : [(0, 0), (0, 1), (1, 1), (1, 0)];
            foreach (var (cu, cv) in corners)
            {
                var p = Vector3.Zero;
                p[a] = s > 0 ? hi[a] : lo[a];
                p[u] = cu == 1 ? hi[u] : lo[u];
                p[v] = cv == 1 ? hi[v] : lo[v];
                m.Vertices.Add(new Vertex { Pos = p, Normal = Axis3(a) * s, Data0 = Pack((byte)appearance, 0, 0, 0), Data1 = Pack(0, 0, 0, 255) });
            }
            m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
    }

    /// <summary>
    /// A lava block: a molten surface just below the block top (sources sit at 7/8), sides where it spills into
    /// open space. Where it meets smooth terrain the surface reaches under the bank so no seam shows.
    /// Drawn with the buildings (appearance 29: emissive in the shader).
    /// </summary>
    static void AddLava(ChunkMeshes result, byte[] mat, int i, int j, int k, Vector3 basePos, Dictionary<(int, int, int), (Vector3 sum, int n)> glow, bool geometry = true)
    {
        var scratch = new MeshData();
        static int Idx(int x, int y, int z) => (y * W + z) * W + x;
        const byte LavaLook = 29;
        var m = geometry ? result.Buildings : scratch; // without geometry: only the glow and embers
        uint data0 = Pack(LavaLook, 128, 128, 0), data1 = Pack(0, 0, 0, 255);
        byte self = mat[Idx(i, j, k)], above = mat[Idx(i, j + 1, k)];
        bool covered = Mat.IsLava(above);
        float h = covered ? 1f : Mat.LavaHeight(self);
        bool exposed = false;
        if (!covered && !Mat.IsOpaqueCube(above))
        {
            // Sloped like flowing lava in Minecraft (corner heights averaged with the neighbours), with the flow
            // direction at each corner (the shader carries the crust along it).
            float Ext(int dx, int dz) => Mat.IsTerrain(mat[Idx(i + dx, j, k + dz)]) ? 0.4f : 0f;
            float x0 = basePos.X - Ext(-1, 0), x1 = basePos.X + 1 + Ext(1, 0);
            float z0 = basePos.Z - Ext(0, -1), z1 = basePos.Z + 1 + Ext(0, 1);
            ReadOnlySpan<(int x, int z)> corner = [(0, 0), (0, 1), (1, 1), (1, 0)];
            ReadOnlySpan<Vector3> at = [new(x0, 0, z0), new(x0, 0, z1), new(x1, 0, z1), new(x1, 0, z0)];
            uint b = (uint)m.Vertices.Count;
            for (int c = 0; c < 4; c++)
            {
                float ch = CornerFluidHeight(mat, i, j, k, corner[c].x, corner[c].z, lava: true);
                var f = CornerFlow(mat, i, j, k, corner[c].x, corner[c].z, lava: true);
                m.Vertices.Add(new Vertex { Pos = at[c] + new Vector3(0, basePos.Y + ch, 0), Normal = Vector3.UnitY,
                    Data0 = Pack(LavaLook, ToByte(f.X * 0.5f + 0.5f), ToByte(f.Y * 0.5f + 0.5f), 0), Data1 = data1 });
            }
            m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            exposed = true;
            // Now and then an exposed lava surface throws up embers.
            if (Hash((int)basePos.X, (int)basePos.Y, (int)basePos.Z, 31) < 0.12f && result.Emitters.Count < 64)
                result.Emitters.Add(new Vector4(basePos.X + 0.5f, basePos.Y + h, basePos.Z + 0.5f, -1));
        }
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            byte nb = mat[Idx(i + dx, j, k + dz)];
            // Against lava the face shows only where this block stands higher (a flow stepping down).
            float nh = Mat.IsLava(nb) ? (Mat.IsLava(mat[Idx(i + dx, j + 1, k + dz)]) ? 1f : Mat.LavaHeight(nb)) : 0f;
            if (nh >= h - 0.01f || Mat.IsOpaqueCube(nb)) continue;
            var n = new Vector3(dx, 0, dz);
            data0 = Pack(LavaLook, 128, 128, 255); // sides: falling lava (w = 1 tells the shader it pours down)
            var centre = basePos + new Vector3(0.5f, 0, 0.5f) + n * 0.5f;
            var t = new Vector3(-dz, 0, dx) * 0.5f;
            uint b = (uint)m.Vertices.Count;
            foreach (var c in new[] { centre - t + new Vector3(0, nh, 0), centre + t + new Vector3(0, nh, 0), centre + t + new Vector3(0, h, 0), centre - t + new Vector3(0, h, 0) })
                m.Vertices.Add(new Vertex { Pos = c, Normal = n, Data0 = data0, Data1 = data1 });
            // Wind so the face points along its normal.
            if (Vector3.Dot(Vector3.Cross(t, Vector3.UnitY), n) > 0) m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            else m.Indices.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
            exposed = true;
        }
        if (!exposed) return;
        var key = ((int)basePos.X >> 2, (int)MathF.Floor(basePos.Y) >> 2, (int)basePos.Z >> 2);
        glow.TryGetValue(key, out var g);
        glow[key] = (g.sum + basePos + new Vector3(0.5f, h + 0.6f, 0.5f), g.n + 1);
    }

    static readonly (int dx, int dz)[] Dirs4 = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    /// <summary>A light's w at or above this is a nether portal's violet glow (strength = w - PortalLight).</summary>
    public const float PortalLight = 1000;
    static readonly int[] Axes = [0, 2, 1, 0, 2, 1, 0, 2, 0, 2]; // fluid blur passes: across x/z five times, y twice

    static uint PackFlow(Vector2 f) => Pack(ToByte(f.X * 0.5f + 0.5f), ToByte(f.Y * 0.5f + 0.5f), 0, 0);

    static bool IsFluid(byte m, bool lava) => lava ? Mat.IsLava(m) : Mat.IsWater(m) && m != Mat.FrozenWater;
    static float FluidHeight(byte m, bool lava) => lava ? Mat.LavaHeight(m) : Mat.WaterHeight(m);

    /// <summary>Fluid surface height at a block corner (as Minecraft computes it): the columns around it averaged,
    /// full height next to a falling column.</summary>
    static float CornerFluidHeight(byte[] mat, int i, int j, int k, int cx, int cz, bool lava)
    {
        static int Idx(int x, int y, int z) => (y * W + z) * W + x;
        float sum = 0;
        int n = 0;
        for (int dz = cz - 1; dz <= cz; dz++)
        for (int dx = cx - 1; dx <= cx; dx++)
        {
            byte f = mat[Idx(i + dx, j, k + dz)];
            if (!IsFluid(f, lava)) continue;
            if (IsFluid(mat[Idx(i + dx, j + 1, k + dz)], lava)) return 1f;
            sum += FluidHeight(f, lava);
            n++;
        }
        return n > 0 ? sum / n : FluidHeight(mat[Idx(i, j, k)], lava);
    }

    /// <summary>
    /// Which way a fluid block runs, as Minecraft's flow: toward lower neighbours, and strongly toward open edges
    /// it pours over. Length 0 (still) .. 1 (rushing).
    /// </summary>
    static Vector2 BlockFlow(byte[] mat, int i, int j, int k, bool lava)
    {
        static int Idx(int x, int y, int z) => (y * W + z) * W + x;
        byte self = mat[Idx(i, j, k)];
        if (!IsFluid(self, lava) || IsFluid(mat[Idx(i, j + 1, k)], lava)) return Vector2.Zero; // under the surface
        float hs = FluidHeight(self, lava);
        var flow = Vector2.Zero;
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            if ((uint)(i + dx) >= W || (uint)(k + dz) >= W) continue;
            byte nb = mat[Idx(i + dx, j, k + dz)];
            float drop;
            if (IsFluid(nb, lava)) drop = IsFluid(mat[Idx(i + dx, j + 1, k + dz)], lava) ? hs - 1.2f : hs - FluidHeight(nb, lava);
            else if (Mat.IsOpaqueCube(nb) || Mat.IsBuilt(nb)) continue;
            else drop = IsFluid(mat[Idx(i + dx, j - 1, k + dz)], lava) || mat[Idx(i + dx, j - 1, k + dz)] == Mat.Air ? hs + 0.5f : hs;
            flow += new Vector2(dx, dz) * drop;
        }
        float len = flow.Length();
        return len < 1e-4f ? Vector2.Zero : flow / len * Math.Min(len * (lava ? 2.5f : 2f), 1f);
    }

    /// <summary>Flow at a block corner: the flows of the fluid blocks around it averaged (smooth across blocks).</summary>
    static Vector2 CornerFlow(byte[] mat, int i, int j, int k, int cx, int cz, bool lava)
    {
        var sum = Vector2.Zero;
        int n = 0;
        for (int dz = cz - 1; dz <= cz; dz++)
        for (int dx = cx - 1; dx <= cx; dx++)
        {
            if ((uint)(i + dx) >= W || (uint)(k + dz) >= W || !IsFluid(mat[((j * W + k + dz) * W) + i + dx], lava)) continue;
            sum += BlockFlow(mat, i + dx, j, k + dz, lava);
            n++;
        }
        return n > 0 ? sum / n : Vector2.Zero;
    }

    /// <summary>
    /// An amethyst bud or cluster as real crystals: pointed hexagonal prisms growing out of the face it is attached to
    /// (the solid neighbour, preferring the floor), more and longer with each growth stage. Appearance 37.
    /// </summary>
    static void AddCrystals(MeshData m, byte[] mat, int i, int j, int k, Vector3 basePos, int stage)
    {
        static int Idx(int x, int y, int z) => (y * W + z) * W + x;
        var grow = Vector3.UnitY;
        foreach (var (dx, dy, dz) in new[] { (0, -1, 0), (0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1) })
        {
            byte nb = mat[Idx(i + dx, j + dy, k + dz)];
            if (Mat.IsTerrain(nb) || Mat.IsOpaqueCube(nb)) { grow = -new Vector3(dx, dy, dz); break; }
        }
        var centre = basePos + new Vector3(0.5f) - grow * 0.5f; // on the face it grows from
        var u = Vector3.Normalize(Vector3.Cross(grow, MathF.Abs(grow.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));
        var v = Vector3.Cross(grow, u);
        int count = stage switch { 0 => 1, 1 => 2, 2 => 4, _ => 6 };
        float maxLen = stage switch { 0 => 0.22f, 1 => 0.38f, 2 => 0.6f, _ => 0.85f };
        uint data0 = Pack(37, 0, 0, 0), data1 = Pack(0, 0, 0, 255);
        for (int c = 0; c < count; c++)
        {
            float h1 = Hash((int)basePos.X, (int)basePos.Y, (int)basePos.Z, 40 + c), h2 = Hash((int)basePos.X, (int)basePos.Y, (int)basePos.Z, 50 + c);
            float h3 = Hash((int)basePos.X, (int)basePos.Y, (int)basePos.Z, 60 + c);
            // The tallest crystal stands near the middle; the others lean outward around it.
            float ang = h1 * MathF.Tau, off = c == 0 ? 0.04f : 0.12f + 0.16f * h2;
            var outward = u * MathF.Cos(ang) + v * MathF.Sin(ang);
            var axis = Vector3.Normalize(grow + outward * (c == 0 ? 0.1f : 0.35f + 0.25f * h3));
            var b = centre + outward * off;
            float len = maxLen * (c == 0 ? 1f : 0.5f + 0.4f * h2), r = len * (0.16f + 0.05f * h3);
            var au = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY));
            var av = Vector3.Cross(axis, au);
            Span<Vector3> ring0 = stackalloc Vector3[6], ring1 = stackalloc Vector3[6];
            for (int s = 0; s < 6; s++)
            {
                float a = s * MathF.PI / 3 + h3;
                var d = au * MathF.Cos(a) + av * MathF.Sin(a);
                ring0[s] = b - axis * 0.05f + d * r;          // starts just inside the rock
                ring1[s] = b + axis * (len * 0.72f) + d * r * 0.92f;
            }
            var tip = b + axis * len;
            for (int s = 0; s < 6; s++)
            {
                int n = (s + 1) % 6;
                var side = Vector3.Normalize((ring0[s] + ring0[n]) * 0.5f - (b - axis * 0.05f));
                Tri(m, ring0[s], ring0[n], ring1[n], side, data0, data1);
                Tri(m, ring0[s], ring1[n], ring1[s], side, data0, data1);
                Tri(m, ring1[s], ring1[n], tip, Vector3.Normalize(side + axis), data0, data1);
            }
        }

        // A flat-shaded triangle facing along `outward`.
        static void Tri(MeshData m, Vector3 a, Vector3 b, Vector3 c, Vector3 outward, uint data0, uint data1)
        {
            var nrm = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(nrm, outward) < 0) { (b, c) = (c, b); nrm = -nrm; }
            nrm = Vector3.Normalize(nrm);
            uint i0 = (uint)m.Vertices.Count;
            foreach (var p in new[] { a, b, c }) m.Vertices.Add(new Vertex { Pos = p, Normal = nrm, Data0 = data0, Data1 = data1 });
            m.Indices.AddRange([i0, i0 + 1, i0 + 2]);
        }
    }

    static Vector3 Axis3(int a) => a == 0 ? Vector3.UnitX : a == 1 ? Vector3.UnitY : Vector3.UnitZ;

    /// <summary>Height (array-local y) where the smoothed terrain surface crosses this column.</summary>
    /// <summary>Height (array-local y) of the smoothed floor under the open cell above block <paramref name="floor"/>
    /// (cave floors: searching down from that open cell, never up into the ceiling).</summary>
    static float FloorY(Field field, float x, float z, int floor)
    {
        float prevY = floor + 1, prev = field.Terrain(new Vector3(x, prevY, z));
        if (prev >= 0.5f) return floor + 0.5f;
        for (float y = prevY - 0.125f; y > floor - 4; y -= 0.125f)
        {
            float d = field.Terrain(new Vector3(x, y, z));
            if (d >= 0.5f) return y + (prevY - y) * (d - 0.5f) / Math.Max(d - prev, 1e-5f);
            prev = d;
            prevY = y;
        }
        return floor + 0.5f;
    }

    static float SurfaceY(Field field, float x, float z, int topBlock)
    {
        float prevY = topBlock + 3, prev = field.Terrain(new Vector3(x, prevY, z));
        for (float y = prevY - 0.25f; y > topBlock - 4; y -= 0.25f)
        {
            float d = field.Terrain(new Vector3(x, y, z));
            if (d >= 0.5f) return y + (prevY - y) * (d - 0.5f) / Math.Max(d - prev, 1e-5f);
            prev = d;
            prevY = y;
        }
        return topBlock + 0.5f;
    }

    /// <summary>Deterministic 0..1 hash so the same world always grows the same grass.</summary>
    static float Hash(int x, int y, int z, int n)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)z * 2246822519u + (uint)n * 3266489917u;
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777216f;
    }

    /// <summary>Atlas UV (0..1) as two 16-bit values in four bytes (the shader rebuilds them).</summary>
    static uint PackUv(Vector2 uv)
    {
        uint u = (uint)Math.Clamp(MathF.Round(uv.X * 65535), 0, 65535), v = (uint)Math.Clamp(MathF.Round(uv.Y * 65535), 0, 65535);
        return u | (v << 16);
    }

    static uint Pack(byte a, byte b, byte c, byte d) => a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
    static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);

    static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
