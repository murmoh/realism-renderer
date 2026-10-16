using System.Buffers.Binary;
using System.Numerics;
using Realism.Live;
using Realism.Meshing;
using Silk.NET.OpenGL;

namespace Realism.Rendering;

/// <summary>
/// Draws Minecraft's mobs (its own models, textures and animation, posed by the mod every frame) and dropped items
/// (their real icons: a small textured cube for blocks, a flat sprite otherwise; bobbing and spinning as in Minecraft).
/// </summary>
public sealed class EntityDrawer : IDisposable
{
    readonly GL _gl;
    /// <summary>A mob model: Minecraft's blocky parts, and for supported animals their realistic versions.</summary>
    sealed record MobModel(string Type, GpuMesh?[] Blocky, GpuMesh?[]? Organic, GpuMesh?[]? Coat) : IDisposable
    {
        public void Dispose()
        {
            foreach (var set in new[] { Blocky, Organic, Coat }) if (set != null) foreach (var m in set) m?.Dispose();
        }
    }

    readonly Dictionary<int, MobModel> _models = new();

    /// <summary>Draw animals with their realistic bodies (false: Minecraft's blocky models).</summary>
    public bool Realistic = true;
    readonly Dictionary<int, uint> _textures = new();
    // A texture's two main colours (the larger group first): what a realistic animal is coloured with.
    readonly Dictionary<int, (Vector3 main, Vector3 second)> _palettes = new();

