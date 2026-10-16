package com.realismrenderer;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import net.fabricmc.loader.api.FabricLoader;

/** The mod's settings, saved as config/realism_renderer.json (changed from the settings screen, see RealismConfigScreen). */
public final class RealismConfig {
    private static final Gson GSON = new GsonBuilder().setPrettyPrinting().create();
    private static final Path FILE = FabricLoader.getInstance().getConfigDir().resolve("realism_renderer.json");
    private static RealismConfig instance;

    /** Break one log of a natural tree and the whole tree comes down (see TreeFeller). */
    public boolean treeFelling = true;

    public static RealismConfig get() {
        if (instance == null) instance = load();
        return instance;
    }

    private static RealismConfig load() {
        try {
            if (Files.exists(FILE)) {
                RealismConfig c = GSON.fromJson(Files.readString(FILE), RealismConfig.class);
                if (c != null) return c;
            }
        } catch (IOException | RuntimeException e) {
            System.err.println("[Realism Renderer] Couldn't read " + FILE + ", using defaults: " + e);
        }
        RealismConfig c = new RealismConfig();
        c.save();
        return c;
    }

    public void save() {
        try {
            Files.createDirectories(FILE.getParent());
            Files.writeString(FILE, GSON.toJson(this));
        } catch (IOException e) {
            System.err.println("[Realism Renderer] Couldn't save " + FILE + ": " + e);
        }
    }
}
