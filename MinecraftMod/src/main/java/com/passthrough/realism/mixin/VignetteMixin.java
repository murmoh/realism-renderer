package com.passthrough.realism.mixin;

import com.passthrough.realism.BridgeMod;
import net.minecraft.client.gui.DrawContext;
import net.minecraft.client.gui.hud.InGameHud;
import net.minecraft.entity.Entity;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** The screen-edge vignette overwrites the captured UI's transparency (the hand turned see-through); the renderer has its own. */
@Mixin(InGameHud.class)
public abstract class VignetteMixin {
    @Inject(method = "renderVignetteOverlay", at = @At("HEAD"), cancellable = true)
    private void passthrough$noVignette(DrawContext context, Entity entity, CallbackInfo ci) {
        if (BridgeMod.bridge != null && BridgeMod.bridge.controlActive()) ci.cancel();
    }
}
