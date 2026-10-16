package com.realismrenderer.mixin;

import java.util.List;
import java.util.Map;
import net.minecraft.client.model.ModelPart;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

@Mixin(ModelPart.class)
public interface ModelPartAccessor {
    @Accessor("cuboids") List<ModelPart.Cuboid> realism$cuboids();
    @Accessor("children") Map<String, ModelPart> realism$children();
}
