using System.Numerics;
using Realism.Meshing;
using Silk.NET.OpenGL;

namespace Realism.Rendering;

/// <summary>
/// Shows which block Minecraft is targeting: a thin dark outline around it (the smooth terrain doesn't line up with
/// Minecraft's blocks, so without this you can't tell what you'll break or where you'll place), plus a crack overlay
/// that fills in while mining.
/// </summary>
public sealed class TargetOutline : IDisposable
{
    readonly GpuMesh _edges, _faces;

    public TargetOutline(GL gl)
    {
        _edges = new GpuMesh(gl, Edges(0.012f));
        _faces = new GpuMesh(gl, Faces());
    }

    public void Draw(ShaderProgram p, int x, int y, int z, int stage)
    {
        var at = Matrix4x4.CreateTranslation(x, y, z);
        p.Set("uModel", at);
        p.Set("uColor", new Vector4(0.02f, 0.02f, 0.02f, 0.75f));
        _edges.Draw();
        if (stage >= 0)
        {
            float k = (stage + 1) / 10f;
            p.Set("uModel", Matrix4x4.CreateScale(1.004f) * Matrix4x4.CreateTranslation(-0.002f, -0.002f, -0.002f) * at);
            p.Set("uColor", new Vector4(0.05f, 0.04f, 0.03f, Math.Clamp(k, 0, 1) * 0.6f));
            _faces.Draw();
        }
    }

    /// <summary>The 12 edges of a unit block as thin bars (marked with uv.x = 0).</summary>
    static MeshData Edges(float t)
    {
        var m = new MeshData();
        for (int axis = 0; axis < 3; axis++)
        for (int a = 0; a <= 1; a++)
        for (int b = 0; b <= 1; b++)
        {
            var lo = Vector3.Zero;
            var hi = Vector3.Zero;
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            lo[axis] = -t; hi[axis] = 1 + t;
            lo[u] = a - t; hi[u] = a + t;
            lo[v] = b - t; hi[v] = b + t;
            Box(m, lo, hi, 0);
        }
        return m;
    }

    /// <summary>The six faces of a unit block for the crack overlay (marked with uv.x = 1).</summary>
    static MeshData Faces()
    {
        var m = new MeshData();
        Box(m, Vector3.Zero, Vector3.One, 65535);
        return m;
    }

    static void Box(MeshData m, Vector3 lo, Vector3 hi, uint uvx)
    {
        for (int face = 0; face < 6; face++)
        {
            int a = face / 2, u = (a + 1) % 3, v = (a + 2) % 3;
            float s = face % 2 == 0 ? 1 : -1;
            uint b = (uint)m.Vertices.Count;
            foreach (var (cu, cv) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
            {
                var p = Vector3.Zero;
                p[a] = s > 0 ? hi[a] : lo[a];
                p[u] = cu == 1 ? hi[u] : lo[u];
                p[v] = cv == 1 ? hi[v] : lo[v];
                var n = Vector3.Zero;
                n[a] = s;
                m.Vertices.Add(new Vertex { Pos = p, Normal = n, Data0 = (uvx & 0xFF) | ((uvx >> 8) << 8) });
            }
            if (s > 0) m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            else m.Indices.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
        }
    }

    public void Dispose()
    {
        _edges.Dispose();
        _faces.Dispose();
    }
}
