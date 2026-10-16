using System.Numerics;
using Silk.NET.OpenGL;

namespace Realism.Rendering;

/// <summary>
/// HDR frame pipeline: the scene renders into a 4x MSAA float buffer, which is resolved, then SSAO
/// (half resolution, depth-aware blur), a bloom mip chain, and a final composite with ACES tone mapping.
/// </summary>
public sealed class PostProcess : IDisposable
{
    const int BloomLevels = 6;

    readonly GL _gl;
    readonly ShaderProgram _ssao, _ssaoBlur, _down, _up, _composite, _vol, _lum;
    // Eye adaptation: the view's log-average brightness, read back a couple of frames late (no stall).
    const int LumSize = 64, LumLevels = 7; // 64x64 -> 1x1 at mip 6
    uint _lumTex, _lumFbo;
    readonly uint[] _lumPbo = new uint[3];
    int _lumFrame;

    /// <summary>Average (log-average) linear brightness of the last frames' scene, before exposure.</summary>
    public float AverageLuminance { get; private set; } = 0.2f;
    readonly uint _vao;
    readonly Vector3[] _kernel = new Vector3[16];

    int _w, _h;
    uint _sceneFbo, _sceneColor, _sceneDepth;
    uint _resolveFbo, _colorTex, _depthTex;
    readonly uint[] _aoFbo = new uint[2], _aoTex = new uint[2], _volFbo = new uint[2], _volTex = new uint[2];
    readonly List<(uint fbo, uint tex, int w, int h)> _bloom = new();

    public PostProcess(GL gl, int width, int height)
    {
        _gl = gl;
        _ssao = new ShaderProgram(gl, PostShaders.Fullscreen, PostShaders.Ssao);
        _ssaoBlur = new ShaderProgram(gl, PostShaders.Fullscreen, PostShaders.SsaoBlur);
        _down = new ShaderProgram(gl, PostShaders.Fullscreen, PostShaders.BloomDown);
        _up = new ShaderProgram(gl, PostShaders.Fullscreen, PostShaders.BloomUp);
        _composite = new ShaderProgram(gl, PostShaders.Fullscreen, PostShaders.Composite);
        _vol = new ShaderProgram(gl, PostShaders.Fullscreen, PostShaders.Volumetric);
        _lum = new ShaderProgram(gl, PostShaders.Fullscreen, PostShaders.LogLuminance);
        unsafe
        {
            _lumTex = Texture(InternalFormat.R16f, PixelFormat.Red, PixelType.HalfFloat, LumSize, LumSize, true);
            _gl.GenerateMipmap(TextureTarget.Texture2D);
            _lumFbo = ColorFbo(_lumTex);
            for (int i = 0; i < _lumPbo.Length; i++)
            {
                _lumPbo[i] = _gl.GenBuffer();
                _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _lumPbo[i]);
                _gl.BufferData(BufferTargetARB.PixelPackBuffer, 4, (void*)0, BufferUsageARB.StreamRead);
            }
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        }
        _vao = gl.GenVertexArray();

