using System.Numerics;
using System.Runtime.InteropServices;
using Realism.Meshing;
using Silk.NET.OpenGL;

namespace Realism.Rendering;

public sealed class ShaderProgram : IDisposable
{
    readonly GL _gl;
    readonly Dictionary<string, int> _locations = new();
    public uint Handle { get; }

    public ShaderProgram(GL gl, string vertex, string fragment)
    {
        _gl = gl;
        uint vs = Compile(ShaderType.VertexShader, vertex);
        uint fs = Compile(ShaderType.FragmentShader, fragment);
        Handle = gl.CreateProgram();
        gl.AttachShader(Handle, vs);
        gl.AttachShader(Handle, fs);
        gl.LinkProgram(Handle);
        gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException("Shader link failed: " + gl.GetProgramInfoLog(Handle));
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
    }

    uint Compile(ShaderType type, string source)
    {
        uint s = _gl.CreateShader(type);
        _gl.ShaderSource(s, source);
        _gl.CompileShader(s);
        _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type} compile failed: {_gl.GetShaderInfoLog(s)}");
        return s;
    }

    public void Use() => _gl.UseProgram(Handle);

    int Loc(string name)
    {
        if (!_locations.TryGetValue(name, out int loc))
            _locations[name] = loc = _gl.GetUniformLocation(Handle, name);
        return loc;
    }

    public void Set(string name, int v) => _gl.Uniform1(Loc(name), v);
    public void Set(string name, float v) => _gl.Uniform1(Loc(name), v);
    public void Set(string name, Vector3 v) => _gl.Uniform3(Loc(name), v.X, v.Y, v.Z);
    public void Set(string name, Vector2 v) => _gl.Uniform2(Loc(name), v.X, v.Y);
    public void Set(string name, Vector4 v) => _gl.Uniform4(Loc(name), v.X, v.Y, v.Z, v.W);

    // System.Numerics matrices are row-major with row vectors; uploading untransposed gives the
    // column-vector form GLSL expects, so `M * v` in the shader matches `v * M` in C#.
    public unsafe void Set(string name, Matrix4x4 m) => _gl.UniformMatrix4(Loc(name), 1, false, (float*)&m);

    public void Dispose() => _gl.DeleteProgram(Handle);
}

public sealed class GpuMesh : IDisposable
{
    readonly GL _gl;
    readonly uint _vao, _vbo, _ebo;
    readonly uint _count;

    public unsafe GpuMesh(GL gl, MeshData data)
    {
        _gl = gl;
        _count = (uint)data.Indices.Count;
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);

        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, CollectionsMarshal.AsSpan(data.Vertices), BufferUsageARB.StaticDraw);

        _ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, CollectionsMarshal.AsSpan(data.Indices), BufferUsageARB.StaticDraw);

        SetupVertexAttribs(gl);
        gl.BindVertexArray(0);
    }

    public static unsafe void SetupVertexAttribs(GL gl)
    {
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vertex.Size, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, Vertex.Size, (void*)12);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, Vertex.Size, (void*)24);
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(3, 4, VertexAttribPointerType.UnsignedByte, true, Vertex.Size, (void*)28);
    }

    public uint Vbo => _vbo;
    public uint Ebo => _ebo;
    public uint IndexCount => _count;

    public uint TriangleCount => _count / 3;

    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, _count, DrawElementsType.UnsignedInt, (void*)0);
    }

    public void Dispose()
    {
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
    }
}

public static class Mat4
{
    /// <summary>OpenGL-style perspective (clip z in [-w, w]), in System.Numerics row-vector layout.</summary>
    public static Matrix4x4 Perspective(float fovY, float aspect, float near, float far)
    {
        float y = 1f / MathF.Tan(fovY / 2), x = y / aspect;
        return new Matrix4x4(
            x, 0, 0, 0,
            0, y, 0, 0,
            0, 0, (far + near) / (near - far), -1,
            0, 0, 2 * far * near / (near - far), 0);
    }

    public static Matrix4x4 Ortho(float l, float r, float b, float t, float near, float far) => new(
        2 / (r - l), 0, 0, 0,
        0, 2 / (t - b), 0, 0,
        0, 0, -2 / (far - near), 0,
        -(r + l) / (r - l), -(t + b) / (t - b), -(far + near) / (far - near), 1);

    /// <summary>True if the box is at least partly inside the frustum of <paramref name="m"/>.</summary>
    public static bool BoxVisible(Matrix4x4 m, Vector3 min, Vector3 max)
    {
        Span<Vector4> planes = stackalloc Vector4[]
        {
            new(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41),
            new(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41),
            new(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42),
            new(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42),
            new(m.M14 + m.M13, m.M24 + m.M23, m.M34 + m.M33, m.M44 + m.M43),
            new(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43),
        };
        foreach (var p in planes)
        {
            var v = new Vector3(p.X >= 0 ? max.X : min.X, p.Y >= 0 ? max.Y : min.Y, p.Z >= 0 ? max.Z : min.Z);
            if (p.X * v.X + p.Y * v.Y + p.Z * v.Z + p.W < 0) return false;
        }
        return true;
    }
}
