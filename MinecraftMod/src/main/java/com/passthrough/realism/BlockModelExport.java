package com.passthrough.realism;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

import com.passthrough.realism.mixin.GlTextureAccessor;
import net.minecraft.block.Block;
import net.minecraft.block.BlockState;
import net.minecraft.client.MinecraftClient;
import net.minecraft.client.render.RenderLayer;
import net.minecraft.client.render.RenderLayers;
import net.minecraft.client.render.model.BakedQuad;
import net.minecraft.client.render.model.BlockModelPart;
import net.minecraft.client.texture.GlTexture;
import net.minecraft.client.texture.SpriteAtlasTexture;
import net.minecraft.util.math.Direction;
import net.minecraft.util.math.random.Random;
import org.lwjgl.opengl.GL11;

/**
 * Minecraft's own block models and textures for the renderer, which draws everything that isn't landscape
 * (torches, placed blocks, buildings, rails, ...) exactly as Minecraft does.
 *
 * MODEL (8): int stateId, int flags (1 translucent, 2 full opaque cube, 4 cut-out), int luminance, int quads;
 *            per quad: byte cullFace (Direction ordinal, 255 = none), byte shade, byte emission, byte 0,
 *            int tint (0xRRGGBB, white when untinted), 4 x (float x, y, z in block units, float u, v in the atlas).
 * ATLAS (9): int width, height, RGBA bytes (the block atlas, mip level 0).
 */
final class BlockModelExport {
    static final int MSG_MODEL = 8, MSG_ATLAS = 9;
    private final Set<Integer> sent = new HashSet<>();

    /** New session: the renderer has no models yet. */
    void reset() { sent.clear(); }

    /** The model message for a state the renderer hasn't been sent yet, else null. */
    ByteBuffer modelIfNew(MinecraftClient mc, BlockState state) {
        int id = Block.getRawIdFromState(state);
        if (!sent.add(id) || state.isAir()) return null;
        List<BakedQuad> quads = new ArrayList<>();
        List<Integer> culls = new ArrayList<>();
        for (BlockModelPart part : mc.getBlockRenderManager().getModel(state).getParts(Random.create(42L))) {
            for (Direction d : Direction.values())
                for (BakedQuad q : part.getQuads(d)) { quads.add(q); culls.add(d.ordinal()); }
            for (BakedQuad q : part.getQuads(null)) { quads.add(q); culls.add(255); }
        }
        RenderLayer layer = RenderLayers.getBlockLayer(state);
        int flags = (layer == RenderLayer.getTranslucent() ? 1 : 0)
            | (state.isOpaqueFullCube() ? 2 : 0)
            | (layer == RenderLayer.getCutout() || layer == RenderLayer.getCutoutMipped() ? 4 : 0);
        ByteBuffer m = ByteBuffer.allocate(8 + 16 + quads.size() * (8 + 4 * 20)).order(ByteOrder.LITTLE_ENDIAN);
        m.putInt(MSG_MODEL).putInt(m.capacity() - 8);
        m.putInt(id).putInt(flags).putInt(state.getLuminance()).putInt(quads.size());
        for (int i = 0; i < quads.size(); i++) {
            BakedQuad q = quads.get(i);
            int tint = 0xFFFFFF;
            if (q.hasTint() && mc.world != null && mc.player != null) {
                int c = mc.getBlockColors().getColor(state, mc.world, mc.player.getBlockPos(), q.tintIndex());
                if (c != -1) tint = c & 0xFFFFFF;
            }
            m.put((byte) (int) culls.get(i)).put((byte) (q.shade() ? 1 : 0)).put((byte) q.lightEmission()).put((byte) 0).putInt(tint);
            int[] v = q.vertexData();
            int stride = v.length / 4;
            for (int k = 0; k < 4; k++) {
                int o = k * stride;
                m.putFloat(Float.intBitsToFloat(v[o])).putFloat(Float.intBitsToFloat(v[o + 1])).putFloat(Float.intBitsToFloat(v[o + 2]))
                    .putFloat(Float.intBitsToFloat(v[o + 4])).putFloat(Float.intBitsToFloat(v[o + 5]));
            }
        }
        m.flip();
        return m;
    }

    /** The block texture atlas, read back from the GPU (render thread only). */
    ByteBuffer atlas(MinecraftClient mc) {
        var texture = mc.getTextureManager().getTexture(SpriteAtlasTexture.BLOCK_ATLAS_TEXTURE);
        if (!(texture.getGlTexture() instanceof GlTexture gl)) return null;
        int id = ((GlTextureAccessor) gl).passthrough$getGlId();
        com.mojang.blaze3d.opengl.GlStateManager._bindTexture(id);
        int w = GL11.glGetTexLevelParameteri(GL11.GL_TEXTURE_2D, 0, GL11.GL_TEXTURE_WIDTH);
        int h = GL11.glGetTexLevelParameteri(GL11.GL_TEXTURE_2D, 0, GL11.GL_TEXTURE_HEIGHT);
        ByteBuffer pixels = ByteBuffer.allocateDirect(w * h * 4);
        GL11.glPixelStorei(GL11.GL_PACK_ALIGNMENT, 4);
        GL11.glGetTexImage(GL11.GL_TEXTURE_2D, 0, GL11.GL_RGBA, GL11.GL_UNSIGNED_BYTE, pixels);
        com.mojang.blaze3d.opengl.GlStateManager._bindTexture(0);
        ByteBuffer m = ByteBuffer.allocate(8 + 8 + w * h * 4).order(ByteOrder.LITTLE_ENDIAN);
        m.putInt(MSG_ATLAS).putInt(8 + w * h * 4).putInt(w).putInt(h).put(pixels);
        m.flip();
        return m;
    }
}
