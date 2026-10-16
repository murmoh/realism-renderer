package com.passthrough.realism.mixin;

import com.passthrough.realism.BridgeMod;
import net.minecraft.client.MinecraftClient;
import net.minecraft.client.render.GameRenderer;
import net.minecraft.client.render.RenderTickCounter;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Per frame: apply renderer input before the frame; after it, the GUI is complete and can be captured. */
@Mixin(GameRenderer.class)
public abstract class GameRendererMixin {
    @Inject(method = "render", at = @At("HEAD"))
    private void passthrough$beforeFrame(RenderTickCounter tickCounter, boolean tick, CallbackInfo ci) {
        if (BridgeMod.bridge != null) BridgeMod.bridge.beforeFrame(MinecraftClient.getInstance());
    }

    /** Playing in Minecraft's own window with the renderer following: mark the first-person hand (see Bridge.markHand). */
    @Inject(method = "renderHand", at = @At("RETURN"))
    private void passthrough$afterHand(net.minecraft.client.render.Camera camera, float tickProgress, org.joml.Matrix4f positionMatrix, CallbackInfo ci) {
        var bridge = BridgeMod.bridge;
        if (bridge != null && !bridge.controlActive() && bridge.readerAlive()) bridge.markHand(MinecraftClient.getInstance());
    }

    /** The field of view Minecraft draws the world with (its FOV setting, sprint/speed zoom, underwater), for the renderer. */
    @Inject(method = "getFov", at = @At("RETURN"))
    private void passthrough$fov(net.minecraft.client.render.Camera camera, float tickProgress, boolean changingFov, CallbackInfoReturnable<Float> cir) {
        if (changingFov && BridgeMod.bridge != null) BridgeMod.bridge.writeFov(cir.getReturnValueF());
    }

    @Inject(method = "render", at = @At("RETURN"))
    private void passthrough$afterFrame(RenderTickCounter tickCounter, boolean tick, CallbackInfo ci) {
        if (BridgeMod.bridge == null) return;
        BridgeMod.bridge.writeEntities(MinecraftClient.getInstance());
        BridgeMod.bridge.captureGui(MinecraftClient.getInstance());
    }
}
