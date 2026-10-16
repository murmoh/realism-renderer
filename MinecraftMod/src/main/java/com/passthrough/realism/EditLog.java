package com.passthrough.realism;

import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Files;
import java.nio.file.Path;

import it.unimi.dsi.fastutil.longs.LongOpenHashSet;
import net.minecraft.client.MinecraftClient;
import net.minecraft.util.WorldSavePath;

/**
 * Every block that went between empty and filled in play (digging, placing), kept per world and dimension and
 * saved next to the world (passthrough_edits_<dimension>.bin) so it survives restarts. The renderer draws these
 * spots exactly (crisp placed blocks, open tunnels) instead of smoothing them away.
 */
public final class EditLog {
    private final LongOpenHashSet edits = new LongOpenHashSet();
    private Path file;
    private boolean dirty;

    /** Loads the log for the current world/dimension (singleplayer saves; otherwise in memory only). */
    public void load(MinecraftClient mc) {
        ensure(mc);
    }

    /**
     * Called every tick: attaches the log to the world being played as soon as it is joined (not only once the
     * renderer connects), so digs and placements made without the renderer are kept too. Edits recorded before
     * the world was known belong to it and are merged in.
     */
    public void ensure(MinecraftClient mc) {
        if (mc.world == null || mc.getServer() == null) return;
        String dim = mc.world.getRegistryKey().getValue().getPath();
        Path target = mc.getServer().getSavePath(WorldSavePath.ROOT).resolve("passthrough_edits_" + dim + ".bin");
        if (target.equals(file)) return;
        save(); // the previous world's edits
        LongOpenHashSet early = file == null ? new LongOpenHashSet(edits) : new LongOpenHashSet();
        edits.clear();
        file = target;
        try {
            if (Files.exists(file)) {
                ByteBuffer b = ByteBuffer.wrap(Files.readAllBytes(file)).order(ByteOrder.LITTLE_ENDIAN);
                while (b.remaining() >= 8) edits.add(b.getLong());
            }
        } catch (IOException e) {
            System.err.println("[PassThrough Realism] couldn't read " + file + ": " + e);
        }
        edits.addAll(early);
        dirty = !early.isEmpty();
    }

    public void record(long pos) {
        if (edits.add(pos)) dirty = true;
    }

    public void save() {
        if (!dirty || file == null) return;
        ByteBuffer b = ByteBuffer.allocate(edits.size() * 8).order(ByteOrder.LITTLE_ENDIAN);
        edits.forEach((long p) -> b.putLong(p));
        try {
            Files.write(file, b.array());
            dirty = false;
        } catch (IOException e) {
            System.err.println("[PassThrough Realism] couldn't save " + file + ": " + e);
        }
    }

    /** Message 7 EDITS: int count, then BlockPos.asLong() values. */
    public ByteBuffer message() {
        ByteBuffer m = ByteBuffer.allocate(12 + edits.size() * 8).order(ByteOrder.LITTLE_ENDIAN);
        m.putInt(7).putInt(4 + edits.size() * 8).putInt(edits.size());
        edits.forEach((long p) -> m.putLong(p));
        m.flip();
        return m;
    }
}
