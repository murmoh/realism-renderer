package com.realismrenderer.mixin;

import com.realismrenderer.BridgeMod;
import net.minecraft.client.MinecraftClient;
import net.minecraft.client.render.Camera;
import net.minecraft.client.render.GameRenderer;
import net.minecraft.client.render.RenderTickCounter;
import net.minecraft.client.render.WorldRenderer;
import net.minecraft.client.util.ObjectAllocator;
import org.joml.Matrix4f;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * While the renderer is in control it draws the world, so Minecraft skips its own (the hand, HUD and screens are
 * still drawn and captured). Saves a lot of GPU. The camera is still published every frame.
 */
@Mixin(WorldRenderer.class)
public abstract class WorldRendererMixin {
    @Inject(method = "render", at = @At("HEAD"), cancellable = true)
    private void realism$skipWorld(ObjectAllocator allocator, RenderTickCounter tickCounter, boolean renderBlockOutline, Camera camera,
                                       GameRenderer gameRenderer, Matrix4f positionMatrix, Matrix4f projectionMatrix, CallbackInfo ci) {
        var bridge = BridgeMod.bridge;
        if (bridge == null || !bridge.controlActive()) return;
        var mc = MinecraftClient.getInstance();
        // "Loading terrain..." closes once the player's chunk is built for drawing, which only happens in here.
        if (mc.currentScreen instanceof net.minecraft.client.gui.screen.DownloadingTerrainScreen) return;
        bridge.applyLook(mc);
        bridge.writeCamera(camera);
        // Minecraft normally prepares entity renderers here; entity posing (EntitySync) needs it.
        mc.getEntityRenderDispatcher().configure(mc.world, camera, mc.targetedEntity);
        ci.cancel();
    }

    /** Playing in Minecraft's own window with the renderer following: mark the world as uncovered (see Bridge.clearWorldAlpha). */
    @Inject(method = "render", at = @At("RETURN"))
    private void realism$afterWorld(ObjectAllocator allocator, RenderTickCounter tickCounter, boolean renderBlockOutline, Camera camera,
                                        GameRenderer gameRenderer, Matrix4f positionMatrix, Matrix4f projectionMatrix, CallbackInfo ci) {
        var bridge = BridgeMod.bridge;
        if (bridge != null && !bridge.controlActive() && bridge.readerAlive()) bridge.clearWorldAlpha(MinecraftClient.getInstance());
    }
}
