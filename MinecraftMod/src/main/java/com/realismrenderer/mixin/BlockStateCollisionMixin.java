package com.realismrenderer.mixin;

import com.realismrenderer.SmoothCollision;
import net.minecraft.block.AbstractBlock;
import net.minecraft.block.BlockState;
import net.minecraft.block.EntityShapeContext;
import net.minecraft.block.ShapeContext;
import net.minecraft.entity.player.PlayerEntity;
import net.minecraft.util.math.BlockPos;
import net.minecraft.util.shape.VoxelShape;
import net.minecraft.world.BlockView;
import net.minecraft.world.World;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Players collide with the smooth terrain surface (see SmoothCollision); mobs and everything else stay vanilla. */
@Mixin(AbstractBlock.AbstractBlockState.class)
public abstract class BlockStateCollisionMixin {
    @Inject(method = "getCollisionShape(Lnet/minecraft/world/BlockView;Lnet/minecraft/util/math/BlockPos;Lnet/minecraft/block/ShapeContext;)Lnet/minecraft/util/shape/VoxelShape;",
            at = @At("HEAD"), cancellable = true)
    private void realism$smoothCollision(BlockView world, BlockPos pos, ShapeContext context, CallbackInfoReturnable<VoxelShape> cir) {
        if (context instanceof EntityShapeContext esc && esc.getEntity() instanceof PlayerEntity && world instanceof World w) {
            VoxelShape shape = SmoothCollision.shapeFor(w, pos, (BlockState) (Object) this);
            if (shape != null) cir.setReturnValue(shape);
        }
    }
}
