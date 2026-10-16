using System.Numerics;
using Realism.Meshing;

namespace Realism.Rendering;

/// <summary>
/// Hand-designed animal anatomy on Minecraft's skeletons. Each species is built from smooth shapes (barrel torso
/// with chest, belly and rump, a real neck, skull and muzzle, ears, horns, eyes, jointed legs with hooves or paws,
/// tail) placed from the model's rest pose in design space (see MobRig), each shape owned by the Minecraft part it
/// moves with. Every part's shapes are meshed (Surface Nets) and moved into that part's local space, so walking,
/// grazing and head turns still come from Minecraft.
///
/// Vertex data: Data1.x = material (OrganicMobs: 0 coat, 1 skin, 2 wool, 3 feathers, 4 horn/hoof, 5 eye, 6 nose,
/// 7 beak, 8 comb, 9 mane, 10 pink skin).
/// </summary>
public static class Anatomy
{
    public const byte Coat = 0, Skin = 1, Wool = 2, Feathers = 3, Horn = 4, Eye = 5, Nose = 6, Beak = 7, Comb = 8, Mane = 9, Pink = 10;

    static readonly HashSet<string> Supported = ["cow", "mooshroom", "pig", "sheep", "horse", "donkey", "mule", "wolf", "chicken"];
    public static bool Supports(string type) => Supported.Contains(type);

    /// <summary>One shape: signed distance in design space, its material, how softly it blends in, its bounds.</summary>
    sealed record Shape(Func<Vector3, float> Sdf, byte Mat, float Blend, Vector3 Min, Vector3 Max);

    sealed class Builder(MobRig rig)
    {
        public readonly MobRig Rig = rig;
        public readonly Dictionary<int, List<Shape>> Parts = new();
        public readonly Dictionary<int, List<Shape>> Coat = new(); // sheep fleece (base shapes, grown outward)

        public void Add(string part, Shape s)
        {
            int i = Rig.Parts.FindIndex(p => p.Name == part);
            if (i < 0) return;
            if (!Parts.TryGetValue(i, out var list)) Parts[i] = list = new();
            list.Add(s);
        }

        public void AddCoat(string part, Shape s)
        {
            int i = Rig.Parts.FindIndex(p => p.Name == part);
            if (i < 0) return;
            if (!Coat.TryGetValue(i, out var list)) Coat[i] = list = new();
            list.Add(s);
        }
    }

    // ---------------------------------------------------------------- shapes

    static Shape Ellipsoid(Vector3 c, Vector3 r, byte mat, float blend = 0.06f) =>
        new(p =>
        {
            var q = p - c;
            float k0 = (q / r).Length(), k1 = (q / (r * r)).Length();
            return k1 < 1e-6f ? -MathF.Min(r.X, MathF.Min(r.Y, r.Z)) : k0 * (k0 - 1) / k1;
        }, mat, blend, c - r, c + r);

    static Shape Sphere(Vector3 c, float r, byte mat, float blend = 0.03f) =>
        new(p => (p - c).Length() - r, mat, blend, c - new Vector3(r), c + new Vector3(r));

    /// <summary>A capsule whose radius goes from r1 at a to r2 at b (exact round cone).</summary>
    static Shape Cone(Vector3 a, Vector3 b, float r1, float r2, byte mat, float blend = 0.05f)
    {
        var ba = b - a;
        float l2 = ba.LengthSquared(), rr = r1 - r2, a2 = l2 - rr * rr, il2 = 1 / MathF.Max(l2, 1e-8f);
        float rm = MathF.Max(r1, r2);
        return new(p =>
        {
            var pa = p - a;
            float y = Vector3.Dot(pa, ba), z = y - l2;
            var x = pa * l2 - ba * y;
            float x2 = x.LengthSquared(), y2 = y * y * l2, z2 = z * z * l2;
            float k = MathF.Sign(rr) * rr * rr * x2;
            if (MathF.Sign(z) * a2 * z2 > k) return MathF.Sqrt(x2 + z2) * il2 - r2;
            if (MathF.Sign(y) * a2 * y2 < k) return MathF.Sqrt(x2 + y2) * il2 - r1;
            return (MathF.Sqrt(x2 * a2 * il2) + y * rr) * il2 - r1;
        }, mat, blend, Vector3.Min(a, b) - new Vector3(rm), Vector3.Max(a, b) + new Vector3(rm));
    }

