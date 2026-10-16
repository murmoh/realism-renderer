package com.realismrenderer;

import org.lwjgl.opengl.GL11;
import org.lwjgl.opengl.GL13;
import org.lwjgl.opengl.GL20;
import org.lwjgl.opengl.GL30;
import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.lang.invoke.MethodHandles;
import java.lang.invoke.VarHandle;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Random;

import net.minecraft.block.BlockState;
import net.minecraft.block.Blocks;
import net.fabricmc.fabric.api.client.keybinding.v1.KeyBindingHelper;
import net.minecraft.client.MinecraftClient;
import net.minecraft.entity.attribute.EntityAttributeInstance;
import net.minecraft.entity.attribute.EntityAttributes;
import net.minecraft.server.integrated.IntegratedServer;
import net.minecraft.client.gui.screen.GameMenuScreen;
import net.minecraft.client.option.GameOptions;
import net.minecraft.client.option.InactivityFpsLimit;
import net.minecraft.client.option.KeyBinding;
import net.minecraft.client.render.Camera;
import net.minecraft.client.world.ClientWorld;
import net.minecraft.registry.Registries;
import net.minecraft.state.property.Properties;
import net.minecraft.util.math.BlockPos;
import net.minecraft.util.math.ChunkPos;
import net.minecraft.util.math.Vec3d;
import net.minecraft.world.chunk.ChunkSection;
import net.minecraft.world.chunk.ChunkStatus;
import net.minecraft.world.chunk.WorldChunk;

/**
 * Writer side of the shared-memory bridge (see Realism/Live/BridgeProtocol.cs for the layout).
 *
 * The file holds a header with the live camera/time/weather state, and a ring buffer of messages:
 * chunk loaded (full block data), chunk unloaded, block changed. Nothing is written while no renderer is
 * reading; when one appears, a new session starts and every loaded chunk is sent, nearest first.
 */
public final class Bridge {
    // Must match BridgeProtocol.cs
    static final int MAGIC = 0x50545242, VERSION = 1, HEADER = 4096;
    static final long RING = 64L << 20, RING2 = 16L << 20; // ring: mod -> renderer, ring2: renderer -> mod
    static final int OFF_MAGIC = 0, OFF_VERSION = 4, OFF_RING = 8, OFF_WRITE = 16, OFF_READ = 24, OFF_SESSION = 32,
        OFF_READER_HEARTBEAT = 40, OFF_WRITER_HEARTBEAT = 48;
    static final int OFF_SEQ = 64, OFF_X = 72, OFF_Y = 80, OFF_Z = 88, OFF_YAW = 96, OFF_PITCH = 100, OFF_DAYTIME = 104,
        OFF_RAIN = 112, OFF_THUNDER = 116, OFF_IN_WORLD = 120, OFF_SLOT = 124, OFF_ON_GROUND = 128, OFF_FEET_Y = 136,
        OFF_PX = 144, OFF_PY = 152, OFF_PZ = 160, OFF_BODY_YAW = 168, OFF_HEAD_YAW = 172, OFF_HEAD_PITCH = 176,
        OFF_LIMB_POS = 180, OFF_LIMB_AMP = 184, OFF_SWING = 188, OFF_SNEAK = 192, OFF_PLAYER_VALID = 196,
        OFF_WRITE2 = 208, OFF_READ2 = 216;
    // Input block, written by the renderer
    static final int OFF_IN_ACTIVE = 256, OFF_IN_KEYS = 260, OFF_IN_YAW = 264, OFF_IN_PITCH = 268, OFF_IN_SLOT = 272,
        OFF_IN_SLOT_SEQ = 276, OFF_IN_ATTACK = 280, OFF_IN_USE = 284, OFF_IN_PICK = 288, OFF_IN_DROP = 292;
    static final int MSG_CHUNK = 1, MSG_UNLOAD = 2, MSG_BLOCK = 3, MSG_COLLISION = 10;

    static final VarHandle LONG = MethodHandles.byteBufferViewVarHandle(long[].class, ByteOrder.LITTLE_ENDIAN);
    static final VarHandle INT = MethodHandles.byteBufferViewVarHandle(int[].class, ByteOrder.LITTLE_ENDIAN);

    private final MappedByteBuffer buf;
    private final LinkedHashSet<Long> pendingChunks = new LinkedHashSet<>();
    private final ArrayDeque<byte[]> outbox = new ArrayDeque<>(); // serialized, waiting for ring space
    private final Map<BlockState, String> keyCache = new IdentityHashMap<>();
    private final Random random = new Random();
    private ClientWorld lastWorld;
    private boolean readerWasAlive;
    private int seq;
    private ByteBuffer scratch = ByteBuffer.allocate(1 << 20).order(ByteOrder.LITTLE_ENDIAN);

    public Bridge() {
        MappedByteBuffer b = null;
        try {
            // -Dptr.bridge.dir=... gives a separate bridge (used by the isolated test client).
            String custom = System.getProperty("ptr.bridge.dir");
            File dir = custom != null ? new File(custom) : new File(System.getenv().getOrDefault("LOCALAPPDATA", System.getProperty("java.io.tmpdir")), "RealismRenderer");
            dir.mkdirs();
            RandomAccessFile file = new RandomAccessFile(new File(dir, "bridge.bin"), "rw");
            if (file.length() < FILE_SIZE) file.setLength(FILE_SIZE);
            b = file.getChannel().map(FileChannel.MapMode.READ_WRITE, 0, FILE_SIZE);
            b.order(ByteOrder.LITTLE_ENDIAN);
            b.putInt(OFF_MAGIC, MAGIC);
            b.putInt(OFF_VERSION, VERSION);
            b.putLong(OFF_RING, RING);
        } catch (IOException e) {
            System.err.println("[Realism Renderer] could not open bridge file: " + e);
        }
        buf = b;
    }

    // ---- events ----

    public void onChunkLoad(ChunkPos pos) {
        if (buf != null && readerWasAlive) pendingChunks.add(pos.toLong());
    }

    public void onChunkUnload(ChunkPos pos) {
        if (buf == null || !readerWasAlive) return;
        if (!pendingChunks.remove(pos.toLong())) {
            ByteBuffer m = message(MSG_UNLOAD, 8);
            m.putInt(pos.x).putInt(pos.z);
            enqueue(m);
        }
    }

