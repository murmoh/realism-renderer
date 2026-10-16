package com.realismrenderer.mixin;

import com.realismrenderer.SmoothCollision;
import net.minecraft.client.network.ClientPlayerEntity;
import net.minecraft.util.math.BlockPos;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Minecraft pushes the player away from any neighbouring block cell that has collision anywhere between their feet
 * and head ("stuck inside a block"). On smooth terrain the ground ahead always rises a little above the feet, so that
 * push fought every step uphill. While smooth collision is active, push only if the player really overlaps something.
 */
@Mixin(ClientPlayerEntity.class)
public abstract class PushOutMixin {
    @Inject(method = "wouldCollideAt", at = @At("HEAD"), cancellable = true)
    private void realism$ignoreWalkableGround(BlockPos pos, CallbackInfoReturnable<Boolean> cir) {
        if (!SmoothCollision.isEnabled()) return;
        ClientPlayerEntity self = (ClientPlayerEntity) (Object) this;
        // Not actually inside anything: no push, however steep the ground next to us. Otherwise vanilla handles it.
        if (!self.getWorld().canCollide(self, self.getBoundingBox().contract(1.0E-3))) cir.setReturnValue(false);
    }
}
