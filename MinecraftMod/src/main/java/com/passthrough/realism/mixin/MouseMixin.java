package com.passthrough.realism.mixin;

import com.passthrough.realism.BridgeMod;
import net.minecraft.client.Mouse;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Minecraft only mines while its own cursor is captured; treat it as captured while the renderer is in control. */
@Mixin(Mouse.class)
public abstract class MouseMixin {
    @Inject(method = "isCursorLocked", at = @At("HEAD"), cancellable = true)
    private void passthrough$cursorLocked(CallbackInfoReturnable<Boolean> cir) {
        if (BridgeMod.bridge != null && BridgeMod.bridge.controlActive() && net.minecraft.client.MinecraftClient.getInstance().currentScreen == null)
            cir.setReturnValue(true);
    }
}
