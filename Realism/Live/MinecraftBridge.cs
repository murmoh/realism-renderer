using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Realism.Meshing;
using Realism.Minecraft;

namespace Realism.Live;

/// <summary>
/// Shared-memory layout, written by the Fabric mod (MinecraftMod/.../Bridge.java) and read here.
/// All values little-endian. The file lives at %LOCALAPPDATA%\RealismRenderer\bridge.bin.
///
///   Header (4096 bytes)
///     0  int  magic 'PTRB'          16 long writePos (writer)      40 long readerHeartbeat (unix ms, reader)
///     4  int  version               24 long readPos  (reader)      48 long writerHeartbeat (unix ms, writer)
///     8  long ringSize              32 long sessionId (new on every (re)start or world change)
///    64  int  seq (seqlock: odd while the camera block is being written)
///    72  double x, y, z (camera)  96 float yaw, pitch (Minecraft degrees)  104 long dayTime
///   112  float rain, thunder      120 int inWorld   124 int selectedHotbarSlot   128 int onGround   136 double feetY
///   144  double player x, y, z (feet)  168 float bodyYaw, headYaw, headPitch, limbPos, limbAmplitude, swing
///   192  int sneaking, playerValid      208 long write2 (renderer)  216 long read2 (mod)
///   Ring2 (after Ring): renderer -> mod, message 10 COLLISION: long session, int cx, cz, short tops[256],
///        short heights[4096] (4x4 per column, Y*16; see SmoothCollision.java)
///   224  int guiW, guiH; 232 long guiFrame; 240 int guiSlot; 244 int screenOpen; 248 int thirdPerson
///   After Ring2: two RGBA UI frames (Minecraft HUD, hand and screens), GuiSlot bytes each, written alternately.
///   Then two entity snapshots (EntSlot bytes each; 320 long frame, 328 int slot). Ring messages 5 MODEL and
///   6 TEXTURE carry mob geometry and PNG textures; formats in MinecraftMod/.../EntitySync.java.
///   Ring message 4 SKIN: int slim, int length, PNG bytes. Ring2 message 11 INPUT: see SendInputEvent.
///   Input block also has 296 int windowWidth, 300 windowHeight (renderer window; Minecraft resizes to match).
///   256  input block (renderer -> mod): int active, int keys (bitmask), float yaw, pitch (Minecraft degrees),
///        int slot, slotSeq, then click counters attack, use, pick, drop (each press increments)
///   Ring (ringSize bytes) of messages: int type, int payloadLength, payload
///     1 CHUNK  int cx, cz, minSectionY, sectionCount; per section: byte present;
///              if present: short paletteCount, palette[ short nameLen, utf8 name, sbyte fluidLevel, int stateId ],
///              4096 indices (byte if paletteCount ≤ 256 else short), order y*256 + z*16 + x
///     2 UNLOAD int cx, cz
///     3 BLOCK  int x, y, z, short nameLen, utf8 name, sbyte fluidLevel, int stateId
///     8 BLOCK MODEL, 9 ATLAS: Minecraft's block models and texture atlas (see Minecraft/BlockModels.cs)
/// </summary>
public static class BridgeProtocol
{
    public const int Magic = 0x50545242, Version = 1, Header = 4096;
    public const long Ring = 64L << 20, Ring2 = 16L << 20; // Ring: mod -> renderer, Ring2 (right after it): renderer -> mod
    public const int OffMagic = 0, OffVersion = 4, OffRing = 8, OffWrite = 16, OffRead = 24, OffSession = 32,
        OffReaderHeartbeat = 40, OffWriterHeartbeat = 48;
    public const int OffSeq = 64, OffX = 72, OffY = 80, OffZ = 88, OffYaw = 96, OffPitch = 100, OffDayTime = 104,
        OffRain = 112, OffThunder = 116, OffInWorld = 120, OffSlot = 124, OffOnGround = 128, OffFeetY = 136,
        OffPX = 144, OffPY = 152, OffPZ = 160, OffBodyYaw = 168, OffHeadYaw = 172, OffHeadPitch = 176,
        OffLimbPos = 180, OffLimbAmp = 184, OffSwing = 188, OffSneak = 192, OffPlayerValid = 196,
        OffWrite2 = 208, OffRead2 = 216;
    // Input block (renderer writes, mod applies)
    public const int OffInActive = 256, OffInKeys = 260, OffInYaw = 264, OffInPitch = 268, OffInSlot = 272, OffInSlotSeq = 276,
        OffInAttack = 280, OffInUse = 284, OffInPick = 288, OffInDrop = 292;
    public const int KeyForward = 1, KeyBack = 2, KeyLeft = 4, KeyRight = 8, KeyJump = 16, KeySneak = 32, KeySprint = 64,
        KeyAttack = 128, KeyUse = 256;
    public const int MsgChunk = 1, MsgUnload = 2, MsgBlock = 3, MsgSkin = 4, MsgCollision = 10, MsgInput = 11;
    public const int OffGuiW = 224, OffGuiH = 228, OffGuiFrame = 232, OffGuiSlot = 240, OffScreenOpen = 244, OffThirdPerson = 248,
        OffInWinW = 296, OffInWinH = 300, OffInReaderId = 304;
    public const long GuiSlot = 3840L * 2160 * 4, GuiBase = Header + Ring + Ring2;
    public const long EntSlot = 4L << 20, EntBase = GuiBase + 2 * GuiSlot, FileSize = EntBase + 2 * EntSlot;
    public const int OffEntFrame = 320, OffEntSlot = 328, OffTarget = 336, MsgModel = 5, MsgTexture = 6, MsgEdits = 7,
        MsgBlockModel = 8, MsgAtlas = 9;
    public const int OffBrightness = 372, OffNightVision = 376; // floats: 1 + Brightness setting (0 = not sent), night vision 0..1
    public const int OffHideToggle = 368; // int, renderer -> mod: bumped to show/hide Minecraft's window
    public const int OffFullscreen = 380; // int: 1 = the player had Minecraft fullscreen; the mod keeps it windowed while we run
    public const int OffGuiStraight = 364; // int: 1 = the UI frame is straight alpha over Minecraft's own world (playing in its window)
    public const int OffFov = 360; // float: Minecraft's vertical field of view this frame, degrees (0: older mod)

