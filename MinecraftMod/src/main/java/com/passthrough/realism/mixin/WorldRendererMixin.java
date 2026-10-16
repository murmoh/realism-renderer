package com.passthrough.realism.mixin;

import com.passthrough.realism.BridgeMod;
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
    private void passthrough$skipWorld(ObjectAllocator allocator, RenderTickCounter tickCounter, boolean renderBlockOutline, Camera camera,
                                       GameRenderer gameRenderer, Matrix4f positionMatrix, Matrix4f projectionMatrix, CallbackInfo ci) {
        var bridge = BridgeMod.bridge;
        if (bridge == null || !bridge.controlActive()) return;
        bridge.applyLook(MinecraftClient.getInstance());
        bridge.writeCamera(camera);
        // Minecraft normally prepares entity renderers here; entity posing (EntitySync) needs it.
        var mc = MinecraftClient.getInstance();
        mc.getEntityRenderDispatcher().configure(mc.world, camera, mc.targetedEntity);
        ci.cancel();
    }

    /** Playing in Minecraft's own window with the renderer following: mark the world as uncovered (see Bridge.clearWorldAlpha). */
    @Inject(method = "render", at = @At("RETURN"))
    private void passthrough$afterWorld(ObjectAllocator allocator, RenderTickCounter tickCounter, boolean renderBlockOutline, Camera camera,
                                        GameRenderer gameRenderer, Matrix4f positionMatrix, Matrix4f projectionMatrix, CallbackInfo ci) {
        var bridge = BridgeMod.bridge;
        if (bridge != null && !bridge.controlActive() && bridge.readerAlive()) bridge.clearWorldAlpha(MinecraftClient.getInstance());
    }
}