    public void onBlockChanged(BlockPos pos, BlockState state) {
        if (buf == null || !readerWasAlive) return;
        if (pendingChunks.contains(ChunkPos.toLong(pos.getX() >> 4, pos.getZ() >> 4))) return; // full chunk will follow
        String key = key(state);
        ByteBuffer model = models.modelIfNew(MinecraftClient.getInstance(), state);
        if (model != null) enqueue(model); // the renderer needs the model before the block
        byte[] name = key.substring(0, key.indexOf('|')).getBytes(StandardCharsets.UTF_8);
        ByteBuffer m = message(MSG_BLOCK, 12 + 2 + name.length + 1 + 4);
        m.putInt(pos.getX()).putInt(pos.getY()).putInt(pos.getZ());
        m.putShort((short) name.length).put(name).put((byte) level(key)).putInt(net.minecraft.block.Block.getRawIdFromState(state));
        enqueue(m);
    }

    /** Per rendered frame: where the camera is, so the renderer can follow it smoothly. */
    public void writeCamera(Camera camera) {
        MinecraftClient mc = MinecraftClient.getInstance();
        if (buf == null || mc.world == null || camera == null || !camera.isReady()) return;
        // The player's eye, not Minecraft's camera: in F5 views that camera is pulled away from the player, and the
        // renderer places its own third-person camera.
        Vec3d p = mc.player != null ? mc.player.getCameraPosVec(mc.getRenderTickCounter().getTickProgress(true)) : camera.getPos();
        INT.setRelease(buf, OFF_SEQ, ++seq); // odd while writing (seqlock)
        buf.putDouble(OFF_X, p.x).putDouble(OFF_Y, p.y).putDouble(OFF_Z, p.z);
        buf.putFloat(OFF_YAW, camera.getYaw()).putFloat(OFF_PITCH, camera.getPitch());
        buf.putLong(OFF_DAYTIME, mc.world.getTimeOfDay());
        buf.putFloat(OFF_RAIN, mc.world.getRainGradient(1f)).putFloat(OFF_THUNDER, mc.world.getThunderGradient(1f));
        buf.putInt(OFF_IN_WORLD, 1);
        if (mc.player != null) {
            buf.putInt(OFF_SLOT, mc.player.getInventory().getSelectedSlot());
            buf.putInt(OFF_ON_GROUND, mc.player.isOnGround() ? 1 : 0);
            float td = mc.getRenderTickCounter().getTickProgress(true);
            Vec3d feet = mc.player.getLerpedPos(td);
            buf.putDouble(OFF_FEET_Y, feet.y);
            buf.putDouble(OFF_PX, feet.x).putDouble(OFF_PY, feet.y).putDouble(OFF_PZ, feet.z);
            buf.putFloat(OFF_BODY_YAW, net.minecraft.util.math.MathHelper.lerpAngleDegrees(td, mc.player.lastBodyYaw, mc.player.bodyYaw));
            buf.putFloat(OFF_HEAD_YAW, mc.player.getYaw(td)).putFloat(OFF_HEAD_PITCH, mc.player.getPitch(td));
            buf.putFloat(OFF_LIMB_POS, mc.player.limbAnimator.getAnimationProgress(td)).putFloat(OFF_LIMB_AMP, mc.player.limbAnimator.getAmplitude(td));
            buf.putFloat(OFF_SWING, mc.player.getHandSwingProgress(td));
            buf.putInt(OFF_SNEAK, mc.player.isInSneakingPose() ? 1 : 0).putInt(OFF_PLAYER_VALID, 1);
            // The block the crosshair is on (the renderer outlines it) and how far it is mined (-1 = not mining).
            boolean hit = mc.crosshairTarget instanceof net.minecraft.util.hit.BlockHitResult bh && bh.getType() == net.minecraft.util.hit.HitResult.Type.BLOCK;
            buf.putInt(OFF_TARGET, hit ? 1 : 0);
            if (hit) {
                var bhr = (net.minecraft.util.hit.BlockHitResult) mc.crosshairTarget;
                buf.putInt(OFF_TARGET + 4, bhr.getBlockPos().getX()).putInt(OFF_TARGET + 8, bhr.getBlockPos().getY()).putInt(OFF_TARGET + 12, bhr.getBlockPos().getZ());
                buf.putInt(OFF_TARGET + 16, bhr.getSide().ordinal()).putInt(OFF_TARGET + 20, mc.interactionManager.getBlockBreakingProgress());
            }
        }
        INT.setRelease(buf, OFF_SEQ, ++seq); // even: consistent
    }

    public void tick(MinecraftClient mc) {
        if (buf == null) return;
        long now = System.currentTimeMillis();
        LONG.setRelease(buf, OFF_WRITER_HEARTBEAT, now);
        if (mc.world == null) buf.putInt(OFF_IN_WORLD, 0);
        editLog.ensure(mc); // digs and placements are kept whether or not the renderer is connected
        updateWindowVisibility(mc);

        boolean alive = now - (long) LONG.getAcquire(buf, OFF_READER_HEARTBEAT) < 3000;
        updateStepHeight(mc, alive);
        if (alive && mc.world != null && mc.world.getTime() % 20 == 0) sendSkin(mc);
        if (mc.world != null && mc.world.getTime() % 200 == 0) editLog.save();
        SmoothCollision.setEnabled(alive && mc.isIntegratedServerRunning());
        if (!alive) {
            readerWasAlive = false;
            pendingChunks.clear();
            outbox.clear();
            lastWorld = mc.world;
            return;
        }
        // A new renderer instance (or a different world) gets a fresh session: rings reset, world and skin resent.
        long id = buf.getLong(OFF_IN_READER_ID);
        if (!readerWasAlive || mc.world != lastWorld || id != readerId) { readerId = id; startSession(mc); }
        if (mc.world == null) return;

        // Send what's waiting, then serialize more chunks (nearest first) within a small time budget.
        long deadline = System.nanoTime() + 6_000_000;
        while (!outbox.isEmpty() && tryWrite(outbox.peekFirst())) outbox.pollFirst();
        while (outbox.isEmpty() && !pendingChunks.isEmpty() && System.nanoTime() < deadline) {
            long nearest = nearestPending(mc);
            pendingChunks.remove(nearest);
            WorldChunk chunk = mc.world.getChunkManager().getChunk(ChunkPos.getPackedX(nearest), ChunkPos.getPackedZ(nearest), ChunkStatus.FULL, false);
            if (chunk == null) continue;
            byte[] msg = serialize(chunk);
            for (ByteBuffer model : newModels) enqueue(model); // models first: the chunk is meshed with them
            newModels.clear();
            if (!outbox.isEmpty() || !tryWrite(msg)) outbox.add(msg);
        }
    }

