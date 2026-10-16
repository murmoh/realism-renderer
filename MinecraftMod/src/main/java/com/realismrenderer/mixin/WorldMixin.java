package com.realismrenderer.mixin;

import com.realismrenderer.BridgeMod;
import net.minecraft.block.BlockState;
import net.minecraft.client.world.ClientWorld;
import net.minecraft.util.math.BlockPos;
import net.minecraft.world.World;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** Every client-side block change (server packets and the player's own predicted edits) ends up here. */
@Mixin(World.class)
public abstract class WorldMixin {
    @Inject(method = "setBlockState(Lnet/minecraft/util/math/BlockPos;Lnet/minecraft/block/BlockState;II)Z", at = @At("HEAD"))
    private void realism$beforeSetBlockState(BlockPos pos, BlockState state, int flags, int maxUpdateDepth, CallbackInfoReturnable<Boolean> cir) {
        // Empty <-> filled is a dig or a placement: remember it so the renderer shows that spot exactly.
        if ((Object) this instanceof ClientWorld world && BridgeMod.bridge != null && world.getBlockState(pos).isAir() != state.isAir())
            BridgeMod.bridge.recordEdit(pos);
    }

    @Inject(method = "setBlockState(Lnet/minecraft/util/math/BlockPos;Lnet/minecraft/block/BlockState;II)Z", at = @At("RETURN"))
    private void realism$onSetBlockState(BlockPos pos, BlockState state, int flags, int maxUpdateDepth, CallbackInfoReturnable<Boolean> cir) {
        if (cir.getReturnValueZ() && (Object) this instanceof ClientWorld && BridgeMod.bridge != null)
            BridgeMod.bridge.onBlockChanged(pos, state);
    }
}
