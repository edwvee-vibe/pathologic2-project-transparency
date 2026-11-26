# Pathologic 2 Project Transparency

A BepInEx 5 plugin for Pathologic 2 (2.1) that makes the world semi-transparent: walls, houses, people, props. Grass and the ground (terrain) stay opaque.
Press **Page Up** in game to toggle, **Page Down** to cycle the four modes:
**Fade → FadeOld → Dither → DitherOld**. `Fade` and `Dither` are the current ones; `FadeOld` and `DitherOld`
are the modes of 1.0.0, kept unchanged.

## Why

Just for fun, and to show how weak the game's occlusion culling is. With the walls see-through you can watch it work:
the houses, yards and props behind a house are drawn the whole time you walk up to it, and they disappear only when
you stand right at the wall, about a metre from the door. The game's baked occlusion data (Umbra) covers only the
house shells (about 12 thousand of some 213 thousand renderers), and an object is hidden only when those shells
cover it completely, which from the street almost never happens. Leave `DisableOcclusionCulling = false`
(the default) to see it.

In other words, **everything you see through the walls with the mod on is rendered by the game with the mod off
too.** Which objects get drawn is decided before rendering, from the camera frustum, layer cull distances, LODs,
distance-based streaming and the baked occlusion data, none of which depend on materials. With
`DisableOcclusionCulling = false` the mod only swaps shaders, so the set of drawn objects is the same. The only
difference is that behind opaque walls most of their pixels fail the depth test and are not shaded: the game pays
for their draw calls and vertices, not for full shading.

## Performance

Expect a noticeable FPS drop in every mode. With the walls see-through, everything behind them is not only drawn but
also fully shaded, and Fade adds extra forward passes for every nearby light. This is a just-for-fun project, so the
author does not worry much about it.

## How it works

The game already has dithered ("screen-door") versions of its main shaders, used for soft hair edges:
`Pathologic/Universal Dithered` and `Pathologic/Universal Metallic Dithered`. They draw only part of the pixels,
depending on the opacity and a noise pattern, and TAA / DLSS blend the pattern into translucency.
They have exactly the same properties as the shaders most of the town uses (`Universal Opaque/Cutout`,
`Universal Metallic Opaque/Cutout`), so the plugin swaps the shader on every loaded material and sets `_Color.a`.

- Everything stays in the deferred pass: lighting, shadows and fog keep working, no sorting problems.
- Game files are not modified; everything happens in memory and is put back when toggled off.
- Newly streamed houses and people are picked up on scene load and by a rescan every 2 s.
- Optionally (`DisableOcclusionCulling = true`) occlusion culling is turned off while the mod is on, so houses hidden behind other houses are drawn too. Off by default: the game culls them only when you stand right at a wall, so the gain is small and the cost is FPS.

Limitations:
- The pattern is visible as a fine grain; it looks best with TAA, DLAA or DLSS.
  With the hair-dither fix (`_Time.y`) the pattern changes every frame and is smoothed much better.
- Shadows are dithered as well (half-strength, slightly noisy).
- Terrain, decals, glass, particles, grass and UI are left as they are (see below why the ground and grass).
- `Scope = AllOpaque` also converts skin, trees and other special shaders as a best effort: tree wind and
  skin shading are lost while the mod is on.

### Why the ground and grass stay opaque

They are not impossible to convert; the mod leaves them out on purpose, because a plain shader swap breaks them:
- **Ground (terrain).** It is drawn by Unity's terrain system with its own splat-map shaders
  (`Pathologic/Nature/Terrain/Standard` plus hidden base and add passes that blend several ground textures by a control map).
  The Universal shaders do not understand splat maps, so the ground would lose its textures. And there is nothing
  under the terrain: a see-through ground would show the sky below your feet.
- **Grass.** It is not Unity's terrain grass but the game's own `TerrainDetailLayer`, which builds grass meshes in
  chunks with the shader `Pathologic/Special/Terrain Details`. That shader has its own properties (wind, ground normal
  lighting, size range, `_Cutout` instead of `_Cutoff`, `_DrawDistance`) that the Universal shaders lack. Grass would stop
  swaying and be lit differently, and the game reads `_DrawDistance` from the material every frame to decide which
  chunks to build, so after a swap the grass might vanish beyond your feet (not tested).

Separate grass and bush meshes (`Standard (Specular setup)` materials such as `Burdock_A`, `Bush_01_A`) are converted.

## Fade modes