    // PTR_BRIDGE_DIR points at a separate bridge (the isolated test client); otherwise the shared default.
    public static string FilePath => Environment.GetEnvironmentVariable("PTR_BRIDGE_DIR") is { Length: > 0 } dir ? Path.Combine(dir, "bridge.bin") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RealismRenderer", "bridge.bin");
}

/// <summary>What the Minecraft camera is doing right now.</summary>
public readonly record struct PlayerState(bool InWorld, Vector3 Eye, float Yaw, float Pitch, long DayTime, float Rain, float Thunder,
    bool OnGround = false, float FeetY = 0, PlayerPose Pose = default)
{
    /// <summary>Minecraft yaw (0 = +Z, clockwise) and pitch (positive = down) as this renderer's yaw/pitch in radians.</summary>
    public (float yaw, float pitch) RendererAngles()
    {
        float y = Yaw * MathF.PI / 180;
        return (MathF.Atan2(MathF.Cos(y), -MathF.Sin(y)), -Pitch * MathF.PI / 180);
    }

    /// <summary>Minecraft day time (ticks, 0 = sunrise) as this renderer's time of day (0 sunrise .. 1 sunset .. 2).</summary>
    public float TimeOfDay => (DayTime % 24000 + 24000) % 24000 / 12000f;
}

/// <summary>
/// Reads the bridge on a background thread: applies chunk loads, unloads and block changes to the live
/// <see cref="World"/> and tells the streamer which chunks to re-mesh. Camera state is read on demand.
/// </summary>
public sealed unsafe class MinecraftBridge : IDisposable
{
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    readonly byte* _p;
    readonly World _world;
    readonly Thread _thread;
    ChunkStreamer? _streamer;
    volatile bool _stop;
    long _session, _cursor;

    public int ChunksReceived, BlocksReceived;

    public World World => _world;

    /// <summary>Diagnostics (REALISM_TRACE): log when block changes arrive and chunks are uploaded.</summary>
    public static readonly bool TraceTimes = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REALISM_TRACE"));

