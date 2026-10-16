package com.realismrenderer.mixin;

import net.minecraft.client.Keyboard;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Invoker;

/** Typed characters from the renderer window (chat, signs, anvils). Key presses use the public onKey. */
@Mixin(Keyboard.class)
public interface KeyboardInvoker {
    @Invoker("onChar") void realism$onChar(long window, int codePoint, int modifiers);
}
