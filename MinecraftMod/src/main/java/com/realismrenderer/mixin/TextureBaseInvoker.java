package com.realismrenderer.mixin;

import java.util.Optional;

import net.minecraft.client.render.RenderPhase;
import net.minecraft.util.Identifier;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Invoker;

@Mixin(RenderPhase.TextureBase.class)
public interface TextureBaseInvoker {
    @Invoker("getId") Optional<Identifier> realism$getId();
}
