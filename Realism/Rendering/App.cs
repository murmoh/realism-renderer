using System.Numerics;
using Realism.Meshing;
using Realism.Minecraft;
using Realism.Live;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Realism.Rendering;

public sealed record AppOptions(
    Vector3 CameraPos, float Yaw, float Pitch, float TimeOfDay, float FogRange, string? ScreenshotPath, string Title, int Weather = 0,
    Realism.Live.MinecraftBridge? Bridge = null, bool ThirdPerson = false,
    Cinematic? Film = null, string? FilmOut = null, string? FilmPreviewDir = null, Gameplay? Play = null);

/// <summary>
/// Window, fly camera and the frame: shadow cascades, sky, opaque world, foliage, then transparent water.
/// </summary>
public sealed class App
{
    const int ShadowSize = 4096;      // far cascade: whole loaded area, re-rendered when the sun moves
    const int NearShadowSize = 2048;  // near cascade: follows the camera every frame
    const float NearShadowRadius = 45f;
    const float PebbleFar = 45f;      // pebbles are only drawn this close
    const float NearPlane = 0.1f;
    const float GrassFar = 75f;       // grass blades fade out by this distance; beyond it the ground texture takes over

    sealed class ChunkGpu
    {
        public GpuMesh? Terrain, LeafCore, Wood, Water, Buildings, Glass, ShadowCanopy, Blocks, BlocksTranslucent, Ores;
        public InstanceBatch? Grass, LeafCards, Rocks, Pebbles;
        public Vector4[] Lights = [];
        public Vector4[] Emitters = [];
        public Vector3 Min, Max;
        public Vector3 Centre => (Min + Max) / 2;

        public void Dispose()
        {
            foreach (var m in new IDisposable?[] { Terrain, LeafCore, Wood, Water, Buildings, Glass, ShadowCanopy, Blocks, BlocksTranslucent, Ores, Grass, LeafCards, Rocks, Pebbles }) m?.Dispose();
        }
    }

    readonly ChunkStreamer _streamer;
    readonly Realism.Live.MinecraftBridge? _bridge;
    float _fov = 60; // vertical field of view drawn with, degrees
    bool _follow = true, _liveInWorld, _captured = true, _windowFocused = true, _lookInit;
    int _slot, _slotSeq, _attackClicks, _useClicks, _pickClicks, _dropPresses;
    int _generation;
    int _chunksSinceShadow;
    Vector3 _farShadowCentre;
    double _farShadowAge;
    readonly AppOptions _opt;
    readonly IWindow _window;
    GL _gl = null!;
    IInputContext _input = null!;

    ShaderProgram _world = null!, _water = null!, _glass = null!, _sky = null!, _shadow = null!, _rainStreaks = null!, _dust = null!, _blocks = null!, _ores = null!, _particles = null!;
    uint _atlasTex;      // Minecraft's block atlas (live mode)
    int _atlasVersion;
    ShaderProgram _grass = null!, _cards = null!, _cardShadow = null!, _rocks = null!, _rockShadow = null!;
    GpuMesh _grassTemplate = null!, _cardTemplate = null!, _rockTemplate = null!, _pebbleTemplate = null!;
    PostProcess? _post;
    string? _pendingScreenshot;
    double _fpsTimer;
    int _fpsFrames;
    uint _skyVao;
    (uint fbo, uint tex) _farShadow, _nearShadow;
    bool _shadowDirty = true;
    Matrix4x4 _lightViewProj, _lightViewProjNear;

    readonly Dictionary<long, ChunkGpu> _chunks = new();

    Vector3 _camPos;
    float _yaw, _pitch, _timeOfDay, _time, _speed = 20f;
    int _weather;                 // 0 clear, 1 overcast, 2 rain
    float _overcast, _rain;       // eased toward the weather target
    Vector3 _trueSun, _lastKeyDir;
    float _night, _underground;
    readonly System.Diagnostics.Stopwatch _perfClock = System.Diagnostics.Stopwatch.StartNew();
    double _perfSum, _perfMax;
    int _perfN;
    float _adapt = 1; // eye adaptation: 1 = the tuned exposure, lower when the view is too bright for it
    readonly List<Vector4> _pointLights = new();
    readonly Vector4[] _lightColors = new Vector4[32];
    Vector2? _lastMouse;
    int _frame, _settledFrames;

    public App(ChunkStreamer streamer, AppOptions opt)
    {
        _streamer = streamer;
        _bridge = opt.Bridge;
        _thirdPerson = opt.ThirdPerson;
        _opt = opt;
        _camPos = opt.CameraPos;
        _yaw = opt.Yaw;
        _pitch = opt.Pitch;
        _timeOfDay = opt.TimeOfDay;
        _weather = opt.Weather;
        _overcast = _weather >= 1 ? 1 : 0;
        _rain = _weather == 2 ? 1 : 0;

        var options = WindowOptions.Default with
        {
            Size = opt.Film != null || opt.Play != null ? new Vector2D<int>(1920, 1080) : new Vector2D<int>(1600, 900),
            Title = opt.Title,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            Samples = 0, // anti-aliasing happens in the post-process scene buffer
            IsVisible = opt.ScreenshotPath == null && opt.Film == null && opt.Play == null, // recordings render hidden, never stealing focus
            VSync = opt.Film == null && opt.Play == null,
        };
        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.FramebufferResize += s => _post?.Resize(s.X, s.Y);
        // Tests: REALISM_FOLLOW behaves as if you were playing in Minecraft's own window (the renderer just follows).
        if (Environment.GetEnvironmentVariable("REALISM_FOLLOW") != null) _windowFocused = false;
        _window.FocusChanged += focused =>
        {
            _windowFocused = focused;
            if (!focused && _bridge != null) SetCaptured(false); // never leave keys held in the game
        };
    }

    public void Run() => _window.Run();

    void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();

        _world = new ShaderProgram(_gl, Shaders.WorldVertex, Shaders.WorldFragment);
        _water = new ShaderProgram(_gl, Shaders.WorldVertex, Shaders.WaterFragment);
        _glass = new ShaderProgram(_gl, Shaders.WorldVertex, Shaders.GlassFragment);
        _rainStreaks = new ShaderProgram(_gl, Shaders.RainVertex, Shaders.RainFragment);
        _dust = new ShaderProgram(_gl, Shaders.DustVertex, Shaders.DustFragment);
        _blocks = new ShaderProgram(_gl, Shaders.BlockVertex, Shaders.BlockFragment);
        _ores = new ShaderProgram(_gl, Shaders.OreVertex, Shaders.OreFragment);
        _particles = new ShaderProgram(_gl, Shaders.ParticleVertex, Shaders.ParticleFragment);
        _sky = new ShaderProgram(_gl, Shaders.SkyVertex, Shaders.SkyFragment);
        _shadow = new ShaderProgram(_gl, Shaders.ShadowVertex, Shaders.ShadowFragment);
        _grass = new ShaderProgram(_gl, Shaders.GrassVertex, Shaders.GrassFragment);
        _cards = new ShaderProgram(_gl, Shaders.LeafCardVertex, Shaders.LeafCardFragment);
        _cardShadow = new ShaderProgram(_gl, Shaders.LeafCardShadowVertex, Shaders.LeafCardShadowFragment);
        _skyVao = _gl.GenVertexArray();
        _grassTemplate = new GpuMesh(_gl, Foliage.GrassTuft());
        _cardTemplate = new GpuMesh(_gl, Foliage.LeafCluster());
        _rockTemplate = new GpuMesh(_gl, Foliage.Rock(2));
        _pebbleTemplate = new GpuMesh(_gl, Foliage.Rock(1));
        _rocks = new ShaderProgram(_gl, Shaders.RockVertex, Shaders.RockFragment);
        _actor = new ShaderProgram(_gl, Shaders.ActorVertex, Shaders.ActorFragment);
        _playerModel = new PlayerModel(_gl);
        _skinProgram = new ShaderProgram(_gl, Shaders.ActorVertex, Shaders.SkinFragment);
        _entityProgram = new ShaderProgram(_gl, Shaders.EntityVertex, Shaders.EntityFragment);
        _organicProgram = new ShaderProgram(_gl, Shaders.OrganicVertex, Shaders.OrganicFragment);
        _entityDrawer = new EntityDrawer(_gl);
        _outlineProgram = new ShaderProgram(_gl, Shaders.EntityVertex, Shaders.OutlineFragment);
        _targetOutline = new TargetOutline(_gl);
        _guiProgram = new ShaderProgram(_gl, PostShaders.Fullscreen, Shaders.GuiFragment);
        _guiTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _guiTex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _rockShadow = new ShaderProgram(_gl, Shaders.RockShadowVertex, Shaders.ShadowFragment);
        _post = new PostProcess(_gl, _window.FramebufferSize.X, _window.FramebufferSize.Y);
        if (_opt.ScreenshotPath != null || _opt.Film != null || _opt.Play != null) _post.OutputFbo = CreateOffscreenTarget(_window.FramebufferSize.X, _window.FramebufferSize.Y);