    // ---- session ----

    private void startSession(MinecraftClient mc) {
        readerWasAlive = true;
        lastWorld = mc.world;
        pendingChunks.clear();
        outbox.clear();
        LONG.setRelease(buf, OFF_WRITE, 0L);
        LONG.setRelease(buf, OFF_READ, 0L);
        read2 = 0;
        LONG.setRelease(buf, OFF_READ2, 0L);
        LONG.setRelease(buf, OFF_WRITE2, 0L);
        SmoothCollision.reset(mc.world == null ? null : mc.world.getRegistryKey());
        sentSkin = null; // resend the skin in the new session
        entities.reset();
        models.reset();
        if (mc.world != null) { ByteBuffer atlas = models.atlas(mc); if (atlas != null) enqueue(atlas); }
        editLog.load(mc);
        enqueue(editLog.message()); // before any chunk, so they are meshed with the edits applied
        LONG.setRelease(buf, OFF_SESSION, random.nextLong() | 1);
        if (mc.world == null || mc.player == null) return;
        int r = mc.options.getClampedViewDistance() + 2;
        ChunkPos c = mc.player.getChunkPos();
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
                if (mc.world.getChunkManager().getChunk(c.x + dx, c.z + dz, ChunkStatus.FULL, false) != null)
                    pendingChunks.add(ChunkPos.toLong(c.x + dx, c.z + dz));
    }

    private long nearestPending(MinecraftClient mc) {
        ChunkPos c = mc.player != null ? mc.player.getChunkPos() : new ChunkPos(0, 0);
        long best = 0;
        int bestD = Integer.MAX_VALUE;
        for (long k : pendingChunks) {
            int dx = ChunkPos.getPackedX(k) - c.x, dz = ChunkPos.getPackedZ(k) - c.z;
            int d = dx * dx + dz * dz;
            if (d < bestD) { bestD = d; best = k; }
        }
        return best;
    }

    // ---- serialization ----

    /** Palette key: "namespace:id|fluidLevel|stateId" (level -1 for everything but water and lava). */
    private String key(BlockState state) {
        return keyCache.computeIfAbsent(state, s -> Registries.BLOCK.getId(s.getBlock()) + "|"
            + ((s.getBlock() == Blocks.WATER || s.getBlock() == Blocks.LAVA) && s.contains(Properties.LEVEL_15) ? s.get(Properties.LEVEL_15) : -1)
            + "|" + net.minecraft.block.Block.getRawIdFromState(s));
    }

    private static int level(String key) {
        int a = key.indexOf('|');
        return Integer.parseInt(key.substring(a + 1, key.indexOf('|', a + 1)));
    }

    private static int stateId(String key) {
        return Integer.parseInt(key.substring(key.lastIndexOf('|') + 1));
    }

    private byte[] serialize(WorldChunk chunk) {
        ChunkSection[] sections = chunk.getSectionArray();
        ByteBuffer m = scratch;
        m.clear();
        m.putInt(MSG_CHUNK).putInt(0); // length patched below
        m.putInt(chunk.getPos().x).putInt(chunk.getPos().z).putInt(chunk.getBottomSectionCoord()).putInt(sections.length);
        Map<String, Integer> palette = new HashMap<>();
        String[] names = new String[4096];
        BlockState[] states = new BlockState[4096];
        MinecraftClient mc = MinecraftClient.getInstance();
        short[] indices = new short[4096];
        for (ChunkSection section : sections) {
            ensure(4 + 2 + 4096 * 2 + 64 * 1024);
            m = scratch;
            if (section == null || section.isEmpty()) { m.put((byte) 0); continue; }
            palette.clear();
            int count = 0;
            for (int y = 0; y < 16; y++)
                for (int z = 0; z < 16; z++)
                    for (int x = 0; x < 16; x++) {
                        String k = key(section.getBlockState(x, y, z));
                        Integer idx = palette.get(k);
                        if (idx == null) { idx = count; palette.put(k, count); states[count] = section.getBlockState(x, y, z); names[count++] = k; }
                        indices[(y << 8) | (z << 4) | x] = (short) (int) idx;
                    }
            m.put((byte) 1).putShort((short) count);
            for (int i = 0; i < count; i++) {
                String k = names[i];
                byte[] name = k.substring(0, k.indexOf('|')).getBytes(StandardCharsets.UTF_8);
                ensure(name.length + 7);
                m = scratch;
                m.putShort((short) name.length).put(name).put((byte) level(k)).putInt(stateId(k));
                ByteBuffer model = models.modelIfNew(mc, states[i]);
                if (model != null) newModels.add(model);
            }
            if (count <= 256) for (short s : indices) m.put((byte) s);
            else for (short s : indices) m.putShort(s);
        }
        m.putInt(4, m.position() - 8);
        byte[] out = new byte[m.position()];
        m.flip();
        m.get(out);
        return out;
    }

    private void ensure(int extra) {
        if (scratch.remaining() >= extra) return;
        ByteBuffer bigger = ByteBuffer.allocate(Math.max(scratch.capacity() * 2, scratch.position() + extra)).order(ByteOrder.LITTLE_ENDIAN);
        scratch.flip();
        bigger.put(scratch);
        scratch = bigger;
    }

    private static ByteBuffer message(int type, int payload) {
        ByteBuffer m = ByteBuffer.allocate(8 + payload).order(ByteOrder.LITTLE_ENDIAN);
        return m.putInt(type).putInt(payload);
    }

    private void enqueue(ByteBuffer m) {
        byte[] bytes = m.array();
        if (!outbox.isEmpty() || !tryWrite(bytes)) outbox.add(bytes);
    }

    // ---- ring buffer ----

