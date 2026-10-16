using System.Numerics;
using Realism.Live;
using Realism.Meshing;
using Silk.NET.OpenGL;

namespace Realism.Rendering;

/// <summary>
/// The real Minecraft player: Minecraft's player geometry (in 1/16-block pixels) textured with the player's actual
/// skin using the standard 64x64 skin layout, including the outer layer (hat, jacket, sleeves, trousers) and
/// slim (3px) or wide (4px) arms. Posed by <see cref="PlayerModel.Pose"/>.
/// </summary>
public sealed class MinecraftPlayerModel : IDisposable
{
    readonly GL _gl;
    readonly uint _skin;
    readonly GpuMesh _head, _body, _armR, _armL, _legR, _legL;

    public MinecraftPlayerModel(GL gl, byte[] png, bool slim)
    {
        _gl = gl;
        var (rgba, w, h) = PngDecoder.Decode(png);
        if (h == 32) (rgba, h) = (UpgradeLegacySkin(rgba), 64);
        _skin = Upload(rgba, w, h);

        const float i1 = 0.5f, i2 = 0.25f; // outer-layer inflation (pixels), as Minecraft does
        int arm = slim ? 3 : 4;
        _head = Part(new Box(-4, 0, -4, 4, 8, 4, 0, 0, 8, 8, 8), new Box(-4, 0, -4, 4, 8, 4, 32, 0, 8, 8, 8, i1));
        _body = Part(new Box(-4, 0, -2, 4, 12, 2, 16, 16, 8, 12, 4), new Box(-4, 0, -2, 4, 12, 2, 16, 32, 8, 12, 4, i2));
        // Character's right is -X (the model faces +Z). Arms hang from the shoulder pivot, legs from the hip.
        _armR = Part(new Box(-arm + 1, -10, -2, 1, 2, 2, 40, 16, arm, 12, 4), new Box(-arm + 1, -10, -2, 1, 2, 2, 40, 32, arm, 12, 4, i2));
        _armL = Part(new Box(-1, -10, -2, arm - 1, 2, 2, 32, 48, arm, 12, 4), new Box(-1, -10, -2, arm - 1, 2, 2, 48, 48, arm, 12, 4, i2));
        _legR = Part(new Box(-2, -12, -2, 2, 0, 2, 0, 16, 4, 12, 4), new Box(-2, -12, -2, 2, 0, 2, 0, 32, 4, 12, 4, i2));
        _legL = Part(new Box(-2, -12, -2, 2, 0, 2, 16, 48, 4, 12, 4), new Box(-2, -12, -2, 2, 0, 2, 0, 48, 4, 12, 4, i2));
    }

    public void Draw(PlayerPose pose, Vector3 feet, Action<Matrix4x4> setModel)
    {
        _gl.ActiveTexture(TextureUnit.Texture4);
        _gl.BindTexture(TextureTarget.Texture2D, _skin);
        var m = PlayerModel.Pose(pose, feet);
        setModel(m.body); _body.Draw();
        setModel(m.head); _head.Draw();
        setModel(m.armR); _armR.Draw();
        setModel(m.armL); _armL.Draw();
        setModel(m.legR); _legR.Draw();
        setModel(m.legL); _legL.Draw();
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>A cuboid in pixels with its skin UV origin (u, v) and UV size (w, h, d); inflate grows the outer layer.</summary>
    readonly record struct Box(float X0, float Y0, float Z0, float X1, float Y1, float Z1, int U, int V, int W, int H, int D, float Inflate = 0)
    {
        public bool Overlay => Inflate > 0;
    }

    GpuMesh Part(params Box[] boxes)
    {
        var m = new MeshData();
        foreach (var b in boxes) AddBox(m, b);
        return new GpuMesh(_gl, m);
    }

    /// <summary>
    /// Six faces with Minecraft's skin layout: top (u+d, v), bottom (u+d+w, v), right (u, v+d), front (u+d, v+d),
    /// left (u+d+w, v+d), back (u+2d+w, v+d). Positions are converted from pixels to blocks.
    /// </summary>
    static void AddBox(MeshData m, Box b)
    {
        float s = 1 / 16f, g = b.Inflate;
        float x0 = (b.X0 - g) * s, x1 = (b.X1 + g) * s, y0 = (b.Y0 - g) * s, y1 = (b.Y1 + g) * s, z0 = (b.Z0 - g) * s, z1 = (b.Z1 + g) * s;
        int u = b.U, v = b.V, w = b.W, h = b.H, d = b.D;
        // Each face: four corners (pos) with their skin pixel (uv), in order top-left, top-right, bottom-right, bottom-left.
        Face(m, b, new Vector3(0, 0, 1), [new(x0, y1, z1), new(x1, y1, z1), new(x1, y0, z1), new(x0, y0, z1)], u + d, v + d, w, h);            // front
        Face(m, b, new Vector3(0, 0, -1), [new(x1, y1, z0), new(x0, y1, z0), new(x0, y0, z0), new(x1, y0, z0)], u + 2 * d + w, v + d, w, h); // back
        Face(m, b, new Vector3(-1, 0, 0), [new(x0, y1, z0), new(x0, y1, z1), new(x0, y0, z1), new(x0, y0, z0)], u, v + d, d, h);            // right
        Face(m, b, new Vector3(1, 0, 0), [new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), new(x1, y0, z1)], u + d + w, v + d, d, h);     // left
        Face(m, b, new Vector3(0, 1, 0), [new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1)], u + d, v, w, d);             // top
        Face(m, b, new Vector3(0, -1, 0), [new(x0, y0, z1), new(x1, y0, z1), new(x1, y0, z0), new(x0, y0, z0)], u + d + w, v, w, d);        // bottom
    }

    static void Face(MeshData m, Box b, Vector3 n, Vector3[] corners, int u, int v, int fw, int fh)
    {
        uint i = (uint)m.Vertices.Count;
        (int du, int dv)[] uv = [(0, 0), (fw, 0), (fw, fh), (0, fh)];
        for (int c = 0; c < 4; c++)
            m.Vertices.Add(new Vertex
            {
                Pos = corners[c], Normal = n,
                // Skin pixel coordinates (0..64) stored directly as bytes; .z marks the outer layer (alpha-tested).
                Data0 = (uint)(u + uv[c].du) | ((uint)(v + uv[c].dv) << 8) | ((uint)(b.Overlay ? 255 : 0) << 16),
            });
        // Corners go clockwise when seen from outside; emit counter-clockwise triangles.
        m.Indices.AddRange([i, i + 3, i + 2, i, i + 2, i + 1]);
    }

    /// <summary>Old 64x32 skins have no separate left limbs: copy the right ones down, as Minecraft does.</summary>
    static byte[] UpgradeLegacySkin(byte[] rgba)
    {
        var o = new byte[64 * 64 * 4];
        Array.Copy(rgba, o, rgba.Length);
        void Copy(int sx, int sy, int dx, int dy, int w, int h)
        {
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Array.Copy(o, ((sy + y) * 64 + sx + x) * 4, o, ((dy + y) * 64 + dx + w - 1 - x) * 4, 4);
        }
        Copy(0, 16, 16, 48, 16, 16);  // leg
        Copy(40, 16, 32, 48, 16, 16); // arm
        return o;
    }

    unsafe uint Upload(byte[] rgba, int w, int h)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = rgba)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    public void Dispose()
    {
        foreach (var p in new[] { _head, _body, _armR, _armL, _legR, _legL }) p.Dispose();
        _gl.DeleteTexture(_skin);
    }
}
