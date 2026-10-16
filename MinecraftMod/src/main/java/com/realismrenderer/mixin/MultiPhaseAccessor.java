package com.realismrenderer.mixin;

import net.minecraft.client.render.RenderLayer;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

/** A render layer's parameters (for its texture: captured mob layers are textured in the renderer). */
@Mixin(targets = "net.minecraft.client.render.RenderLayer$MultiPhase")
public interface MultiPhaseAccessor {
    @Accessor("phases") RenderLayer.MultiPhaseParameters realism$phases();
}
