package com.passthrough.realism;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Consumer;

import com.passthrough.realism.mixin.LivingEntityRendererInvoker;
import com.passthrough.realism.mixin.ModelPartAccessor;
import net.minecraft.client.MinecraftClient;
import net.minecraft.client.model.ModelPart;
import net.minecraft.client.render.entity.LivingEntityRenderer;
import net.minecraft.client.render.entity.state.LivingEntityRenderState;
import net.minecraft.client.util.math.MatrixStack;
import net.minecraft.entity.Entity;
import net.minecraft.entity.ItemEntity;
import net.minecraft.entity.LivingEntity;
import net.minecraft.registry.Registries;
import net.minecraft.util.Identifier;
import net.minecraft.util.math.Vec3d;
import org.joml.Matrix4f;

/**
 * Mobs and dropped items for the renderer, using Minecraft's own models, textures and animation.
 *
 * Each frame every living entity nearby is posed exactly as LivingEntityRenderer would (render state, setupTransforms,
 * scale, setAngles) without drawing; the world matrix of every model part goes into a double-buffered snapshot.
 * Model geometry (the parts' boxes as quads, in pixels with normalised UVs) and textures (PNG) are sent once.
 *
 * Snapshot: int items; per item: float x, y, z, int texture, int isBlock, int count, float age, float seed;
 *           int mobs;  per mob: int model, int texture, int parts, then 16 floats (column-major) per part (all zero = hidden),
 *                      int layerQuads, per quad: int texture (-1 = block atlas), 4 x (float x, y, z world, float u, v, int ARGB).
 *
 * Layers (sheep wool, saddles, armour, held items, glowing eyes, ...) are captured by running Minecraft's own feature
 * renderers into a recording VertexConsumerProvider: whatever they draw arrives as world-space quads.
 */
public final class EntitySync {
    static final int MSG_MODEL = 5, MSG_TEXTURE = 6;
    static final double RANGE = 48;
    static final int MAX_MOBS = 128, MAX_ITEMS = 256;

    private final Map<ModelPart, Integer> models = new IdentityHashMap<>();
    private final Map<Identifier, Integer> textures = new HashMap<>();
    private final Map<Identifier, int[]> itemTextures = new HashMap<>(); // item id -> {texture, isBlock}
    private final float[] matrix = new float[16];
    private final Capture capture = new Capture();

    /** New session: the renderer has nothing, so everything is sent again. */
    public void reset() {
        models.clear();
        textures.clear();
        itemTextures.clear();
    }