    public MinecraftBridge(World world)
    {
        _world = world;
        _world.Models ??= new BlockModels();
        string path = BridgeProtocol.FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long size = BridgeProtocol.FileSize;
        var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < size) fs.SetLength(size);
        _file = MemoryMappedFile.CreateFromFile(fs, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        _view = _file.CreateViewAccessor(0, size);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _p = p + _view.PointerOffset;
        // A fresh id per renderer instance: Minecraft starts a new session for it (resends world, skin; resets rings).
        *(long*)(_p + BridgeProtocol.OffInReaderId) = Random.Shared.NextInt64(1, long.MaxValue);
        // The input block still holds the last renderer's look and keys; once our heartbeat starts Minecraft would
        // apply them (snapping the view to where the previous session looked). Nothing is driven until we write.
        Volatile.Write(ref *(int*)(_p + BridgeProtocol.OffInActive), 0);
        _thread = new Thread(Run) { IsBackground = true, Name = "Minecraft bridge" };
    }

    public void Start(ChunkStreamer streamer)
    {
        _streamer = streamer;
        _thread.Start();
    }

    long ReadLong(int off) => Volatile.Read(ref *(long*)(_p + off));
    void WriteLong(int off, long v) => Volatile.Write(ref *(long*)(_p + off), v);

    static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>True while the Minecraft mod is running and writing.</summary>
    public bool MinecraftRunning => Now - ReadLong(BridgeProtocol.OffWriterHeartbeat) < 3000;

    /// <summary>Minecraft's vertical field of view right now (its FOV setting with sprint/speed zoom), degrees; 0 if unknown.</summary>
    public float ReadFov() => Volatile.Read(ref *(float*)(_p + BridgeProtocol.OffFov));