        // Hemisphere kernel, denser near the centre so close contacts darken most.
        var rng = new Random(7);
        for (int i = 0; i < _kernel.Length; i++)
        {
            var v = Vector3.Normalize(new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 0.9f + 0.1f));
            float s = i / (float)_kernel.Length;
            _kernel[i] = v * (0.1f + 0.9f * s * s) * (0.5f + 0.5f * (float)rng.NextDouble());
        }
        Resize(width, height);
    }

    public void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (width == _w && height == _h)) return;
        DeleteTargets();
        _w = width;
        _h = height;

        _sceneFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        _sceneColor = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _sceneColor);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.Rgba16f, (uint)_w, (uint)_h);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, _sceneColor);
        _sceneDepth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _sceneDepth);
        _gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.DepthComponent32f, (uint)_w, (uint)_h);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _sceneDepth);
        Check("scene");

        _colorTex = Texture(InternalFormat.Rgba16f, PixelFormat.Rgba, PixelType.HalfFloat, _w, _h, true);
        _depthTex = Texture(InternalFormat.DepthComponent32f, PixelFormat.DepthComponent, PixelType.Float, _w, _h, false);
        _resolveFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _resolveFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _colorTex, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _depthTex, 0);
        Check("resolve");

        for (int i = 0; i < 2; i++)
        {
            _aoTex[i] = Texture(InternalFormat.R8, PixelFormat.Red, PixelType.UnsignedByte, _w / 2, _h / 2, true);
            _aoFbo[i] = ColorFbo(_aoTex[i]);
            _volTex[i] = Texture(InternalFormat.R16f, PixelFormat.Red, PixelType.HalfFloat, _w / 2, _h / 2, true);
            _volFbo[i] = ColorFbo(_volTex[i]);
        }

        int bw = _w / 2, bh = _h / 2;
        for (int i = 0; i < BloomLevels && bw >= 2 && bh >= 2; i++, bw /= 2, bh /= 2)
        {
            uint tex = Texture(InternalFormat.R11fG11fB10f, PixelFormat.Rgb, PixelType.Float, bw, bh, true);
            _bloom.Add((ColorFbo(tex), tex, bw, bh));
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Bind the HDR scene target; draw the frame after this.</summary>
    public void BeginScene()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _sceneFbo);
        _gl.Viewport(0, 0, (uint)_w, (uint)_h);
    }

    /// <summary>
    /// Resolves the opaque scene so far into textures the water shader can read (refraction, depth, reflections),
    /// then rebinds the scene target to keep drawing.
    /// </summary>
    public (uint color, uint depth) CopySceneForWater()
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _sceneFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFbo);
        _gl.BlitFramebuffer(0, 0, _w, _h, 0, 0, _w, _h, ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        BeginScene();
        return (_colorTex, _depthTex);
    }

    public Vector2 Size => new(_w, _h);

    /// <summary>Where the finished frame goes: 0 = the window; an offscreen framebuffer for hidden screenshot runs.</summary>
    public uint OutputFbo { get; set; }

    /// <summary>Resolve, run SSAO and bloom, and composite into the window's framebuffer.</summary>
    public void Finish(Matrix4x4 proj, float exposure, FrameLight light)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _sceneFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _resolveFbo);
        _gl.BlitFramebuffer(0, 0, _w, _h, 0, 0, _w, _h, ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.DepthMask(false);
        _gl.BindVertexArray(_vao);

        MeasureLuminance();

        // SSAO at half resolution, then blur.
        Matrix4x4.Invert(proj, out var invProj);
        Target(_aoFbo[0], _w / 2, _h / 2);
        _ssao.Use();
        Bind(0, _depthTex);
        _ssao.Set("uDepth", 0);
        _ssao.Set("uProj", proj);
        _ssao.Set("uInvProj", invProj);
        _ssao.Set("uTexel", new Vector2(1f / _w, 1f / _h));
        for (int i = 0; i < _kernel.Length; i++) _ssao.Set($"uKernel[{i}]", _kernel[i]);
        Draw();

        Target(_aoFbo[1], _w / 2, _h / 2);
        _ssaoBlur.Use();
        Bind(0, _aoTex[0]);
        Bind(1, _depthTex);
        _ssaoBlur.Set("uAo", 0);
        _ssaoBlur.Set("uDepth", 1);
        _ssaoBlur.Set("uTexel", new Vector2(2f / _w, 2f / _h));
        Draw();

        // Volumetric sunlight at half resolution, blurred with the same depth-aware filter.
        Target(_volFbo[0], _w / 2, _h / 2);
        _vol.Use();
        Bind(0, _depthTex);
        Bind(1, light.FarShadow);
        Bind(2, light.NearShadow);
        _vol.Set("uDepth", 0);
        _vol.Set("uShadow", 1);
        _vol.Set("uShadowNear", 2);
        _vol.Set("uLightViewProj", light.LightViewProj);
        _vol.Set("uLightViewProjNear", light.LightViewProjNear);
        _vol.Set("uInvViewProj", light.InvViewProj);
        _vol.Set("uCamPos", light.CamPos);
        _vol.Set("uSunDir", light.SunDir);
        _vol.Set("uMaxDist", 140f);
        Draw();

        Target(_volFbo[1], _w / 2, _h / 2);
        _ssaoBlur.Use();
        Bind(0, _volTex[0]);
        Bind(1, _depthTex);
        Draw();

        // Bloom: progressively downsample the bright parts, then add them back up the chain.
        _down.Use();
        _down.Set("uSrc", 0);
        for (int i = 0; i < _bloom.Count; i++)
        {
            var (srcTex, srcW, srcH) = i == 0 ? (_colorTex, _w, _h) : (_bloom[i - 1].tex, _bloom[i - 1].w, _bloom[i - 1].h);
            Target(_bloom[i].fbo, _bloom[i].w, _bloom[i].h);
            Bind(0, srcTex);
            _down.Set("uTexel", new Vector2(1f / srcW, 1f / srcH));
            _down.Set("uFirst", i == 0 ? 1 : 0);
            Draw();
        }
        _up.Use();
        _up.Set("uSrc", 0);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.One);
        for (int i = _bloom.Count - 1; i > 0; i--)
        {
            Target(_bloom[i - 1].fbo, _bloom[i - 1].w, _bloom[i - 1].h);
            Bind(0, _bloom[i].tex);
            _up.Set("uTexel", new Vector2(1f / _bloom[i].w, 1f / _bloom[i].h));
            Draw();
        }
        _gl.Disable(EnableCap.Blend);

        Target(OutputFbo, _w, _h);
        _composite.Use();
        Bind(0, _colorTex);
        Bind(1, _aoTex[1]);
        Bind(2, _bloom[0].tex);
        Bind(3, _volTex[1]);
        _composite.Set("uVolumetric", 3);
        _composite.Set("uSunColor", light.SunColor);
        _composite.Set("uVolumetricStrength", 0.4f * light.VolumetricScale);
        _composite.Set("uNight", light.Night);
        _composite.Set("uBrightness", light.Brightness);
        _composite.Set("uScene", 0);
        _composite.Set("uAo", 1);
        _composite.Set("uBloom", 2);
        _composite.Set("uExposure", exposure);
        _composite.Set("uBloomStrength", 0.05f);
        Draw();

        _gl.DepthMask(true);
        _gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>Log-average scene brightness into a 1x1 mip; read back through a ring of pixel buffers.</summary>
    unsafe void MeasureLuminance()
    {
        Target(_lumFbo, LumSize, LumSize);
        _lum.Use();
        Bind(0, _colorTex);
        _lum.Set("uScene", 0);
        _lum.Set("uCell", new Vector2(1f / LumSize, 1f / LumSize));
        Draw();
        Bind(0, _lumTex);
        _gl.GenerateMipmap(TextureTarget.Texture2D);

        int slot = _lumFrame % _lumPbo.Length;
        _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _lumPbo[slot]);
        _gl.GetTexImage(TextureTarget.Texture2D, LumLevels - 1, PixelFormat.Red, PixelType.Float, (void*)0);
        if (_lumFrame >= _lumPbo.Length)
        {
            // The oldest buffer was filled frames ago: reading it doesn't wait for the GPU.
            _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, _lumPbo[(slot + 1) % _lumPbo.Length]);
            float* v = (float*)_gl.MapBufferRange(BufferTargetARB.PixelPackBuffer, 0, 4, (uint)MapBufferAccessMask.ReadBit);
            if (v != null)
            {
                if (float.IsFinite(*v)) AverageLuminance = MathF.Exp(*v);
                _gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
            }
        }
        _gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
        _lumFrame++;
    }

    void Target(uint fbo, int w, int h)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.Viewport(0, 0, (uint)w, (uint)h);
    }

    void Bind(int unit, uint tex)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
    }

    void Draw() => _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

    unsafe uint Texture(InternalFormat internalFormat, PixelFormat format, PixelType type, int w, int h, bool linear)
    {
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, (uint)w, (uint)h, 0, format, type, (void*)0);
        int filter = (int)(linear ? GLEnum.Linear : GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    uint ColorFbo(uint tex)
    {
        uint fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
        Check("post");
        return fbo;
    }

    void Check(string what)
    {
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"{what} framebuffer incomplete: {status}");
    }

    void DeleteTargets()
    {
        if (_w == 0) return;
        _gl.DeleteFramebuffer(_sceneFbo);
        _gl.DeleteRenderbuffer(_sceneColor);
        _gl.DeleteRenderbuffer(_sceneDepth);
        _gl.DeleteFramebuffer(_resolveFbo);
        _gl.DeleteTexture(_colorTex);
        _gl.DeleteTexture(_depthTex);
        for (int i = 0; i < 2; i++)
        {
            _gl.DeleteFramebuffer(_aoFbo[i]);
            _gl.DeleteTexture(_aoTex[i]);
            _gl.DeleteFramebuffer(_volFbo[i]);
            _gl.DeleteTexture(_volTex[i]);
        }
        foreach (var (fbo, tex, _, _) in _bloom)
        {
            _gl.DeleteFramebuffer(fbo);
            _gl.DeleteTexture(tex);
        }
        _bloom.Clear();
    }

    public void Dispose()
    {
        DeleteTargets();
        foreach (var p in new[] { _ssao, _ssaoBlur, _down, _up, _composite, _vol, _lum }) p.Dispose();
        _gl.DeleteFramebuffer(_lumFbo);
        _gl.DeleteTexture(_lumTex);
        foreach (var b in _lumPbo) _gl.DeleteBuffer(b);
        _gl.DeleteVertexArray(_vao);
    }
}

/// <summary>What the volumetric pass needs from the frame: shadow cascades, sun and camera.</summary>
public readonly record struct FrameLight(
    uint FarShadow, uint NearShadow, Matrix4x4 LightViewProj, Matrix4x4 LightViewProjNear,
    Matrix4x4 InvViewProj, Vector3 CamPos, Vector3 SunDir, Vector3 SunColor, float Night = 0, float VolumetricScale = 1,
    float Brightness = 0); // Minecraft's Brightness slider, -0.5 (Moody) .. 0 (default) .. +0.5 (Bright), plus night vision