    /** Writes this frame's snapshot into {@code out}; geometry/textures not yet sent go through {@code send}. */
    @SuppressWarnings({ "unchecked", "rawtypes" })
    public void write(MinecraftClient mc, float td, ByteBuffer out, Consumer<ByteBuffer> send) {
        out.clear();
        var dispatcher = mc.getEntityRenderDispatcher();
        Vec3d me = mc.player.getPos();

        int itemCountPos = out.position();
        out.putInt(0);
        int items = 0;
        for (Entity e : mc.world.getEntities()) {
            if (!(e instanceof ItemEntity item) || e.squaredDistanceTo(me) > RANGE * RANGE || items >= MAX_ITEMS) continue;
            int[] tex = itemTexture(mc, Registries.ITEM.getId(item.getStack().getItem()), send);
            Vec3d p = e.getLerpedPos(td);
            out.putFloat((float) p.x).putFloat((float) p.y).putFloat((float) p.z);
            out.putInt(tex[0]).putInt(tex[1]).putInt(item.getStack().getCount());
            out.putFloat(item.getItemAge() + td).putFloat(item.uniqueOffset);
            items++;
        }
        out.putInt(itemCountPos, items);

        int mobCountPos = out.position();
        out.putInt(0);
        int mobs = 0;
        for (Entity e : mc.world.getEntities()) {
            if (!(e instanceof LivingEntity living) || e == mc.player || e.squaredDistanceTo(me) > RANGE * RANGE || mobs >= MAX_MOBS) continue;
            if (!(dispatcher.getRenderer(e) instanceof LivingEntityRenderer renderer)) continue;
            int start = out.position();
            try {
                LivingEntityRenderState state = (LivingEntityRenderState) renderer.getAndUpdateRenderState(living, td);
                MatrixStack ms = new MatrixStack();
                Vec3d p = e.getLerpedPos(td);
                ms.translate(p.x, p.y, p.z);
                // Exactly LivingEntityRenderer.render's positioning.
                float g = state.baseScale;
                ms.scale(g, g, g);
                ((LivingEntityRendererInvoker) renderer).passthrough$setupTransforms(state, ms, state.bodyYaw, g);
                ms.scale(-1.0F, -1.0F, 1.0F);
                ((LivingEntityRendererInvoker) renderer).passthrough$scale(state, ms);
                ms.translate(0.0F, -1.501F, 0.0F);
                var model = renderer.getModel();
                model.setAngles(state);
                ModelPart root = model.getRootPart();

                String type = Registries.ENTITY_TYPE.getId(e.getType()).getPath();
                int modelId = models.computeIfAbsent(root, r -> { int id = models.size() + 1; send.accept(geometry(id, r, type)); return id; });
                int textureId = texture(mc, renderer.getTexture(state), send);

                List<float[]> parts = new ArrayList<>();
                collect(root, ms, true, parts);

                // Extra layers, drawn by Minecraft into the recorder (same matrices as LivingEntityRenderer uses).
                capture.begin(mc, send);
                if (!state.invisibleToPlayer)
                    for (var feature : ((LivingEntityRendererInvoker) renderer).passthrough$features()) {
                        try {
                            ((net.minecraft.client.render.entity.feature.FeatureRenderer) feature).render(ms, capture, 0xF000F0, state, state.relativeHeadYaw, state.pitch);
                        } catch (RuntimeException ignored) { } // one odd layer doesn't lose the mob
                    }
                ByteBuffer layers = capture.end();

                if (out.remaining() < 16 + parts.size() * 64 + layers.remaining() + 1024) break;
                out.putInt(modelId).putInt(textureId).putInt(parts.size());
                for (float[] m : parts)
                    for (int i = 0; i < 16; i++) out.putFloat(m == null ? 0 : m[i]);
                out.putInt(capture.quads).put(layers);
                mobs++;
            } catch (RuntimeException ex) {
                out.position(start); // this mob's renderer didn't cooperate; skip it
            }
        }
        out.putInt(mobCountPos, mobs);
    }

    /** Pre-order walk of the part tree, applying each part's transform as ModelPart.render does. */
    private void collect(ModelPart part, MatrixStack ms, boolean parentVisible, List<float[]> out) {
        boolean visible = parentVisible && part.visible;
        ms.push();
        part.applyTransform(ms);
        if (visible && !part.hidden) {
            float[] m = new float[16];
            ms.peek().getPositionMatrix().get(m);
            out.add(m);
        } else out.add(null);
        for (ModelPart child : ((ModelPartAccessor) (Object) part).passthrough$children().values()) collect(child, ms, visible, out);
        ms.pop();
    }

