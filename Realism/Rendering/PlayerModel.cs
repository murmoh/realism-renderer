using System.Numerics;
using Realism.Live;
using Realism.Meshing;
using Silk.NET.OpenGL;

namespace Realism.Rendering;

/// <summary>
/// The player's body: six rounded parts (head, torso, arms, legs) in Minecraft player proportions, posed and
/// animated from Minecraft's own state (body/head facing, walk cycle, arm swing, sneaking).
/// </summary>
public sealed class PlayerModel : IDisposable
{
    const float Scale = 0.9375f; // Minecraft draws the 32px-tall player model at 15/16

    readonly GpuMesh _head, _body, _arm, _leg;

    public PlayerModel(GL gl)
    {
        var skin = new Vector3(0.58f, 0.40f, 0.29f);
        var hair = new Vector3(0.11f, 0.07f, 0.045f);
        var shirt = new Vector3(0.02f, 0.30f, 0.33f);
        var pants = new Vector3(0.10f, 0.10f, 0.26f);
        var shoes = new Vector3(0.07f, 0.06f, 0.055f);

        // Local spaces: legs/arms hang down from their pivot, torso and head rise from theirs; +Z is the front.
        _leg = new GpuMesh(gl, RoundedBox(new Vector3(-0.125f, -0.75f, -0.125f), new Vector3(0.125f, 0, 0.125f), 0.035f,
            p => p.Y < -0.66f ? shoes : pants));
        _body = new GpuMesh(gl, RoundedBox(new Vector3(-0.25f, 0, -0.125f), new Vector3(0.25f, 0.75f, 0.125f), 0.05f,
            p => p.Y < 0.06f ? pants * 0.8f : shirt));
        _arm = new GpuMesh(gl, RoundedBox(new Vector3(-0.115f, -0.625f, -0.115f), new Vector3(0.115f, 0.125f, 0.115f), 0.045f,
            p => p.Y > -0.25f ? shirt : skin));
        _head = new GpuMesh(gl, RoundedBox(new Vector3(-0.25f, 0, -0.25f), new Vector3(0.25f, 0.5f, 0.25f), 0.07f, p =>
        {
            bool front = p.Z > 0.2f;
            if (p.Y > 0.38f || (!front && p.Y > 0.15f) || (front && p.Y > 0.36f)) return hair;
            if (front && p.Y is > 0.2f and < 0.27f && MathF.Abs(p.X) is > 0.06f and < 0.16f)
                return MathF.Abs(p.X) > 0.11f ? new Vector3(0.75f, 0.75f, 0.75f) : new Vector3(0.08f, 0.12f, 0.25f); // eyes
            return skin;
        }));
    }

    /// <summary>World matrices for every part (System.Numerics row-vector order).</summary>
    public static (Matrix4x4 head, Matrix4x4 body, Matrix4x4 armR, Matrix4x4 armL, Matrix4x4 legR, Matrix4x4 legL)
        Pose(PlayerPose p, Vector3 feet)
    {
        float rad = MathF.PI / 180;
        var root = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationY(-p.BodyYaw * rad) * Matrix4x4.CreateTranslation(feet);

        float walk = p.LimbPos * 0.6662f, amp = Math.Min(p.LimbAmp, 1f);
        float lean = p.Sneaking ? 0.5f : 0f, drop = p.Sneaking ? 0.2f : 0f;
        var waist = Matrix4x4.CreateRotationX(lean) * Matrix4x4.CreateTranslation(0, 0.75f - drop, 0) * root;

        var legR = Matrix4x4.CreateRotationX(MathF.Cos(walk) * 1.4f * amp) * Matrix4x4.CreateTranslation(-0.11875f, 0.75f - drop, p.Sneaking ? -0.1f : 0) * root;
        var legL = Matrix4x4.CreateRotationX(MathF.Cos(walk + MathF.PI) * 1.4f * amp) * Matrix4x4.CreateTranslation(0.11875f, 0.75f - drop, p.Sneaking ? -0.1f : 0) * root;

        float swing = -MathF.Sin(p.Swing * MathF.PI) * 1.6f; // arm comes forward and up when mining/attacking
        var armR = Matrix4x4.CreateRotationX(MathF.Cos(walk + MathF.PI) * amp + swing) * Matrix4x4.CreateTranslation(-0.3125f, 0.625f, 0) * waist;
        var armL = Matrix4x4.CreateRotationX(MathF.Cos(walk) * amp) * Matrix4x4.CreateTranslation(0.3125f, 0.625f, 0) * waist;

        float headYaw = WrapDegrees(p.HeadYaw - p.BodyYaw) * rad;
        var head = Matrix4x4.CreateRotationX(Math.Clamp(p.HeadPitch, -90, 90) * rad) * Matrix4x4.CreateRotationY(-headYaw)
                   * Matrix4x4.CreateTranslation(0, 0.75f, 0) * waist;
        return (head, waist, armR, armL, legR, legL);
    }