    /// <summary>A chain of round cones through the points (radii per point): legs, tails, curved horns.</summary>
    static IEnumerable<Shape> Chain(Vector3[] pts, float[] radii, byte mat, float blend = 0.04f)
    {
        for (int i = 0; i + 1 < pts.Length; i++) yield return Cone(pts[i], pts[i + 1], radii[i], radii[i + 1], mat, blend);
    }

    static Vector3 V(float x, float y, float z) => new(x, y, z);

    // ---------------------------------------------------------------- species

    sealed record Quad(
        float Width = 0.85f, float TopDrop = 0.04f, float BellyLift = 0.02f, byte BodyMat = Coat,
        Vector3 Skull = default, float SkullUp = 0.08f, float SkullFwd = 0.12f,
        float HeadLen = 0.42f, float MuzzleUp = -0.1f, float MuzzleR = 0.1f, byte NoseMat = Nose,
        float NeckR1 = 0.22f, float NeckR2 = 0.15f, float NeckUp = -0.02f,
        string Ears = "side", float EarLen = 0.12f, bool Horns = false,
        float LegThick = 1f, bool Hooves = true, float TailEnd = 0.45f, bool TailTuft = true, bool Udder = false,
        bool Snout = false, float Paw = 0);

    static Quad SpecFor(string type) => type switch
    {
        // Cattle: a broad skull and a short, deep muzzle (a long thin one reads as a tapir).
        "cow" or "mooshroom" => new Quad(Skull: V(0.15f, 0.15f, 0.15f), SkullUp: 0.06f, SkullFwd: 0.1f, HeadLen: 0.33f,
            MuzzleUp: -0.12f, MuzzleR: 0.125f, Horns: true, Udder: true),
        "pig" => new Quad(Width: 0.95f, BodyMat: Pink, Skull: V(0.16f, 0.15f, 0.15f), SkullUp: 0.02f, SkullFwd: 0.1f,
            HeadLen: 0.36f, MuzzleUp: -0.05f, MuzzleR: 0.12f, NoseMat: Pink, NeckR1: 0.25f, NeckR2: 0.2f,
            Ears: "floppy", EarLen: 0.1f, LegThick: 1.15f, TailEnd: 0.6f, TailTuft: false, Snout: true),
        "sheep" => new Quad(Width: 0.85f, Skull: V(0.085f, 0.1f, 0.12f), SkullUp: 0.05f, SkullFwd: 0.08f,
            HeadLen: 0.32f, MuzzleUp: -0.08f, MuzzleR: 0.065f, NeckR1: 0.14f, NeckR2: 0.1f, Ears: "side", EarLen: 0.1f,
            LegThick: 0.72f, TailEnd: 0.85f, TailTuft: false),
        "wolf" => new Quad(Width: 0.8f, Skull: V(0.11f, 0.1f, 0.11f), SkullUp: 0.04f, SkullFwd: 0.04f,
            HeadLen: 0.3f, MuzzleUp: -0.04f, MuzzleR: 0.05f, NeckR1: 0.14f, NeckR2: 0.11f, Ears: "up", EarLen: 0.1f,
            LegThick: 0.6f, Hooves: false, Paw: 0.045f, TailTuft: false),
        _ => new Quad(Width: 0.8f, Skull: V(0.12f, 0.13f, 0.14f), Ears: "up", EarLen: 0.13f, LegThick: 0.9f, TailEnd: 0.35f), // horses
    };

    /// <summary>The meshes per part (organic, and the fleece for sheep), or null if the rig isn't as expected.</summary>
    public static (Dictionary<int, MeshData> parts, Dictionary<int, MeshData> coat)? Build(string type, MobRig rig)
    {
        var b = new Builder(rig);
        try
        {
            if (type == "chicken") BuildChicken(b);
            else BuildQuadruped(b, type, SpecFor(type));
        }
        catch (InvalidOperationException) { return null; }
        if (b.Parts.Count == 0) return null;

        var parts = new Dictionary<int, MeshData>();
        foreach (var (pi, shapes) in b.Parts)
            if (Mesh(shapes, rig.Parts[pi], coat: false) is { } m) parts[pi] = m;
        var coat = new Dictionary<int, MeshData>();
        foreach (var (pi, shapes) in b.Coat)
            if (Mesh(shapes, rig.Parts[pi], coat: true) is { } m) coat[pi] = m;
        return (parts, coat);
    }

