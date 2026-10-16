# Realism Renderer

A realistic C# renderer for Minecraft terrain, inspired by [SkyCraft](https://github.com/chasmlol/SkyCraft).
Minecraft decides *what* the world is; this program decides *how it looks*.

## Run

```
cd Realism
dotnet run -c Release                      # newest world in %APPDATA%\.minecraft\saves
dotnet run -c Release -- "ahmeds world"    # a specific save (name or full path)
dotnet run -c Release -- "New World" --radius 20 --time 0.1
```

| Option | Meaning |
|---|---|
| `--radius N` | view distance in chunks; the world streams in around the camera as you fly (default 12) |
| `--time t` | time of day: 0 sunrise, 0.5 noon, 1 sunset, 1.5 midnight (T/G keep it moving through night) |
| `--cam x,y,z,yaw,pitch` | start camera |
| `--screenshot file.png` | render one frame, save it, exit |
| `--weather w` | `clear`, `overcast` or `rain` |
| `--blocks` | print how each block type was classified |

Controls: mouse look, WASD fly, Space/C up/down, Shift fast, mouse wheel speed, T/G move the sun, R weather, F2 screenshot, Esc quit.

## Live sync with Minecraft

Like SkyCraft: Minecraft runs the game, this renderer draws it.

1. One command starts both:
   `dotnet run --project .\Realism\ -c Release -- --live "New World (4)"`
   This launches Minecraft 1.21.5 + Fabric with the bridge mod (offline singleplayer, your real saves) straight
   into that world, then the renderer syncs with it. Leave out the world name to get the title screen.
   Add `--no-launch` if you'd rather start Minecraft yourself from the official launcher (Fabric 1.21.5
   profile with `realism-renderer-0.1.0.jar` and Fabric API in `%APPDATA%\.minecraft\mods`).

Play from the renderer window: it captures the mouse and drives the Minecraft player: WASD move, Space jump,
Shift sneak, Ctrl sprint, mouse look, left click mine/attack, right click place/use, middle click pick block,
Q drop, 1-9 or the wheel for the hotbar. **Esc** releases the mouse (click the window to take it back); while
released, playing directly in the Minecraft window works as before. **F** switches to a free camera and back.
Time of day and weather follow Minecraft. Either side can start first; switching worlds resyncs automatically.
Menus and the inventory screen still appear in the Minecraft window.

Smooth movement: the renderer sends Minecraft the smooth surface it draws (16 heights per block column, from the
same field as the mesh), and the mod reshapes the player's collision with natural ground to match, so you walk on
the hills you see instead of block steps (singleplayer; mobs and builds keep vanilla collision). The camera eases
over any remaining steps. **V** switches to a third-person camera that shows your character; the body (walk cycle,
head turn, arm swing, sneaking) mirrors Minecraft.

How it works: the mod (`MinecraftMod/`) writes into a memory-mapped file
`%LOCALAPPDATA%\RealismRenderer\bridge.bin`: a header with the camera state (written every frame) and a
64 MB ring buffer of messages: chunk loaded (full block data), chunk unloaded, block changed. The renderer
(`Realism/Live/MinecraftBridge.cs`, which also documents the byte layout) applies them on a background thread
and re-meshes only the affected chunks. Only Minecraft's loaded chunks are shown, so the view distance follows
Minecraft's render distance.

## How it works

1. **Minecraft/**: reads the save directly. Region files (`.mca`), NBT and paletted chunk sections are
   collapsed into material classes (grass, dirt, stone, sand, snow, water, leaves, wood, …) in `Blocks.cs`.
2. **Meshing/**: `ChunkStreamer` loads and meshes chunks nearest-first on a background thread as the camera moves;
   `Mesher` turns each chunk into geometry:
   - block occupancy becomes a density field, blurred 3x, then polygonized with **Surface Nets** so cubes
     become smooth hills and cliffs; lighting normals come from a 5x-blurred field to hide Minecraft's staircases
   - surface materials blend over nearby *exposed* blocks; ambient occlusion and canopy shade are baked per vertex
   - deep caves are filled in (hidden), logs become round trunks, every leaf block becomes a cluster of
     alpha-cut leaf cards (lit as a canopy volume), grass blocks grow instanced grass tufts (tall grass and
     flowers from Minecraft plants),
     water/ice become surfaces that extend under the shore for curved waterlines
3. **Rendering/**: OpenGL 3.3 via Silk.NET. Procedural 3D-noise materials with bump detail, rock exposed on
   steep slopes, two shadow cascades (4096² for the whole area, 2048² following the camera), wind on grass and
   leaves, analytic sky with clouds, height fog. Water reads the opaque scene behind it: depth-based colour
   absorption, refraction and screen-space reflections. Tree species (oak, birch, spruce, cherry, dark oak,
   pale oak, …) get their own bark, leaf shape and colour. Rocks and pebbles are scattered by ground type
   (pebbles on gravel, scree and boulders on stone, the odd mossy rock in grass).
   Buildings stay crisp blocks (planks per wood type, cobblestone, bricks, glass, wool/concrete colours,
   slabs, fences and panes) with voxel AO. Weather (R): overcast greys the sky and softens shadows; rain adds
   falling streaks, wet glossy surfaces, puddles and ripples on water.
   Night: the moon takes over as the shadow-casting light, with stars, a Milky Way band and a cratered moon;
   torches, lanterns, glowstone and other light blocks glow and light their surroundings (32 nearest lights),
   and the image dims toward blue-grey like human night vision.
4. **Rendering/PostProcess.cs**: HDR pipeline. 4x MSAA float scene buffer, SSAO, volumetric sunbeams
   (raymarched through the shadow maps), bloom mip chain, ACES tone
   mapping and vignette. FPS is shown in the window title.

## Roadmap

- **Milestone 1 (done):** render a saved Minecraft world realistically, streaming chunks as you fly.
- **Milestone 2 (done):** live sync. A Fabric mod (Java) runs inside Minecraft and writes loaded chunks, block
  changes and player state into shared memory; this app reads it and re-meshes changed chunks.
- **Milestone 3 (done):** two-way sync. Keyboard/mouse from this window drive the Minecraft player; digging/placing
  happens in Minecraft and appears here. (Same architecture as SkyCraft, with this renderer in Skyrim's place.)
- Visual: snowfall, thunderstorms, smooth waterfalls, stairs as real stair shapes.
