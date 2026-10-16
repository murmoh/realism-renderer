using System.Collections.Concurrent;

namespace Realism.Minecraft;

/// <summary>
/// The renderer doesn't care about every Minecraft block, only what it means for the landscape.
/// Each block name collapses to one of these material classes.
/// </summary>
public static class Mat
{
    // Non-terrain
    public const byte Air = 0, Plant = 1, Water = 2, Lava = 3, SnowLayer = 5, FrozenWater = 6,
        TallGrass = 7, Flower = 8, // ground cover: drawn as instanced grass blades and flowers
        Torch = 9;                 // small light sources: torches, lanterns, campfires, candles

    // Flowing water: 150 + Minecraft level (1..7 spreading, 8 falling). Plain Water is a still source block.
    public const byte FlowingWater = 150;
    public static byte WaterLevel(int level) => level <= 0 ? Water : (byte)(FlowingWater + Math.Min(level, 8));
    public static bool IsWater(byte m) => m is Water or FrozenWater || m is >= FlowingWater and <= FlowingWater + 8;
    public static bool IsFalling(byte m) => m == FlowingWater + 8;

    // Flowing lava: 160 + level, like water. Plain Lava is a still source block.
    public const byte FlowingLava = 160;
    public static byte LavaLevel(int level) => level <= 0 ? Lava : (byte)(FlowingLava + Math.Min(level, 8));
    public static bool IsLava(byte m) => m == Lava || m is > FlowingLava and <= FlowingLava + 8;
    /// <summary>Surface height of a lava block: sources a little below the top, thinner as it spreads, falling lava full.</summary>
    public static float LavaHeight(byte m) => m == Lava ? 0.875f : m == FlowingLava + 8 ? 1f : (8 - (m - FlowingLava)) / 9f;

    /// <summary>Surface height of a water block, as in Minecraft: sources 8/9, thinner as flow spreads, falling water full.</summary>
    public static float WaterHeight(byte m) => m == FrozenWater ? 0.9f : m == Water ? 0.889f : IsFalling(m) ? 1f : (8 - (m - FlowingWater)) / 9f;

    // Terrain (solid, smoothed into the landscape surface)
    public const byte Grass = 10, Dirt = 11, Stone = 12, Sand = 13, Snow = 14, Gravel = 15, Clay = 16, Ice = 17,
        Amethyst = 18, // geode walls: smooth terrain with a crystal look (shares the snow/ice channel, see Channel)
        // Ores: smooth rock like stone, with Minecraft's own ore texture painted on where they're exposed (see the
        // Mesher's ore overlay). Ore + n sits in rock OreRocks[n].
        Ore = 19, DeepslateOre = 20, NetherOre = 21;
    public static bool IsOre(byte m) => m is Ore or DeepslateOre or NetherOre;
    /// <summary>The plain rock each ore class is set in; its texture tells the ore's own pixels apart.</summary>
    public static readonly string[] OreRocks = ["minecraft:stone", "minecraft:deepslate", "minecraft:netherrack"];

    /// <summary>Nether portal: Minecraft's block model, drawn as a swirling, glowing surface (frame kept crisp).</summary>
    public const byte Portal = 147;

    // Amethyst buds and clusters (170 + growth stage 0 small .. 3 full cluster): drawn as crystal prisms.
    public const byte Crystal = 170;
    public static bool IsCrystal(byte m) => m is >= Crystal and <= Crystal + 3;

    public static bool IsTerrain(byte m) => m >= Grass && m < Leaves;

    // Trees: 40 + species = leaves, 50 + species = logs.
    public const byte Leaves = 40, Wood = 50;
    public const int Oak = 0, Birch = 1, Spruce = 2, Jungle = 3, Acacia = 4, DarkOak = 5, Cherry = 6, Azalea = 7, Mangrove = 8, PaleOak = 9;

    public static bool IsLeaves(byte m) => m is >= Leaves and < Leaves + 10;
    public static bool IsWood(byte m) => m is >= Wood and < Wood + 10;
    public static int Species(byte m) => m % 10;