    static MobRig.Part Need(MobRig rig, string name) => rig.Find(name) is { HasBoxes: true } p ? p : throw new InvalidOperationException(name);

    static void BuildQuadruped(Builder b, string type, Quad s)
    {
        var rig = b.Rig;
        bool horse = type is "horse" or "donkey" or "mule";
        var body = Need(rig, "body");
        string headPart = type == "wolf" ? "real_head" : "head";
        var head = horse ? rig.Find("head_parts") ?? Need(rig, "head") : rig.Find(headPart) ?? Need(rig, "head");
        string neckPart = horse ? head.Name : "body";

        // Torso: a barrel with a deep chest, a rounded rump, a slightly sagging belly and a rise at the withers.
        var bmin = body.Min;
        var bmax = body.Max;
        if (type == "wolf" && rig.Find("upper_body") is { HasBoxes: true } ub) { bmin = Vector3.Min(bmin, ub.Min); bmax = Vector3.Max(bmax, ub.Max); }
        float W = (bmax.X - bmin.X) / 2 * s.Width, top = bmax.Y - s.TopDrop, bottom = bmin.Y + s.BellyLift;
        float front = bmax.Z - 0.02f, rear = bmin.Z + 0.04f;
        float cy = (top + bottom) / 2, hy = (top - bottom) / 2, cz = (front + rear) / 2, hz = (front - rear) / 2;
        var torso = new List<Shape>
        {
            Ellipsoid(V(0, cy, cz), V(W, hy * 0.95f, hz * 0.92f), s.BodyMat, 0.08f),
            Ellipsoid(V(0, cy - 0.02f, front - hz * 0.35f), V(W * 0.95f, hy * 1.0f, hz * 0.45f), s.BodyMat, 0.08f),
            Ellipsoid(V(0, cy + hy * 0.12f, rear + hz * 0.35f), V(W * 0.95f, hy * 0.9f, hz * 0.42f), s.BodyMat, 0.08f),
            Ellipsoid(V(0, bottom + hy * 0.5f, cz), V(W * 1.02f, hy * 0.62f, hz * 0.68f), s.BodyMat, 0.08f),
            Ellipsoid(V(0, top - hy * 0.2f, front - hz * 0.3f), V(W * 0.55f, hy * 0.45f, hz * 0.35f), s.BodyMat, 0.08f),
        };
        if (type == "wolf") torso.Add(Ellipsoid(V(0, cy + 0.02f, front - hz * 0.2f), V(W * 1.05f, hy * 1.1f, hz * 0.45f), Coat, 0.08f)); // ruff
        foreach (var piece in torso) b.Add("body", piece);
        if (type == "sheep") foreach (var piece in torso) b.AddCoat("body", piece);
        if (s.Udder) b.Add("body", Ellipsoid(V(0, bottom + 0.03f, rear + hz * 0.6f), V(0.09f, 0.07f, 0.1f), Pink, 0.04f));

        // Neck: from the top of the chest up to the head joint (horses: part of the head, which carries it).
        var hp = horse ? (rig.Find("head") ?? head).Pivot : head.Pivot;
        var neckBase = V(0, top - hy * 0.25f, front - hz * 0.25f);
        var neckEnd = horse ? Need(rig, "head").Centre + V(0, -0.05f, -0.12f) : hp + V(0, s.NeckUp, -0.02f);
        b.Add(neckPart, Cone(neckBase, neckEnd, s.NeckR1 * (horse ? 0.85f : 1), s.NeckR2, Coat, 0.08f));
        if (horse)
        {
            // Mane along the top of the neck.
            var up = V(0, 1, 0);
            b.Add(neckPart, Cone(neckBase + up * (s.NeckR1 * 0.7f) + V(0, 0, -0.05f), neckEnd + up * (s.NeckR2 * 0.9f), 0.06f, 0.045f, Mane, 0.03f));
        }
        if (type is "cow" or "mooshroom") b.Add("body", Cone(neckBase + V(0, -hy * 0.6f, 0.05f), hp + V(0, -0.14f, 0), 0.08f, 0.05f, Coat, 0.06f)); // dewlap

        // Head: a skull tapering into the muzzle, nose, eyes, ears, horns. Horses' head sits on its own joint.
        string hpart = horse ? "head" : head.Name;
        Vector3 skull, tip;
        if (horse)
        {
            var hd = Need(rig, "head");
            var mouth = rig.Find("upper_mouth") is { HasBoxes: true } um ? um : hd;
            skull = V(0, hd.Max.Y - s.Skull.Y * 1.1f, hd.Min.Z + s.Skull.Z);
            tip = V(0, mouth.Centre.Y - 0.06f, mouth.Max.Z - 0.07f);
            hpart = "head_parts"; // the whole head moves with the neck
        }
        else
        {
            skull = hp + V(0, s.SkullUp, s.SkullFwd);
            tip = hp + V(0, s.MuzzleUp, s.HeadLen);
        }
        var sk = s.Skull;
        b.Add(hpart, Ellipsoid(skull, sk, Coat, 0.05f));
        b.Add(hpart, Cone(skull + V(0, -sk.Y * 0.15f, sk.Z * 0.3f), tip, sk.X * 0.95f, s.MuzzleR, Coat, 0.06f));
        if (!horse) b.Add(hpart, Cone(hp + V(0, s.NeckUp, -0.06f), skull, s.NeckR2 * 0.95f, sk.X, Coat, 0.06f)); // joins the neck
        var fwd = Vector3.Normalize(tip - skull);
        if (s.Snout) b.Add(hpart, Cone(tip - fwd * 0.02f, tip + fwd * 0.06f, s.MuzzleR * 0.85f, s.MuzzleR * 0.8f, Pink, 0.015f));
        else b.Add(hpart, Ellipsoid(tip + fwd * (s.MuzzleR * 0.55f) + V(0, s.MuzzleR * 0.1f, 0), V(s.MuzzleR * 0.95f, s.MuzzleR * 0.7f, s.MuzzleR * 0.5f), s.NoseMat, 0.02f));
        foreach (float side in new[] { -1f, 1f })
        {
            var eye = skull + V(side * sk.X * 0.82f, sk.Y * 0.2f, sk.Z * 0.45f);
            b.Add(hpart, Sphere(eye, MathF.Max(sk.X * 0.2f, 0.018f), Eye, 0.004f));
            var earBase = skull + V(side * sk.X * 0.6f, sk.Y * 0.75f, -sk.Z * 0.25f);
            var earTip = s.Ears switch
            {
                "up" => earBase + V(side * 0.03f, s.EarLen, -0.02f),
                "floppy" => earBase + V(side * 0.07f, 0.02f, s.EarLen),
                _ => earBase + V(side * s.EarLen, -0.02f, -0.01f),
            };
            b.Add(hpart, Cone(earBase, earTip, 0.035f, 0.012f, Coat, 0.02f));
            if (s.Horns)
            {
                var hb = skull + V(side * sk.X * 0.75f, sk.Y * 0.8f, -sk.Z * 0.35f);
                foreach (var h in Chain([hb, hb + V(side * 0.045f, 0.02f, 0), hb + V(side * 0.07f, 0.055f, 0.015f)], [0.026f, 0.019f, 0.008f], Horn, 0.01f))
                    b.Add(hpart, h);
            }
        }

        // Legs: shoulder/hip blending into the body, knee or hock, slim cannon bone, fetlock, hoof (or paw).
        float t = s.LegThick;
        foreach (var leg in new[] { "right_front_leg", "left_front_leg", "right_hind_leg", "left_hind_leg" })
        {
            var lp = Need(rig, leg).Pivot;
            bool hind = leg.Contains("hind");
            float x = lp.X * 0.92f, h = lp.Y, z = lp.Z;
            float footR = s.Hooves ? 0.05f * t : s.Paw;
            Vector3[] pts;
            float[] r;
            if (hind)
            {
                pts = [V(x, h + 0.14f, z), V(x, h * 0.66f, z + 0.07f), V(x, h * 0.36f, z - 0.07f), V(x, 0.08f, z - 0.03f), V(x, 0.02f, z - 0.01f)];
                r = [0.15f * t, 0.1f * t, 0.055f * t, 0.042f * t, 0.045f * t];
            }
            else
            {
                pts = [V(x, h + 0.1f, z), V(x, h * 0.6f, z + 0.02f), V(x, h * 0.32f, z + 0.03f), V(x, 0.08f, z + 0.04f), V(x, 0.02f, z + 0.05f)];
                r = [0.12f * t, 0.08f * t, 0.05f * t, 0.04f * t, 0.042f * t];
            }
            foreach (var c in Chain(pts, r, Coat, 0.05f)) b.Add(leg, c);
            if (s.Hooves) b.Add(leg, Cone(pts[3], V(x, 0, pts[4].Z + 0.01f), footR * 0.85f, footR * 1.2f, Horn, 0.01f));
            else b.Add(leg, Ellipsoid(V(x, footR * 0.8f, pts[4].Z + 0.03f), V(footR * 1.1f, footR * 0.8f, footR * 1.5f), Coat, 0.02f));
            if (type == "sheep") foreach (var c in Chain(pts[..2], r[..2], Wool)) b.AddCoat(leg, c); // fleece on the upper legs
        }

        // Tail.
        string tailPart = horse ? "tail" : type == "wolf" ? "real_tail" : "body";
        var tb = V(0, top - 0.04f, rear + 0.02f);
        if (horse)
            foreach (var c in Chain([tb, tb + V(0, -0.15f, -0.12f), V(0, s.TailEnd, rear - 0.2f)], [0.07f, 0.08f, 0.05f], Mane, 0.04f)) b.Add(tailPart, c);
        else if (type == "wolf")
            foreach (var c in Chain([tb, tb + V(0, -0.1f, -0.12f), tb + V(0, -0.25f, -0.2f)], [0.05f, 0.07f, 0.04f], Coat, 0.04f)) b.Add(tailPart, c);
        else if (type == "pig")
            foreach (var c in Chain([tb, tb + V(0.03f, 0.03f, -0.05f), tb + V(-0.02f, 0.05f, -0.07f)], [0.018f, 0.015f, 0.01f], Pink, 0.01f)) b.Add(tailPart, c);
        else
        {
            foreach (var c in Chain([tb, tb + V(0, -0.1f, -0.06f), V(0, s.TailEnd, rear - 0.07f)], [0.035f, 0.025f, 0.018f], Coat, 0.03f)) b.Add(tailPart, c);
            if (s.TailTuft) b.Add(tailPart, Ellipsoid(V(0, s.TailEnd - 0.05f, rear - 0.07f), V(0.04f, 0.08f, 0.04f), Mane, 0.02f));
        }
    }

