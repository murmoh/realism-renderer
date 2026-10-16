package com.passthrough.realism;

import net.minecraft.client.gui.DrawContext;
import net.minecraft.client.gui.screen.Screen;
import net.minecraft.client.gui.tooltip.Tooltip;
import net.minecraft.client.gui.widget.ButtonWidget;
import net.minecraft.client.gui.widget.CyclingButtonWidget;
import net.minecraft.screen.ScreenTexts;
import net.minecraft.text.Text;

/** Realism Renderer settings (opened from Mod Menu's mod list). Changes are saved straight away. */
public final class RealismConfigScreen extends Screen {
    private final Screen parent;

    public RealismConfigScreen(Screen parent) {
        super(Text.literal("Realism Renderer Settings"));
        this.parent = parent;
    }

    @Override
    protected void init() {
        RealismConfig config = RealismConfig.get();
        int x = width / 2 - 100;
        addDrawableChild(CyclingButtonWidget.onOffBuilder(config.treeFelling)
            .tooltip(on -> Tooltip.of(Text.literal(on
                ? "Breaking the bottom log of a tree brings the whole tree down. Sneak to break a single log."
                : "Trees are chopped one log at a time, as in normal Minecraft.")))
            .build(x, height / 4 + 24, 200, 20, Text.literal("Tree felling"), (button, on) -> {
                config.treeFelling = on;
                config.save();
            }));
        addDrawableChild(ButtonWidget.builder(ScreenTexts.DONE, button -> close())
            .dimensions(x, height / 4 + 120, 200, 20).build());
    }

    @Override
    public void render(DrawContext context, int mouseX, int mouseY, float delta) {
        super.render(context, mouseX, mouseY, delta);
        context.drawCenteredTextWithShadow(textRenderer, title, width / 2, 20, 0xFFFFFF);
    }

    @Override
    public void close() {
        if (client != null) client.setScreen(parent);
    }
}
