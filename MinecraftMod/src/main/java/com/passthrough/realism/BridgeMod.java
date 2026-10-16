package com.passthrough.realism;

import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientChunkEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.rendering.v1.WorldRenderEvents;
import net.minecraft.client.MinecraftClient;

/** Client entrypoint: wires Minecraft events to the shared-memory bridge. */
public final class BridgeMod implements ClientModInitializer {
    public static Bridge bridge;

    @Override
    public void onInitializeClient() {
        bridge = new Bridge();
        ClientChunkEvents.CHUNK_LOAD.register((world, chunk) -> bridge.onChunkLoad(chunk.getPos()));
        ClientChunkEvents.CHUNK_UNLOAD.register((world, chunk) -> bridge.onChunkUnload(chunk.getPos()));
        // Tree felling runs on the integrated server (singleplayer), which shares this JVM.
        net.fabricmc.fabric.api.event.player.PlayerBlockBreakEvents.AFTER.register((world, player, pos, state, be) -> TreeFeller.onBlockBroken(world, player, pos, state));
        net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents.END_SERVER_TICK.register(server -> TreeFeller.tick());
        ClientTickEvents.START_CLIENT_TICK.register(bridge::applyInput);
        ClientTickEvents.END_CLIENT_TICK.register(bridge::tick);
        ClientTickEvents.END_CLIENT_TICK.register(TestHooks::tick);
        WorldRenderEvents.START.register(context -> bridge.applyLook(MinecraftClient.getInstance()));
        WorldRenderEvents.END.register(context -> bridge.writeCamera(context.camera()));
    }
}