    static void BuildChicken(Builder b)
    {
        var rig = b.Rig;
        var body = Need(rig, "body");
        var head = Need(rig, "head");
        var c = body.Centre;
        // Body: a plump egg, tail feathers rising at the back, a full breast.
        b.Add("body", Ellipsoid(c + V(0, 0, -0.02f), V(0.16f, 0.16f, 0.22f), Feathers, 0.06f));
        b.Add("body", Ellipsoid(c + V(0, -0.02f, 0.1f), V(0.14f, 0.15f, 0.13f), Feathers, 0.06f));
        b.Add("body", Ellipsoid(c + V(0, 0.12f, -0.2f), V(0.08f, 0.13f, 0.07f), Feathers, 0.06f));
        // Neck and head (on the head joint): small round head, short beak, red comb and wattle, eyes.
        var hp = head.Pivot;
        var hc = V(0, head.Max.Y - 0.08f, head.Centre.Z + 0.02f);
        b.Add("head", Cone(hp + V(0, 0, -0.04f), hc, 0.09f, 0.075f, Feathers, 0.05f));
        b.Add("head", Sphere(hc, 0.075f, Feathers, 0.03f));
        b.Add("head", Cone(hc + V(0, -0.005f, 0.06f), hc + V(0, -0.02f, 0.13f), 0.028f, 0.006f, Beak, 0.005f));
        foreach (var cs in Chain([hc + V(0, 0.06f, 0.03f), hc + V(0, 0.09f, 0.0f), hc + V(0, 0.08f, -0.04f)], [0.018f, 0.022f, 0.015f], Comb, 0.01f))
            b.Add("head", cs);
        b.Add("head", Ellipsoid(hc + V(0, -0.07f, 0.06f), V(0.015f, 0.03f, 0.015f), Comb, 0.01f));
        foreach (float side in new[] { -1f, 1f }) b.Add("head", Sphere(hc + V(side * 0.06f, 0.015f, 0.03f), 0.013f, Eye, 0.003f));
        // Wings folded along the sides.
        foreach (var (wing, side) in new[] { ("right_wing", -1f), ("left_wing", 1f) })
        {
            var w = Need(rig, wing);
            b.Add(wing, Ellipsoid(V(side * 0.14f, w.Centre.Y + 0.02f, w.Centre.Z - 0.03f), V(0.035f, 0.1f, 0.17f), Feathers, 0.03f));
        }
        // Legs: thin yellow shanks, three forward toes and one back.
        foreach (var leg in new[] { "right_leg", "left_leg" })
        {
            var lp = Need(rig, leg).Pivot;
            float x = lp.X * 0.7f, z = lp.Z + 0.04f;
            b.Add(leg, Ellipsoid(V(x, lp.Y + 0.02f, z), V(0.05f, 0.07f, 0.06f), Feathers, 0.03f)); // drumstick
            b.Add(leg, Cone(V(x, lp.Y, z), V(x, 0.02f, z), 0.022f, 0.016f, Beak, 0.01f));
            foreach (var (dx, dz) in new[] { (-0.04f, 0.06f), (0f, 0.08f), (0.04f, 0.06f), (0f, -0.05f) })
                b.Add(leg, Cone(V(x, 0.015f, z), V(x + dx, 0.008f, z + dz), 0.012f, 0.006f, Beak, 0.006f));
        }
    }

