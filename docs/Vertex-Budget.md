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
- **There is NO hard per-mesh cap in practice.** Each layer carries a `maxMeshTriangleCount` — a
  per-mesh triangle ceiling whose overflow is even nastier than the buffer's: quads beyond it are
  **silently truncated at encode** (`FillIndexBufferContentFromQuad` clamps to the cap) — the model
  renders with holes and *nothing* is logged. But the shipped layer data sets it to **0 = unlimited on
  every layer** (verified live), so a single unit's practical limit is simply the free space left in
  the shared pool.

## Measured live (F8 window ▸ "Mesh Budget", a Contemporary-era save)

Three layers exist; custom models land in the pawn layer (`FXMeshLayerIndex = 2`):

| layer | name | vertices | indices | meshes | maxTris/mesh |
|---|---|---|---|---|---|
| 0 | `Visual` | 2,995,550 / **3,000,000** (99%)¹ | 5.73M / 9M (63%) | 4606 / 8000 | unlimited |
| 1 | `Emitter` | 2,700 / 10,000 (27%) | 10k / 90k (11%) | 20 / 2000 | unlimited |
| **2** | **`MeshWithSkeletonParticleIndexBuffer`** ← **custom models** | **694,126 / 1,000,000 (69%)** | 1.48M / 6.5M (22%) | 695 / 2500 | unlimited |

¹ before `DistrictBufferHeadroom`; with the district headroom set it reads e.g. 5,000,000.

**The pawn-layer ceiling is ~1,000,000 vertices / 6,500,000 indices / 2,500 meshes** — 10× the source
default. Vertices are the binding constraint (69% used vs 22% indices / 28% mesh slots). Layer 0
`Visual` is the shared **building/district** buffer (the district axis draws from it), unrelated to
your unit models.

## Raising the ceilings — `[Buffers] BufferOverrides`

All four limits are plain fields set at layer creation, and the plugin can override any of them
(same Harmony seam as `DistrictBufferHeadroom`, generalized):

```ini
[Buffers]
BufferOverrides = MeshWithSkeleton:verts=+1000000,idx=+2000000,meshes=+1000
```

- Format: `<layerNameSubstring>:verts=+N,idx=+N,meshes=+N,maxtris=N`, semicolon-separated for several
  layers. `verts`/`idx`/`meshes` **add** to the buffer sizes; `maxtris` **sets** the per-mesh cap
  absolutely (0 = unlimited). Layer names come from the Mesh Budget dump.
- Applied once at layer creation (next launch); the confirmation is logged as
  `[Buffers] '<layer>' baseVertexBufferSize: 1000000 -> 2000000`.
- Cost is VRAM only: pawn vertices are ~28 B packed, so `verts=+1000000` ≈ +28 MB, `idx=+2000000`
  ≈ +8 MB. The shader reads buffer sizes dynamically, so the change is transparent.

With the override above, a **100k-vert hero unit is entirely practical** — the shipped default pool
already fits one today (~300k free late-game); the override just restores comfortable headroom for
the rest of the roster.

## What actually counts

**buffer used = Σ (vertices of each *distinct loaded model type*)** — independent of instance count.

- **Not units on screen** — instancing; copies are free.
- **Not the whole catalog** — only meshes that are **loaded** register. The layer held 695 meshes while
  ~10 units were visible, so it's "loaded types," far more than what's on screen, but not everything
  possible. A roster unit that never loads in a given game costs nothing.
- **Diversity is the cost, weighted by size.** A wide variety of *lean* units is cheap; a few *heavy*
  unique types can cost as much as many lean ones.

## The fill is roster-wide at load, not per-era

Originally we assumed "era-clustering": that a late-game save loads more of the roster at once. The
measurements say otherwise — **a brand-new game reads 701,866 verts (70%), virtually identical to a
Contemporary save's 694,126 (69%)**. The pawn pool is filled at load with (nearly) the full roster's
meshes, regardless of era. Consequences:

- **The baseline is ~700k everywhere** — you always budget against the same ~300k free, whether Era 0
  or endgame. No per-era relief, but also no late-game surprise.
- **Every model you add costs its verts in every game**, from turn 1 — that's roughly room for
  **7–10 more heavy (30–42k) types** or **~20 lean (~15k) ones** on the default pool, or set
  `BufferOverrides` (above) and stop worrying.
- **Leanness still compounds**: trimming each model 40k→15k more than doubles how many distinct types
  fit (see the weld / low-poly notes in the Factory manual).

## How to measure (in-game)

The plugin exposes the live buffer usage:

- **F8 window ▸ "Mesh Budget"** — dumps every layer's fill (verts/idx/meshes/maxTris) into the window
  *and* to `BepInEx/LogOutput.log` (`[Budget]` lines). (Shift+F8 logs the same but proved flaky on
  some keyboard layouts — prefer the button.)

Spawning 10 more of the same unit won't move the numbers (instancing — copies are free).

## Answered (previously open) questions

1. **When do meshes register?** At load, roster-wide: a fresh Era-0 game already reads ~70% — the same
   as a Contemporary save. Not on-demand per spawn, not per era.
2. **Per-era unload vs accumulate?** Moot — the pool is filled up-front and stays ~constant. The real
   ceiling is "the whole loaded roster," identical in every era, and it's ~700k/1M with vanilla + the
   current ENC set.
