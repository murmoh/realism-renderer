package com.realismrenderer.mixin;

import net.minecraft.client.texture.GlTexture;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

@Mixin(GlTexture.class)
public interface GlTextureAccessor {
    @Accessor("glId")
    int realism$getGlId();
}