The default: real alpha blending with the game's `Pathologic/Universal Fade` /
`Universal Metallic Fade` shaders (again the same properties). Smooth, no grain, true layering, but:
- converted objects are drawn in the forward path, once more for every extra light nearby;
- they cast no shadows (the Fade shaders have no shadow pass) and receive no deferred fog or ambient occlusion;
- objects are sorted as a whole, so large meshes can appear in the wrong order;
- no depth, so DLSS/TAA may smear them while moving.
- the higher the alpha, the worse the sorting looks: at 0.9 an object drawn later covers an object in front of it almost
  completely, so some things look solid, others see-through, and some seem to vanish. Use Dither for high alpha.

## Current modes and the old ones

`FadeOld` and `DitherOld` are exactly the Fade and Dither of 1.0.0. The current `Fade` and `Dither` add fixes on top:

| Mode | Old mode | Adds |
|---|---|---|
| `Fade` | `FadeOld` | dull specular for best-effort materials; dithered eyeballs, teeth and mouths; a better drawing order |
| `Dither` | `DitherOld` | TAA smoothing of the grain; dull specular for best-effort materials |

- **TAA smoothing (Dither).** The game's TAA (Post Processing Stack v1) averages more frames: the share of the
  history goes up from 0.95 to 0.98 for still pixels and from 0.85 to 0.95 for moving ones, and TAA sharpening (0.25,
  it brings the grain back) is turned off. The pattern then blurs into translucency, at the cost of more ghosting while
  moving. Works only with the game's TAA: the DLSS mod replaces the TAA pass, so press F8 there to switch back.
  The values are in the `[Dither]` section of the config (blending up to 0.99).
- **Dull specular (both).** The Universal shaders default to a white specular color and map, which turns trees, bushes
  and skin of other shaders (`Scope = AllOpaque`) into white mirrors. In these modes such materials get a dull specular
  (4%, low smoothness).
- **Heads (Fade).** With alpha blending nothing writes depth, so in FadeOld eyeballs and teeth either show over the face
  or vanish under the back of the head and the hair. In Fade they (materials named `*Eye*`, `*Mouth*`, `*Teeth*`) are drawn
  dithered with the same `Alpha`: they write depth, so the back of the head cannot paint over them, and the see-through
  face and eyelids are blended over them. The game's own eye gloss layers are drawn before the faces.

- **Drawing order (Fade).** FadeOld draws see-through objects far first by the center of their bounds, without depth.
  At a high alpha the object drawn last covers the others: a room (one big mesh with its center next to the camera)
  covered everything in it, a table covered the branch lying on it, a head covered the fringe. Fade reorders them
  (`Renderer.sortingOrder`, recomputed every scan): houses and rooms (over 12 m) first; objects within 40 m bottom first,
  by the lowest point of their bounds (the floor before the table, the table before the branch on it); characters by the
  height of their feet, bigger parts first (body before shirt, head before fringe); everything farther by distance.
  Any order without depth is still wrong somewhere: a small thing behind a wall may show through it more than it should.

Everything is put back when the mod is turned off or switched to another mode, so in DitherOld and FadeOld the trees,
bushes and skin of other shaders stay white as before.

## What `Alpha` means

`Alpha` is opacity: 1 is solid, 0.99 is nearly solid, 0.5 is half see-through. The shaders multiply it by the alpha
channel of the texture, and some textures (skin, for example) keep other data there, so some objects stay more
see-through than others. The mod cannot change that.

## Settings

`BepInEx\config\pathologic2.projecttransparency.cfg`:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Mod on/off (also toggled by the key) |
| `Mode` | `Fade` | `Fade`, `FadeOld`, `Dither`, `DitherOld` (see above) |
| `Alpha` | `0.5` | Opacity of every surface, 0–1 |
| `Scope` | `AllOpaque` | `Universal`: only the game's main shaders; `AllOpaque`: every opaque shader |
| `DisableOcclusionCulling` | `false` | Draw houses hidden behind other houses |
| `RescanInterval` | `2` | Seconds between scans for new materials |
| `ToggleKey` | `PageUp` | Toggle key |
| `ModeKey` | `PageDown` | Cycle the modes (also turns the mod on) |
| `ShowMessages` | `true` | On-screen message when toggling |
| `[Dither] StationaryBlending` | `0.98` | TAA history share in still pixels (game: 0.95) |
| `[Dither] MotionBlending` | `0.95` | TAA history share in moving pixels (game: 0.85) |
| `[Dither] Sharpen` | `0` | TAA sharpening (game: 0.25) |

## Build and install

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1   # -Uninstall to remove
```
