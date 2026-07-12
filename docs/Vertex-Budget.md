# The GPU Vertex Budget — how many custom models you can add

The real limit on custom models is **not download size** (that compresses ~5:1 in the shipped
bundle; a 190 MB bundle zips to ~69 MB, and mod.io's soft limit is 100 MB with generous headroom
above). The real limit is a **GPU vertex buffer** the game packs every skinned mesh into. This page
records what that buffer actually is, measured live, and how to budget against it.

## The mechanism (decompiled: `Amplitude.Graphics.FxComponentMeshContentManager`)

- Skinned pawn meshes are packed into **shared GPU buffers, one per "content layer."** Each layer has a
  vertex buffer, an index buffer, and a mesh registry (`FxOneMeshStruct[]`), filled by a running
  `currentVertexIndex` / `currentIndexIndex` / `currentMeshAddedCount` cursor.
- **Instancing makes copies free.** Rendering is `DrawMeshInstancedIndirect` — each **unique mesh is
  stored once** and drawn for every pawn of that type. 1 tank or 100 tanks of the same type = **one**
  entry. Units on screen are irrelevant to the budget.
- **Overflow is silent.** When the cursor would exceed a buffer, the manager logs
  `"Unable to store mesh … vertex buffer is not large enough"` and **drops the mesh** — you get
  missing / see-through geometry in-game (this is the vanished-rotor-mast class of bug), never a crash.
- The `baseVertexBufferSize = 100000` / `baseIndexBufferSize = 250000` / `maxMeshCount = 256` constants
  in the decompiled source are only **default initializers** — at runtime the game sizes the layers
  **far larger** (see measured values below). Do not trust the source defaults; measure.

## Measured live (Shift+F8 / the F8 window, a Contemporary-era save)

Three layers exist; custom models land in the pawn layer (`FXMeshLayerIndex = 2`):

| layer | name | vertices | indices | meshes |
|---|---|---|---|---|
| 0 | `Visual` | 2,995,550 / **3,000,000** (99%) | 5.73M / 9M (63%) | 4606 / 8000 |
| 1 | `Emitter` | 2,700 / 10,000 (27%) | 10k / 90k (11%) | 20 / 2000 |
| **2** | **`MeshWithSkeletonParticleIndexBuffer`** ← **custom models** | **694,126 / 1,000,000 (69%)** | 1.48M / 6.5M (22%) | 695 / 2500 |

**The pawn-layer ceiling is ~1,000,000 vertices / 6,500,000 indices / 2,500 meshes** — 10× the source
default. Vertices are the binding constraint (69% used vs 22% indices / 28% mesh slots). Layer 0
`Visual` sitting at 99% is the **game's own** fx/particle buffer, unrelated to your models.

## What actually counts

**buffer used = Σ (vertices of each *distinct loaded model type*)** — independent of instance count.

- **Not units on screen** — instancing; copies are free.
- **Not the whole catalog** — only meshes that are **loaded** register. The layer held 695 meshes while
  ~10 units were visible, so it's "loaded types," far more than what's on screen, but not everything
  possible. A roster unit that never loads in a given game costs nothing.
- **Diversity is the cost, weighted by size.** A wide variety of *lean* units is cheap; a few *heavy*
  unique types can cost as much as many lean ones.

## The concern for THIS mod: era-clustering

Most ENCReload custom models are **Industrial / Contemporary**. Because they're bunched in the late
eras rather than spread across all six, a **late-game save loads most of the custom roster at once** —
they all share the pawn layer's ~1M budget simultaneously. So:

- **Size for the latest-era peak**, not an average. The 69% reading above is already near that peak
  (it's a Contemporary save with helicopters/drones/ship/howitzer), leaving **~306k vertices free** —
  room for roughly **7–10 more heavy (30–42k) types** or **~20 lean (~15k) ones**.
- **Budget the sum of same-era models**, not each in isolation — every new Contemporary unit adds to a
  pool already carrying the others.
- **Leanness compounds**: trimming each model 40k→15k is the difference between fitting the whole
  late-era set and silently dropping geometry (see the weld / low-poly notes in the Factory manual).

## How to measure (in-game)

The plugin exposes the live buffer usage:

- **F8** opens the ENC Access Proof window — the pawn-layer usage is shown live and updates as units
  load/spawn.
- **Shift+F8** logs the same readout to `BepInEx/LogOutput.log` (`[Budget]` lines).

Use it empirically: open F8 on a fresh game, then build your first custom unit — if the vertex count
*ticks up now* it's on-demand per type; if it was *already* counted it's all-at-load. Spawn 10 more of
the same unit and it won't move (instancing). Compare an early-era vs late-era save to see whether the
game **unloads** earlier eras (plateaus) or **accumulates** them (keeps climbing).

## Open questions (not yet pinned down)

1. **When does a custom model register** — all-at-load (the plugin uploads every registry entry at game
   start) or on-demand (first spawn of that type)? Determines whether an unused custom type still costs.
   Measurable live per above; also traceable in the plugin's `EnsureUploaded` / injection hooks.
2. **Per-era unload vs accumulate** — does advancing eras free earlier-era meshes, or do they stay
   resident? Determines whether the real ceiling is "all Contemporary models" or "all models, all eras."
