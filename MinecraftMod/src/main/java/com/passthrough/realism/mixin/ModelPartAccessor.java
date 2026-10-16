package com.passthrough.realism.mixin;

import java.util.List;
import java.util.Map;
import net.minecraft.client.model.ModelPart;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

@Mixin(ModelPart.class)
public interface ModelPartAccessor {
    @Accessor("cuboids") List<ModelPart.Cuboid> passthrough$cuboids();
    @Accessor("children") Map<String, ModelPart> passthrough$children();
}