    // Buildings: 60 + appearance * 3 + shape. Drawn as crisp blocks, not smoothed into the landscape.
    public const byte BuiltBase = 60;
    public const int OakPlanks = 0, SprucePlanks = 1, DarkPlanks = 2, BirchPlanks = 3, CherryPlanks = 4, WarmPlanks = 5,
        Cobblestone = 6, StoneBricks = 7, Bricks = 8, Glass = 9, Metal = 10, Misc = 11, Dyed = 12, // Dyed + 0..15 colours
        Glow = 28;                 // light-emitting blocks: glowstone, sea lanterns, shroomlights, lamps
    /// <summary>Appearance codes 30..36: a natural material (terrain channel 0..6) drawn as a crisp block.</summary>
    public const int TerrainLook = 30;
    /// <summary>Appearance codes 38..47: a building log of species 0..9 (bark sides, rings on the ends).</summary>
    public const int LogLook = 38;
    public const int ShapeFull = 0, ShapeSlab = 1, ShapePost = 2;

    public static byte Built(int appearance, int shape) => (byte)(BuiltBase + appearance * 3 + shape);
    public static bool IsBuilt(byte m) => m is >= BuiltBase and < BuiltBase + (Glow + 1) * 3;
    public static int Appearance(byte m) => (m - BuiltBase) / 3;
    public static int Shape(byte m) => (m - BuiltBase) % 3;
    /// <summary>A full opaque block: hides the faces of neighbours touching it.</summary>
    public static bool IsOpaqueCube(byte m) => IsTerrain(m) || (IsBuilt(m) && Shape(m) == ShapeFull && Appearance(m) != Glass);

    public static int SpeciesOf(string name) =>
        name.Contains("birch") ? Birch : name.Contains("spruce") ? Spruce : name.Contains("jungle") ? Jungle
        : name.Contains("acacia") ? Acacia : name.Contains("pale_oak") ? PaleOak : name.Contains("dark_oak") ? DarkOak
        : name.Contains("cherry") ? Cherry : name.Contains("azalea") ? Azalea : name.Contains("mangrove") ? Mangrove : Oak;

    /// <summary>Surface material channel used for shading: grass, dirt, stone, sand, snow, gravel, clay.</summary>
    public const int ChannelCount = 7;

    public static int Channel(byte m) => m switch
    {
        // Amethyst shares channel 4 with snow and ice: the terrain shader draws it as crystal where the ground is
        // covered (geodes are deep underground; snow never forms there).
        Grass => 0, Dirt => 1, Sand => 3, Snow or Ice or Amethyst => 4, Gravel => 5, Clay => 6, _ => 2,
    };
}

public static class Blocks
{
    static readonly ConcurrentDictionary<string, byte> Cache = new();

    public static byte Classify(string name) => Cache.GetOrAdd(name, ClassifyUncached);

    /// <summary>Live mode: a block state id of each of Mat.OreRocks (0 until seen), for the ore overlay.</summary>
    public static readonly int[] OreRockStates = new int[3];

    /// <summary>Notes the state id of a block name the bridge has seen (only the ore rocks are kept).</summary>
    public static void SeeState(string name, int state)
    {
        int r = Array.IndexOf(Mat.OreRocks, name);
        if (r >= 0 && OreRockStates[r] == 0) Volatile.Write(ref OreRockStates[r], state);
    }

    /// <summary>Every block name seen so far and the material it was mapped to.</summary>
    public static IEnumerable<KeyValuePair<string, byte>> Classified => Cache;