    public PlayerState ReadPlayer()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            int s1 = Volatile.Read(ref *(int*)(_p + BridgeProtocol.OffSeq));
            if ((s1 & 1) != 0) { Thread.SpinWait(20); continue; }
            var state = new PlayerState(
                *(int*)(_p + BridgeProtocol.OffInWorld) != 0,
                new Vector3((float)*(double*)(_p + BridgeProtocol.OffX), (float)*(double*)(_p + BridgeProtocol.OffY), (float)*(double*)(_p + BridgeProtocol.OffZ)),
                *(float*)(_p + BridgeProtocol.OffYaw), *(float*)(_p + BridgeProtocol.OffPitch),
                *(long*)(_p + BridgeProtocol.OffDayTime),
                *(float*)(_p + BridgeProtocol.OffRain), *(float*)(_p + BridgeProtocol.OffThunder),
                *(int*)(_p + BridgeProtocol.OffOnGround) != 0, (float)*(double*)(_p + BridgeProtocol.OffFeetY),
                new PlayerPose(*(int*)(_p + BridgeProtocol.OffPlayerValid) != 0,
                    new Vector3((float)*(double*)(_p + BridgeProtocol.OffPX), (float)*(double*)(_p + BridgeProtocol.OffPY), (float)*(double*)(_p + BridgeProtocol.OffPZ)),
                    *(float*)(_p + BridgeProtocol.OffBodyYaw), *(float*)(_p + BridgeProtocol.OffHeadYaw), *(float*)(_p + BridgeProtocol.OffHeadPitch),
                    *(float*)(_p + BridgeProtocol.OffLimbPos), *(float*)(_p + BridgeProtocol.OffLimbAmp), *(float*)(_p + BridgeProtocol.OffSwing),
                    *(int*)(_p + BridgeProtocol.OffSneak) != 0));
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref *(int*)(_p + BridgeProtocol.OffSeq)) == s1) return state with { InWorld = state.InWorld && MinecraftRunning };
        }
        return default;
    }


    /// <summary>Keyboard/mouse from the renderer window, applied to the Minecraft player by the mod.</summary>
    public void WriteInput(in ControlInput input)
    {
        *(int*)(_p + BridgeProtocol.OffInKeys) = input.Keys;
        *(float*)(_p + BridgeProtocol.OffInYaw) = input.Yaw;
        *(float*)(_p + BridgeProtocol.OffInPitch) = input.Pitch;
        *(int*)(_p + BridgeProtocol.OffInSlot) = input.Slot;
        *(int*)(_p + BridgeProtocol.OffInSlotSeq) = input.SlotSeq;
        *(int*)(_p + BridgeProtocol.OffInAttack) = input.AttackClicks;
        *(int*)(_p + BridgeProtocol.OffInUse) = input.UseClicks;
        *(int*)(_p + BridgeProtocol.OffInPick) = input.PickClicks;
        *(int*)(_p + BridgeProtocol.OffInDrop) = input.DropPresses;
        Volatile.Write(ref *(int*)(_p + BridgeProtocol.OffInActive), input.Active ? 1 : 0);
    }

    public int SelectedSlot => *(int*)(_p + BridgeProtocol.OffSlot);

    // ---- renderer -> mod: smooth collision ----

    readonly object _writeLock = new();
    long _write2;

    /// <summary>Sends a chunk's smooth-collision height map to the mod (called from mesher threads).</summary>
    public void SendCollision(ChunkMeshes m)
    {
        // REALISM_NO_FLOORS (diagnostics): send only the tops, as before cave floors had smooth collision.
        int floors = Environment.GetEnvironmentVariable("REALISM_NO_FLOORS") != null ? 0 : m.CollisionFloors.Count;
        int len = 8 + 8 + 256 * 2 + 4096 * 2 + 4 + floors * 36;
        var msg = new byte[8 + len];
        BinaryPrimitives.WriteInt32LittleEndian(msg, BridgeProtocol.MsgCollision);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(4), len);
        lock (_writeLock)
        {
            BinaryPrimitives.WriteInt64LittleEndian(msg.AsSpan(8), _session);
            BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(16), m.X);
            BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(20), m.Z);
            MemoryMarshal.AsBytes(m.CollisionTops.AsSpan()).CopyTo(msg.AsSpan(24));
            MemoryMarshal.AsBytes(m.CollisionHeights.AsSpan()).CopyTo(msg.AsSpan(24 + 512));
            // Then the floors below the tops (caves): int count; per floor: short column, short Y, short heights[16].
            int p = 24 + 512 + 8192;
            BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(p), floors);
            p += 4;
            foreach (var (col, top, heights) in m.CollisionFloors.Take(floors))
            {
                BinaryPrimitives.WriteInt16LittleEndian(msg.AsSpan(p), col);
                BinaryPrimitives.WriteInt16LittleEndian(msg.AsSpan(p + 2), top);
                MemoryMarshal.AsBytes(heights.AsSpan()).CopyTo(msg.AsSpan(p + 4));
                p += 36;
            }
            WriteRing2(msg, waitMs: 15); // if the mod isn't reading, drop it (that chunk keeps vanilla collision)
        }
    }

    /// <summary>
    /// An input event for Minecraft's own handlers (used for its screens: inventory, chat, menus, and for keys).
    /// Kinds: 0 key (key, scancode, action, mods), 1 char (codepoint, mods), 2 mouse button (button, action, mods),
    /// 3 cursor (float x, y in Minecraft window pixels), 4 scroll (float horizontal, vertical).
    /// </summary>
    public void SendInputEvent(int kind, int a = 0, int b = 0, int c = 0, int d = 0)
    {
        var msg = new byte[8 + 20];
        BinaryPrimitives.WriteInt32LittleEndian(msg, BridgeProtocol.MsgInput);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(4), 20);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(8), kind);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(12), a);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(16), b);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(20), c);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(24), d);
        lock (_writeLock) WriteRing2(msg, waitMs: 0);
    }

    public void SendInputEvent(int kind, float x, float y) =>
        SendInputEvent(kind, BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y));

    void WriteRing2(byte[] msg, int waitMs)
    {
        for (int waited = 0; ; waited += 5)
        {
            long read2 = ReadLong(BridgeProtocol.OffRead2);
            long used = _write2 - read2;
            if (used < 0 || used > BridgeProtocol.Ring2) used = _write2;
            if (BridgeProtocol.Ring2 - used >= msg.Length) break;
            if (waited >= waitMs) return;
            Thread.Sleep(5);
        }
        long ring2 = BridgeProtocol.Ring2;
        int start = (int)(_write2 % ring2);
        int first = (int)Math.Min(msg.Length, ring2 - start);
        byte* baseP = _p + BridgeProtocol.Header + BridgeProtocol.Ring;
        msg.AsSpan(0, first).CopyTo(new Span<byte>(baseP + start, first));
        if (first < msg.Length) msg.AsSpan(first).CopyTo(new Span<byte>(baseP, msg.Length - first));
        _write2 += msg.Length;
        WriteLong(BridgeProtocol.OffWrite2, _write2);
    }

    // ---- Minecraft's UI, captured every frame ----

    /// <summary>The newest captured UI frame: RGBA (premultiplied, bottom row first), or false if none.</summary>
    /// <summary>True when the last UI frame holds Minecraft's world too, with alpha marking the UI (straight alpha): you
    /// are playing in Minecraft's own window and the renderer follows. Otherwise it is the UI alone, premultiplied.</summary>
    /// <summary>Minecraft's Brightness setting (0 Moody .. 0.5 default .. 1 Bright) and Night Vision strength (0..1).</summary>
    public (float brightness, float nightVision) ReadBrightness()
    {
        float b = Volatile.Read(ref *(float*)(_p + BridgeProtocol.OffBrightness));
        float nv = Volatile.Read(ref *(float*)(_p + BridgeProtocol.OffNightVision));
        return (b >= 1 && b <= 2 ? b - 1 : 0.5f, Math.Clamp(nv, 0, 1));
    }

    /// <summary>Asks the mod to show or hide Minecraft's own window (it remembers the choice).</summary>
    public void ToggleMinecraftWindow() => Interlocked.Increment(ref *(int*)(_p + BridgeProtocol.OffHideToggle));

    /// <summary>True when the player had Minecraft fullscreen: the mod made it windowed, so this window goes fullscreen instead.</summary>
    public bool MinecraftWantsFullscreen => MinecraftRunning && Volatile.Read(ref *(int*)(_p + BridgeProtocol.OffFullscreen)) == 1;

    public bool GuiStraightAlpha => *(int*)(_p + BridgeProtocol.OffGuiStraight) == 1;

    public bool TryGetGui(ref long lastFrame, out IntPtr pixels, out int width, out int height)
    {
        long frame = ReadLong(BridgeProtocol.OffGuiFrame);
        pixels = IntPtr.Zero;
        width = *(int*)(_p + BridgeProtocol.OffGuiW);
        height = *(int*)(_p + BridgeProtocol.OffGuiH);
        if (frame == lastFrame || width <= 0 || height <= 0) return false;
        int slot = *(int*)(_p + BridgeProtocol.OffGuiSlot) & 1;
        pixels = (IntPtr)(_p + BridgeProtocol.GuiBase + slot * BridgeProtocol.GuiSlot);
        lastFrame = frame;
        return true;
    }

    /// <summary>Model geometry and textures for mobs/items, in arrival order (see EntitySync.java).</summary>
    public readonly System.Collections.Concurrent.ConcurrentQueue<(int type, byte[] data)> EntityAssets = new();

    /// <summary>The newest mob/item snapshot (see EntitySync.java), or false if nothing new.</summary>
    public bool TryGetEntities(ref long lastFrame, out ReadOnlySpan<byte> snapshot)
    {
        snapshot = default;
        long frame = ReadLong(BridgeProtocol.OffEntFrame);
        if (frame == lastFrame) return false;
        lastFrame = frame;
        int slot = *(int*)(_p + BridgeProtocol.OffEntSlot) & 1;
        snapshot = new ReadOnlySpan<byte>(_p + BridgeProtocol.EntBase + slot * BridgeProtocol.EntSlot, (int)BridgeProtocol.EntSlot);
        return true;
    }

    /// <summary>The block under the crosshair (Minecraft's own raycast) and its mining stage (-1 = not mining).</summary>
    public (bool has, int x, int y, int z, int stage) Target
    {
        get
        {
            int* t = (int*)(_p + BridgeProtocol.OffTarget);
            return (t[0] != 0, t[1], t[2], t[3], t[5]);
        }
    }

    public bool ScreenOpen => *(int*)(_p + BridgeProtocol.OffScreenOpen) != 0;
    public bool ThirdPerson => *(int*)(_p + BridgeProtocol.OffThirdPerson) != 0;

    /// <summary>Tells the mod this window's size, so Minecraft's window (and UI) matches it.</summary>
    public void WriteWindowSize(int w, int h)
    {
        *(int*)(_p + BridgeProtocol.OffInWinW) = w;
        *(int*)(_p + BridgeProtocol.OffInWinH) = h;
    }

    /// <summary>The player's skin (PNG bytes) and whether it uses slim arms; Version bumps when it changes.</summary>
    public (byte[] png, bool slim, int version) Skin => _skin;
    (byte[] png, bool slim, int version) _skin = (Array.Empty<byte>(), false, 0);


    void Run()
    {
        var payload = new byte[1 << 20];
        while (!_stop)
        {
            WriteLong(BridgeProtocol.OffReaderHeartbeat, Now);
            long session = ReadLong(BridgeProtocol.OffSession);
            if (session != _session)
            {
                // Minecraft (re)started a session: new world, dimension, or we just connected.
                lock (_writeLock)
                {
                    _session = session;
                    _write2 = 0;
                    WriteLong(BridgeProtocol.OffWrite2, 0);
                }
                _cursor = 0;
                _world.Clear();
                _world.Models!.Clear(); // the mod resends the models (and atlas) in the new session
                _streamer!.ResetAll();
            }

            long write = ReadLong(BridgeProtocol.OffWrite);
            if (write < _cursor) { Thread.Sleep(5); continue; } // writer is mid-reset; the session id changes next
            bool any = false;
            while (_cursor + 8 <= write && !_stop)
            {
                Span<byte> head = stackalloc byte[8];
                Copy(_cursor, head);
                int type = BinaryPrimitives.ReadInt32LittleEndian(head);
                int len = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
                if (len < 0 || len > BridgeProtocol.Ring) { _cursor = write; break; } // corrupt: skip ahead
                if (payload.Length < len) payload = new byte[Math.Max(len, payload.Length * 2)];
                Copy(_cursor + 8, payload.AsSpan(0, len));
                _cursor += 8 + len;
                try { Handle(type, payload.AsSpan(0, len)); }
                catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    Console.WriteLine($"Bridge: bad message type {type}: {e.Message}");
                }
                WriteLong(BridgeProtocol.OffRead, _cursor);
                any = true;
            }
            if (!any) Thread.Sleep(4);
        }
    }

    void Copy(long pos, Span<byte> dest)
    {
        long ring = BridgeProtocol.Ring;
        int start = (int)(pos % ring);
        int first = (int)Math.Min(dest.Length, ring - start);
        new ReadOnlySpan<byte>(_p + BridgeProtocol.Header + start, first).CopyTo(dest);
        if (first < dest.Length)
            new ReadOnlySpan<byte>(_p + BridgeProtocol.Header, dest.Length - first).CopyTo(dest[first..]);
    }

    void Handle(int type, ReadOnlySpan<byte> d)
    {
        int p = 0;
        int Int(ReadOnlySpan<byte> s) { int v = BinaryPrimitives.ReadInt32LittleEndian(s[p..]); p += 4; return v; }
        byte Material(ReadOnlySpan<byte> s, out ushort state)
        {
            int n = BinaryPrimitives.ReadInt16LittleEndian(s[p..]);
            string name = Encoding.UTF8.GetString(s.Slice(p + 2, n));
            sbyte level = (sbyte)s[p + 2 + n];
            state = (ushort)BinaryPrimitives.ReadInt32LittleEndian(s[(p + 3 + n)..]);
            Blocks.SeeState(name, state);
            p += 7 + n;
            return level < 0 ? Blocks.Classify(name)
                : name == "minecraft:water" ? Mat.WaterLevel(level) : name == "minecraft:lava" ? Mat.LavaLevel(level) : Blocks.Classify(name);
        }

        switch (type)
        {
            case BridgeProtocol.MsgChunk:
            {
                int cx = Int(d), cz = Int(d), minSection = Int(d), count = Int(d);
                var sections = new byte[]?[count];
                var states = new ushort[]?[count];
                Span<byte> palette = stackalloc byte[4096];
                Span<ushort> paletteStates = stackalloc ushort[4096];
                for (int s = 0; s < count; s++)
                {
                    if (d[p++] == 0) continue;
                    int pc = BinaryPrimitives.ReadInt16LittleEndian(d[p..]);
                    p += 2;
                    var mats = pc <= 4096 ? palette[..pc] : new byte[pc];
                    for (int i = 0; i < pc; i++) mats[i] = Material(d, out paletteStates[i]);
                    var data = new byte[4096];
                    var st = new ushort[4096];
                    for (int i = 0; i < 4096; i++)
                    {
                        int idx = pc <= 256 ? d[p + i] : BinaryPrimitives.ReadInt16LittleEndian(d[(p + i * 2)..]);
                        data[i] = mats[idx];
                        st[i] = paletteStates[idx];
                    }
                    p += pc <= 256 ? 4096 : 8192;
                    bool allAir = pc == 1 && mats[0] == Mat.Air;
                    sections[s] = allAir ? null : data;
                    states[s] = allAir ? null : st;
                }
                _world.Put(Chunk.FromSections(cx, cz, minSection, sections, states));
                _streamer!.MarkDirty(cx, cz);
                Interlocked.Increment(ref ChunksReceived);
                break;
            }
            case BridgeProtocol.MsgEdits:
            {
                // Blocks dug or placed in play, saved by the mod with the world (BlockPos.asLong layout).
                int count = Int(d);
                for (int i = 0; i < count; i++, p += 8)
                {
                    long l = BinaryPrimitives.ReadInt64LittleEndian(d[p..]);
                    _world.MarkEdit((int)(l >> 38), (int)(l << 52 >> 52), (int)(l << 26 >> 38));
                }
                break;
            }
            case BridgeProtocol.MsgBlockModel:
                _world.Models!.ReadModel(d);
                break;
            case BridgeProtocol.MsgAtlas:
                _world.Models!.ReadAtlas(d);
                break;
            case BridgeProtocol.MsgModel or BridgeProtocol.MsgTexture:
                EntityAssets.Enqueue((type, d.ToArray())); // turned into GPU meshes/textures on the render thread
                break;
            case BridgeProtocol.MsgSkin:
            {
                bool slim = Int(d) != 0;
                int len = Int(d);
                _skin = (d.Slice(p, len).ToArray(), slim, _skin.version + 1);
                break;
            }
            case BridgeProtocol.MsgUnload:
            {
                int cx = Int(d), cz = Int(d);
                _world.Remove(cx, cz);
                _streamer!.Remove(cx, cz);
                break;
            }
            case BridgeProtocol.MsgBlock:
            {
                int x = Int(d), y = Int(d), z = Int(d);
                byte m = Material(d, out ushort state);
                var chunk = _world.GetChunk(x >> 4, z >> 4);
                if (chunk == null) break;
                // Solid ground <-> open space is a dig or a placement (grass spreading onto dirt etc. is not).
                if (Mat.IsTerrain(chunk.Get(x & 15, y, z & 15)) != Mat.IsTerrain(m)) _world.MarkEdit(x, y, z);
                _world.Put(chunk.WithBlock(x & 15, y, z & 15, m, state));
                if (TraceTimes) Console.WriteLine($"[trace] {Environment.TickCount64 % 100000} block {x},{y},{z} -> {m}");
                _streamer!.MarkDirty(x >> 4, z >> 4);
                Interlocked.Increment(ref BlocksReceived);
                break;
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join();
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>What the player is doing in the renderer window, in Minecraft terms.</summary>
public struct ControlInput
{
    public bool Active;
    public int Keys;              // BridgeProtocol.Key* bits held
    public float Yaw, Pitch;      // Minecraft degrees
    public int Slot, SlotSeq;     // hotbar slot request; SlotSeq bumps on each change
    public int AttackClicks, UseClicks, PickClicks, DropPresses;

    /// <summary>Renderer yaw/pitch (radians, see <see cref="PlayerState.RendererAngles"/>) to Minecraft degrees.</summary>
    public static (float yaw, float pitch) MinecraftAngles(float yaw, float pitch) =>
        (MathF.Atan2(-MathF.Cos(yaw), MathF.Sin(yaw)) * 180 / MathF.PI, -pitch * 180 / MathF.PI);
}

/// <summary>The player's body for drawing: feet position, facing (Minecraft degrees) and animation state.</summary>
public readonly record struct PlayerPose(bool Valid, Vector3 Feet, float BodyYaw, float HeadYaw, float HeadPitch,
    float LimbPos, float LimbAmp, float Swing, bool Sneaking);