        _farShadow = CreateShadowMap(ShadowSize);
        _nearShadow = CreateShadowMap(NearShadowSize);

        if (_opt.ScreenshotPath == null && _opt.Film == null && _opt.Play == null)
        {
            foreach (var mouse in _input.Mice)
            {
                mouse.Cursor.CursorMode = CursorMode.Raw;
                mouse.MouseDown += OnMouseDown;
                mouse.MouseUp += OnMouseUp;
                mouse.MouseMove += OnMouseMove;
                mouse.Scroll += OnScroll;
            }
            foreach (var kb in _input.Keyboards)
            {
                kb.KeyDown += OnKeyDown;
                kb.KeyUp += OnKeyUp;
                kb.KeyChar += OnKeyChar;
            }
            PrintControls();
        }
    }

    /// <summary>Uploads finished chunk meshes from the streamer (time-boxed) and frees chunks that fell out of range.</summary>
    void StreamChunks()
    {
        if (_streamer.Generation != _generation)
        {
            // New live session (other world or dimension): drop every chunk on the GPU.
            _generation = _streamer.Generation;
            foreach (var c in _chunks.Values) c.Dispose();
            _chunks.Clear();
        }
        _streamer.SetCamera(_camPos);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < 6 && _streamer.TryTake(out var m))
        {
            GpuMesh? Up(MeshData d) => d.IsEmpty ? null : new GpuMesh(_gl, d);
            InstanceBatch? Inst(GpuMesh t, List<Instance> list) =>
                list.Count == 0 ? null : new InstanceBatch(_gl, t.Vbo, t.Ebo, t.IndexCount, list);
            long key = World.Key(m.X, m.Z);
            if (_chunks.Remove(key, out var old)) old.Dispose();
            if (m.Min.X > m.Max.X) continue; // nothing to draw
            _chunks[key] = new ChunkGpu
            {
                Terrain = Up(m.Terrain), LeafCore = Up(m.Leaves), Wood = Up(m.Wood), Water = Up(m.Water),
                Buildings = Up(m.Buildings), Glass = Up(m.Glass), ShadowCanopy = Up(m.ShadowCanopy),
                Blocks = Up(m.Blocks), BlocksTranslucent = Up(m.BlocksTranslucent), Ores = Up(m.Ores),
                Grass = Inst(_grassTemplate, m.Grass), LeafCards = Inst(_cardTemplate, m.LeafCards),
                Rocks = Inst(_rockTemplate, m.Rocks), Pebbles = Inst(_pebbleTemplate, m.Pebbles),
                Min = m.Min - Vector3.One, Max = m.Max + Vector3.One,
                Lights = m.Lights.ToArray(),
                Emitters = m.Emitters.ToArray(),
            };
            _chunksSinceShadow++;
            if (Realism.Live.MinecraftBridge.TraceTimes) Console.WriteLine($"[trace] {Environment.TickCount64 % 100000} uploaded {m.X},{m.Z}");
        }

        int ccx = (int)MathF.Floor(_camPos.X) >> 4, ccz = (int)MathF.Floor(_camPos.Z) >> 4;
        int keep = _streamer.Radius + 2;
        foreach (var key in _chunks.Keys.ToList())
        {
            var (cx, cz) = World.Unkey(key);
            if ((cx - ccx) * (cx - ccx) + (cz - ccz) * (cz - ccz) > keep * keep)
            {
                _chunks[key].Dispose();
                _chunks.Remove(key);
            }
        }
    }

    static void PrintControls() => Console.WriteLine("""

        Controls:
          Mouse        look around
          W A S D      fly
          Space / C    up / down
          Shift        fly 5x faster   (mouse wheel changes base speed)
          T / G        time of day (keeps going through night)
          R            weather: clear / overcast / rain
          F6           live: play / free camera
          F7           realistic / Minecraft-style animals
          Live play    all Minecraft keys work as in Minecraft (E inventory, T chat, Esc menu, F5 view, ...);
                       WASD move, Space jump, Shift sneak, Ctrl sprint, mouse look,
                       left click mine/attack, right click place/use, middle pick, Q drop, 1-9 / wheel hotbar
          F2           save screenshot
          Esc          quit  (live: release the mouse; click the window to take control again)
        """);

    unsafe (uint fbo, uint tex) CreateShadowMap(int size)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent32f, (uint)size, (uint)size, 0,
            PixelFormat.DepthComponent, PixelType.Float, (void*)0);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)GLEnum.Lequal);

        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, tex, 0);
        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return (fbo, tex);
    }

    /// <summary>
    /// Time of day runs 0..2: 0..1 is daytime (sunrise to sunset), 1..2 is night. The key light is the sun by day
    /// and the moon (opposite the sun) by night; each fades to zero just below the horizon, so they swap without a pop.
    /// </summary>
    (Vector3 keyDir, Vector3 keyColor, Vector3 trueSun, float night) Lighting()
    {
        float a = _timeOfDay * MathF.PI;
        var sun = Vector3.Normalize(new Vector3(MathF.Cos(a), MathF.Sin(a), 0.4f));
        var moon = Vector3.Normalize(new Vector3(-MathF.Cos(a), -MathF.Sin(a), 0.25f));
        float night = 1 - SmoothStep(-0.15f, 0.08f, sun.Y);
        if (sun.Y > -0.04f) return (sun, SunColor(sun), sun, night);
        float moonFade = Math.Clamp((-sun.Y - 0.04f) / 0.12f, 0, 1) * Math.Clamp(moon.Y * 5, 0, 1);
        return (moon, new Vector3(0.42f, 0.6f, 1.0f) * 0.3f * moonFade, sun, night); // cool, clear moonlight
    }

    static float SmoothStep(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }


    static Vector3 SunColor(Vector3 sun)
    {
        float t = Math.Clamp(sun.Y / 0.45f, 0, 1);
        var warm = new Vector3(1.0f, 0.42f, 0.16f);
        var noon = new Vector3(1.0f, 0.95f, 0.87f);
        float fade = Math.Clamp((sun.Y + 0.04f) / 0.12f, 0, 1);
        return Vector3.Lerp(warm, noon, MathF.Sqrt(t)) * 3.4f * fade;
    }

    // Pitch is kept just short of vertical: a camera looking exactly straight up/down has no defined "up".
    Vector3 Forward()
    {
        float p = Math.Clamp(_pitch, -1.55f, 1.55f);
        return new(MathF.Cos(p) * MathF.Cos(_yaw), MathF.Sin(p), MathF.Cos(p) * MathF.Sin(_yaw));
    }

    float? _smoothEyeY;
    ShaderProgram _actor = null!;
    PlayerModel _playerModel = null!;
    PlayerPose _pose;
    Vector3 _poseFeet;
    bool _thirdPerson, _wasScreen;

    /// <summary>Draw the player body: in third person, or in free camera (looking at yourself).</summary>
    bool ShowPlayer => _bridge != null && _liveInWorld && _pose.Valid && (_thirdPerson || !_follow);

    /// <summary>
    /// Minecraft walks on block steps; the world here is smooth. On natural ground the eye rides on the smooth
    /// surface under the feet; in the air, on built floors, or far from the surface, it eases back to Minecraft's
    /// exact height, so jumps, falls and stairs stay correct.
    /// </summary>
    Vector3 SmoothCamera(PlayerState ps, float dt)
    {
        float eyeHeight = ps.Eye.Y - ps.FeetY; // 1.62 standing, less when sneaking or swimming
        float target = ps.Eye.Y;
        var world = _bridge!.World;
        var under = world.GetChunk((int)MathF.Floor(ps.Eye.X) >> 4, (int)MathF.Floor(ps.Eye.Z) >> 4)?
            .Get((int)MathF.Floor(ps.Eye.X) & 15, (int)MathF.Floor(ps.FeetY - 0.05f), (int)MathF.Floor(ps.Eye.Z) & 15);
        // Around blocks dug or placed in play the ground is Minecraft's exact blocks: follow its height there.
        int fx = (int)MathF.Floor(ps.Eye.X), fy = (int)MathF.Floor(ps.FeetY), fz = (int)MathF.Floor(ps.Eye.Z);
        bool nearEdit = world.EditsNear(fx - 2, fz - 2, fx + 2, fz + 2)
            .Any(e => Math.Abs(e.x - fx) <= 2 && Math.Abs(e.z - fz) <= 2 && Math.Abs(e.y - fy) <= 3);
        if (!nearEdit && ps.OnGround && under is byte b && Mat.IsTerrain(b)
            && TerrainProbe.SurfaceY(world, ps.Eye.X, ps.Eye.Z, ps.FeetY) is float ground && MathF.Abs(ground - ps.FeetY) < 1.1f)
            target = ground + eyeHeight;

        // Ease toward the target (glides over steps); snap if far off (teleport, big fall).
        if (_smoothEyeY is not float y || MathF.Abs(y - target) > 3) y = target;
        else y += (target - y) * (1 - MathF.Exp(-dt * 14));
        // Never sink more than half a block below Minecraft's real eye (we'd see through the ground).
        y = MathF.Max(y, ps.Eye.Y - 0.6f);
        _smoothEyeY = y;
        return ps.Eye with { Y = y };
    }

    /// <summary>Third-person camera: behind and slightly above the head, pulled in if terrain is in the way.</summary>
    Vector3 ThirdPersonCamera(Vector3 eye)
    {
        var back = -Forward();
        var world = _bridge!.World;
        float dist = 4f;
        for (float d = 0.3f; d <= 4f; d += 0.2f)
        {
            var p = eye + back * d + new Vector3(0, 0.3f * d / 4, 0);
            int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
            byte m = world.GetChunk(x >> 4, z >> 4)?.Get(x & 15, y, z & 15) ?? Mat.Air;
            if (Mat.IsTerrain(m) || Mat.IsOpaqueCube(m)) { dist = Math.Max(d - 0.4f, 0.2f); break; }
        }
        return eye + back * dist + new Vector3(0, 0.3f * dist / 4, 0);
    }

    // ---- Minecraft's UI and character ----

    ShaderProgram _guiProgram = null!, _skinProgram = null!;
    uint _guiTex;
    int _guiW, _guiH, _skinVersion;
    long _guiFrame;
    double _guiAge = 99;
    MinecraftPlayerModel? _skinModel;
    ShaderProgram _entityProgram = null!, _organicProgram = null!;
    EntityDrawer _entityDrawer = null!;
    ShaderProgram _outlineProgram = null!;
    TargetOutline _targetOutline = null!;

    /// <summary>Uploads the newest UI frame Minecraft captured (HUD, hand, screens) and loads the skin when it changes.</summary>
    unsafe void UpdateFromMinecraft(double dt)
    {
        if (_bridge == null) return;
        _guiAge += dt;
        if (_bridge.TryGetGui(ref _guiFrame, out var pixels, out int w, out int h))
        {
            _gl.BindTexture(TextureTarget.Texture2D, _guiTex);
            if (w != _guiW || h != _guiH)
            {
                (_guiW, _guiH) = (w, h);
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);
            }
            else _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);
            _guiAge = 0;
        }

        var skin = _bridge.Skin;
        if (skin.version != _skinVersion)
        {
            _skinVersion = skin.version;
            try
            {
                var model = new MinecraftPlayerModel(_gl, skin.png, skin.slim);
                _skinModel?.Dispose();
                _skinModel = model;
                Console.WriteLine($"Loaded the player's skin ({(skin.slim ? "slim" : "wide")} arms)");
            }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                Console.WriteLine($"Couldn't read the player's skin: {e.Message}");
            }
        }
    }

    /// <summary>Draws Minecraft's UI over the finished frame (it is premultiplied and stored bottom row first).</summary>
    void DrawGui()
    {
        if (_bridge == null || !LiveControl || _guiAge > 0.5 || _guiW == 0) return;
        if (_opt.Play != null && _thirdPerson) return; // gameplay film: Minecraft's HUD (hand, crosshair) only in first person
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        // Playing in Minecraft's own window, its frame holds its world too (straight alpha marks the UI over it).
        _gl.BlendFunc(_bridge.GuiStraightAlpha ? BlendingFactor.SrcAlpha : BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        _guiProgram.Use();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _guiTex);
        _guiProgram.Set("uGui", 0);
        _gl.BindVertexArray(_skyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.Disable(EnableCap.Blend);
    }

    /// <summary>The player: the real skinned Minecraft character once its skin has arrived.</summary>
    void DrawPlayer(ShaderProgram fallback, Action<Matrix4x4> setModelFallback, bool shadowPass)
    {
        if (_skinModel != null)
        {
            if (shadowPass) _skinModel.Draw(_pose, _poseFeet, setModelFallback);
            else _skinModel.Draw(_pose, _poseFeet, m => _skinProgram.Set("uModel", m));
        }
        else _playerModel.Draw(_pose, _poseFeet, setModelFallback);
    }

    const int GroundSize = 48;   // blocks of ground height around the camera, for things that hover over it
    uint _groundTex;
    Vector2 _groundOrigin;
    double _groundAge = 99;

    /// <summary>
    /// Ground (or water surface) height per block around the camera, as a texture: fireflies hover over the real
    /// ground. Rebuilt about once a second or when the camera moves to a new area.
    /// </summary>
    unsafe void UpdateGroundHeights()
    {
        var origin = new Vector2(MathF.Floor(_camPos.X / 4) * 4 - GroundSize / 2, MathF.Floor(_camPos.Z / 4) * 4 - GroundSize / 2);
        _groundAge += 1 / 60.0;
        if (origin == _groundOrigin && _groundTex != 0 && _groundAge < 1) return;
        _groundOrigin = origin;
        _groundAge = 0;
        var heights = new float[GroundSize * GroundSize];
        var world = _bridge?.World ?? _streamer.World;
        for (int k = 0; k < GroundSize; k++)
        for (int i = 0; i < GroundSize; i++)
        {
            int x = (int)origin.X + i, z = (int)origin.Y + k;
            float h = _camPos.Y - 1.6f;
            if (world.GetChunk(x >> 4, z >> 4) is { } c)
            {
                int lx = x & 15, lz = z & 15, y = c.TopTerrain[lz * 16 + lx];
                while (y < c.MaxY - 1 && Mat.IsWater(c.Get(lx, y + 1, lz))) y++; // over lakes: the water surface
                h = y + 0.85f; // the smoothed ground sits a little below the block top
            }
            heights[k * GroundSize + i] = h;
        }
        if (_groundTex == 0)
        {
            _groundTex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _groundTex);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        }
        _gl.BindTexture(TextureTarget.Texture2D, _groundTex);
        fixed (float* p = heights)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R32f, GroundSize, GroundSize, 0, PixelFormat.Red, PixelType.Float, p);
    }

    /// <summary>Uploads Minecraft's block atlas when the mod has sent a new one (mipmapped like Minecraft's).</summary>
    unsafe void UpdateAtlas()
    {
        if (_bridge?.World.Models?.Atlas is not { } atlas || _bridge.World.Models.AtlasVersion == _atlasVersion) return;
        _atlasVersion = _bridge.World.Models.AtlasVersion;
        if (_atlasTex == 0) _atlasTex = _gl.GenTexture();
        _gl.ActiveTexture(TextureUnit.Texture6);
        _gl.BindTexture(TextureTarget.Texture2D, _atlasTex);
        fixed (byte* p = atlas.Rgba)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)atlas.Width, (uint)atlas.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 4); // 16 px sprites: no bleeding up to 1 px
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.NearestMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.ActiveTexture(TextureUnit.Texture0);
        Console.WriteLine($"Minecraft block atlas: {atlas.Width}x{atlas.Height}");
    }

    /// <summary>Eases toward 1 while the camera is below the ground surface of its column (inside a cave).</summary>
    void UpdateUnderground(float dt)
    {
        float target = 0;
        {
            var world = _bridge?.World ?? _streamer.World;
            int x = (int)MathF.Floor(_camPos.X), z = (int)MathF.Floor(_camPos.Z);
            if (world.GetChunk(x >> 4, z >> 4) is { } c && _camPos.Y < c.TopTerrain[(z & 15) * 16 + (x & 15)] - 0.5f) target = 1;
        }
        _underground += (target - _underground) * (1 - MathF.Exp(-dt * 2.5f));
    }

    /// <summary>Two-way control: what this window's keyboard and mouse are doing, for the mod to apply.</summary>
    void SendInput(bool active, bool keys = true)
    {
        if (_bridge == null) return;
        var input = new ControlInput { Active = active, Slot = _slot, SlotSeq = _slotSeq,
            AttackClicks = _attackClicks, UseClicks = _useClicks, PickClicks = _pickClicks, DropPresses = _dropPresses };
        (input.Yaw, input.Pitch) = ControlInput.MinecraftAngles(_yaw, _pitch);
        if (active && keys && _input.Keyboards.Count > 0)
        {
            var kb = _input.Keyboards[0];
            int k = 0;
            if (kb.IsKeyPressed(Key.W)) k |= BridgeProtocol.KeyForward;
            if (kb.IsKeyPressed(Key.S)) k |= BridgeProtocol.KeyBack;
            if (kb.IsKeyPressed(Key.A)) k |= BridgeProtocol.KeyLeft;
            if (kb.IsKeyPressed(Key.D)) k |= BridgeProtocol.KeyRight;
            if (kb.IsKeyPressed(Key.Space)) k |= BridgeProtocol.KeyJump;
            if (kb.IsKeyPressed(Key.ShiftLeft)) k |= BridgeProtocol.KeySneak;
            if (kb.IsKeyPressed(Key.ControlLeft)) k |= BridgeProtocol.KeySprint;
            if (_input.Mice.Count > 0)
            {
                if (_input.Mice[0].IsButtonPressed(MouseButton.Left)) k |= BridgeProtocol.KeyAttack;
                if (_input.Mice[0].IsButtonPressed(MouseButton.Right)) k |= BridgeProtocol.KeyUse;
            }
            input.Keys = k;
        }
        _bridge.WriteInput(input);
    }

    bool LiveControl => _bridge != null && _follow && _liveInWorld;

    void SetCaptured(bool captured)
    {
        _captured = captured;
        _lastMouse = null;
        _lookInit = false;
        foreach (var mouse in _input.Mice) mouse.Cursor.CursorMode = captured ? CursorMode.Raw : CursorMode.Normal;
    }

    /// <summary>A Minecraft screen (inventory, chest, chat, pause menu, ...) is open: the mouse works it as a cursor.</summary>
    bool ScreenMode => LiveControl && _bridge!.ScreenOpen;

    void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (ScreenMode) { _bridge!.SendInputEvent(2, (int)button, 1, Modifiers(_input.Keyboards[0])); return; }
        if (!_captured) { SetCaptured(true); return; } // a click grabs the mouse again; it isn't sent to the game
        if (!LiveControl) return;
        if (button == MouseButton.Left) _attackClicks++;
        if (button == MouseButton.Right) _useClicks++;
        if (button == MouseButton.Middle) _pickClicks++;
    }

    void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (ScreenMode) _bridge!.SendInputEvent(2, (int)button, 0, Modifiers(_input.Keyboards[0]));
    }

    void OnScroll(IMouse mouse, ScrollWheel w)
    {
        // Live: Minecraft handles the wheel itself (hotbar in game, scrolling lists in screens).
        if (LiveControl) _bridge!.SendInputEvent(4, w.X, w.Y);
        else _speed = Math.Clamp(_speed * (w.Y > 0 ? 1.25f : 0.8f), 2f, 500f);
    }

    void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        if (ScreenMode)
        {
            // Cursor position in Minecraft's window pixels (its window is resized to match this one).
            var size = _window.Size;
            _bridge!.SendInputEvent(3, pos.X * _guiW / Math.Max(size.X, 1), pos.Y * _guiH / Math.Max(size.Y, 1));
            return;
        }
        if (!_captured) { _lastMouse = null; return; }
        if (_lastMouse is { } last)
        {
            var d = pos - last;
            _yaw += d.X * 0.0025f;
            _pitch = Math.Clamp(_pitch - d.Y * 0.0025f, -1.55f, 1.55f);
        }
        _lastMouse = pos;
    }

    void OnKeyDown(IKeyboard kb, Key key, int scancode)
    {
        // Renderer-only keys.
        if (key == Key.F2) { _pendingScreenshot = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png"; return; }
        if (key == Key.F7)
        {
            _entityDrawer.Realistic = !_entityDrawer.Realistic;
            Console.WriteLine(_entityDrawer.Realistic ? "Realistic animals" : "Minecraft animals");
            return;
        }
        if (key == Key.F6 && _bridge != null)
        {
            _follow = !_follow;
            _lookInit = false;
            Console.WriteLine(_follow ? "Playing (following the Minecraft player)" : "Free camera (F6 to play again)");
            return;
        }

        if (LiveControl)
        {
            // Everything else goes to Minecraft's own keyboard handling: E inventory, T chat, Esc pause menu,
            // F5 perspective, 1-9 hotbar, Q drop, F3 debug, typing in screens, ...
            _bridge!.SendInputEvent(0, (int)key, scancode, 1, Modifiers(kb));
            return;
        }

        if (key == Key.Escape) _window.Close();
        if (key == Key.R)
        {
            _weather = (_weather + 1) % 3;
            Console.WriteLine($"Weather: {(_weather == 0 ? "clear" : _weather == 1 ? "overcast" : "rain")}");
        }
    }

    void OnKeyUp(IKeyboard kb, Key key, int scancode)
    {
        if (LiveControl && key is not (Key.F2 or Key.F6 or Key.F7)) _bridge!.SendInputEvent(0, (int)key, scancode, 0, Modifiers(kb));
    }

    void OnKeyChar(IKeyboard kb, char c)
    {
        if (LiveControl) _bridge!.SendInputEvent(1, c, Modifiers(kb));
    }

    /// <summary>GLFW modifier bits: shift 1, control 2, alt 4.</summary>
    static int Modifiers(IKeyboard kb) =>
        (kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight) ? 1 : 0)
        | (kb.IsKeyPressed(Key.ControlLeft) || kb.IsKeyPressed(Key.ControlRight) ? 2 : 0)
        | (kb.IsKeyPressed(Key.AltLeft) || kb.IsKeyPressed(Key.AltRight) ? 4 : 0);

    void OnUpdate(double dt)
    {
        float t = (float)dt;
        _time += t;
        // Weather eases in over a few seconds; clouds gather before the rain starts.
        float overcastTarget = _weather >= 1 ? 1 : 0, rainTarget = _weather == 2 ? 1 : 0;
        _overcast += Math.Clamp(overcastTarget - _overcast, -t * 0.3f, t * 0.3f);
        _rain += Math.Clamp(rainTarget - _rain, -t * 0.2f, _overcast > 0.8f ? t * 0.25f : 0);
        // Live mode: the camera, time of day and weather follow the Minecraft player.
        if (_bridge != null)
        {
            var ps = _bridge.ReadPlayer();
            _liveInWorld = ps.InWorld;
            if (!ps.InWorld || !_follow) SendInput(false);
            if (ps.InWorld)
            {
                _overcast = Math.Max(ps.Rain, ps.Thunder);
                _rain = ps.Rain;
                _timeOfDay = ps.TimeOfDay;
                _pose = ps.Pose;
                if (!_follow) _poseFeet = ps.Pose.Feet;
                if (_follow)
                {
                    var eye = SmoothCamera(ps, t);
                    // The body follows the same smoothed height as the eye, so feet stay on the drawn ground.
                    _pose = ps.Pose;
                    _poseFeet = ps.Pose.Feet with { Y = ps.Pose.Feet.Y + (eye.Y - ps.Eye.Y) };
                    _camPos = _thirdPerson ? ThirdPersonCamera(eye) : eye;
                    // The renderer drives Minecraft while this window is focused, also while a Minecraft screen is
                    // open (its UI keeps coming through); looking and walking pause while a screen is open.
                    bool screen = _bridge.ScreenOpen;
                    if (_opt.ScreenshotPath == null && _windowFocused)
                    {
                        if (screen && _captured) SetCaptured(false);              // inventory/chat/menu: free cursor
                        else if (!screen && _wasScreen && !_captured) SetCaptured(true); // closed again: back to playing
                    }
                    _wasScreen = screen;
                    if (_opt.Play != null) { GameplayUpdate(eye); return; }
                    _thirdPerson = _bridge.ThirdPerson; // F5 in Minecraft
                    bool driving = _windowFocused;
                    bool looking = driving && _captured && !screen && _opt.ScreenshotPath == null;
                    if (!looking || !_lookInit)
                    {
                        // Not looking around here: mirror Minecraft's view. Starting to: begin from where it looks.
                        (_yaw, _pitch) = ps.RendererAngles();
                        _lookInit = looking;
                    }
                    // Automated screenshots can pick the view: REALISM_SHOT_LOOK="yaw,pitch" (degrees).
                    if (_opt.ScreenshotPath != null && Environment.GetEnvironmentVariable("REALISM_SHOT_LOOK") is { } look)
                    {
                        var a = look.Split(',');
                        _yaw = float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture) * MathF.PI / 180;
                        _pitch = float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture) * MathF.PI / 180;
                    }
                    if (_opt.ScreenshotPath != null && Environment.GetEnvironmentVariable("REALISM_SHOT_POS") is { } at)
                    {
                        var a = at.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                        _camPos = new Vector3(a[0], a[1], a[2]);
                    }
                    SendInput(driving, keys: looking);
                    return;
                }
            }
        }
        if (_opt.ScreenshotPath != null || _opt.Film != null || _input.Keyboards.Count == 0) return;
        var kb = _input.Keyboards[0];

        var fwd = Forward();
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var move = Vector3.Zero;
        if (kb.IsKeyPressed(Key.W)) move += fwd;
        if (kb.IsKeyPressed(Key.S)) move -= fwd;
        if (kb.IsKeyPressed(Key.D)) move += right;
        if (kb.IsKeyPressed(Key.A)) move -= right;
        if (kb.IsKeyPressed(Key.Space)) move += Vector3.UnitY;
        if (kb.IsKeyPressed(Key.C) || kb.IsKeyPressed(Key.ControlLeft)) move -= Vector3.UnitY;
        if (move != Vector3.Zero)
            _camPos += Vector3.Normalize(move) * _speed * (kb.IsKeyPressed(Key.ShiftLeft) ? 5f : 1f) * t;

        float sunStep = 0;
        if (kb.IsKeyPressed(Key.T)) sunStep += t * 0.08f;
        if (kb.IsKeyPressed(Key.G)) sunStep -= t * 0.08f;
        if (sunStep != 0)
        {
            _timeOfDay = ((_timeOfDay + sunStep) % 2 + 2) % 2; // wraps through night
            _shadowDirty = true;
        }
    }

    static Matrix4x4 LightMatrix(Vector3 sun, Vector3 centre, float radius, int resolution)
    {
        var up = MathF.Abs(sun.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4x4.CreateLookAt(centre + sun * radius * 2, centre, up);
        // Snap the centre to whole shadow texels so the near cascade doesn't shimmer as the camera moves.
        float texel = 2 * radius / resolution;
        var c = Vector3.Transform(centre, view);
        float sx = MathF.Round(c.X / texel) * texel - c.X, sy = MathF.Round(c.Y / texel) * texel - c.Y;
        return view * Mat4.Ortho(-radius + sx, radius + sx, -radius + sy, radius + sy, radius * 0.5f, radius * 4);
    }

    void RenderShadowMap((uint fbo, uint tex) target, int size, Matrix4x4 lightViewProj, Func<ChunkGpu, bool> include, bool pebbles)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, target.fbo);
        _gl.Viewport(0, 0, (uint)size, (uint)size);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(1.5f, 2f);

        var chunks = _chunks.Values.Where(include).ToList();
        _shadow.Use();
        _shadow.Set("uLightViewProj", lightViewProj);
        _shadow.Set("uModel", Matrix4x4.Identity);
        foreach (var c in chunks)
        {
            c.Terrain?.Draw();
            c.ShadowCanopy?.Draw(); // dense crown shade (the visible inner core is inside it)
            c.Wood?.Draw();
            c.Buildings?.Draw();
            c.Blocks?.Draw();
        }
        _cardShadow.Use();
        _cardShadow.Set("uLightViewProj", lightViewProj);
        _cardShadow.Set("uTime", _time);
        foreach (var c in chunks) c.LeafCards?.Draw();
        if (pebbles && _bridge != null && _liveInWorld) // mobs and items cast shadows (near cascade)
        {
            _shadow.Use();
            _entityDrawer.Draw(_time, m => _shadow.Set("uModel", m), bindTextures: false);
            _shadow.Set("uModel", Matrix4x4.Identity);
        }
        if (pebbles && ShowPlayer) // near cascade only (it is redrawn every frame)
        {
            _shadow.Use();
            DrawPlayer(_shadow, m => _shadow.Set("uModel", m), shadowPass: true);
        }
        _rockShadow.Use();
        _rockShadow.Set("uLightViewProj", lightViewProj);
        foreach (var c in chunks)
        {
            c.Rocks?.Draw();
            if (pebbles) c.Pebbles?.Draw();
        }

        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    void SetCommon(ShaderProgram p, Vector3 sun, Vector3 sunColor, Matrix4x4 viewProj)
    {
        p.Use();
        p.Set("uCamPos", _camPos);
        p.Set("uSunDir", sun);
        p.Set("uSunColor", sunColor);
        p.Set("uTime", _time);
        p.Set("uFogRange", _opt.FogRange);
        p.Set("uExposure", 1.0f);
        p.Set("uOvercast", _overcast);
        p.Set("uRain", _rain);
        p.Set("uTrueSunDir", _trueSun);
        p.Set("uNight", _night);
        p.Set("uUnderground", _underground);
        p.Set("uLightCount", _pointLights.Count);
        p.Set("uGlintCount", Math.Min(_pointLights.Count, 8));
        for (int i = 0; i < _pointLights.Count; i++)
        {
            p.Set($"uLights[{i}]", _pointLights[i]);
            p.Set($"uLightColors[{i}]", _lightColors[i]);
        }
        p.Set("uViewProj", viewProj);
        p.Set("uLightViewProj", _lightViewProj);
        p.Set("uLightViewProjNear", _lightViewProjNear);
        p.Set("uShadow", 0);
        p.Set("uShadowNear", 1);
    }

    // Gameplay recording: current segment, whether it is recording yet, its clock, the next frame's time in it.
    readonly System.Diagnostics.Stopwatch _gpClock = System.Diagnostics.Stopwatch.StartNew();
    int _gpSeg = -1, _gpSlot = -1, _gpSlotSeq;
    bool _gpRec, _gpDone;
    double _gpWarmStart, _gpRecStart, _gpNext;
    float _gpReach = 1;

    /// <summary>
    /// Gameplay recording: run the segment's commands, wait for the scene to load, then play its actions (the
    /// player's look, keys and hotbar slot go to Minecraft as if typed here) with its camera.
    /// </summary>
    void GameplayUpdate(Vector3 eye)
    {
        var g = _opt.Play!;
        if (_gpDone) return;
        double now = _gpClock.Elapsed.TotalSeconds;
        if (_gpSeg < 0 || (_gpRec && now - _gpRecStart >= g.Segments[_gpSeg].Seconds))
        {
            _gpSeg++;
            if (_gpSeg >= g.Segments.Count)
            {
                _gpDone = true;
                _bridge!.WriteInput(new ControlInput { Active = false });
                _video?.Dispose();
                _video = null;
                Console.WriteLine($"Gameplay film saved: {Path.GetFullPath(_opt.FilmOut!)}");
                _window.Close();
                return;
            }
            var seg0 = g.Segments[_gpSeg];
            if (seg0.Commands.Count > 0)
                File.WriteAllLines(Path.Combine(Path.GetDirectoryName(BridgeProtocol.FilePath)!, "director.txt"), seg0.Commands);
            _gpRec = false;
            _gpWarmStart = now;
            Console.WriteLine($"Gameplay: segment {_gpSeg + 1} of {g.Segments.Count}");
        }
        var seg = g.Segments[_gpSeg];
        float t = _gpRec ? (float)(now - _gpRecStart) : 0;
        var (mcYaw, mcPitch) = seg.LookAt(t);
        int slot = seg.SlotAt(t);
        if (slot >= 0 && slot != _gpSlot) { _gpSlot = slot; _gpSlotSeq++; }
        _bridge!.WriteInput(new ControlInput { Active = true, Keys = _gpRec ? seg.KeysAt(t) : 0, Yaw = mcYaw, Pitch = mcPitch,
            Slot = Math.Max(_gpSlot, 0), SlotSeq = _gpSlotSeq });

        // The view: the player's own look (first person), or a camera around the player (third person).
        float y = mcYaw * MathF.PI / 180;
        _yaw = MathF.Atan2(MathF.Cos(y), -MathF.Sin(y));
        _pitch = -mcPitch * MathF.PI / 180;
        _thirdPerson = seg.Third;
        if (seg.Third)
        {
            var (off, dist, height, tilt) = seg.CamAt(t);
            float cy = _yaw + off * MathF.PI / 180;
            var dir = new Vector3(MathF.Cos(cy), 0, MathF.Sin(cy));
            var want = -dir * dist + new Vector3(0, height, 0);
            // Pulled in toward the player before rock, trunks and blocks, so it never sits inside them.
            float reach = 1;
            var world = _bridge.World;
            for (float f = 0.1f; f <= 1.0001f; f += 0.05f)
            {
                var p = eye + want * f;
                int bx = (int)MathF.Floor(p.X), by = (int)MathF.Floor(p.Y), bz = (int)MathF.Floor(p.Z);
                bool blocked = false;
                for (int k = 0; k < 8 && !blocked; k++) // the camera's small box, not just its centre
                {
                    var q = p + new Vector3((k & 1) != 0 ? 0.3f : -0.3f, (k & 2) != 0 ? 0.3f : -0.3f, (k & 4) != 0 ? 0.3f : -0.3f);
                    byte m = world.GetChunk((int)MathF.Floor(q.X) >> 4, (int)MathF.Floor(q.Z) >> 4)?.Get((int)MathF.Floor(q.X) & 15, (int)MathF.Floor(q.Y), (int)MathF.Floor(q.Z) & 15) ?? Mat.Air;
                    blocked = Mat.IsTerrain(m) || Mat.IsOpaqueCube(m) || Mat.IsWood(m); // leaves are see-through: not walls
                }
                if (blocked) { reach = MathF.Max(f - 0.1f, 0.15f); break; }
            }
            _gpReach += (reach - _gpReach) * (reach < _gpReach ? 0.5f : 0.05f); // in fast, back out slowly
            _camPos = eye + want * _gpReach;
            var d = eye + new Vector3(0, -0.25f, 0) - _camPos;
            _yaw = MathF.Atan2(d.Z, d.X);
            _pitch = MathF.Asin(Math.Clamp(d.Y / d.Length(), -1, 1)) + tilt * MathF.PI / 180;
        }
        else _camPos = eye;

        // Start recording once the scene has loaded.
        // Settled, or after 15 s anyway (lush caves never settle: growing vines and berries keep re-meshing).
        if (!_gpRec && now - _gpWarmStart > 3 && (_settledFrames >= 20 || now - _gpWarmStart > 15))
        {
            _gpRec = true;
            _gpRecStart = now;
            _gpNext = 0;
            _gpReach = 1;
        }
    }

    /// <summary>Gameplay recording: frames at a steady 30 per second of play, with a quick dip through black at cuts.</summary>
    unsafe void GameplayCapture()
    {
        if (!_gpRec || _gpDone) return;
        var seg = _opt.Play!.Segments[_gpSeg];
        double t = _gpClock.Elapsed.TotalSeconds - _gpRecStart;
        if (_gpNext > t || _gpNext >= seg.Seconds) return;
        var size = _window.FramebufferSize;
        _video ??= new VideoWriter(Comparison.Ffmpeg(), _opt.FilmOut!, size.X, size.Y, 30, letterbox: false, hardware: true);
        var pixels = new byte[size.X * size.Y * 4];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _post!.OutputFbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        fixed (byte* p = pixels)
            _gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        bool last = _gpSeg == _opt.Play.Segments.Count - 1;
        while (_gpNext <= t && _gpNext < seg.Seconds)
        {
            float fadeIn = _gpSeg == 0 ? 0.8f : 0.3f, fadeOut = last ? 1f : 0.3f;
            float f = (float)Math.Clamp(Math.Min(_gpNext / fadeIn, (seg.Seconds - _gpNext) / fadeOut), 0, 1);
            _video.Write(pixels, f * f * (3 - 2 * f));
            _gpNext += 1.0 / 30;
        }
    }

    // Film recording: the frame being rendered, warm-up frames left after a cut, the encoder.
    int _filmFrame, _filmWarmup = 90, _filmShot = -1;
    VideoWriter? _video;

    /// <summary>Film mode: camera, time and weather for the current frame; a fixed 1/30 s clock (faster while warming
    /// up after a cut, so lighting and eye adaptation settle before the first frame of a shot is kept).</summary>
    double FilmStep()
    {
        var film = _opt.Film!;
        var (shot, _, pos, yaw, pitch, time, weather) = film.At(_filmFrame);
        if (shot != _filmShot)
        {
            _filmShot = shot;
            _filmWarmup = 90;
            _weather = weather;
            _overcast = weather >= 1 ? 1 : 0;
            _rain = weather == 2 ? 1 : 0;
        }
        _camPos = pos;
        _yaw = yaw;
        _pitch = pitch;
        _timeOfDay = time;
        _time = 50 + _filmFrame / (float)Cinematic.Fps; // animation clock follows the film, not the wall clock
        return _filmWarmup > 0 ? 0.2 : 1.0 / Cinematic.Fps;
    }

    void OnRender(double dt)
    {
        if (_opt.Film != null) dt = FilmStep();
        var (sun, keyColor, trueSun, night) = Lighting();
        _trueSun = trueSun;
        _night = night;
        var sunColor = keyColor * (1 - 0.85f * _overcast);
        if (Vector3.Distance(sun, _lastKeyDir) > 0.002f) { _shadowDirty = true; _lastKeyDir = sun; }
        StreamChunks();
        UpdateUnderground((float)dt);
        UpdateFromMinecraft(dt);
        if (_bridge != null && _liveInWorld) _entityDrawer.Update(_bridge);
        if (_frame == 120 && _bridge != null && Environment.GetEnvironmentVariable("REALISM_TEST_KEYS") is { } tk)
            foreach (var k in tk.Split(",")) { _bridge.SendInputEvent(0, int.Parse(k), 0, 1, 0); _bridge.SendInputEvent(0, int.Parse(k), 0, 0, 0); }
        if (_bridge != null) _bridge.WriteWindowSize(_window.FramebufferSize.X, _window.FramebufferSize.Y);

        // The 32 nearest torches / glowing blocks light the scene this frame.
        _pointLights.Clear();
        _pointLights.AddRange(_chunks.Values
            .Where(c => c.Lights.Length > 0 && Vector2.Distance(new(c.Centre.X, c.Centre.Z), new(_camPos.X, _camPos.Z)) < 96)
            .SelectMany(c => c.Lights)
            .OrderBy(l => Vector3.DistanceSquared(new Vector3(l.X, l.Y, l.Z), _camPos))
            .Take(32));
        // Each light's colour this frame: torches flicker on their own rhythm, lava pulses slowly (once per light
        // here, rather than for every pixel in the shaders).
        for (int i = 0; i < _pointLights.Count; i++)
        {
            var l = _pointLights[i];
            float h = (l.X * 0.371f + l.Y * 0.719f + l.Z * 0.137f) % 1f;
            if (l.W >= Mesher.PortalLight)
            {
                // Nether portal: a violet glow that slowly breathes.
                float breathe = 0.85f + 0.15f * MathF.Sin(_time * 1.7f + h * 20);
                _lightColors[i] = new Vector4(new Vector3(0.5f, 0.15f, 1.0f) * (l.W - Mesher.PortalLight) * 1.6f * breathe, 196);
            }
            else if (l.W < 0)
            {
                float pulse = 0.88f + 0.12f * MathF.Sin(_time * 0.9f + h * 40) * MathF.Sin(_time * 0.37f + h * 13);
                _lightColors[i] = new Vector4(new Vector3(1.0f, 0.36f, 0.08f) * -l.W * pulse, 484);
            }
            else
            {
                float t = _time + h * 50;
                float flicker = 0.86f + 0.07f * MathF.Sin(t * 5.3f) + 0.05f * MathF.Sin(t * 11.7f + 1.3f) + 0.03f * MathF.Sin(t * 23.1f);
                _lightColors[i] = new Vector4(new Vector3(1.0f, 0.6f, 0.28f) * l.W * flicker, 196);
            }
        }

        // The far cascade covers the whole view radius around the camera. Re-render it when the sun moves,
        // or (at most twice a second) when the camera has travelled or new chunks have arrived.
        _farShadowAge += dt;
        bool moved = Vector2.Distance(new(_camPos.X, _camPos.Z), new(_farShadowCentre.X, _farShadowCentre.Z)) > 24;
        if (_shadowDirty || (_farShadowAge > 0.5 && (moved || _chunksSinceShadow > 0)))
        {
            float radius = _streamer.Radius * 16 * 1.2f + 32;
            _farShadowCentre = new Vector3(_camPos.X, Math.Clamp(_camPos.Y - 20, 40, 160), _camPos.Z);
            _lightViewProj = LightMatrix(sun, _farShadowCentre, radius, ShadowSize);
            RenderShadowMap(_farShadow, ShadowSize, _lightViewProj, _ => true, pebbles: false);
            _shadowDirty = false;
            _chunksSinceShadow = 0;
            _farShadowAge = 0;
        }
        var nearCentre = _camPos + Forward() * (NearShadowRadius * 0.6f);
        _lightViewProjNear = LightMatrix(sun, nearCentre, NearShadowRadius, NearShadowSize);
        RenderShadowMap(_nearShadow, NearShadowSize, _lightViewProjNear,
            c => Vector2.Distance(new(c.Centre.X, c.Centre.Z), new(nearCentre.X, nearCentre.Z)) < NearShadowRadius + 24, pebbles: true);

        var size = _window.FramebufferSize;
        _post!.Resize(size.X, size.Y);
        _post.BeginScene();
        var view = Matrix4x4.CreateLookAt(_camPos, _camPos + Forward(), Vector3.UnitY);
        // Playing: Minecraft's own field of view (its FOV setting, with the sprint and speed zoom); otherwise 60 degrees.
        float fov = _bridge != null && _follow && _liveInWorld && _bridge.ReadFov() is var mcFov and >= 10 and <= 170 ? mcFov : 60;
        _fov = fov;
        var proj = Mat4.Perspective(fov * MathF.PI / 180, size.X / (float)Math.Max(size.Y, 1), NearPlane, _opt.FogRange * 1.6f);
        var viewProj = view * proj;
        Matrix4x4.Invert(viewProj, out var invViewProj);

        _gl.ClearColor(0, 0, 0, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Sky
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(false);
        SetCommon(_sky, sun, sunColor, viewProj);
        _sky.Set("uInvViewProj", invViewProj);
        _gl.BindVertexArray(_skyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.DepthMask(true);
        _gl.Enable(EnableCap.DepthTest);

        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _farShadow.tex);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _nearShadow.tex);

        var visible = _chunks.Values.Where(c => Mat4.BoxVisible(viewProj, c.Min, c.Max)).ToList();

        // Opaque world
        _gl.Enable(EnableCap.CullFace);
        _gl.CullFace(TriangleFace.Back);
        SetCommon(_world, sun, sunColor, viewProj);
        _world.Set("uGrassFar", GrassFar);
        _world.Set("uKind", 0);
        foreach (var c in visible) c.Terrain?.Draw();
        _world.Set("uKind", 2);
        foreach (var c in visible) c.Wood?.Draw();
        _world.Set("uKind", 3);
        foreach (var c in visible) c.Buildings?.Draw();
        _world.Set("uKind", 1);
        foreach (var c in visible) c.LeafCore?.Draw();

        // Minecraft's own blocks (torches, builds, ...): its models and textures.
        UpdateAtlas();
        if (_atlasTex != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture6);
            _gl.BindTexture(TextureTarget.Texture2D, _atlasTex);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _farShadow.tex);
            _gl.Disable(EnableCap.CullFace);
            SetCommon(_blocks, sun, sunColor, viewProj);
            _blocks.Set("uAtlas", 6);
            _blocks.Set("uTranslucent", 0);
            foreach (var c in visible) c.Blocks?.Draw();

            // Ores: their texture over the smooth rock, on the same triangles (drawn on top).
            if (_bridge?.World.Models is { Atlas: { } atlas } models)
            {
                SetCommon(_ores, sun, sunColor, viewProj);
                _ores.Set("uAtlas", 6);
                _ores.Set("uAtlasSize", new Vector2(atlas.Width, atlas.Height));
                _ores.Set("uTile", 16f / atlas.Width); // vanilla block textures; replaced by the stone texture's own width below
                for (int r = 0; r < 3; r++)
                {
                    var corner = Vector2.Zero;
                    if (models.Get(Realism.Minecraft.Blocks.OreRockStates[r]) is { Quads.Length: > 0 } rockModel)
                    {
                        (corner, float width) = rockModel.SideTexture();
                        _ores.Set("uTile", width);
                    }
                    _ores.Set("uRock" + r, corner);
                }
                // Multiplies the lit rock already drawn (see Shaders.OreFragment), so ores get exactly its light.
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.Zero);
                _gl.DepthFunc(DepthFunction.Lequal);
                _gl.DepthMask(false);
                _gl.Enable(EnableCap.PolygonOffsetFill);
                _gl.PolygonOffset(-1f, -2f);
                foreach (var c in visible) c.Ores?.Draw();
                _gl.Disable(EnableCap.PolygonOffsetFill);
                _gl.DepthMask(true);
                _gl.DepthFunc(DepthFunction.Less);
                _gl.Disable(EnableCap.Blend);
            }
            _gl.Enable(EnableCap.CullFace);
        }

        // Mobs and dropped items (Minecraft's own models and textures, posed by the mod).
        if (_bridge != null && _liveInWorld)
        {
            SetCommon(_entityProgram, sun, sunColor, viewProj);
            _entityProgram.Set("uTex", 5);
            _entityDrawer.AtlasTexture = _atlasTex;
            _gl.Disable(EnableCap.CullFace);
            _entityDrawer.Draw(_time, m => _entityProgram.Set("uModel", m), bindTextures: true);
            // Realistic animals: Minecraft's skeleton and animation, organic bodies.
            SetCommon(_organicProgram, sun, sunColor, viewProj);
            _organicProgram.Set("uTex", 5);
            _entityDrawer.DrawRealistic(m => _organicProgram.Set("uModel", m), w => _organicProgram.Set("uWool", w), (a, b, patch, designed) =>
            {
                _organicProgram.Set("uCoatMain", a);
                _organicProgram.Set("uCoatSecond", b);
                _organicProgram.Set("uPatches", patch);
                _organicProgram.Set("uDesigned", designed);
            });
        }

        if (ShowPlayer)
        {
            SetCommon(_skinModel != null ? _skinProgram : _actor, sun, sunColor, viewProj);
            if (_skinModel != null) _skinProgram.Set("uSkin", 4);
            _gl.Enable(EnableCap.CullFace);
            DrawPlayer(_actor, m => _actor.Set("uModel", m), shadowPass: false);
        }

        // Rocks everywhere, pebbles only near the camera (they shrink away before they'd become sub-pixel).
        SetCommon(_rocks, sun, sunColor, viewProj);
        _rocks.Set("uFadeFar", 0f);
        foreach (var c in visible) c.Rocks?.Draw();
        _rocks.Set("uFadeFar", PebbleFar);
        foreach (var c in visible)
            if (Vector2.Distance(new(c.Centre.X, c.Centre.Z), new(_camPos.X, _camPos.Z)) < PebbleFar + 12)
                c.Pebbles?.Draw();

        // Foliage: two-sided, alpha-cut. Alpha-to-coverage softens leaf edges with MSAA.
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.SampleAlphaToCoverage);
        SetCommon(_cards, sun, sunColor, viewProj);
        foreach (var c in visible) c.LeafCards?.Draw();
        _gl.Disable(EnableCap.SampleAlphaToCoverage);

        SetCommon(_grass, sun, sunColor, viewProj);
        _grass.Set("uGrassFar", GrassFar);
        foreach (var c in visible)
            if (Vector2.Distance(new(c.Centre.X, c.Centre.Z), new(_camPos.X, _camPos.Z)) < GrassFar + 12)
                c.Grass?.Draw();

        // Water: reads a resolved copy of the opaque scene for refraction, depth-based colour and reflections.
        var (sceneColor, sceneDepth) = _post.CopySceneForWater();
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, sceneColor);
        _gl.ActiveTexture(TextureUnit.Texture3);
        _gl.BindTexture(TextureTarget.Texture2D, sceneDepth);
        _gl.DepthMask(false);
        SetCommon(_water, sun, sunColor, viewProj);
        _water.Set("uKind", 3);
        _water.Set("uSceneColor", 2);
        _water.Set("uSceneDepth", 3);
        _water.Set("uScreenSize", _post.Size);
        _water.Set("uNear", NearPlane);
        _water.Set("uFar", _opt.FogRange * 1.6f);
        foreach (var c in visible) c.Water?.Draw();

        // Glass after water so lakes show through windows.
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        if (_atlasTex != 0)
        {
            // Minecraft's translucent blocks (stained glass, ice, slime, ...).
            _gl.Disable(EnableCap.CullFace);
            SetCommon(_blocks, sun, sunColor, viewProj);
            _blocks.Set("uAtlas", 6);
            _blocks.Set("uTranslucent", 1);
            foreach (var c in visible) c.BlocksTranslucent?.Draw();
            _gl.Enable(EnableCap.CullFace);
        }
        SetCommon(_glass, sun, sunColor, viewProj);
        foreach (var c in visible) c.Glass?.Draw();
        // Spray and mist where waterfalls land, embers over lava: the 24 nearest emitters.
        var emitters = _chunks.Values
            .Where(c => c.Emitters.Length > 0 && Vector2.Distance(new(c.Centre.X, c.Centre.Z), new(_camPos.X, _camPos.Z)) < 80)
            .SelectMany(c => c.Emitters)
            .OrderBy(e => Vector3.DistanceSquared(new Vector3(e.X, e.Y, e.Z), _camPos))
            .Take(24).ToList();
        if (emitters.Count > 0)
        {
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha); // premultiplied: spray covers, embers add
            _gl.Enable(EnableCap.ProgramPointSize);
            SetCommon(_particles, sun, sunColor, viewProj);
            _particles.Set("uViewportHeight", (float)size.Y);
            _particles.Set("uEmitterCount", emitters.Count);
            for (int i = 0; i < emitters.Count; i++) _particles.Set($"uEmitters[{i}]", emitters[i]);
            _gl.BindVertexArray(_skyVao);
            _gl.DrawArrays(PrimitiveType.Points, 0, (uint)(emitters.Count * 48));
            _gl.Disable(EnableCap.ProgramPointSize);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }

        bool fireflies = _night > 0.3f && _underground < 0.5f && _rain < 0.2f;
        if (_underground > 0.02f || fireflies)
        {
            // Dust motes hanging in the cave air, catching the torchlight; on clear nights outside, fireflies.
            _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
            _gl.Enable(EnableCap.ProgramPointSize);
            SetCommon(_dust, sun, sunColor, viewProj);
            _dust.Set("uViewportHeight", (float)size.Y);
            _dust.Set("uFireflies", fireflies ? 1 : 0);
            if (fireflies) UpdateGroundHeights();
            _gl.ActiveTexture(TextureUnit.Texture7);
            _gl.BindTexture(TextureTarget.Texture2D, _groundTex);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _dust.Set("uGround", 7);
            _dust.Set("uGroundOrigin", _groundOrigin);
            _dust.Set("uGroundSize", _groundTex != 0 ? (float)GroundSize : 0f);
            _gl.BindVertexArray(_skyVao);
            _gl.DrawArrays(PrimitiveType.Points, 0, fireflies ? 600u : 2500u);
            _gl.Disable(EnableCap.ProgramPointSize);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        }
        if (_rain > 0.01f && _underground < 0.5f) // no rain falls inside a cave
        {
            SetCommon(_rainStreaks, sun, sunColor, viewProj);
            _gl.BindVertexArray(_skyVao);
            _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, 16000);
        }
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(true);

        // Night exposure: like a camera at night, open up so moonlit scenes stay readable but dark and blue.
        // The block Minecraft is targeting (always visible: the smooth surface may hide parts of the real block).
        if (_bridge != null && LiveControl && _follow && _opt.Play == null && _bridge.Target is { has: true } target)
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.CullFace);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            SetCommon(_outlineProgram, sun, sunColor, viewProj);
            _targetOutline.Draw(_outlineProgram, target.x, target.y, target.z, target.stage);
            _gl.Disable(EnableCap.Blend);
            _gl.Enable(EnableCap.DepthTest);
        }

        // Exposure: tuned per situation (night, underground), then eye adaptation pulls it down whenever the view is
        // much brighter than that suits - looking out of a cave into daylight - instead of blowing out to white.
        float baseExposure = 0.85f * (1 + 3.0f * _night * (1 - _underground)) * (1 + 2.5f * _underground);
        float wanted = Math.Clamp(0.3f / MathF.Max(_post.AverageLuminance * baseExposure, 1e-4f), 0.12f, 1f);
        float rate = wanted < _adapt ? 3f : 0.8f; // eyes stop down for brightness fast, open up for darkness slowly
        _adapt += (wanted - _adapt) * (1 - MathF.Exp(-(float)dt * rate));
        _post.Finish(proj, baseExposure * _adapt, new FrameLight(_farShadow.tex, _nearShadow.tex, _lightViewProj, _lightViewProjNear,
            invViewProj, _camPos, sun, sunColor * (1 - 0.85f * _night), _night, 1 - 0.6f * _underground)); // moonlit air barely glows
        DrawGui();
        if (_opt.Play != null) GameplayCapture();
        if (_pendingScreenshot != null)
        {
            SaveScreenshot(_pendingScreenshot);
            _pendingScreenshot = null;
        }

        _frame++;
        if (Environment.GetEnvironmentVariable("REALISM_PERF") != null)
        {
            // Diagnostics: frame times (the GPU is waited for, so this is the real cost of a frame).
            _gl.Finish();
            double ms = _perfClock.Elapsed.TotalMilliseconds;
            _perfClock.Restart();
            _perfSum += ms; _perfMax = Math.Max(_perfMax, ms); _perfN++;
            if (_perfN == 120)
            {
                Console.WriteLine($"Perf: avg {_perfSum / _perfN:0.0} ms ({1000 * _perfN / _perfSum:0} fps), worst {_perfMax:0.0} ms, {_chunks.Count} chunks, {_pointLights.Count} lights");
                _perfSum = _perfMax = 0; _perfN = 0;
            }
        }
        _fpsFrames++;
        _fpsTimer += dt;
        if (_fpsTimer >= 1)
        {
            string live = _bridge == null ? "" : !_bridge.MinecraftRunning ? "  |  LIVE: waiting for Minecraft..."
                : !_liveInWorld ? "  |  LIVE: Minecraft is in the menu"
                : $"  |  LIVE: {(_follow ? "following player" : "free camera (F)")}, FOV {_fov:0}, {_bridge.ChunksReceived} chunks / {_bridge.BlocksReceived} block changes synced";
            _window.Title = $"{_opt.Title}  |  {_fpsFrames / _fpsTimer:0} FPS  |  {_chunks.Count} chunks{(_streamer.Idle ? "" : "  (loading...)")}{live}";
            _fpsTimer = 0;
            _fpsFrames = 0;
        }
        _settledFrames = _streamer.Idle && _chunksSinceShadow == 0 ? _settledFrames + 1 : 0;
        if (_opt.Film != null) FilmCapture();
        int minFrames = Environment.GetEnvironmentVariable("REALISM_PERF") != null ? 1000 : 200;
        if (_opt.ScreenshotPath != null && ((_settledFrames >= 3 && _frame > minFrames) || _frame == 5000))
        {
            SaveScreenshot(_opt.ScreenshotPath);
            _window.Close();
        }
    }

    /// <summary>Film mode: keep this frame once everything around the camera has loaded, then advance.</summary>
    unsafe void FilmCapture()
    {
        var film = _opt.Film!;
        if (_filmWarmup > 0) { _filmWarmup--; return; }
        if (_settledFrames < 8) return; // still streaming in: render again before keeping it
        var size = _window.FramebufferSize;
        if (_opt.FilmPreviewDir != null)
        {
            // Preview: the first, middle and last frame of each shot as images.
            var (shot, local, _, _, _, _, _) = film.At(_filmFrame);
            int n = (int)MathF.Round(film.Shots[shot].Seconds * Cinematic.Fps);
            if (local == 0 || local == n / 2 || local == n - 1)
                SaveScreenshot(Path.Combine(_opt.FilmPreviewDir, $"shot{shot + 1}_{(local == 0 ? "a" : local == n - 1 ? "c" : "b")}.png"));
            // Jump to the next key frame of interest.
            int next = local == 0 ? n / 2 : local == n / 2 ? n - 1 : n;
            _filmFrame += next - local;
        }
        else
        {
            _video ??= new VideoWriter(Comparison.Ffmpeg(), _opt.FilmOut!, size.X, size.Y, Cinematic.Fps);
            var pixels = new byte[size.X * size.Y * 4];
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _post!.OutputFbo);
            _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
            fixed (byte* p = pixels)
                _gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            _video.Write(pixels, film.Fade(_filmFrame));
            if (_filmFrame % 30 == 0) Console.WriteLine($"Film: frame {_filmFrame} / {film.FrameCount}");
            _filmFrame++;
        }
        _settledFrames = 0;
        if (_filmFrame >= film.FrameCount)
        {
            _video?.Dispose();
            _video = null;
            Console.WriteLine(_opt.FilmOut != null && _opt.FilmPreviewDir == null ? $"Film saved: {Path.GetFullPath(_opt.FilmOut)}" : "Preview done.");
            _window.Close();
        }
    }

    /// <summary>An RGBA framebuffer the finished frame is drawn into when the window is hidden (screenshot runs).</summary>
    unsafe uint CreateOffscreenTarget(int w, int h)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return fbo;
    }

    unsafe void SaveScreenshot(string path)
    {
        var size = _window.FramebufferSize;
        var pixels = new byte[size.X * size.Y * 4];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _post!.OutputFbo);
        _gl.ReadBuffer(_post.OutputFbo == 0 ? ReadBufferMode.Back : ReadBufferMode.ColorAttachment0);
        fixed (byte* p = pixels)
            _gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        Png.Save(path, size.X, size.Y, pixels);
        Console.WriteLine($"Saved {Path.GetFullPath(path)} (camera {_camPos}, yaw {_yaw:0.#}, pitch {_pitch:0.#}, underground {_underground:0.##}, adapt {_adapt:0.##}, scene {_post!.AverageLuminance:0.###})");
    }
}