    /// <summary>Draws every part with <paramref name="setModel"/> called before each (sets the shader's uModel).</summary>
    public void Draw(PlayerPose pose, Vector3 feet, Action<Matrix4x4> setModel)
    {
        var m = Pose(pose, feet);
        setModel(m.body); _body.Draw();
        setModel(m.head); _head.Draw();
        setModel(m.armR); _arm.Draw();
        setModel(m.armL); _arm.Draw();
        setModel(m.legR); _leg.Draw();
        setModel(m.legL); _leg.Draw();
    }

    static float WrapDegrees(float d) => ((d + 180) % 360 + 360) % 360 - 180;

    /// <summary>A box with rounded edges: a subdivided cube whose vertices are pushed onto a rounded shell.</summary>
    static MeshData RoundedBox(Vector3 min, Vector3 max, float radius, Func<Vector3, Vector3> color)
    {
        const int N = 6;
        var m = new MeshData();
        var inner0 = min + new Vector3(radius);
        var inner1 = max - new Vector3(radius);
        for (int face = 0; face < 6; face++)
        {
            int a = face / 2, u = (a + 1) % 3, v = (a + 2) % 3;
            float s = face % 2 == 0 ? 1 : -1;
            uint b = (uint)m.Vertices.Count;
            for (int j = 0; j <= N; j++)
            for (int i = 0; i <= N; i++)
            {
                var p = Vector3.Zero;
                p[a] = s > 0 ? max[a] : min[a];
                p[u] = min[u] + (max[u] - min[u]) * i / N;
                p[v] = min[v] + (max[v] - min[v]) * j / N;
                var core = Vector3.Clamp(p, inner0, inner1);
                var dir = p - core;
                var n = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : Vector3.Zero;
                if (n == Vector3.Zero) n[a] = s;
                var pos = core + n * radius;
                var c = color(pos);
                m.Vertices.Add(new Vertex
                {
                    Pos = pos, Normal = n,
                    Data0 = (uint)(Math.Clamp(c.X, 0, 1) * 255) | ((uint)(Math.Clamp(c.Y, 0, 1) * 255) << 8) | ((uint)(Math.Clamp(c.Z, 0, 1) * 255) << 16),
                });
            }
            for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++)
            {
                uint q = b + (uint)(j * (N + 1) + i);
                // Winding: face normal must point outward (+s along axis a); u x v = a.
                if (s > 0) m.Indices.AddRange([q, q + 1, q + N + 2, q, q + N + 2, q + N + 1]);
                else m.Indices.AddRange([q, q + N + 2, q + 1, q, q + N + 1, q + N + 2]);
            }
        }
        return m;
    }

    public void Dispose()
    {
        _head.Dispose();
        _body.Dispose();
        _arm.Dispose();
        _leg.Dispose();
    }
}
