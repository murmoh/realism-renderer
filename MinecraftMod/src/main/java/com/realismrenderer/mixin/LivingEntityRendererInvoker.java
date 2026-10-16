package com.realismrenderer.mixin;

import net.minecraft.client.render.entity.LivingEntityRenderer;
import net.minecraft.client.render.entity.state.LivingEntityRenderState;
import net.minecraft.client.util.math.MatrixStack;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;
import org.spongepowered.asm.mixin.gen.Invoker;

/** The positioning steps of LivingEntityRenderer.render, so mobs can be posed exactly as Minecraft would. */
@Mixin(LivingEntityRenderer.class)
public interface LivingEntityRendererInvoker {
    @Invoker("setupTransforms") void realism$setupTransforms(LivingEntityRenderState state, MatrixStack matrices, float bodyYaw, float baseHeight);
    @Invoker("scale") void realism$scale(LivingEntityRenderState state, MatrixStack matrices);
    /** Its extra layers (sheep wool, saddles, armour, held items, eyes, ...). */
    @Accessor("features") java.util.List<net.minecraft.client.render.entity.feature.FeatureRenderer<?, ?>> realism$features();
}
