package com.realismrenderer;

import net.minecraft.client.MinecraftClient;
import net.minecraft.entity.EntityType;
import net.minecraft.entity.ItemEntity;
import net.minecraft.entity.SpawnReason;
import net.minecraft.entity.mob.MobEntity;
import net.minecraft.item.ItemStack;
import net.minecraft.item.Items;
import net.minecraft.util.math.BlockPos;
import net.minecraft.world.Heightmap;

/**
 * Only with -Dptr.test (the isolated test client, on its own world copy): once in the world, set noon and line up
 * still mobs and dropped items in front of the player, so automated screenshots have something to look at.
 */
public final class TestHooks {
    private static int settingsTicks, mcShotTicks;
    private static boolean done;
    private static volatile float lookYaw, lookPitch;
    private static volatile int lookTicks;

    private static int directorTicks;

    /**
     * Test clients only: commands the renderer's gameplay director drops into director.txt next to the bridge
     * (teleport, time, weather, summon, give, ...) run with full permission on the integrated server, then the file
     * is removed.
     */
    private static void director(MinecraftClient mc) {
        if (System.getProperty("ptr.test") == null || mc.getServer() == null || ++directorTicks % 5 != 0) return;
        String dir = System.getProperty("ptr.bridge.dir");
        if (dir == null) return;
        var file = java.nio.file.Path.of(dir, "director.txt");
        if (!java.nio.file.Files.exists(file)) return;
        java.util.List<String> lines;
        try {
            lines = java.nio.file.Files.readAllLines(file);
            java.nio.file.Files.delete(file);
        } catch (java.io.IOException e) { return; }
        var server = mc.getServer();
        server.execute(() -> {
            var source = server.getCommandSource().withSilent();
            var player = mc.player == null ? null : server.getPlayerManager().getPlayer(mc.player.getUuid());
            if (player != null) source = player.getCommandSource().withLevel(4).withSilent();
            for (String line : lines) {
                if (line.isBlank()) continue;
                server.getCommandManager().executeWithPrefix(source, line.trim());
                System.out.println("[ptr.director] " + line.trim());
            }
        });
    }