    static readonly string[] NonSolid =
    {
        "grass", "fern", "flower", "dandelion", "poppy", "tulip", "orchid", "allium", "bluet", "daisy", "cornflower", "lily", "rose",
        "peony", "lilac", "sunflower", "bush", "sugar_cane", "sapling", "vine", "torch", "berry", "bamboo",
        "cactus", "carpet", "rail", "button", "lever", "sign", "pressure_plate", "fire", "ladder", "lichen",
        "petals", "litter", "cobweb", "roots", "spore_blossom", "pointed_dripstone", "dripleaf", "azalea",
        "sprouts", "pickle", "eyeblossom", "pitcher", "wheat", "carrots", "potatoes", "beetroots", "cocoa",
        "banner", "head", "skull", "candle", "melon_stem", "pumpkin_stem", "hanging_moss", "frogspawn",
    };

    static readonly string[] Flowers =
    {
        "flower", "dandelion", "poppy", "tulip", "orchid", "allium", "bluet", "daisy", "cornflower", "lily_of_the_valley",
        "rose_bush", "peony", "lilac", "sunflower", "petals", "eyeblossom", "wildflowers",
    };

    static readonly string[] NaturalStone =
    {
        "stone", "deepslate", "granite", "diorite", "andesite", "tuff", "calcite", "dripstone", "_ore",
        "bedrock", "obsidian", "netherrack", "basalt", "blackstone", "magma", "amethyst",
    };

    static readonly string[] BuildingWords =
    {
        "brick", "cobble", "polished", "smooth_stone", "chiseled", "cut_", "tiles", "pillar", "_slab", "_stairs",
        "_wall", "planks", "glass", "wool", "concrete", "fence", "pane", "bars", "chain", "door", "bed", "_block",
        "copper", "lantern", "chest", "barrel", "table", "furnace", "bookshelf", "anvil", "hopper",
    };

    static readonly string[] DyeColors =
    {
        "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "light_gray",
        "gray", "cyan", "purple", "blue", "brown", "green", "red", "black",
    };

    /// <summary>Appearance and shape for a man-made block.</summary>
    static byte ClassifyBuilt(string n)
    {
        if (n.Contains("door")) return Mat.Air; // doors and trapdoors read as openings
        int shape = n.Contains("fence") || n.EndsWith("_wall") || n.Contains("pane") || n.Contains("bars") || n.Contains("chain") || n.Contains("lantern")
            ? Mat.ShapePost
            : n.Contains("_slab") || n.EndsWith("_bed") ? Mat.ShapeSlab : Mat.ShapeFull;

        int appearance;
        if (n.Contains("glass")) appearance = Mat.Glass;
        else if (n.Contains("cobble")) appearance = Mat.Cobblestone;
        else if (n.Contains("brick") && !n.Contains("stone") && !n.Contains("deepslate") && !n.Contains("mud") && !n.Contains("nether"))
            appearance = Mat.Bricks;
        else if (n.Contains("copper") || n.Contains("iron") || n.Contains("gold") || n.Contains("chain") || n.Contains("bars")
                 || n.Contains("lantern") || n.Contains("anvil") || n.Contains("hopper"))
            appearance = Mat.Metal;
        else if (Array.FindIndex(DyeColors, c => n.StartsWith(c + "_")) is var dye and >= 0
                 && (n.Contains("wool") || n.Contains("concrete") || n.EndsWith("_bed")))
            appearance = Mat.Dyed + dye;
        else if (n.Contains("spruce")) appearance = Mat.SprucePlanks;
        else if (n.Contains("dark_oak") || n.Contains("mangrove") || n.Contains("crimson")) appearance = Mat.DarkPlanks;
        else if (n.Contains("birch") || n.Contains("pale_oak") || n.Contains("bamboo")) appearance = Mat.BirchPlanks;
        else if (n.Contains("cherry")) appearance = Mat.CherryPlanks;
        else if (n.Contains("jungle") || n.Contains("acacia") || n.Contains("warped")) appearance = Mat.WarmPlanks;
        else if (n.Contains("oak") || n.Contains("planks") || n.Contains("chest") || n.Contains("barrel") || n.Contains("table") || n.Contains("bookshelf"))
            appearance = Mat.OakPlanks;
        else if (n.Contains("stone") || n.Contains("deepslate") || n.Contains("polished") || n.Contains("tiles") || n.Contains("brick") || n.Contains("furnace"))
            appearance = Mat.StoneBricks;
        else appearance = Mat.Misc;
        return Mat.Built(appearance, shape);
    }

