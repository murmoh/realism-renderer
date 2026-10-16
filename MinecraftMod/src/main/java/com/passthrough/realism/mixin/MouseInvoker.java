package com.passthrough.realism.mixin;

import net.minecraft.client.Mouse;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Invoker;

/** Lets the bridge feed mouse events from the renderer window into Minecraft's own handlers. */
@Mixin(Mouse.class)
public interface MouseInvoker {
    @Invoker("onMouseButton") void passthrough$onMouseButton(long window, int button, int action, int mods);
    @Invoker("onCursorPos") void passthrough$onCursorPos(long window, double x, double y);
    @Invoker("onMouseScroll") void passthrough$onMouseScroll(long window, double horizontal, double vertical);
}
