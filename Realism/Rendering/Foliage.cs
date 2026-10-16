using System.Numerics;
using System.Runtime.InteropServices;
using Realism.Meshing;
using Silk.NET.OpenGL;

namespace Realism.Rendering;

/// <summary>Template geometry shared by every instance of a foliage type.</summary>
public static class Foliage
{
    /// <summary>
    /// One grass tuft: several tapered, leaning blades around a central stem, plus a small flower head
    /// (y &gt; 1) that the shader collapses unless the instance is a flower. Blade y runs 0..1 and is scaled per instance.
    /// </summary>
    public static MeshData GrassTuft()
    {
        var m = new MeshData();
        var rng = new Random(1234);
        const int Blades = 12, Segments = 4;
        for (int b = 0; b < Blades; b++)
        {
            float angle = (float)rng.NextDouble() * MathF.Tau;
            float r = b == 0 ? 0 : 0.04f + (float)rng.NextDouble() * 0.16f;
            var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
            float leanAngle = b == 0 ? 0 : angle + ((float)rng.NextDouble() - 0.5f);
            float lean = b == 0 ? 0.03f : 0.1f + (float)rng.NextDouble() * 0.3f;
            var leanDir = new Vector2(MathF.Cos(leanAngle), MathF.Sin(leanAngle));
            float facing = (float)rng.NextDouble() * MathF.Tau;
            var side = new Vector2(MathF.Cos(facing), MathF.Sin(facing));
            var normal = Vector3.Normalize(new Vector3(-side.Y, 0.4f, side.X));
            float width = 0.018f + (float)rng.NextDouble() * 0.014f;
            float height = b == 0 ? 1f : 0.65f + (float)rng.NextDouble() * 0.35f;

            uint baseIdx = (uint)m.Vertices.Count;
            for (int s = 0; s <= Segments; s++)
            {
                float t = s / (float)Segments;
                var c = offset + leanDir * lean * t * t;
                float hw = width * MathF.Pow(1 - t, 0.7f);
                foreach (float sign in new[] { -1f, 1f })
                {
                    var xz = c + side * hw * sign;
                    m.Vertices.Add(new Vertex { Pos = new Vector3(xz.X, t * height, xz.Y), Normal = normal });
                }
            }
            for (uint s = 0; s < Segments; s++)
            {
                uint a = baseIdx + s * 2;
                m.Indices.AddRange([a, a + 1, a + 3, a, a + 3, a + 2]);
            }
        }

        // Flower head: three small crossed petals on top of the central stem.
        for (int q = 0; q < 3; q++)
        {
            float a = q * MathF.PI / 3;
            var dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 0.035f;
            var n = new Vector3(-MathF.Sin(a), 0.5f, MathF.Cos(a));
            uint b = (uint)m.Vertices.Count;
            m.Vertices.Add(new Vertex { Pos = new Vector3(-dir.X, 1.0f, -dir.Z), Normal = n });
            m.Vertices.Add(new Vertex { Pos = new Vector3(dir.X, 1.0f, dir.Z), Normal = n });
            m.Vertices.Add(new Vertex { Pos = new Vector3(dir.X, 1.04f, dir.Z), Normal = n });
            m.Vertices.Add(new Vertex { Pos = new Vector3(-dir.X, 1.04f, -dir.Z), Normal = n });
            m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        // Mark head vertices (y >= 1 but belonging to the head) by nudging them just above 1.
        for (int v = m.Vertices.Count - 12; v < m.Vertices.Count; v++)
        {
            var vert = m.Vertices[v];
            vert.Pos.Y += 0.002f;
            m.Vertices[v] = vert;
        }
        return m;
    }

    /// <summary>
    /// A lumpy unit rock: an icosphere displaced by layered noise with a few flat facets (fracture planes).
    /// Instances scale and rotate it, so one template gives endless variety.
    /// </summary>
    public static MeshData Rock(int subdivisions)
    {
        float t = (1 + MathF.Sqrt(5)) / 2;
        var verts = new List<Vector3>
        {
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0), new(0, -1, t), new(0, 1, t),
            new(0, -1, -t), new(0, 1, -t), new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        };
        var faces = new List<(int a, int b, int c)>
        {
            (0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
            (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1),
        };
        for (int i = 0; i < verts.Count; i++) verts[i] = Vector3.Normalize(verts[i]);

        for (int level = 0; level < subdivisions; level++)
        {
            var mid = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                if (!mid.TryGetValue(key, out int idx))
                {
                    idx = verts.Count;
                    verts.Add(Vector3.Normalize((verts[a] + verts[b]) / 2));
                    mid[key] = idx;
                }
                return idx;
            }
            var next = new List<(int, int, int)>();
            foreach (var (a, b, c) in faces)
            {
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                next.AddRange([(a, ab, ca), (b, bc, ab), (c, ca, bc), (ab, bc, ca)]);
            }
            faces = next;
        }

        var rng = new Random(99);
        var planes = Enumerable.Range(0, 5).Select(_ => Vector3.Normalize(new Vector3(
            (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1))).ToArray();
        for (int i = 0; i < verts.Count; i++)
        {
            var d = verts[i];
            float r = 1 + 0.18f * Noise(d * 1.7f) + 0.07f * Noise(d * 4.3f + new Vector3(5)) + 0.03f * Noise(d * 9f);
            var p = d * r;
            foreach (var pl in planes) // fracture facets: clip the rock against a few planes
            {
                float dist = Vector3.Dot(p, pl) - 0.82f;
                if (dist > 0) p -= pl * dist;
            }
            verts[i] = p;
        }

        var normals = new Vector3[verts.Count];
        foreach (var (a, b, c) in faces)
        {
            var n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
            normals[a] += n;
            normals[b] += n;
            normals[c] += n;
        }
        var m = new MeshData();
        for (int i = 0; i < verts.Count; i++)
            m.Vertices.Add(new Vertex { Pos = verts[i], Normal = Vector3.Normalize(normals[i]) });
        foreach (var (a, b, c) in faces) m.Indices.AddRange([(uint)a, (uint)b, (uint)c]);
        return m;
    }

    /// <summary>Cheap smooth 3D value noise in -1..1 for shaping the rock template.</summary>
    static float Noise(Vector3 p)
    {
        static float H(int x, int y, int z)
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)z * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 32767.5f - 1;
        }
        int x0 = (int)MathF.Floor(p.X), y0 = (int)MathF.Floor(p.Y), z0 = (int)MathF.Floor(p.Z);
        float fx = p.X - x0, fy = p.Y - y0, fz = p.Z - z0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        float L(float a, float b, float t) => a + (b - a) * t;
        return L(L(L(H(x0, y0, z0), H(x0 + 1, y0, z0), fx), L(H(x0, y0 + 1, z0), H(x0 + 1, y0 + 1, z0), fx), fy),
                 L(L(H(x0, y0, z0 + 1), H(x0 + 1, y0, z0 + 1), fx), L(H(x0, y0 + 1, z0 + 1), H(x0 + 1, y0 + 1, z0 + 1), fx), fy), fz);
    }

    /// <summary>Three unit quads crossing at the centre in different orientations; UVs in Data0.xy.</summary>
    public static MeshData LeafCluster()
    {
        var m = new MeshData();
        var planes = new[]
        {
            (u: new Vector3(1, 0, 0), v: new Vector3(0, 1, 0)),
            (u: Vector3.Normalize(new Vector3(0.5f, 0, 0.87f)), v: Vector3.Normalize(new Vector3(0, 1, 0.3f))),
            (u: Vector3.Normalize(new Vector3(-0.5f, 0.2f, 0.87f)), v: Vector3.Normalize(new Vector3(0.6f, 0.3f, 0.4f))),
        };
        foreach (var (u, v) in planes)
        {
            var n = Vector3.Normalize(Vector3.Cross(u, v));
            uint b = (uint)m.Vertices.Count;
            foreach (var (su, sv) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
                m.Vertices.Add(new Vertex
                {
                    Pos = u * (su - 0.5f) + v * (sv - 0.5f),
                    Normal = n,
                    Data0 = (uint)(su * 255) | ((uint)(sv * 255) << 8),
                });
            m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        return m;
    }
}

/// <summary>A per-chunk instance buffer drawn with a shared template mesh.</summary>
public sealed class InstanceBatch : IDisposable
{
    readonly GL _gl;
    readonly uint _vao, _instanceVbo;
    readonly uint _indexCount, _instanceCount;

    public unsafe InstanceBatch(GL gl, uint templateVbo, uint templateEbo, uint indexCount, List<Instance> instances)
    {
        _gl = gl;
        _indexCount = indexCount;
        _instanceCount = (uint)instances.Count;
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        gl.BindBuffer(BufferTargetARB.ArrayBuffer, templateVbo);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, templateEbo);
        GpuMesh.SetupVertexAttribs(gl);

        _instanceVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instanceVbo);
        gl.BufferData<Instance>(BufferTargetARB.ArrayBuffer, CollectionsMarshal.AsSpan(instances), BufferUsageARB.StaticDraw);
        for (uint i = 0; i < 2; i++)
        {
            gl.EnableVertexAttribArray(4 + i);
            gl.VertexAttribPointer(4 + i, 4, VertexAttribPointerType.Float, false, Instance.Size, (void*)(i * 16));
            gl.VertexAttribDivisor(4 + i, 1);
        }
        gl.BindVertexArray(0);
    }

    public uint TriangleCount => _indexCount / 3 * _instanceCount;

    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElementsInstanced(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, (void*)0, _instanceCount);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_instanceVbo);
    }
}
