package com.passthrough.realism;

import java.util.concurrent.ConcurrentHashMap;

import net.minecraft.block.BlockState;
import net.minecraft.block.Blocks;
import net.minecraft.registry.RegistryKey;
import net.minecraft.util.math.BlockPos;
import net.minecraft.util.math.ChunkPos;
import net.minecraft.util.shape.VoxelShape;
import net.minecraft.util.shape.VoxelShapes;
import net.minecraft.world.World;

/**
 * Collision that matches the smooth terrain the renderer draws.
 *
 * For each chunk the renderer sends, per block column, the top natural block T and the smooth surface height at
 * 4x4 points across the column (1/16 block units). For the player only, the blocks T-4 .. T+2 of that column
 * collide as a stack of quarter-block columns rising to that surface: rounded hill-tops dip into block T, filled-in
 * steps rise into the air block above. Everything else keeps vanilla collision.
 *
 * Accessed from both the client and the integrated server thread (singleplayer), hence the concurrent maps.
 */
public final class SmoothCollision {
    public static final short NONE = Short.MIN_VALUE;

    static final class ChunkData {
        final short[] tops;     // 256 columns, index z*16+x; NONE = vanilla
        final short[] heights;  // 256 columns x 16 sub-cells (index col*16 + b*4 + a), absolute Y * 16
        // Floors below the tops (cave floors, ledges under overhangs): per column, {floor Y, offset into floorHeights}
        final java.util.Map<Integer, int[][]> floors;
        final short[] floorHeights;
        final ConcurrentHashMap<Long, VoxelShape> shapes = new ConcurrentHashMap<>();
        ChunkData(short[] tops, short[] heights, java.util.Map<Integer, int[][]> floors, short[] floorHeights) {
            this.tops = tops; this.heights = heights; this.floors = floors; this.floorHeights = floorHeights;
        }
    }

    private static final ConcurrentHashMap<Long, ChunkData> chunks = new ConcurrentHashMap<>();
    private static volatile RegistryKey<World> dimension;
    private static volatile boolean enabled;

    public static void reset(RegistryKey<World> dim) {
        chunks.clear();
        dimension = dim;
    }

    public static void setEnabled(boolean on) {
        enabled = on;
        if (!on) chunks.clear();
    }

    public static void put(int cx, int cz, short[] tops, short[] heights) {
        put(cx, cz, tops, heights, new short[0], new short[0], new short[0]);
    }

    /** With the floors below the tops: per floor its column, floor Y, and 16 heights (floorHeights[i*16 ..]). */
    public static void put(int cx, int cz, short[] tops, short[] heights, short[] floorCols, short[] floorYs, short[] floorHeights) {
        var floors = new java.util.HashMap<Integer, int[][]>();
        for (int i = 0; i < floorCols.length; i++) {
            int[][] old = floors.get((int) floorCols[i]);
            int[][] list = old == null ? new int[1][] : java.util.Arrays.copyOf(old, old.length + 1);
            list[list.length - 1] = new int[] { floorYs[i], i * 16 };
            floors.put((int) floorCols[i], list);
        }
        chunks.put(ChunkPos.toLong(cx, cz), new ChunkData(tops, heights, floors, floorHeights));
    }

    public static int chunkCount() { return chunks.size(); }

    public static boolean isEnabled() { return enabled; }

    /** The smooth collision shape for this block, or null to use vanilla. */
    public static VoxelShape shapeFor(World world, BlockPos pos, BlockState state) {
        if (!enabled || world.getRegistryKey() != dimension) return null;
        ChunkData data = chunks.get(ChunkPos.toLong(pos.getX() >> 4, pos.getZ() >> 4));
        if (data == null) return null;
        int col = ((pos.getZ() & 15) << 4) | (pos.getX() & 15);
        long key = pos.asLong();
        VoxelShape cached = data.shapes.get(key);
        if (cached != null) return cached;

        // The column's top surface, else the nearest floor below it this block belongs to (caves).
        VoxelShape shape = null;
        if (data.tops[col] != NONE) shape = shapeAgainst(world, pos, state, data.tops[col], data.heights, col * 16);
        int[][] floors = data.floors.get(col);
        if (shape == null && floors != null) {
            int best = Integer.MAX_VALUE;
            for (int[] f : floors) {
                int dist = Math.abs(pos.getY() - f[0]);
                if (dist >= best) continue;
                VoxelShape s = shapeAgainst(world, pos, state, f[0], data.floorHeights, f[1]);
                if (s != null) { shape = s; best = dist; }
            }
        }
        if (shape != null) data.shapes.put(key, shape);
        return shape;
    }

    /** The shape of this block against one smooth surface (floor block {@code top}, heights at {@code at..at+15}). */
    private static VoxelShape shapeAgainst(World world, BlockPos pos, BlockState state, int top, short[] heights, int at) {
        int dy = pos.getY() - top;
        if (dy < -4 || dy > 2) return null; // smoothing can round cliff edges down by up to ~3 blocks
        // Only reshape natural ground (full cubes) and the empty space just above it; never builds, slabs, etc.
        // Above the top block: empty space or snow layers (snow lies on the smooth ground, it must not float).
        boolean above = dy > 0;
        if (above ? !(state.getCollisionShape(world, pos).isEmpty() || state.isOf(Blocks.SNOW)) : !state.isFullCube(world, pos)) return null;

        VoxelShape shape = VoxelShapes.empty();
        int full = 0, empty = 0;
        for (int b = 0; b < 4; b++)
            for (int a = 0; a < 4; a++) {
                double h = heights[at + b * 4 + a] / 16.0 - pos.getY();
                h = Math.max(0, Math.min(1, Math.round(h * 8) / 8.0)); // eighth-block steps: smooth, cacheable
                if (h >= 1) full++;
                if (h <= 0) { empty++; continue; }
                shape = VoxelShapes.union(shape, VoxelShapes.cuboid(a / 4.0, 0, b / 4.0, (a + 1) / 4.0, h, (b + 1) / 4.0));
            }
        if (full == 16) return VoxelShapes.fullCube();
        if (empty == 16) return VoxelShapes.empty();
        return shape.simplify();
    }

}