    /**
     * Geometry message: int id, short typeLen, utf8 entity type ("cow", "sheep", ...), int parts; per part (same order
     * as collect): short nameLen, utf8 name ("head", "body", "right_hind_leg", ...), int parent (-1 = root), rest
     * transform (9 floats: pivot x, y, z in pixels, pitch, yaw, roll, scale x, y, z), int cuboids; per cuboid:
     * 6 floats box (min xyz, max xyz in pixels), int quads, then per quad: normal, 4 x (x, y, z in pixels, u, v).
     */
    private ByteBuffer geometry(int id, ModelPart root, String type) {
        List<ModelPart> parts = new ArrayList<>();
        List<String> names = new ArrayList<>();
        List<Integer> parents = new ArrayList<>();
        flatten(root, "root", -1, parts, names, parents);
        byte[] typeBytes = type.getBytes(java.nio.charset.StandardCharsets.UTF_8);
        int size = 16 + typeBytes.length;
        for (int i = 0; i < parts.size(); i++) {
            size += 8 + names.get(i).length() * 3 + 4 + 36;
            for (ModelPart.Cuboid c : ((ModelPartAccessor) (Object) parts.get(i)).passthrough$cuboids()) size += 28 + c.sides.length * (12 + 4 * 20);
        }
        ByteBuffer m = ByteBuffer.allocate(8 + size).order(ByteOrder.LITTLE_ENDIAN);
        m.putInt(MSG_MODEL).putInt(0);
        m.putInt(id).putShort((short) typeBytes.length).put(typeBytes).putInt(parts.size());
        for (int i = 0; i < parts.size(); i++) {
            byte[] name = names.get(i).getBytes(java.nio.charset.StandardCharsets.UTF_8);
            m.putShort((short) name.length).put(name).putInt(parents.get(i));
            var t = parts.get(i).getDefaultTransform();
            m.putFloat(t.x()).putFloat(t.y()).putFloat(t.z()).putFloat(t.pitch()).putFloat(t.yaw()).putFloat(t.roll())
                .putFloat(t.xScale()).putFloat(t.yScale()).putFloat(t.zScale());
            var cuboids = ((ModelPartAccessor) (Object) parts.get(i)).passthrough$cuboids();
            m.putInt(cuboids.size());
            for (ModelPart.Cuboid c : cuboids) {
                m.putFloat(c.minX).putFloat(c.minY).putFloat(c.minZ).putFloat(c.maxX).putFloat(c.maxY).putFloat(c.maxZ);
                m.putInt(c.sides.length);
                for (ModelPart.Quad q : c.sides) {
                    m.putFloat(q.direction().x()).putFloat(q.direction().y()).putFloat(q.direction().z());
                    for (ModelPart.Vertex v : q.vertices())
                        m.putFloat(v.pos().x()).putFloat(v.pos().y()).putFloat(v.pos().z()).putFloat(v.u()).putFloat(v.v());
                }
            }
        }
        m.putInt(4, m.position() - 8);
        m.flip();
        return m;
    }

    private void flatten(ModelPart part, String name, int parent, List<ModelPart> out, List<String> names, List<Integer> parents) {
        int index = out.size();
        out.add(part);
        names.add(name);
        parents.add(parent);
        for (var child : ((ModelPartAccessor) (Object) part).passthrough$children().entrySet())
            flatten(child.getValue(), child.getKey(), index, out, names, parents);
    }

    /** Records what feature renderers draw: every 4 vertices become a quad with its layer's texture. */
    private final class Capture implements net.minecraft.client.render.VertexConsumerProvider, net.minecraft.client.render.VertexConsumer {
        static final int MAX_QUADS = 4096;
        final ByteBuffer quadsOut = ByteBuffer.allocate(MAX_QUADS * 100).order(ByteOrder.LITTLE_ENDIAN);
        final float[] v = new float[4 * 5];
        final int[] colors = new int[4];
        int quads, vertex = -1, layerTexture;
        MinecraftClient mc;
        Consumer<ByteBuffer> send;

        void begin(MinecraftClient mc, Consumer<ByteBuffer> send) {
            this.mc = mc;
            this.send = send;
            quadsOut.clear();
            quads = 0;
            vertex = -1;
        }

        ByteBuffer end() {
            quadsOut.flip();
            return quadsOut;
        }

        @Override
        public net.minecraft.client.render.VertexConsumer getBuffer(net.minecraft.client.render.RenderLayer layer) {
            layerTexture = textureOf(layer);
            vertex = -1;
            return this;
        }