    private boolean tryWrite(byte[] msg) {
        long write = (long) LONG.getAcquire(buf, OFF_WRITE);
        long read = (long) LONG.getAcquire(buf, OFF_READ);
        long used = write - read;
        if (used < 0 || used > RING) used = write; // reader hasn't caught up with a new session yet
        if (RING - used < msg.length) return false;
        int pos = (int) (write % RING);
        int first = (int) Math.min(msg.length, RING - pos);
        buf.put(HEADER + pos, msg, 0, first);
        if (first < msg.length) buf.put(HEADER, msg, first, msg.length - first);
        LONG.setRelease(buf, OFF_WRITE, write + msg.length);
        return true;
    }

    // ---- input from the renderer (two-way control) ----

    private int lastAttack, lastUse, lastPick, lastDrop, lastSlotSeq;
    private boolean controlWasActive;

    /** True while the renderer window has the mouse and is driving the player. */
    public boolean controlActive() {
        if (buf == null) return false;
        long now = System.currentTimeMillis();
        return buf.getInt(OFF_IN_ACTIVE) == 1 && now - (long) LONG.getAcquire(buf, OFF_READER_HEARTBEAT) < 1000;
    }

    /** Start of each client tick, before Minecraft handles input: press the keys the renderer has held. */
    public void applyInput(MinecraftClient mc) {
        boolean active = controlActive();
        GameOptions o = mc.options;
        if (!active) {
            if (controlWasActive) for (KeyBinding k : new KeyBinding[] { o.forwardKey, o.backKey, o.leftKey, o.rightKey, o.jumpKey, o.sneakKey, o.sprintKey, o.attackKey, o.useKey }) k.setPressed(false);
            controlWasActive = false;
            return;
        }
        if (!controlWasActive) {
            // Keep running at full speed while the Minecraft window is in the background.
            controlWasActive = true;
            o.pauseOnLostFocus = false;
            o.getInactivityFpsLimit().setValue(InactivityFpsLimit.MINIMIZED);
            lastAttack = buf.getInt(OFF_IN_ATTACK);
            lastUse = buf.getInt(OFF_IN_USE);
            lastPick = buf.getInt(OFF_IN_PICK);
            lastDrop = buf.getInt(OFF_IN_DROP);
            lastSlotSeq = buf.getInt(OFF_IN_SLOT_SEQ);
            if (mc.currentScreen instanceof GameMenuScreen) mc.setScreen(null); // opened by the focus loss when switching windows
        }
        if (mc.player == null) return;

        // Movement only while no screen is open (typing in chat must not walk).
        int keys = mc.currentScreen == null ? buf.getInt(OFF_IN_KEYS) : 0;
        o.forwardKey.setPressed((keys & 1) != 0);
        o.backKey.setPressed((keys & 2) != 0);
        o.leftKey.setPressed((keys & 4) != 0);
        o.rightKey.setPressed((keys & 8) != 0);
        o.jumpKey.setPressed((keys & 16) != 0);
        o.sneakKey.setPressed((keys & 32) != 0);
        o.sprintKey.setPressed((keys & 64) != 0);
        o.attackKey.setPressed((keys & 128) != 0);
        o.useKey.setPressed((keys & 256) != 0);
        lastAttack = clicks(o.attackKey, OFF_IN_ATTACK, lastAttack);
        lastUse = clicks(o.useKey, OFF_IN_USE, lastUse);
        lastPick = clicks(o.pickItemKey, OFF_IN_PICK, lastPick);
        lastDrop = clicks(o.dropKey, OFF_IN_DROP, lastDrop);

        int slotSeq = buf.getInt(OFF_IN_SLOT_SEQ);
        if (slotSeq != lastSlotSeq) {
            lastSlotSeq = slotSeq;
            int slot = buf.getInt(OFF_IN_SLOT);
            if (slot >= 0 && slot < 9) mc.player.getInventory().setSelectedSlot(slot);
        }
    }

    /** Short clicks between ticks are counted, so none are lost even if the button is already released. */
    private int clicks(KeyBinding binding, int offset, int last) {
        int now = buf.getInt(offset);
        for (int i = 0; i < Math.min(now - last, 5); i++) KeyBinding.onKeyPressed(KeyBindingHelper.getBoundKeyOf(binding));
        return now;
    }

    /** Every frame: the renderer turns the view itself (no round-trip lag); Minecraft follows. */
    public void applyLook(MinecraftClient mc) {
        if (mc.player == null || !controlActive()) return;
        float pitch = Math.max(-90f, Math.min(90f, buf.getFloat(OFF_IN_PITCH)));
        // The renderer sends yaw wrapped to -180..180; Minecraft's yaw is continuous. Unwrap it next to the current
        // yaw, or looking across south flips it by 360 and the held item's sway (which lags the yaw) spins wildly.
        float current = mc.player.getYaw();
        float yaw = current + net.minecraft.util.math.MathHelper.wrapDegrees(buf.getFloat(OFF_IN_YAW) - current);
        mc.player.setYaw(yaw);
        mc.player.setPitch(pitch);
        mc.player.lastYaw = yaw;
        mc.player.lastPitch = pitch;
    }

    // ---- smooth-terrain movement ----

    private static final double SMOOTH_STEP = 1.0, VANILLA_STEP = 0.6;

    /**
     * The renderer shows smooth hills, so walking should feel like it: while it is connected (singleplayer),
     * the player steps up a full block without jumping. Set on the integrated server too, so the server agrees.
     */
    private void updateStepHeight(MinecraftClient mc, boolean rendererAlive) {
        if (mc.player == null) return;
        double want = rendererAlive && mc.isIntegratedServerRunning() ? SMOOTH_STEP : VANILLA_STEP;
        EntityAttributeInstance attr = mc.player.getAttributeInstance(EntityAttributes.STEP_HEIGHT);
        if (attr == null || attr.getBaseValue() == want) return;
        attr.setBaseValue(want);
        IntegratedServer server = mc.getServer();
        if (server != null) {
            java.util.UUID id = mc.player.getUuid();
            server.execute(() -> {
                var sp = server.getPlayerManager().getPlayer(id);
                if (sp != null && sp.getAttributeInstance(EntityAttributes.STEP_HEIGHT) != null)
                    sp.getAttributeInstance(EntityAttributes.STEP_HEIGHT).setBaseValue(want);
            });
        }
    }