    public static void tick(MinecraftClient mc) {
        director(mc);
        // Test recordings: the player never burns (standing by lava would fill the view with Minecraft's flames).
        if (System.getProperty("ptr.test") != null && mc.player != null && mc.getServer() != null) {
            mc.player.extinguish();
            var server = mc.getServer();
            var id = mc.player.getUuid();
            server.execute(() -> { var sp = server.getPlayerManager().getPlayer(id); if (sp != null) sp.extinguish(); });
        }
        if (lookTicks > 0 && mc.player != null) {
            lookTicks--;
            mc.player.setYaw(lookYaw);
            mc.player.setPitch(lookPitch);
        }
        if (System.getProperty("ptr.nohud") != null && System.getProperty("ptr.test") != null) mc.options.hudHidden = true; // clean screenshots (F1)
        if (System.getProperty("ptr.mcshot") != null && System.getProperty("ptr.test") != null && mc.player != null && mcShotTicks >= 0
            && ++mcShotTicks == Integer.getInteger("ptr.mcshot", 600)) {
            // Minecraft's own view (what its window shows), for comparisons with the renderer.
            net.minecraft.client.util.ScreenshotRecorder.saveScreenshot(mc.runDirectory, "minecraft_view.png", mc.getFramebuffer(),
                text -> System.out.println("[ptr.mcshot] " + text.getString()));
            mcShotTicks = -1;
        }
        if (System.getProperty("ptr.settings") != null && System.getProperty("ptr.test") != null && mc.player != null && settingsTicks >= 0) {
            // Settings screen test: open it once in the world, then save a screenshot of it (run-test/screenshots).
            settingsTicks++;
            if (settingsTicks == 60) mc.setScreen(new RealismConfigScreen(null));
            if (settingsTicks == 100) {
                net.minecraft.client.util.ScreenshotRecorder.saveScreenshot(mc.runDirectory, "settings.png", mc.getFramebuffer(),
                    text -> System.out.println("[ptr.settings] " + text.getString()));
                settingsTicks = -1;
            }
        }
        if (done || System.getProperty("ptr.test") == null || mc.player == null || mc.getServer() == null || mc.world == null) return;
        if (mc.world.getTime() < 40) return;
        done = true;
        var server = mc.getServer();
        var id = mc.player.getUuid();
        server.execute(() -> {
            var sp = server.getPlayerManager().getPlayer(id);
            if (sp == null) return;
            var world = sp.getServerWorld();
            world.setTimeOfDay(Long.getLong("ptr.time", 6000L)); // -Ptime=18000: midnight
            if (System.getProperty("ptr.shaft") != null) {
                // A 3x3 shaft from the cave up to daylight, 9 blocks ahead: a cave opening to look out of.
                var f = sp.getHorizontalFacing();
                BlockPos c = sp.getBlockPos().offset(f, Integer.getInteger("ptr.shaft.at", 9));
                int top = world.getTopPosition(Heightmap.Type.MOTION_BLOCKING_NO_LEAVES, c).getY();
                for (int y = c.getY(); y <= top + 2; y++)
                    for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                        world.setBlockState(new BlockPos(c.getX() + dx, y, c.getZ() + dz), net.minecraft.block.Blocks.AIR.getDefaultState());
                System.out.println("[ptr.shaft] opened at " + c + " up to " + top);
            }
            if (System.getProperty("ptr.water") != null) {
                // Flowing water test: a stone platform 4..9 blocks ahead, a little below eye level, with a water source
                // at its far end: the water runs across it and pours off the edges as waterfalls.
                var f = sp.getHorizontalFacing();
                var side = f.rotateYClockwise();
                BlockPos start = BlockPos.ofFloored(sp.getX(), sp.getEyeY() - 3, sp.getZ()).offset(f, 4);
                for (int a = 0; a < 6; a++) for (int b = -1; b <= 1; b++)
                    world.setBlockState(start.offset(f, a).offset(side, b), net.minecraft.block.Blocks.STONE.getDefaultState());
                world.setBlockState(start.offset(f, 5).up(), net.minecraft.block.Blocks.WATER.getDefaultState());
                double dx = start.getX() + 0.5 - sp.getX(), dy = start.getY() - sp.getEyeY(), dz = start.getZ() + 0.5 - sp.getZ();
                lookYaw = (float) Math.toDegrees(Math.atan2(-dx, dz));
                lookPitch = 25;
                lookTicks = 100;
                System.out.println("[ptr.water] platform at " + start);
            }
            if (System.getProperty("ptr.cavefloor") != null) {
                // Stand on the nearest dry cave floor below (walking, not flying), for cave collision tests.
                BlockPos found = null;
                BlockPos p = sp.getBlockPos();
                search:
                for (int r = 0; r <= 12; r++)
                    for (int dz = -r; dz <= r; dz++) for (int dx = -r; dx <= r; dx++) {
                        if (Math.max(Math.abs(dx), Math.abs(dz)) != r) continue;
                        for (int y = p.getY(); y > world.getBottomY() + 1; y--) {
                            BlockPos b = new BlockPos(p.getX() + dx, y, p.getZ() + dz);
                            if (world.getBlockState(b).isSolidBlock(world, b) && world.getBlockState(b.up()).isAir() && world.getBlockState(b.up(2)).isAir()) {
                                found = b;
                                break search;
                            }
                        }
                    }
                if (found != null) {
                    sp.getAbilities().flying = false;
                    sp.sendAbilitiesUpdate();
                    sp.setNoGravity(false);
                    sp.requestTeleport(found.getX() + 0.5, found.getY() + 1, found.getZ() + 0.5);
                }
                System.out.println("[ptr.cavefloor] standing at " + (found == null ? "nowhere (no floor found)" : found.up()));
            }
            if (System.getProperty("ptr.stream") != null) {
                // Streams on natural slopes: a water source on the highest ground near the player and a lava source on
                // another high point at least 8 blocks away; both run down the hillsides. The player looks at the water.
                BlockPos me = world.getTopPosition(Heightmap.Type.MOTION_BLOCKING_NO_LEAVES, sp.getBlockPos());
                BlockPos waterAt = null, lavaAt = null;
                for (int dz = -14; dz <= 14; dz++) for (int dx = -14; dx <= 14; dx++) {
                    if (dx * dx + dz * dz < 16) continue;
                    BlockPos t = world.getTopPosition(Heightmap.Type.MOTION_BLOCKING_NO_LEAVES, me.add(dx, 0, dz));
                    var under = world.getBlockState(t.down());
                    if (!under.isSolidBlock(world, t.down()) || under.isIn(net.minecraft.registry.tag.BlockTags.LOGS) || under.isIn(net.minecraft.registry.tag.BlockTags.PLANKS)) continue;
                    if (waterAt == null || t.getY() > waterAt.getY()) waterAt = t;
                }
                for (int dz = -14; dz <= 14; dz++) for (int dx = -14; dx <= 14; dx++) {
                    BlockPos t = world.getTopPosition(Heightmap.Type.MOTION_BLOCKING_NO_LEAVES, me.add(dx, 0, dz));
                    if (waterAt == null || t.getSquaredDistance(waterAt) < 64 || dx * dx + dz * dz < 16) continue;
                    var under = world.getBlockState(t.down());
                    if (!under.isSolidBlock(world, t.down()) || under.isIn(net.minecraft.registry.tag.BlockTags.LOGS) || under.isIn(net.minecraft.registry.tag.BlockTags.PLANKS)) continue;
                    if (lavaAt == null || t.getY() > lavaAt.getY()) lavaAt = t;
                }
                if (waterAt != null) world.setBlockState(waterAt, net.minecraft.block.Blocks.WATER.getDefaultState());
                if (lavaAt != null) world.setBlockState(lavaAt, net.minecraft.block.Blocks.LAVA.getDefaultState());
                if (waterAt != null) {
                    double dx = waterAt.getX() + 0.5 - sp.getX(), dz = waterAt.getZ() + 0.5 - sp.getZ();
                    lookYaw = (float) Math.toDegrees(Math.atan2(-dx, dz));
                    lookPitch = 15;
                    lookTicks = 100;
                }
                System.out.println("[ptr.stream] water at " + waterAt + ", lava at " + lavaAt);
            }
            if (System.getProperty("ptr.surface") != null) {
                // Up onto the ground above (the test world's player stands in a cave).
                BlockPos top = world.getTopPosition(Heightmap.Type.MOTION_BLOCKING_NO_LEAVES, sp.getBlockPos());
                sp.requestTeleport(sp.getX(), top.getY() + 0.1, sp.getZ());
                sp.setNoGravity(false);
            }
            double yaw = Math.toRadians(sp.getYaw());
            double fx = -Math.sin(yaw), fz = Math.cos(yaw), rx = -fz, rz = fx; // forward and right
            EntityType<?>[] types = { EntityType.COW, EntityType.PIG, EntityType.SHEEP, EntityType.CHICKEN, EntityType.VILLAGER,
                EntityType.ZOMBIE, EntityType.SKELETON, EntityType.CREEPER, EntityType.HORSE, EntityType.WOLF };
            for (int i = 0; i < types.length && System.getProperty("ptr.mobs") == null; i++) { // the portrait test brings its own
                double side = (i % 5 - 2) * 2.2, dist = 5 + (i / 5) * 3.5;
                BlockPos column = BlockPos.ofFloored(sp.getX() + fx * dist + rx * side, 0, sp.getZ() + fz * dist + rz * side);
                BlockPos at = world.getTopPosition(Heightmap.Type.MOTION_BLOCKING_NO_LEAVES, column);
                var e = types[i].spawn(world, at, SpawnReason.COMMAND);
                if (e instanceof MobEntity mob) {
                    mob.setAiDisabled(true);
                    mob.setPersistent();
                    mob.setYaw((float) Math.toDegrees(Math.atan2(fx, -fz))); // face the player
                    mob.setBodyYaw(mob.getYaw());
                    mob.setHeadYaw(mob.getYaw());
                }
            }
            ItemStack[] stacks = { new ItemStack(Items.DIAMOND_SWORD), new ItemStack(Items.APPLE, 3), new ItemStack(Items.OAK_LOG, 16),
                new ItemStack(Items.DIRT, 1), new ItemStack(Items.TORCH, 8) };
            if (System.getProperty("ptr.cave") != null) {
                // Cave lighting test: torches on the floor around the player and a small lava pool ahead.
                int[][] spots = { { 3, 2 }, { -3, 3 }, { 4, -3 }, { -4, -2 }, { 0, 7 } };
                for (int[] o : spots) {
                    BlockPos floor = floorNear(world, BlockPos.ofFloored(sp.getX() + fx * o[1] + rx * o[0], sp.getY(), sp.getZ() + fz * o[1] + rz * o[0]));
                    if (floor != null) world.setBlockState(floor.up(), net.minecraft.block.Blocks.TORCH.getDefaultState());
                    System.out.println("[ptr.cave] player " + sp.getBlockPos() + " torch at " + (floor == null ? "none" : floor.up()));
                }
                BlockPos pool = floorNear(world, BlockPos.ofFloored(sp.getX() + fx * 5 + rx * 2, sp.getY(), sp.getZ() + fz * 5 + rz * 2));
                System.out.println("[ptr.cave] lava pool at " + pool);
                if (pool != null) {
                    // Face the pool (client side: that's the view the renderer mirrors).
                    double dx = pool.getX() + 0.5 - sp.getX(), dy = pool.getY() + 1 - sp.getEyeY(), dz = pool.getZ() + 0.5 - sp.getZ();
                    float yawTo = (float) Math.toDegrees(Math.atan2(-dx, dz));
                    float pitchTo = (float) -Math.toDegrees(Math.atan2(dy, Math.sqrt(dx * dx + dz * dz)));
                    lookYaw = yawTo;
                    lookPitch = pitchTo;
                    lookTicks = 100;
                }
                for (int dy = 3; dy >= -3; dy--)
                    System.out.println("[ptr.cave] column " + dy + ": " + world.getBlockState(sp.getBlockPos().up(dy)) + " / +3x: "
                        + world.getBlockState(sp.getBlockPos().add(3, dy, 0)) + " / +3z: " + world.getBlockState(sp.getBlockPos().add(0, dy, 3)));
                if (pool != null)
                    for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                        if (world.getBlockState(pool.add(dx, 1, dz)).isAir() && world.getBlockState(pool.add(dx, -1, dz)).isSolidBlock(world, pool.add(dx, -1, dz)))
                            world.setBlockState(pool.add(dx, 0, dz), net.minecraft.block.Blocks.LAVA.getDefaultState());
            }
            if (System.getProperty("ptr.mobs") != null) {
                // Animal portrait test: frozen noon; a row of animals on the ground 5 blocks ahead, spaced along the
                // player's right, side-on to the player (each facing right), AI off. Positions are logged.
                server.getGameRules().get(net.minecraft.world.GameRules.DO_DAYLIGHT_CYCLE).set(false, server);
                var facing = sp.getHorizontalFacing();
                var right = facing.rotateYClockwise();
                EntityType<?>[] row = { EntityType.COW, EntityType.PIG, EntityType.SHEEP, EntityType.CHICKEN, EntityType.HORSE, EntityType.WOLF, EntityType.MOOSHROOM };
                for (int i = 0; i < row.length; i++) {
                    double along = (i - (row.length - 1) / 2.0) * 3.0;
                    BlockPos column = BlockPos.ofFloored(sp.getX() + facing.getOffsetX() * 5 + right.getOffsetX() * along, 0,
                        sp.getZ() + facing.getOffsetZ() * 5 + right.getOffsetZ() * along);
                    BlockPos at = world.getTopPosition(Heightmap.Type.MOTION_BLOCKING_NO_LEAVES, column);
                    MobEntity mob = (MobEntity) row[i].create(world, SpawnReason.COMMAND);
                    mob.refreshPositionAndAngles(at.getX() + 0.5, at.getY(), at.getZ() + 0.5, right.getPositiveHorizontalDegrees(), 0);
                    mob.setAiDisabled(true);
                    mob.setPersistent();
                    mob.setBodyYaw(mob.getYaw());
                    mob.setHeadYaw(mob.getYaw());
                    world.spawnEntity(mob);
                    System.out.println("[ptr.mobs] " + net.minecraft.registry.Registries.ENTITY_TYPE.getId(row[i]).getPath() + " at " + (at.getX() + 0.5) + " " + at.getY() + " " + (at.getZ() + 0.5));
                }
                System.out.println("[ptr.mobs] spawned");
            }
            if (System.getProperty("ptr.blocks") != null) {
                // Block model test: a cobblestone column 4 blocks ahead at eye level with a wall torch facing the
                // player, a crafting table, glass, planks and a lantern beside it; the player looks at it.
                var toPlayer = sp.getHorizontalFacing().getOpposite();
                var side = toPlayer.rotateYClockwise();
                BlockPos base = BlockPos.ofFloored(sp.getX() - toPlayer.getOffsetX() * 4, sp.getEyeY() - 1, sp.getZ() - toPlayer.getOffsetZ() * 4);
                for (int y = 0; y < 3; y++) world.setBlockState(base.up(y), net.minecraft.block.Blocks.COBBLESTONE.getDefaultState());
                world.setBlockState(base.up(1).offset(toPlayer), net.minecraft.block.Blocks.WALL_TORCH.getDefaultState()
                    .with(net.minecraft.block.WallTorchBlock.FACING, toPlayer));
                world.setBlockState(base.offset(side), net.minecraft.block.Blocks.CRAFTING_TABLE.getDefaultState());
                world.setBlockState(base.offset(side, 2), net.minecraft.block.Blocks.GLASS.getDefaultState());
                world.setBlockState(base.offset(side.getOpposite()), net.minecraft.block.Blocks.OAK_PLANKS.getDefaultState());
                world.setBlockState(base.offset(side.getOpposite()).up(), net.minecraft.block.Blocks.LANTERN.getDefaultState());
                world.setBlockState(base.offset(side.getOpposite(), 2), net.minecraft.block.Blocks.BRICKS.getDefaultState());
                double dx = base.getX() + 0.5 - sp.getX(), dy = base.getY() + 1.5 - sp.getEyeY(), dz = base.getZ() + 0.5 - sp.getZ();
                lookYaw = (float) Math.toDegrees(Math.atan2(-dx, dz));
                lookPitch = (float) -Math.toDegrees(Math.atan2(dy, Math.sqrt(dx * dx + dz * dz)));
                lookTicks = 100;
                System.out.println("[ptr.blocks] scene at " + base);
            }
            for (int i = 0; i < stacks.length; i++) {
                double side = (i - 2) * 0.9;
                BlockPos at = world.getTopPosition(Heightmap.Type.MOTION_BLOCKING_NO_LEAVES,
                    BlockPos.ofFloored(sp.getX() + fx * 3 + rx * side, 0, sp.getZ() + fz * 3 + rz * side));
                var item = new ItemEntity(world, at.getX() + 0.5, at.getY() + 0.1, at.getZ() + 0.5, stacks[i]);
                item.setPickupDelay(32767);
                item.setNeverDespawn();
                world.spawnEntity(item);
            }
        });
    }

    /** The nearest solid floor block to {@code p} (within 6 blocks) with dry air on top, or null. */
    private static BlockPos floorNear(net.minecraft.server.world.ServerWorld world, BlockPos p) {
        for (int r = 0; r <= 14; r++)
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++) {
                    if (Math.max(Math.abs(dx), Math.abs(dz)) != r) continue;
                    for (int dy = 10; dy >= -10; dy--) {
                        BlockPos b = p.add(dx, dy, dz);
                        if (world.getBlockState(b).isSolidBlock(world, b) && world.getBlockState(b.up()).isAir()
                            && world.getBlockState(b.up(2)).isAir()) return b;
                    }
                }
        return null;
    }
}
