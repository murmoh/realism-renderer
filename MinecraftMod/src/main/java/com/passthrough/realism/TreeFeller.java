package com.passthrough.realism;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

import net.minecraft.block.Block;
import net.minecraft.block.BlockState;
import net.minecraft.block.LeavesBlock;
import net.minecraft.entity.player.PlayerEntity;
import net.minecraft.item.ItemStack;
import net.minecraft.registry.tag.BlockTags;
import net.minecraft.server.world.ServerWorld;
import net.minecraft.util.math.BlockPos;
import net.minecraft.world.World;

/**
 * Break one log of a tree and the whole tree comes down: every connected log is chopped (with the tool's normal
 * loot and durability), the tree's natural leaves are cleared (dropping saplings, sticks and apples by the normal
 * leaf loot), and everything lands at the foot of the tree. Only natural trees: logs that carry natural
 * (non-persistent) leaves, so log houses stay intact. Sneak to break a single log. Can be turned off in the settings.
 */
public final class TreeFeller {
    static final int MAX_LOGS = 256, MAX_LEAVES = 1500, LEAF_REACH = 7;
    private static boolean felling; // breaking blocks below fires the break event again

    public static void onBlockBroken(World world, PlayerEntity player, BlockPos pos, BlockState state) {
        if (felling || world.isClient() || !(world instanceof ServerWorld server) || player.isSneaking()) return;
        if (!RealismConfig.get().treeFelling) return; // turned off in the settings
        if (!state.isIn(BlockTags.LOGS)) return;

        // All logs connected to the broken one (diagonals too: branches), not going below it.
        List<BlockPos> logs = new ArrayList<>();
        Set<BlockPos> seen = new HashSet<>();
        ArrayDeque<BlockPos> queue = new ArrayDeque<>();
        queue.add(pos);
        seen.add(pos);
        while (!queue.isEmpty() && logs.size() < MAX_LOGS) {
            BlockPos p = queue.poll();
            for (int dy = 0; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++) {
                        BlockPos n = p.add(dx, dy, dz);
                        if (n.getY() < pos.getY() || !seen.add(n)) continue;
                        if (world.getBlockState(n).isIn(BlockTags.LOGS)) { logs.add(n); queue.add(n); }
                    }
        }
        if (logs.isEmpty()) return;

        // Natural leaves around the trunk and branches (flood fill through leaves, staying near the logs).
        List<BlockPos> leaves = new ArrayList<>();
        Set<BlockPos> leafSeen = new HashSet<>();
        ArrayDeque<BlockPos> leafQueue = new ArrayDeque<>();
        for (BlockPos log : logs) for (var d : net.minecraft.util.math.Direction.values()) leafQueue.add(log.offset(d));
        leafQueue.add(pos.up());
        while (!leafQueue.isEmpty() && leaves.size() < MAX_LEAVES) {
            BlockPos p = leafQueue.poll();
            if (!leafSeen.add(p)) continue;
            BlockState s = world.getBlockState(p);
            if (!s.isIn(BlockTags.LEAVES) || (s.contains(LeavesBlock.PERSISTENT) && s.get(LeavesBlock.PERSISTENT))) continue;
            if (nearestDistance(p, logs) > LEAF_REACH) continue;
            leaves.add(p);
            for (var d : net.minecraft.util.math.Direction.values()) leafQueue.add(p.offset(d));
        }
        if (leaves.size() < 4) return; // no canopy: a build made of logs, not a tree

        felling = true;
        try {
            ItemStack tool = player.getMainHandStack();
            boolean hadTool = !tool.isEmpty();
            List<ItemStack> loot = new ArrayList<>();
            for (BlockPos log : logs) {
                if (hadTool && player.getMainHandStack().isEmpty()) break; // the axe broke: stop chopping
                BlockState s = world.getBlockState(log);
                if (!s.isIn(BlockTags.LOGS)) continue;
                loot.addAll(Block.getDroppedStacks(s, server, log, world.getBlockEntity(log), player, player.getMainHandStack()));
                if (!player.isCreative()) player.getMainHandStack().postMine(world, s, log, player);
                world.syncWorldEvent(2001, log, Block.getRawIdFromState(s)); // break particles + sound
                world.removeBlock(log, false);
            }
            for (BlockPos leaf : leaves) {
                BlockState s = world.getBlockState(leaf);
                if (!s.isIn(BlockTags.LEAVES)) continue;
                loot.addAll(Block.getDroppedStacks(s, server, leaf, null)); // no tool: saplings, sticks, apples
                world.removeBlock(leaf, false);
            }
            if (player.isCreative()) return; // no drops in creative, like normal mining
            // The loot lands a moment after the tree comes down (as a falling tree would), not in the same instant.
            PENDING.add(new Object[] { server, pos.toImmutable(), loot, 8 });
        } finally {
            felling = false;
        }
    }

    private static final List<Object[]> PENDING = new ArrayList<>(); // {world, pos, loot, ticks left}

    /** Every server tick: drop the loot whose moment has come. */
    @SuppressWarnings("unchecked")
    public static void tick() {
        for (var it = PENDING.iterator(); it.hasNext(); ) {
            Object[] p = it.next();
            int left = (int) p[3] - 1;
            p[3] = left;
            if (left > 0) continue;
            it.remove();
            for (ItemStack stack : (List<ItemStack>) p[2]) Block.dropStack((ServerWorld) p[0], (BlockPos) p[1], stack);
        }
    }

    private static double nearestDistance(BlockPos p, List<BlockPos> logs) {
        double best = Double.MAX_VALUE;
        for (BlockPos l : logs) best = Math.min(best, Math.sqrt(l.getSquaredDistance(p)));
        return best;
    }
}