        private int textureOf(net.minecraft.client.render.RenderLayer layer) {
            try {
                var phases = ((com.passthrough.realism.mixin.MultiPhaseAccessor) (Object) layer).passthrough$phases();
                var base = ((com.passthrough.realism.mixin.MultiPhaseParametersAccessor) (Object) phases).passthrough$texture();
                var id = ((com.passthrough.realism.mixin.TextureBaseInvoker) base).passthrough$getId();
                if (id.isEmpty()) return 0;
                if (id.get().getPath().startsWith("textures/atlas/")) return -1; // items and blocks: the block atlas
                return EntitySync.this.texture(mc, id.get(), send);
            } catch (ClassCastException e) {
                return 0;
            }
        }

        private void finishVertex() {
            if (vertex == 3) {
                if (quads < MAX_QUADS) {
                    quadsOut.putInt(layerTexture);
                    for (int k = 0; k < 4; k++)
                        quadsOut.putFloat(v[k * 5]).putFloat(v[k * 5 + 1]).putFloat(v[k * 5 + 2]).putFloat(v[k * 5 + 3]).putFloat(v[k * 5 + 4]).putInt(colors[k]);
                    quads++;
                }
                vertex = -1;
            }
        }

        @Override
        public net.minecraft.client.render.VertexConsumer vertex(float x, float y, float z) {
            finishVertex();
            vertex++;
            v[vertex * 5] = x; v[vertex * 5 + 1] = y; v[vertex * 5 + 2] = z;
            colors[vertex] = -1;
            return this;
        }

        @Override
        public net.minecraft.client.render.VertexConsumer color(int r, int g, int b, int a) {
            if (vertex >= 0) colors[vertex] = (a << 24) | (r << 16) | (g << 8) | b;
            return this;
        }

        @Override
        public net.minecraft.client.render.VertexConsumer texture(float u, float vv) {
            if (vertex >= 0) { v[vertex * 5 + 3] = u; v[vertex * 5 + 4] = vv; }
            return this;
        }

        @Override public net.minecraft.client.render.VertexConsumer overlay(int u, int vv) { return this; }
        @Override public net.minecraft.client.render.VertexConsumer light(int u, int vv) { return this; }

        @Override
        public net.minecraft.client.render.VertexConsumer normal(float x, float y, float z) {
            finishVertex(); // normal is the last element of each vertex
            return this;
        }
    }

    /** Texture id for a resource; the PNG is sent the first time (empty if it can't be read: drawn plain). */
    private int texture(MinecraftClient mc, Identifier id, Consumer<ByteBuffer> send) {
        Integer known = textures.get(id);
        if (known != null) return known;
        int texId = textures.size() + 1;
        textures.put(id, texId);
        byte[] png = new byte[0];
        try {
            var res = mc.getResourceManager().getResource(id);
            if (res.isPresent()) try (var in = res.get().getInputStream()) { png = in.readAllBytes(); }
        } catch (java.io.IOException ignored) { }
        ByteBuffer m = ByteBuffer.allocate(16 + png.length).order(ByteOrder.LITTLE_ENDIAN);
        m.putInt(MSG_TEXTURE).putInt(8 + png.length).putInt(texId).putInt(png.length).put(png);
        m.flip();
        send.accept(m);
        return texId;
    }

    /** An item's icon: its item texture, or for blocks the block texture (drawn as a small cube). */
    private int[] itemTexture(MinecraftClient mc, Identifier item, Consumer<ByteBuffer> send) {
        return itemTextures.computeIfAbsent(item, it -> {
            String[][] candidates = {
                { "textures/item/" + it.getPath() + ".png", "0" },
                { "textures/block/" + it.getPath() + ".png", "1" },
                { "textures/block/" + it.getPath() + "_side.png", "1" },
                { "textures/block/" + it.getPath() + "_top.png", "1" },
            };
            for (String[] c : candidates) {
                Identifier id = Identifier.of(it.getNamespace(), c[0]);
                if (mc.getResourceManager().getResource(id).isPresent()) return new int[] { texture(mc, id, send), Integer.parseInt(c[1]) };
            }
            return new int[] { 0, 1 };
        });
    }
}