    static (Vector3 main, Vector3 second) Palette(byte[] rgba)
    {
        var px = new List<Vector3>();
        for (int i = 0; i + 3 < rgba.Length; i += 4)
            if (rgba[i + 3] > 200) px.Add(new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]) / 255f);
        if (px.Count == 0) return (new Vector3(0.6f), new Vector3(0.8f));
        // Two-means on brightness (a few rounds): e.g. a cow's brown and white, a wolf's grey and cream.
        float Lum(Vector3 c) => c.X * 0.3f + c.Y * 0.59f + c.Z * 0.11f;
        float split = px.Average(Lum);
        Vector3 a = default, b = default;
        int na = 0, nb = 0;
        for (int round = 0; round < 4; round++)
        {
            a = b = Vector3.Zero;
            na = nb = 0;
            foreach (var c in px) if (Lum(c) < split) { a += c; na++; } else { b += c; nb++; }
            if (na == 0 || nb == 0) break;
            a /= na; b /= nb;
            split = (Lum(a) + Lum(b)) / 2;
        }
        if (na == 0 || nb == 0) { var m = px.Aggregate(Vector3.Zero, (s, c) => s + c) / px.Count; return (m, m); }
        return na >= nb ? (a, b) : (b, a);
    }
    readonly uint _white;
    readonly GpuMesh _cube, _sprite;
    long _frame;

    readonly List<(int model, int texture, Matrix4x4?[] parts, Vector4 wool)> _mobs = new();
    readonly List<(Vector3 pos, int texture, bool block, int count, float age, float seed)> _items = new();
    // Mob layers (sheep wool, saddles, armour, held items, eyes): this frame's quads, one mesh per texture.
    readonly List<(int texture, GpuMesh mesh)> _layers = new();

    /// <summary>Minecraft's block atlas (set by the app): held items and block-textured layers use it (texture -1).</summary>
    public uint AtlasTexture;

    public int MobCount => _mobs.Count;
    public int ItemCount => _items.Count;

    public EntityDrawer(GL gl)
    {
        _gl = gl;
        _white = Upload([255, 255, 255, 255], 1, 1);
        _cube = new GpuMesh(gl, UnitCube());
        _sprite = new GpuMesh(gl, Sprite());
    }

    /// <summary>Turns newly arrived models/textures into GPU resources and reads the newest snapshot.</summary>
    // Finished anatomy builds waiting to be uploaded (model id, the model's blocky meshes as identity, the meshes).
    readonly System.Collections.Concurrent.ConcurrentQueue<(int id, GpuMesh?[] blocky, Dictionary<int, MeshData> parts, Dictionary<int, MeshData> coat)> _builtAnatomy = new();

    public void Update(MinecraftBridge bridge)
    {
        while (_builtAnatomy.TryDequeue(out var b))
        {
            // Only if that model is still the one it was built for (a new session may have replaced it).
            if (!_models.TryGetValue(b.id, out var mm) || !ReferenceEquals(mm.Blocky, b.blocky)) continue;
            var organic = new GpuMesh?[mm.Blocky.Length];
            var coat = new GpuMesh?[mm.Blocky.Length];
            foreach (var (pi, md) in b.parts) if (pi < organic.Length) organic[pi] = new GpuMesh(_gl, md);
            foreach (var (pi, md) in b.coat) if (pi < coat.Length) coat[pi] = new GpuMesh(_gl, md);
            _models[b.id] = mm with { Organic = organic, Coat = coat };
        }
        while (bridge.EntityAssets.TryDequeue(out var asset))
        {
            try
            {
                if (asset.type == BridgeProtocol.MsgModel) LoadModel(asset.data);
                else LoadTexture(asset.data);
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException)
            {
                Console.WriteLine($"Skipped a Minecraft {(asset.type == BridgeProtocol.MsgModel ? "model" : "texture")}: {e.Message}");
            }
        }
        if (!bridge.TryGetEntities(ref _frame, out var s)) return;

        _items.Clear();
        _mobs.Clear();
        foreach (var (_, mesh) in _layers) mesh.Dispose();
        _layers.Clear();
        var layerData = new Dictionary<int, MeshData>();
        int p = 0;
        int Int(ReadOnlySpan<byte> b) { int v = BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4; return v; }
        float Float(ReadOnlySpan<byte> b) { float v = BinaryPrimitives.ReadSingleLittleEndian(b[p..]); p += 4; return v; }

        int items = Int(s);
        for (int i = 0; i < items; i++)
        {
            var pos = new Vector3(Float(s), Float(s), Float(s));
            _items.Add((pos, Int(s), Int(s) != 0, Int(s), Float(s), Float(s)));
        }
        int mobs = Int(s);
        for (int i = 0; i < mobs; i++)
        {
            int model = Int(s), texture = Int(s), count = Int(s);
            var parts = new Matrix4x4?[count];
            for (int k = 0; k < count; k++)
            {
                // Column-major (JOML) read row by row = the transposed matrix = System.Numerics' row-vector form.
                var m = new Matrix4x4(Float(s), Float(s), Float(s), Float(s), Float(s), Float(s), Float(s), Float(s),
                                      Float(s), Float(s), Float(s), Float(s), Float(s), Float(s), Float(s), Float(s));
                parts[k] = m.M44 == 0 ? null : m; // all-zero = hidden part
            }
            // Realistic sheep: Minecraft's blocky wool layer is replaced by a fleece in the wool's colour.
            bool fleece = Realistic && _models.TryGetValue(model, out var mm) && mm.Coat != null;
            var woolSum = Vector4.Zero;

            int layerQuads = Int(s);
            Span<Vector3> pos = stackalloc Vector3[4];
            Span<uint> uv = stackalloc uint[4], color = stackalloc uint[4];
            for (int q = 0; q < layerQuads; q++)
            {
                int tex = Int(s);
                for (int k = 0; k < 4; k++)
                {
                    pos[k] = new Vector3(Float(s), Float(s), Float(s));
                    float u = Float(s), v = Float(s);
                    uv[k] = PackUv(u, v);
                    uint argb = (uint)Int(s);
                    color[k] = ((argb >> 16) & 0xFF) | (((argb >> 8) & 0xFF) << 8) | ((argb & 0xFF) << 16) | (argb & 0xFF000000); // to RGBA bytes
                }
                if (fleece)
                {
                    for (int k = 0; k < 4; k++)
                        woolSum += new Vector4(color[k] & 0xFF, (color[k] >> 8) & 0xFF, (color[k] >> 16) & 0xFF, 255) / 255f;
                    continue;
                }
                if (!layerData.TryGetValue(tex, out var md)) layerData[tex] = md = new MeshData();
                var n = Vector3.Cross(pos[1] - pos[0], pos[2] - pos[0]);
                n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
                uint b = (uint)md.Vertices.Count;
                for (int k = 0; k < 4; k++) md.Vertices.Add(new Vertex { Pos = pos[k], Normal = n, Data0 = uv[k], Data1 = color[k] });
                md.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            }
            _mobs.Add((model, texture, parts, woolSum.W > 0 ? woolSum / woolSum.W : Vector4.Zero));
        }
        foreach (var (tex, md) in layerData) _layers.Add((tex, new GpuMesh(_gl, md)));
    }

    /// <summary>Draws everything; <paramref name="setModel"/> sets uModel, <paramref name="bindTexture"/> is skipped for shadows.</summary>
    public void Draw(float time, Action<Matrix4x4> setModel, bool bindTextures)
    {
        foreach (var (model, texture, parts, _) in _mobs)
        {
            if (!_models.TryGetValue(model, out var mm)) continue;
            if (Realistic && mm.Organic != null && bindTextures) continue; // drawn by DrawRealistic
            var meshes = Realistic && mm.Organic != null ? mm.Organic : mm.Blocky;
            if (bindTextures) Bind(texture);
            for (int k = 0; k < parts.Length && k < meshes.Length; k++)
            {
                if (parts[k] is not { } m || meshes[k] is not { } mesh) continue;
                setModel(m);
                mesh.Draw();
            }
        }
        if (_layers.Count > 0)
        {
            setModel(Matrix4x4.Identity); // layer quads are already in world space
            foreach (var (texture, mesh) in _layers)
            {
                if (bindTextures) Bind(texture);
                mesh.Draw();
            }
        }
        foreach (var (pos, texture, block, count, age, seed) in _items)
        {
            if (bindTextures) Bind(texture);
            // Minecraft's dropped-item motion: bob up and down, spin slowly.
            float bob = MathF.Sin(age / 10f + seed) * 0.1f + 0.1f;
            float spin = age / 20f + seed;
            float size = block ? 0.25f : 0.5f;
            int copies = count > 32 ? 3 : count > 1 ? 2 : 1;
            for (int c = 0; c < copies; c++)
            {
                var offset = new Vector3(c * 0.05f, c * 0.04f, -c * 0.05f);
                setModel(Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationY(spin) * Matrix4x4.CreateTranslation(pos + new Vector3(0, bob + size / 2, 0) + offset));
                (block ? _cube : _sprite).Draw();
            }
        }
    }

    /// <summary>
    /// The realistic animals, with the organic shader bound by the caller: <paramref name="setWool"/> sets the fleece
    /// colour (alpha 0: no fleece, e.g. sheared) before each mob's coat.
    /// </summary>
    public void DrawRealistic(Action<Matrix4x4> setModel, Action<Vector4> setWool, Action<Vector3, Vector3, float, int>? setPalette = null)
    {
        if (!Realistic) return;
        foreach (var (model, texture, parts, wool) in _mobs)
        {
            if (!_models.TryGetValue(model, out var mm) || mm.Organic == null) continue;
            Bind(texture);
            if (setPalette != null)
            {
                var (main, second) = _palettes.TryGetValue(texture, out var pal) ? pal : (new Vector3(0.6f), new Vector3(0.8f));
                float patches = mm.Type is "cow" or "mooshroom" ? 1f : mm.Type == "wolf" ? 0.35f : 0f;
                setPalette(main, second, patches, Anatomy.Supports(mm.Type) ? 1 : 0);
            }
            setWool(Vector4.Zero);
            for (int k = 0; k < parts.Length && k < mm.Organic.Length; k++)
            {
                if (parts[k] is not { } m || mm.Organic[k] is not { } mesh) continue;
                setModel(m);
                mesh.Draw();
            }
            if (mm.Coat == null || wool.W <= 0) continue;
            setWool(wool);
            for (int k = 0; k < parts.Length && k < mm.Coat.Length; k++)
            {
                if (parts[k] is not { } m || mm.Coat[k] is not { } mesh) continue;
                setModel(m);
                mesh.Draw();
            }
        }
    }

    void Bind(int texture)
    {
        _gl.ActiveTexture(TextureUnit.Texture5);
        _gl.BindTexture(TextureTarget.Texture2D, texture == -1 && AtlasTexture != 0 ? AtlasTexture : _textures.TryGetValue(texture, out var t) ? t : _white);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    void LoadModel(byte[] d)
    {
        int p = 0;
        int Int() { int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)); p += 4; return v; }
        float Float() { float v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p)); p += 4; return v; }
        string Str() { int n = BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(p)); p += 2; var s = System.Text.Encoding.UTF8.GetString(d, p, n); p += n; return s; }
        int id = Int();
        string type = Str();
        int partCount = Int();
        var meshes = new GpuMesh?[partCount];
        var rig = new MobRig();
        bool organic = OrganicMobs.Supports(type) || Anatomy.Supports(type);
        var organicMeshes = organic ? new GpuMesh?[partCount] : null;
        var coatMeshes = organic ? new GpuMesh?[partCount] : null;
        for (int part = 0; part < partCount; part++)
        {
            string name = Str();
            int parent = Int();
            var rest = MobRig.RestLocal(Float(), Float(), Float(), Float(), Float(), Float(), Float(), Float(), Float());
            int cuboidCount = Int();
            var m = new MeshData();
            var cuboids = new List<ModelCuboid>();
            for (int c = 0; c < cuboidCount; c++)
            {
                var min = new Vector3(Float(), Float(), Float()) / 16f;
                var max = new Vector3(Float(), Float(), Float()) / 16f;
                int quads = Int();
                var faces = new (Vector3 normal, Vector3[] pos, Vector2[] uv)[quads];
                for (int q = 0; q < quads; q++)
                {
                    var n = new Vector3(Float(), Float(), Float());
                    uint b = (uint)m.Vertices.Count;
                    var corners = new Vector3[4];
                    var uvs = new Vector2[4];
                    for (int v = 0; v < 4; v++)
                    {
                        corners[v] = new Vector3(Float(), Float(), Float()) / 16f; // model pixels -> blocks, as Minecraft does
                        uvs[v] = new Vector2(Float(), Float());
                        m.Vertices.Add(new Vertex { Pos = corners[v], Normal = n, Data0 = PackUv(uvs[v].X, uvs[v].Y), Data1 = 0xFFFFFFFF });
                    }
                    faces[q] = (n, corners, uvs);
                    // Wind so the triangle faces along the quad's normal.
                    bool flip = Vector3.Dot(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]), n) < 0;
                    if (flip) m.Indices.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
                    else m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
                }
                cuboids.Add(new ModelCuboid(min, max, faces));
            }
            meshes[part] = m.IsEmpty ? null : new GpuMesh(_gl, m);
            rig.Add(name, parent, rest, cuboids);
            if (organic && !Anatomy.Supports(type) && cuboids.Count > 0)
            {
                if (OrganicMobs.BuildPart(type, name, cuboids) is { } om) organicMeshes![part] = new GpuMesh(_gl, om);
                if (OrganicMobs.HasCoat(type, name) && OrganicMobs.BuildPart(type, name, cuboids, coat: true) is { } cm) coatMeshes![part] = new GpuMesh(_gl, cm);
            }
        }
        rig.Finish();
        // Designed anatomy for the common animals, built on a background thread (it takes up to a few hundred ms,
        // which would freeze the view); until it is ready the animal shows Minecraft's shapes.
        if (Anatomy.Supports(type))
        {
            organicMeshes = coatMeshes = null;
            var meshesRef = meshes;
            Task.Run(() =>
            {
                try { if (Anatomy.Build(type, rig) is { } built) _builtAnatomy.Enqueue((id, meshesRef, built.parts, built.coat)); }
                catch (Exception e) { Console.WriteLine($"Anatomy {type} failed: {e.Message}"); }
            });
        }
        if (Environment.GetEnvironmentVariable("REALISM_RIG_DUMP") != null) Console.WriteLine($"Rig {type}:{Environment.NewLine}{rig}");
        if (_models.Remove(id, out var old)) old.Dispose();
        _models[id] = new MobModel(type, meshes, organicMeshes, coatMeshes);
    }

    void LoadTexture(byte[] d)
    {
        int id = BinaryPrimitives.ReadInt32LittleEndian(d);
        int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
        if (len == 0) return; // couldn't be read: drawn plain white
        var (rgba, w, h) = PngDecoder.Decode(d.AsSpan(8, len).ToArray());
        if (_textures.Remove(id, out var old)) _gl.DeleteTexture(old);
        _textures[id] = Upload(rgba, w, h);
        _palettes[id] = Palette(rgba);
    }

    /// <summary>UV (0..1) as two 16-bit values in Data0's four bytes (the shader rebuilds them).</summary>
    static uint PackUv(float u, float v)
    {
        uint U = (uint)Math.Clamp(MathF.Round(u * 65535), 0, 65535), V = (uint)Math.Clamp(MathF.Round(v * 65535), 0, 65535);
        return (U & 0xFF) | ((U >> 8) << 8) | ((V & 0xFF) << 16) | ((V >> 8) << 24);
    }

    static MeshData UnitCube()
    {
        var m = new MeshData();
        for (int face = 0; face < 6; face++)
        {
            int a = face / 2, u = (a + 1) % 3, v = (a + 2) % 3;
            float s = face % 2 == 0 ? 1 : -1;
            uint b = (uint)m.Vertices.Count;
            foreach (var (cu, cv) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
            {
                var pos = Vector3.Zero;
                pos[a] = s * 0.5f;
                pos[u] = cu - 0.5f;
                pos[v] = cv - 0.5f;
                var n = Vector3.Zero;
                n[a] = s;
                m.Vertices.Add(new Vertex { Pos = pos, Normal = n, Data0 = PackUv(cu, 1 - cv), Data1 = 0xFFFFFFFF });
            }
            if (s > 0) m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            else m.Indices.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
        }
        return m;
    }

    static MeshData Sprite()
    {
        var m = new MeshData();
        foreach (float side in new[] { 1f, -1f })
        {
            uint b = (uint)m.Vertices.Count;
            foreach (var (x, y) in new[] { (0, 0), (1, 0), (1, 1), (0, 1) })
                m.Vertices.Add(new Vertex { Pos = new Vector3(x - 0.5f, y - 0.5f, 0), Normal = new Vector3(0, 0, side), Data0 = PackUv(side > 0 ? x : 1 - x, 1 - y), Data1 = 0xFFFFFFFF });
            if (side > 0) m.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            else m.Indices.AddRange([b, b + 2, b + 1, b, b + 3, b + 2]);
        }
        return m;
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
        foreach (var m in _models.Values) m.Dispose();
        foreach (var (_, mesh) in _layers) mesh.Dispose();
        foreach (var t in _textures.Values) _gl.DeleteTexture(t);
        _gl.DeleteTexture(_white);
        _cube.Dispose();
        _sprite.Dispose();
    }
}