    // ---- renderer -> mod ring: smooth collision per chunk ----

    private long read2;

    /** Applies smooth-collision messages the renderer has written (within a small time budget). */
    private void readCollision(MinecraftClient mc, long sessionId) {
        long write2 = (long) LONG.getAcquire(buf, OFF_WRITE2);
        if (write2 < read2) return; // renderer hasn't caught up with the new session yet
        long deadline = System.nanoTime() + 2_000_000;
        byte[] head = new byte[8];
        while (read2 + 8 <= write2 && System.nanoTime() < deadline) {
            copy2(read2, head);
            ByteBuffer h = ByteBuffer.wrap(head).order(ByteOrder.LITTLE_ENDIAN);
            int type = h.getInt(), len = h.getInt();
            if (len < 0 || len > RING2) { read2 = write2; break; }
            byte[] payload = new byte[len];
            copy2(read2 + 8, payload);
            read2 += 8 + len;
            if (type == MSG_INPUT) {
                handleInputEvent(mc, ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN));
                continue;
            }
            if (type == MSG_COLLISION) {
                ByteBuffer p = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
                if (p.getLong() != sessionId) continue; // left over from a previous world
                int cx = p.getInt(), cz = p.getInt();
                short[] tops = new short[256], heights = new short[4096];
                p.asShortBuffer().get(tops).get(heights);
                p.position(p.position() + (256 + 4096) * 2);
                // Floors below the tops (caves): int count; per floor short column, short Y, short heights[16].
                int n = p.remaining() >= 4 ? p.getInt() : 0;
                short[] cols = new short[n], ys = new short[n], fh = new short[n * 16];
                for (int i = 0; i < n; i++) {
                    cols[i] = p.getShort();
                    ys[i] = p.getShort();
                    for (int k = 0; k < 16; k++) fh[i * 16 + k] = p.getShort();
                }
                SmoothCollision.put(cx, cz, tops, heights, cols, ys, fh);
            }
        }
        LONG.setRelease(buf, OFF_READ2, read2);
    }

    private void copy2(long pos, byte[] dest) {
        int start = (int) (pos % RING2);
        int first = (int) Math.min(dest.length, RING2 - start);
        buf.get(HEADER + (int) RING + start, dest, 0, first);
        if (first < dest.length) buf.get(HEADER + (int) RING, dest, first, dest.length - first);
    }

    // ---- per frame: UI capture, renderer input events, window size, skin ----

    static final long GUI_SLOT = 3840L * 2160 * 4;     // two RGBA slots after Ring2, written alternately
    static final long GUI_BASE = HEADER + RING + RING2;
    static final long ENT_SLOT = 4L << 20;                 // two entity snapshots after the UI frames
    static final long ENT_BASE = GUI_BASE + 2 * GUI_SLOT;
    static final long FILE_SIZE = ENT_BASE + 2 * ENT_SLOT;
    static final int OFF_ENT_FRAME = 320, OFF_ENT_SLOT = 328, OFF_TARGET = 336; // target: has, x, y, z, side, break stage
    static final int OFF_HIDE_TOGGLE = 368; // int, renderer -> mod: bumped to show/hide Minecraft's window (F8)
    static final int OFF_GUI_STRAIGHT = 364; // 1: the UI frame is straight alpha over Minecraft's world (playing in its window)
    static final int OFF_FOV = 360; // vertical field of view in degrees, as Minecraft draws the world this frame

    public void writeFov(float degrees) { buf.putFloat(OFF_FOV, degrees); }
    private final EntitySync entities = new EntitySync();
    private final EditLog editLog = new EditLog();
    private final BlockModelExport models = new BlockModelExport();
    private final java.util.List<ByteBuffer> newModels = new java.util.ArrayList<>(); // from the last serialize()

    public void recordEdit(BlockPos pos) { editLog.record(pos.asLong()); }
    private long entFrame;
    static final int MSG_SKIN = 4, MSG_INPUT = 11;
    static final int OFF_GUI_W = 224, OFF_GUI_H = 228, OFF_GUI_FRAME = 232, OFF_GUI_SLOT = 240, OFF_SCREEN_OPEN = 244,
        OFF_THIRD_PERSON = 248, OFF_IN_WIN_W = 296, OFF_IN_WIN_H = 300, OFF_IN_READER_ID = 304;
    private long readerId;

    private long guiFrame;
    private int requestedW, requestedH;
    private net.minecraft.util.Identifier sentSkin;

    /** Start of every frame: renderer -> mod messages (collision, input events), window size. */
    public void beforeFrame(MinecraftClient mc) {
        if (buf == null || !readerWasAlive) return;
        readCollision(mc, (long) LONG.getAcquire(buf, OFF_SESSION));
        // Match the renderer window size so the captured UI maps onto it pixel for pixel.
        int w = buf.getInt(OFF_IN_WIN_W), h = buf.getInt(OFF_IN_WIN_H);
        // (Also while you play in Minecraft's own window, so side-by-side recordings of the two match exactly.)
        if (w > 0 && h > 0 && (w != requestedW || h != requestedH)) {
            requestedW = w;
            requestedH = h;
            org.lwjgl.glfw.GLFW.glfwSetWindowSize(mc.getWindow().getHandle(), w, h);
        }
    }

    private boolean windowHiddenByUs;
    private int lastHideToggle = Integer.MIN_VALUE;

    /**
     * Minecraft's own window, while the renderer runs: hidden if the setting says so (F8 in the renderer toggles it),
     * shown again as soon as the renderer closes. Minimizing it hides it instead: a minimized window stops drawing
     * the hand, HUD and menus the renderer shows (a hidden one keeps drawing at full speed).
     */
    private void updateWindowVisibility(MinecraftClient mc) {
        if (System.getProperty("ptr.test") != null) return; // the test client's window is always hidden
        long win = mc.getWindow().getHandle();
        boolean alive = readerAlive();
        var config = RealismConfig.get();
        int toggle = buf.getInt(OFF_HIDE_TOGGLE);
        if (lastHideToggle == Integer.MIN_VALUE || !alive) lastHideToggle = toggle;
        if (toggle != lastHideToggle) {
            lastHideToggle = toggle;
            config.hideMinecraftWindow = !config.hideMinecraftWindow;
            config.save();
        }
        if (alive && org.lwjgl.glfw.GLFW.glfwGetWindowAttrib(win, org.lwjgl.glfw.GLFW.GLFW_ICONIFIED) == org.lwjgl.glfw.GLFW.GLFW_TRUE) {
            org.lwjgl.glfw.GLFW.glfwRestoreWindow(win);
            if (!config.hideMinecraftWindow) {
                config.hideMinecraftWindow = true;
                config.save();
            }
        }
        boolean hide = alive && config.hideMinecraftWindow;
        if (hide && !windowHiddenByUs) {
            org.lwjgl.glfw.GLFW.glfwHideWindow(win);
            windowHiddenByUs = true;
        } else if (!hide && windowHiddenByUs) {
            org.lwjgl.glfw.GLFW.glfwShowWindow(win);
            windowHiddenByUs = false;
        }
    }

    /** True while the renderer is running (its heartbeat is fresh), whether or not it has the controls. */
    public boolean readerAlive() {
        return buf != null && readerWasAlive && System.currentTimeMillis() - (long) LONG.getAcquire(buf, OFF_READER_HEARTBEAT) < 1000;
    }

    private int alphaFbo;

    /**
     * Playing in Minecraft's own window with the renderer following: Minecraft draws its world (its window shows
     * vanilla), then this clears just the alpha channel, so the captured frame marks only what's drawn after it, the
     * hand and HUD, as covering. The window looks the same (it ignores alpha). Called once the world is drawn.
     */
    public void clearWorldAlpha(MinecraftClient mc) {
        int tex = ((com.realismrenderer.mixin.GlTextureAccessor) mc.getFramebuffer().getColorAttachment()).realism$getGlId();
        int prev = org.lwjgl.opengl.GL11.glGetInteger(org.lwjgl.opengl.GL30.GL_FRAMEBUFFER_BINDING);
        if (alphaFbo == 0) alphaFbo = org.lwjgl.opengl.GL30.glGenFramebuffers();
        org.lwjgl.opengl.GL30.glBindFramebuffer(org.lwjgl.opengl.GL30.GL_FRAMEBUFFER, alphaFbo);
        org.lwjgl.opengl.GL30.glFramebufferTexture2D(org.lwjgl.opengl.GL30.GL_FRAMEBUFFER, org.lwjgl.opengl.GL30.GL_COLOR_ATTACHMENT0, org.lwjgl.opengl.GL11.GL_TEXTURE_2D, tex, 0);
        boolean scissor = org.lwjgl.opengl.GL11.glIsEnabled(org.lwjgl.opengl.GL11.GL_SCISSOR_TEST);
        var maskBuf = org.lwjgl.BufferUtils.createByteBuffer(16);
        org.lwjgl.opengl.GL11.glGetBooleanv(org.lwjgl.opengl.GL11.GL_COLOR_WRITEMASK, maskBuf);
        var clear = org.lwjgl.BufferUtils.createFloatBuffer(16);
        org.lwjgl.opengl.GL11.glGetFloatv(org.lwjgl.opengl.GL11.GL_COLOR_CLEAR_VALUE, clear);
        org.lwjgl.opengl.GL11.glDisable(org.lwjgl.opengl.GL11.GL_SCISSOR_TEST);
        org.lwjgl.opengl.GL11.glColorMask(false, false, false, true);
        org.lwjgl.opengl.GL11.glClearColor(0, 0, 0, 0);
        org.lwjgl.opengl.GL11.glClear(org.lwjgl.opengl.GL11.GL_COLOR_BUFFER_BIT);
        org.lwjgl.opengl.GL11.glColorMask(maskBuf.get(0) != 0, maskBuf.get(1) != 0, maskBuf.get(2) != 0, maskBuf.get(3) != 0);
        org.lwjgl.opengl.GL11.glClearColor(clear.get(0), clear.get(1), clear.get(2), clear.get(3));
        if (scissor) org.lwjgl.opengl.GL11.glEnable(org.lwjgl.opengl.GL11.GL_SCISSOR_TEST);
        org.lwjgl.opengl.GL30.glBindFramebuffer(org.lwjgl.opengl.GL30.GL_FRAMEBUFFER, prev);
    }

    private int handProgram, restoreProgram, handVao, handFbo, savedFbo, savedTex, savedW, savedH;
    private boolean handSaved; // this frame's hand mask is in savedTex

    /**
     * After Minecraft draws the first-person hand (playing in its own window, renderer following): Minecraft's hand
     * doesn't write alpha, so mark it. The hand is drawn just after the depth buffer is cleared, so any pixel with depth
     * below the far plane is the hand (or what it holds): a full-screen triangle just in front of the far plane, passing
     * only where something is nearer, sets alpha there. The HUD's vignette later wipes alpha across the screen and the
     * depth is cleared before the HUD, so the marked frame is saved and its hand alpha put back at capture (restoreHand).
     */
    public void markHand(MinecraftClient mc) {
        var fb = mc.getFramebuffer();
        if (fb.getDepthAttachment() == null) return;
        alphaPass(fb, true);
    }

    /** Before the frame is captured: the saved hand mask's alpha back into it (see markHand). */
    private void restoreHand(MinecraftClient mc) {
        if (!handSaved) return;
        handSaved = false;
        alphaPass(mc.getFramebuffer(), false);
    }

    private void alphaPass(net.minecraft.client.gl.Framebuffer fb, boolean hand) {
        int w = fb.textureWidth, h = fb.textureHeight;
        int color = ((com.realismrenderer.mixin.GlTextureAccessor) fb.getColorAttachment()).realism$getGlId();
        int depth = ((com.realismrenderer.mixin.GlTextureAccessor) fb.getDepthAttachment()).realism$getGlId();
        if (handProgram == 0) {
            String vs = "#version 150\nvoid main() { vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2) * 2.0 - 1.0; gl_Position = vec4(p, 0.9999, 1.0); }";
            handProgram = link(vs, "#version 150\nout vec4 c; void main() { c = vec4(0.0, 0.0, 0.0, 1.0); }");
            restoreProgram = link(vs, "#version 150\nuniform sampler2D saved; out vec4 c;\n"
                + "void main() { if (texelFetch(saved, ivec2(gl_FragCoord.xy), 0).a < 0.5) discard; c = vec4(0.0, 0.0, 0.0, 1.0); }");
            handVao = GL30.glGenVertexArrays();
            handFbo = GL30.glGenFramebuffers();
            savedFbo = GL30.glGenFramebuffers();
            savedTex = GL11.glGenTextures();
        }
        // Save what this changes, so Minecraft's own state tracking stays right.
        int prevRead = GL11.glGetInteger(GL30.GL_READ_FRAMEBUFFER_BINDING);
        int prevDraw = GL11.glGetInteger(GL30.GL_DRAW_FRAMEBUFFER_BINDING);
        int prevProgram = GL11.glGetInteger(GL20.GL_CURRENT_PROGRAM);
        int prevVao = GL11.glGetInteger(GL30.GL_VERTEX_ARRAY_BINDING);
        int prevDepthFunc = GL11.glGetInteger(GL11.GL_DEPTH_FUNC);
        int prevActive = GL11.glGetInteger(GL13.GL_ACTIVE_TEXTURE);
        GL13.glActiveTexture(GL13.GL_TEXTURE15);
        int prevTex15 = GL11.glGetInteger(GL11.GL_TEXTURE_BINDING_2D);
        boolean depthTest = GL11.glIsEnabled(GL11.GL_DEPTH_TEST);
        boolean blend = GL11.glIsEnabled(GL11.GL_BLEND);
        boolean cull = GL11.glIsEnabled(GL11.GL_CULL_FACE);
        boolean scissor = GL11.glIsEnabled(GL11.GL_SCISSOR_TEST);
        boolean depthWrite = GL11.glGetBoolean(GL11.GL_DEPTH_WRITEMASK);
        var mask = org.lwjgl.BufferUtils.createByteBuffer(16);
        GL11.glGetBooleanv(GL11.GL_COLOR_WRITEMASK, mask);
        var viewport = org.lwjgl.BufferUtils.createIntBuffer(16);
        GL11.glGetIntegerv(GL11.GL_VIEWPORT, viewport);

        GL30.glBindFramebuffer(GL30.GL_FRAMEBUFFER, handFbo);
        GL30.glFramebufferTexture2D(GL30.GL_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0, GL11.GL_TEXTURE_2D, color, 0);
        GL30.glFramebufferTexture2D(GL30.GL_FRAMEBUFFER, GL30.GL_DEPTH_ATTACHMENT, GL11.GL_TEXTURE_2D, hand ? depth : 0, 0);
        GL11.glViewport(0, 0, w, h);
        GL11.glDepthMask(false);
        GL11.glDisable(GL11.GL_BLEND);
        GL11.glDisable(GL11.GL_CULL_FACE);
        GL11.glDisable(GL11.GL_SCISSOR_TEST);
        GL11.glColorMask(false, false, false, true);
        GL30.glBindVertexArray(handVao);
        if (hand) {
            GL11.glEnable(GL11.GL_DEPTH_TEST);
            GL11.glDepthFunc(GL11.GL_GREATER);
            GL20.glUseProgram(handProgram);
            GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
            // Keep the marked frame for restoreHand.
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, savedTex);
            if (w != savedW || h != savedH) {
                savedW = w;
                savedH = h;
                GL11.glTexImage2D(GL11.GL_TEXTURE_2D, 0, GL11.GL_RGBA8, w, h, 0, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, (java.nio.ByteBuffer) null);
                GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MIN_FILTER, GL11.GL_NEAREST);
                GL11.glTexParameteri(GL11.GL_TEXTURE_2D, GL11.GL_TEXTURE_MAG_FILTER, GL11.GL_NEAREST);
                GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, savedFbo);
                GL30.glFramebufferTexture2D(GL30.GL_DRAW_FRAMEBUFFER, GL30.GL_COLOR_ATTACHMENT0, GL11.GL_TEXTURE_2D, savedTex, 0);
            }
            GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, handFbo);
            GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, savedFbo);
            GL11.glColorMask(true, true, true, true);
            GL30.glBlitFramebuffer(0, 0, w, h, 0, 0, w, h, GL11.GL_COLOR_BUFFER_BIT, GL11.GL_NEAREST);
            handSaved = true;
        } else {
            GL11.glDisable(GL11.GL_DEPTH_TEST);
            GL20.glUseProgram(restoreProgram);
            GL11.glBindTexture(GL11.GL_TEXTURE_2D, savedTex);
            GL20.glUniform1i(GL20.glGetUniformLocation(restoreProgram, "saved"), 15);
            GL11.glDrawArrays(GL11.GL_TRIANGLES, 0, 3);
        }

        GL11.glBindTexture(GL11.GL_TEXTURE_2D, prevTex15);
        GL13.glActiveTexture(prevActive);
        GL30.glBindVertexArray(prevVao);
        GL20.glUseProgram(prevProgram);
        GL11.glColorMask(mask.get(0) != 0, mask.get(1) != 0, mask.get(2) != 0, mask.get(3) != 0);
        GL11.glDepthMask(depthWrite);
        GL11.glDepthFunc(prevDepthFunc);
        if (depthTest) GL11.glEnable(GL11.GL_DEPTH_TEST); else GL11.glDisable(GL11.GL_DEPTH_TEST);
        if (blend) GL11.glEnable(GL11.GL_BLEND);
        if (cull) GL11.glEnable(GL11.GL_CULL_FACE);
        if (scissor) GL11.glEnable(GL11.GL_SCISSOR_TEST);
        GL11.glViewport(viewport.get(0), viewport.get(1), viewport.get(2), viewport.get(3));
        GL30.glBindFramebuffer(GL30.GL_READ_FRAMEBUFFER, prevRead);
        GL30.glBindFramebuffer(GL30.GL_DRAW_FRAMEBUFFER, prevDraw);
    }

    private static int link(String vertex, String fragment) {
        int p = GL20.glCreateProgram();
        GL20.glAttachShader(p, compile(GL20.GL_VERTEX_SHADER, vertex));
        GL20.glAttachShader(p, compile(GL20.GL_FRAGMENT_SHADER, fragment));
        GL20.glLinkProgram(p);
        if (GL20.glGetProgrami(p, GL20.GL_LINK_STATUS) == 0)
            System.err.println("[Realism Renderer] hand mask program: " + GL20.glGetProgramInfoLog(p));
        return p;
    }

    private static int compile(int type, String src) {
        int s = org.lwjgl.opengl.GL20.glCreateShader(type);
        org.lwjgl.opengl.GL20.glShaderSource(s, src);
        org.lwjgl.opengl.GL20.glCompileShader(s);
        if (org.lwjgl.opengl.GL20.glGetShaderi(s, org.lwjgl.opengl.GL20.GL_COMPILE_STATUS) == 0)
            System.err.println("[Realism Renderer] hand mask shader: " + org.lwjgl.opengl.GL20.glGetShaderInfoLog(s));
        return s;
    }

    /**
     * End of every frame: copy Minecraft's UI (HUD, hand, screens) into shared memory for the renderer. While the
     * renderer has the controls the frame holds only the UI (premultiplied); while you play in Minecraft's window it
     * also holds the world, with alpha marking the UI (see clearWorldAlpha), flagged as straight alpha.
     */
    public void captureGui(MinecraftClient mc) {
        if (buf == null || !readerAlive()) return;
        if (!controlActive()) restoreHand(mc);
        var fb = mc.getFramebuffer();
        int w = fb.textureWidth, h = fb.textureHeight;
        if (w <= 0 || h <= 0 || (long) w * h * 4 > GUI_SLOT) return;
        int slot = (int) ((guiFrame + 1) & 1);
        int id = ((com.realismrenderer.mixin.GlTextureAccessor) fb.getColorAttachment()).realism$getGlId();
        ByteBuffer dst = buf.slice((int) (GUI_BASE + slot * GUI_SLOT), w * h * 4);
        com.mojang.blaze3d.opengl.GlStateManager._bindTexture(id);
        org.lwjgl.opengl.GL11.glPixelStorei(org.lwjgl.opengl.GL11.GL_PACK_ALIGNMENT, 4);
        org.lwjgl.opengl.GL11.glGetTexImage(org.lwjgl.opengl.GL11.GL_TEXTURE_2D, 0, org.lwjgl.opengl.GL11.GL_RGBA, org.lwjgl.opengl.GL11.GL_UNSIGNED_BYTE, dst);
        com.mojang.blaze3d.opengl.GlStateManager._bindTexture(0);
        buf.putInt(OFF_GUI_W, w).putInt(OFF_GUI_H, h).putInt(OFF_GUI_SLOT, slot);
        buf.putInt(OFF_SCREEN_OPEN, mc.currentScreen != null ? 1 : 0);
        buf.putInt(OFF_THIRD_PERSON, mc.options.getPerspective().isFirstPerson() ? 0 : 1);
        buf.putInt(OFF_GUI_STRAIGHT, controlActive() ? 0 : 1);
        LONG.setRelease(buf, OFF_GUI_FRAME, ++guiFrame);
    }

    /** Applies one input event typed/clicked in the renderer window (used while a Minecraft screen is open). */
    private void handleInputEvent(MinecraftClient mc, ByteBuffer p) {
        long win = mc.getWindow().getHandle();
        int kind = p.getInt();
        switch (kind) {
            case 0 -> mc.keyboard.onKey(win, p.getInt(), p.getInt(), p.getInt(), p.getInt());
            case 1 -> ((com.realismrenderer.mixin.KeyboardInvoker) mc.keyboard).realism$onChar(win, p.getInt(), p.getInt());
            case 2 -> ((com.realismrenderer.mixin.MouseInvoker) mc.mouse).realism$onMouseButton(win, p.getInt(), p.getInt(), p.getInt());
            case 3 -> ((com.realismrenderer.mixin.MouseInvoker) mc.mouse).realism$onCursorPos(win, p.getFloat(), p.getFloat());
            case 4 -> ((com.realismrenderer.mixin.MouseInvoker) mc.mouse).realism$onMouseScroll(win, p.getFloat(), p.getFloat());
            default -> { }
        }
    }

    /** Sends the player's skin (PNG) and arm model when it changes, so the renderer can draw the real character. */
    private void sendSkin(MinecraftClient mc) {
        if (mc.player == null) return;
        var skin = mc.player.getSkinTextures();
        if (skin.texture().equals(sentSkin)) return;
        byte[] png = null;
        try {
            var res = mc.getResourceManager().getResource(skin.texture());
            if (res.isPresent()) try (var in = res.get().getInputStream()) { png = in.readAllBytes(); }
        } catch (IOException ignored) { }
        if (png == null && skin.texture().getPath().startsWith("skins/")) {
            // Downloaded skins live in the skin cache: assets/skins/<first two hex>/<hash>
            String hash = skin.texture().getPath().substring(6);
            for (File base : new File[] { new File(mc.runDirectory, "assets/skins"),
                    new File(System.getenv().getOrDefault("APPDATA", ""), ".minecraft/assets/skins") }) {
                File f = new File(base, hash.substring(0, Math.min(2, hash.length())) + "/" + hash);
                if (f.isFile()) try { png = java.nio.file.Files.readAllBytes(f.toPath()); break; } catch (IOException ignored) { }
            }
        }
        if (png == null) return; // not available yet (still downloading): try again next second
        ByteBuffer m = message(MSG_SKIN, 8 + png.length);
        m.putInt(skin.model() == net.minecraft.client.util.SkinTextures.Model.SLIM ? 1 : 0).putInt(png.length).put(png);
        enqueue(m);
        sentSkin = skin.texture();
    }

    /** End of every frame: mobs and dropped items near the player, posed by Minecraft (see EntitySync). */
    public void writeEntities(MinecraftClient mc) {
        if (buf == null || !readerWasAlive || mc.world == null || mc.player == null) return;
        int slot = (int) ((entFrame + 1) & 1);
        ByteBuffer out = buf.slice((int) (ENT_BASE + slot * ENT_SLOT), (int) ENT_SLOT).order(ByteOrder.LITTLE_ENDIAN);
        float td = mc.getRenderTickCounter().getTickProgress(true);
        entities.write(mc, td, out, m -> enqueue(ByteBuffer.wrap(java.util.Arrays.copyOf(m.array(), m.limit())).order(ByteOrder.LITTLE_ENDIAN)));
        buf.putInt(OFF_ENT_SLOT, slot);
        LONG.setRelease(buf, OFF_ENT_FRAME, ++entFrame);
    }
}
