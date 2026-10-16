using System.Numerics;
using Realism.Meshing;

namespace Realism.Rendering;

/// <summary>One box of a Minecraft model part (part-local, in blocks) and its six textured faces.</summary>
public sealed record ModelCuboid(Vector3 Min, Vector3 Max, (Vector3 normal, Vector3[] pos, Vector2[] uv)[] Faces);

/// <summary>
/// Realistic animals. Minecraft still decides what the mob is and how it moves: every part keeps its place on
/// Minecraft's skeleton and is posed by Minecraft each frame. What changes is the shape: each part's boxes become an
/// organic form (rounded volumes smoothly blended together, legs tapering to hooves, horns as nubs, a thick fleece on
/// sheep), meshed with Surface Nets like the terrain. Colours come from the mob's own texture, mapped from the
/// nearest box face, so species and variants keep their colouring.
///
/// Vertex data: Data0 = texture UV (2 x 16 bit); Data1 = material (0 hair, 1 skin, 2 wool, 3 feathers, 4 horn/beak, 5 eye),
/// position along the part (0 at the joint, 255 at the far end: hooves), hoof flag.
/// </summary>
public static class OrganicMobs
{
    public const byte Hair = 0, Skin = 1, Wool = 2, Feathers = 3, Horn = 4, Eye = 5;

    static readonly HashSet<string> Hoofed = ["cow", "mooshroom", "pig", "sheep", "goat", "horse", "donkey", "mule", "llama", "trader_llama", "camel"];
    static readonly HashSet<string> Supported =
    [
        "cow", "mooshroom", "pig", "sheep", "chicken", "goat", "horse", "donkey", "mule", "llama", "trader_llama",
        "wolf", "fox", "cat", "ocelot", "rabbit", "polar_bear", "panda", "camel",
    ];

    public static bool Supports(string type) => Supported.Contains(type);

    /// <summary>Sheep get a separate fleece mesh (drawn only while the sheep has its wool).</summary>
    public static bool HasCoat(string type, string part) => type == "sheep" && (part == "body" || part.Contains("leg"));