    static byte ClassifyUncached(string full)
    {
        string n = full.StartsWith("minecraft:") ? full[10..] : full;

        switch (n)
        {
            case "air" or "cave_air" or "void_air" or "structure_void" or "light" or "barrier": return Mat.Air;
            case "water" or "bubble_column" or "kelp" or "kelp_plant" or "seagrass" or "tall_seagrass": return Mat.Water;
            case "snow": return Mat.SnowLayer;
            case "amethyst_block" or "budding_amethyst": return Mat.Amethyst;
            case "small_amethyst_bud": return Mat.Crystal;
            case "medium_amethyst_bud": return Mat.Crystal + 1;
            case "large_amethyst_bud": return Mat.Crystal + 2;
            case "amethyst_cluster": return Mat.Crystal + 3;
            case "grass_block" or "podzol" or "mycelium" or "moss_block" or "pale_moss_block": return Mat.Grass;
            case "dirt" or "coarse_dirt" or "rooted_dirt" or "dirt_path" or "farmland" or "mud" or "muddy_mangrove_roots":
                return Mat.Dirt;
            case "snow_block" or "powder_snow": return Mat.Snow;
            case "gravel" or "suspicious_gravel": return Mat.Gravel;
            case "ice" or "frosted_ice": return Mat.FrozenWater; // thin sheets on lakes: rendered as a frozen water surface
            case "packed_ice" or "blue_ice": return Mat.Ice;
            case "red_sand" or "clay": return Mat.Clay;
            case "sand" or "suspicious_sand": return Mat.Sand;
            case "lava": return Mat.Lava;
            case "ancient_debris": return Mat.NetherOre;
            case "nether_portal": return Mat.Portal;
            case "short_grass" or "tall_grass" or "grass" or "fern" or "large_fern" or "short_dry_grass" or "tall_dry_grass":
                return Mat.TallGrass;
        }

        if (n.EndsWith("_ore")) return n.StartsWith("deepslate_") ? Mat.DeepslateOre : n.StartsWith("nether_") ? Mat.NetherOre : Mat.Ore;
        if (n.EndsWith("leaves")) return (byte)(Mat.Leaves + Mat.SpeciesOf(n));
        if (n.Contains("terracotta") || n.Contains("red_sandstone")) return Mat.Clay;
        if (n.Contains("sandstone")) return Mat.Sand;
        if ((n.EndsWith("_log") || n.EndsWith("_wood") || n.EndsWith("_stem")) && !n.Contains("mushroom") && !n.Contains("dripleaf")
            && !n.Contains("melon") && !n.Contains("pumpkin"))
            return (byte)(Mat.Wood + Mat.SpeciesOf(n));
        if (n.Contains("coral") && !n.Contains("block")) return Mat.Water;
        if (n is "glowstone" or "sea_lantern" or "shroomlight" or "redstone_lamp" or "beacon" || n.EndsWith("froglight") || n.Contains("jack_o_lantern") || n.EndsWith("copper_bulb"))
            return Mat.Built(Mat.Glow, Mat.ShapeFull);
        if ((n.EndsWith("torch") && !n.Contains("redstone")) || n.EndsWith("lantern") || n.Contains("campfire") || n.EndsWith("candle") || n == "end_rod")
            return Mat.Torch;
        if (n.Contains("mushroom") && !n.Contains("block") && !n.Contains("stem")) return Mat.Plant;
        foreach (var k in Flowers)
            if (n.Contains(k)) return Mat.Flower;
        foreach (var k in NonSolid)
            if (n.Contains(k)) return Mat.Plant;
        bool building = BuildingWords.Any(n.Contains);
        foreach (var k in NaturalStone)
            if (n.Contains(k) && !building) return Mat.Stone;
        return ClassifyBuilt(n);
    }
}