    // ---------------------------------------------------------------- meshing

    static float SmoothMin(float a, float b, float k)
    {
        float h = Math.Clamp(0.5f + 0.5f * (b - a) / k, 0, 1);
        return b + (a - b) * h - k * h * (1 - h);
    }

    static float Noise(Vector3 p)
    {
        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
        float fx = p.X - x, fy = p.Y - y, fz = p.Z - z;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        static float H(int x, int y, int z)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
        }
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(H(x, y, z), H(x + 1, y, z), fx), L(H(x, y + 1, z), H(x + 1, y + 1, z), fx), fy),
                 L(L(H(x, y, z + 1), H(x + 1, y, z + 1), fx), L(H(x, y + 1, z + 1), H(x + 1, y + 1, z + 1), fx), fy), fz) - 0.5f;
    }

    /// <summary>Meshes one part's shapes in design space and moves the result into the part's local space.</summary>
    static MeshData? Mesh(List<Shape> shapes, MobRig.Part part, bool coat)
    {
        float Dist(Vector3 p)
        {
            float d = float.MaxValue;
            foreach (var s in shapes) d = d == float.MaxValue ? s.Sdf(p) : SmoothMin(d, s.Sdf(p), s.Blend);
            if (!coat) return d;
            // Fleece: a thick coat of lumpy curls over the base shapes.
            return d - 0.1f + Noise(p * 13f) * 0.04f + Noise(p * 29f) * 0.015f;
        }
        var min = shapes.Aggregate(new Vector3(float.MaxValue), (a, s) => Vector3.Min(a, s.Min)) - new Vector3(coat ? 0.16f : 0.05f);
        var max = shapes.Aggregate(new Vector3(float.MinValue), (a, s) => Vector3.Max(a, s.Max)) + new Vector3(coat ? 0.16f : 0.05f);
        float cell = Math.Clamp((max - min).Length() / 80f, 0.008f, 0.022f);
        var mesh = OrganicMobs.SurfaceNets(Dist, min, max, cell);
        if (mesh.IsEmpty) return null;

        // Material from the nearest shape; then into the part's local space.
        Matrix4x4.Invert(part.FromDesign, out var inv);
        var normalM = Matrix4x4.Transpose(inv);
        for (int v = 0; v < mesh.Vertices.Count; v++)
        {
            var vert = mesh.Vertices[v];
            byte mat = Wool;
            if (!coat)
            {
                float best = float.MaxValue;
                foreach (var s in shapes)
                {
                    float d = s.Sdf(vert.Pos);
                    if (d < best) { best = d; mat = s.Mat; }
                }
            }
            vert.Data0 = 0;
            vert.Data1 = mat;
            vert.Pos = Vector3.Transform(vert.Pos, part.FromDesign);
            vert.Normal = Vector3.Normalize(Vector3.TransformNormal(vert.Normal, normalM));
            mesh.Vertices[v] = vert;
        }
        return mesh;
    }
}