    /// <summary>The organic mesh of one part (null if it has no boxes), or its fleece when <paramref name="coat"/>.</summary>
    public static MeshData? BuildPart(string type, string part, IReadOnlyList<ModelCuboid> cuboids, bool coat = false)
    {
        var boxes = cuboids.Where(c => (c.Max - c.Min).LengthSquared() > 1e-8f).ToList();
        if (boxes.Count == 0) return null;
        bool leg = part.Contains("leg");
        bool hoofed = Hoofed.Contains(type) && leg;
        float largest = boxes.Max(b => Volume(b));

        // The leg's long axis and which end is the foot (the end away from the joint at the part's origin).
        var lo = boxes.Aggregate(new Vector3(float.MaxValue), (a, b) => Vector3.Min(a, b.Min));
        var hi = boxes.Aggregate(new Vector3(float.MinValue), (a, b) => Vector3.Max(a, b.Max));
        var size = hi - lo;
        int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        float a0 = lo[axis], a1 = hi[axis];
        if (MathF.Abs(a0) > MathF.Abs(a1)) (a0, a1) = (a1, a0); // a0 = joint end, a1 = foot end
        float Along(Vector3 p) => Math.Clamp((p[axis] - a0) / (a1 - a0 == 0 ? 1 : a1 - a0), 0, 1);

        bool headPart = part.Contains("head");
        var main = boxes.MaxBy(Volume)!;
        var mainC = (main.Min + main.Max) / 2;
        var mainH = Vector3.Max((main.Max - main.Min) / 2, new Vector3(0.012f));
        float mainR = MathF.Min(mainH.X, MathF.Min(mainH.Y, mainH.Z));
        // Faces look along -z or +z (Minecraft models): the face is the end of the head away from its joint.
        float front = MathF.Sign(mainC.Z) == 0 ? -1 : MathF.Sign(mainC.Z);

        float Body(Vector3 p)
        {
            float d = float.MaxValue;
            foreach (var b in boxes)
            {
                var c = (b.Min + b.Max) / 2;
                var h = Vector3.Max((b.Max - b.Min) / 2, new Vector3(0.012f));
                var q = p - c;
                bool small = Volume(b) < largest * 0.15f;
                float bd;
                if (leg)
                {
                    // Slender legs tapering toward the foot, with a slight swelling at the knee.
                    float t = Along(p);
                    float s = 0.85f * (1 - 0.4f * t + 0.1f * MathF.Exp(-MathF.Pow((t - 0.5f) / 0.12f, 2)));
                    for (int i = 0; i < 3; i++) if (i != axis) q[i] /= s;
                    bd = RoundBox(q, h, MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * 0.95f) * s;
                }
                else if (b == main)
                {
                    float s = 1;
                    if (headPart)
                    {
                        // Skull to muzzle: the head narrows toward the face.
                        float t = Math.Clamp((q.Z * front) / h.Z * 0.5f + 0.5f, 0, 1);
                        s = 1 - 0.28f * t;
                        q.X /= s;
                        q.Y /= s;
                    }
                    bd = Ellipsoid(q, h * 1.1f) * s; // rounded, organic volume (slightly larger than the box)
                }
                else if (small)
                {
                    // Snouts, ears, horns, beaks: rounded, and anchored to the main volume so none of them float.
                    bd = Ellipsoid(q, h * 1.15f);
                    bd = SmoothMin(bd, Capsule(p, c, mainC, MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * 0.9f), 0.02f);
                }
                else bd = RoundBox(q, h, MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * 0.8f);
                d = SmoothMin(d, bd, small ? 0.035f : 0.06f);
            }
            // Joints: heads and legs grow toward the point they hang from, so they flow into the body.
            if ((headPart || leg) && mainC.Length() > mainR * 0.5f)
                d = SmoothMin(d, Capsule(p, mainC, Vector3.Zero, mainR * (leg ? 0.7f : 0.6f)), 0.06f);
            return d;
        }

        // Eyes: two glossy spheres on the head's surface, toward the face.
        var eyes = new List<(Vector3 c, float r)>();
        if (headPart && !coat && main.Faces.Length > 0)
        {
            float r = MathF.Min(mainH.X, mainH.Y) * 0.16f;
            foreach (float side in new[] { -1f, 1f })
            {
                var dir = Vector3.Normalize(new Vector3(side * 0.62f * mainH.X, -0.32f * mainH.Y, front * 0.72f * mainH.Z));
                float lo2 = 0, hi2 = mainH.Length() * 1.5f;
                for (int it = 0; it < 24; it++) { float mid = (lo2 + hi2) / 2; if (Body(mainC + dir * mid) < 0) lo2 = mid; else hi2 = mid; }
                eyes.Add((mainC + dir * (lo2 - r * 0.35f), r));
            }
        }

        float Shape(Vector3 p)
        {
            float d = Body(p);
            foreach (var (c, r) in eyes) d = MathF.Min(d, (p - c).Length() - r);
            if (!coat) return d;
            // Fleece: a thick, lumpy coat over the body and the upper legs.
            float fluff = Noise(p * 14f) * 0.035f + Noise(p * 31f) * 0.015f;
            float thick = part == "body" ? 0.11f : 0.07f;
            float coatD = d - thick + fluff;
            if (leg) coatD = MathF.Max(coatD, (Along(p) - 0.45f) * 0.3f); // only the upper part of the legs
            return coatD;
        }

        float margin = coat ? 0.2f : 0.06f;
        var gmin = lo - new Vector3(margin);
        var gmax = hi + new Vector3(margin);
        float cell = Math.Clamp((gmax - gmin).Length() / 56f, 0.008f, 0.03f);
        var mesh = SurfaceNets(Shape, gmin, gmax, cell);
        if (mesh.IsEmpty) return null;

        // Material, texture coordinates (from the nearest box face) and position along the part.
        for (int v = 0; v < mesh.Vertices.Count; v++)
        {
            var vert = mesh.Vertices[v];
            var p = vert.Pos;
            ModelCuboid nearest = boxes[0];
            float best = float.MaxValue;
            foreach (var b in boxes)
            {
                var c = (b.Min + b.Max) / 2;
                var q = Vector3.Abs(p - c) - (b.Max - b.Min) / 2;
                float dist = Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0);
                if (dist < best) { best = dist; nearest = b; }
            }
            byte material = coat ? Wool : eyes.Any(e => (p - e.c).Length() < e.r * 1.25f) ? Eye
                : MaterialOf(type, part, Volume(nearest) < largest * 0.15f && Volume(nearest) < 0.0016f);
            byte along = leg ? (byte)(Along(p) * 255) : (byte)0;
            vert.Data0 = PackUv(FaceUv(nearest, p));
            vert.Data1 = material | ((uint)along << 8) | ((hoofed ? 1u : 0u) << 16);
            mesh.Vertices[v] = vert;
        }
        return mesh;
    }

    static byte MaterialOf(string type, string part, bool smallBit)
    {
        if (type == "pig") return Skin;
        if (type == "chicken")
            return part is "beak" or "right_leg" or "left_leg" ? Horn : part == "red_thing" ? Skin : Feathers;
        if (smallBit && part == "head" && type is "cow" or "mooshroom" or "goat") return Horn; // horns
        return Hair;
    }

    static float Volume(ModelCuboid b) { var s = b.Max - b.Min; return MathF.Max(s.X, 0.01f) * MathF.Max(s.Y, 0.01f) * MathF.Max(s.Z, 0.01f); }

    /// <summary>Texture coordinate of the box face nearest to p (bilinear over that face's quad).</summary>
    static Vector2 FaceUv(ModelCuboid b, Vector3 p)
    {
        var c = (b.Min + b.Max) / 2;
        var h = Vector3.Max((b.Max - b.Min) / 2, new Vector3(1e-4f));
        var d = (p - c) / h;
        int ax = MathF.Abs(d.X) >= MathF.Abs(d.Y) && MathF.Abs(d.X) >= MathF.Abs(d.Z) ? 0 : MathF.Abs(d.Y) >= MathF.Abs(d.Z) ? 1 : 2;
        var n = Vector3.Zero;
        n[ax] = MathF.Sign(d[ax]) == 0 ? 1 : MathF.Sign(d[ax]);
        var face = b.Faces.Length == 0 ? default : b.Faces.MaxBy(f => Vector3.Dot(f.normal, n));
        if (face.pos == null) return Vector2.Zero;
        var pp = Vector3.Clamp(p, b.Min, b.Max);
        Vector3 e1 = face.pos[1] - face.pos[0], e2 = face.pos[3] - face.pos[0];
        float s = e1.LengthSquared() > 1e-10f ? Math.Clamp(Vector3.Dot(pp - face.pos[0], e1) / e1.LengthSquared(), 0, 1) : 0;
        float t = e2.LengthSquared() > 1e-10f ? Math.Clamp(Vector3.Dot(pp - face.pos[0], e2) / e2.LengthSquared(), 0, 1) : 0;
        // Pull slightly inside the face so blurred lookups stay on this face's texels.
        s = 0.1f + 0.8f * s;
        t = 0.1f + 0.8f * t;
        return face.uv[0] + (face.uv[1] - face.uv[0]) * s + (face.uv[3] - face.uv[0]) * t;
    }

    static uint PackUv(Vector2 uv)
    {
        uint u = (uint)Math.Clamp(MathF.Round(uv.X * 65535), 0, 65535), v = (uint)Math.Clamp(MathF.Round(uv.Y * 65535), 0, 65535);
        return u | (v << 16);
    }

    static float Ellipsoid(Vector3 p, Vector3 r)
    {
        float k0 = (p / r).Length(), k1 = (p / (r * r)).Length();
        return k1 < 1e-6f ? -MathF.Min(r.X, MathF.Min(r.Y, r.Z)) : k0 * (k0 - 1) / k1;
    }

    static float Capsule(Vector3 p, Vector3 a, Vector3 b, float r)
    {
        var pa = p - a;
        var ba = b - a;
        float h = Math.Clamp(Vector3.Dot(pa, ba) / MathF.Max(ba.LengthSquared(), 1e-8f), 0, 1);
        return (pa - ba * h).Length() - r;
    }

    static float RoundBox(Vector3 p, Vector3 h, float r)
    {
        var q = Vector3.Abs(p) - (h - new Vector3(r));
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0) - r;
    }

    static float SmoothMin(float a, float b, float k)
    {
        if (a == float.MaxValue) return b;
        float h = Math.Clamp(0.5f + 0.5f * (b - a) / k, 0, 1);
        return b + (a - b) * h - k * h * (1 - h);
    }

    static float Hash(int x, int y, int z)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
    }

    /// <summary>Smooth value noise in -0.5..0.5.</summary>
    static float Noise(Vector3 p)
    {
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
        float fx = p.X - x, fy = p.Y - y, fz = p.Z - z;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(Hash(x, y, z), Hash(x + 1, y, z), fx), L(Hash(x, y + 1, z), Hash(x + 1, y + 1, z), fx), fy),
                 L(L(Hash(x, y, z + 1), Hash(x + 1, y, z + 1), fx), L(Hash(x, y + 1, z + 1), Hash(x + 1, y + 1, z + 1), fx), fy), fz) - 0.5f;
    }

    /// <summary>Surface Nets over a signed distance function: one vertex per surface cell, quads across sign changes.</summary>
    internal static MeshData SurfaceNets(Func<Vector3, float> sdf, Vector3 min, Vector3 max, float cell)
    {
        int nx = Math.Max(2, (int)MathF.Ceiling((max.X - min.X) / cell)), ny = Math.Max(2, (int)MathF.Ceiling((max.Y - min.Y) / cell)),
            nz = Math.Max(2, (int)MathF.Ceiling((max.Z - min.Z) / cell));
        var f = new float[(nx + 1) * (ny + 1) * (nz + 1)];
        int P(int i, int j, int k) => (k * (ny + 1) + j) * (nx + 1) + i;
        Vector3 At(int i, int j, int k) => min + new Vector3(i, j, k) * cell;
        for (int k = 0; k <= nz; k++) for (int j = 0; j <= ny; j++) for (int i = 0; i <= nx; i++) f[P(i, j, k)] = sdf(At(i, j, k));

        var mesh = new MeshData();
        var index = new int[nx * ny * nz];
        Array.Fill(index, -1);
        int C(int i, int j, int k) => (k * ny + j) * nx + i;
        Vector3 Gradient(Vector3 p)
        {
            float e = cell * 0.5f;
            var g = new Vector3(sdf(p + new Vector3(e, 0, 0)) - sdf(p - new Vector3(e, 0, 0)),
                                sdf(p + new Vector3(0, e, 0)) - sdf(p - new Vector3(0, e, 0)),
                                sdf(p + new Vector3(0, 0, e)) - sdf(p - new Vector3(0, 0, e)));
            return g.LengthSquared() > 1e-14f ? Vector3.Normalize(g) : Vector3.UnitY;
        }

        Span<float> v = stackalloc float[8];
        for (int k = 0; k < nz; k++)
        for (int j = 0; j < ny; j++)
        for (int i = 0; i < nx; i++)
        {
            int inside = 0;
            for (int c = 0; c < 8; c++)
            {
                v[c] = f[P(i + (c & 1), j + ((c >> 1) & 1), k + ((c >> 2) & 1))];
                if (v[c] < 0) inside++;
            }
            if (inside == 0 || inside == 8) continue;
            var sum = Vector3.Zero;
            int n = 0;
            for (int c = 0; c < 8; c++)
            for (int a = 0; a < 3; a++)
            {
                int o = c ^ (1 << a);
                if (o < c || (v[c] < 0) == (v[o] < 0)) continue;
                float t = v[c] / (v[c] - v[o]);
                var pc = new Vector3(c & 1, (c >> 1) & 1, (c >> 2) & 1);
                var po = new Vector3(o & 1, (o >> 1) & 1, (o >> 2) & 1);
                sum += pc + (po - pc) * t;
                n++;
            }
            var pos = min + (new Vector3(i, j, k) + sum / n) * cell;
            index[C(i, j, k)] = mesh.Vertices.Count;
            mesh.Vertices.Add(new Vertex { Pos = pos, Normal = Gradient(pos) });
        }

        // A quad across every grid edge where the sign changes, joining the four cells around it.
        void Quad(int a, int b, int c, int d, bool flip)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0) return;
            if (flip) (b, d) = (d, b);
            mesh.Indices.AddRange([(uint)a, (uint)b, (uint)c, (uint)a, (uint)c, (uint)d]);
        }
        for (int k = 1; k < nz; k++)
        for (int j = 1; j < ny; j++)
        for (int i = 0; i < nx; i++)
        {
            float f0 = f[P(i, j, k)], f1 = f[P(i + 1, j, k)];
            if ((f0 < 0) == (f1 < 0)) continue;
            Quad(index[C(i, j - 1, k - 1)], index[C(i, j, k - 1)], index[C(i, j, k)], index[C(i, j - 1, k)], f0 < 0);
        }
        for (int k = 1; k < nz; k++)
        for (int j = 0; j < ny; j++)
        for (int i = 1; i < nx; i++)
        {
            float f0 = f[P(i, j, k)], f1 = f[P(i, j + 1, k)];
            if ((f0 < 0) == (f1 < 0)) continue;
            Quad(index[C(i - 1, j, k - 1)], index[C(i - 1, j, k)], index[C(i, j, k)], index[C(i, j, k - 1)], f0 < 0);
        }
        for (int k = 0; k < nz; k++)
        for (int j = 1; j < ny; j++)
        for (int i = 1; i < nx; i++)
        {
            float f0 = f[P(i, j, k)], f1 = f[P(i, j, k + 1)];
            if ((f0 < 0) == (f1 < 0)) continue;
            Quad(index[C(i - 1, j - 1, k)], index[C(i, j - 1, k)], index[C(i, j, k)], index[C(i - 1, j, k)], f0 < 0);
        }
        return mesh;
    }
}
