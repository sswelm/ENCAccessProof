# District Visuals — replacing a district's on-map model

**Status: research spike, PAUSED 2026-07-15.** The whole pipeline is decompiled, the Harmony hook + diagnostics
are built and proven in-game, and the FxMesh baker works — but a custom model does **not** yet render on a district.
The blocker is not the hook; it is *how the building mesh is chosen* (a state-driven selector, see below). This page
documents what works, everything we tried, why each attempt failed, and the remaining (deep) path — so a future run
starts from knowledge.

This is the second injection axis, alongside units. Goal: let a pack replace a **district's** on-map building with a
custom static 3D model. Units all work cleanly; districts are a much harder surface.

---

## TL;DR of the blocker

A district's building is **not a single mesh you can point at.** It is drawn by an
`FxEvolverMaterialLevelBuildSelector` — a *selector* that keeps a table of building-variant sub-materials,
**loads them asynchronously**, and **picks one at render time** from the district's era / level / health / faction.
The mesh lives inside whichever sub-material the selector chooses, whenever it finishes loading it. There is no stable
mesh address to overwrite. Everything we tried failed on this one fact.

---

## Which districts even have a replaceable building

Discovered empirically (via the `[District] saw district` + `[DistrictMat]` diagnostics). **This matters — most
districts are the wrong target:**

| District kind | Example | Has a replaceable building? |
|---|---|---|
| **Base city quarter** | `Extension_Base_Industry` (Makers), `_Food`, `_Science`, `_Military` | **Yes** — the prominent quarter building is on the level-build channel we hook. Overriding `Extension_Base_Industry` made *all factory buildings vanish*, proving reachability. |
| City extension | `Extension_Base_BreederReactor` | No — only a tiny/near-empty on-tile element. The visible city buildings around it are placed **procedurally** (borough system), not on this channel. |
| Villages / exploitation | `Villages_StoneQuarry` | No — **invisible by design**; the mountain *is* the quarry. `Villages_Logging_Encampment`, `_Farming_Village` share a generic material. |

So: **target base quarters** (`Extension_Base_Industry` etc.). We wasted several iterations on the Breeder Reactor
(an extension) and the Stone Quarry (invisible) before this was clear.

---

## The runtime pipeline (decompiled)

```
district def ConstructibleVisualAffinity  (e.g. "DistrictVisualAffinity_Base_Industry")
  → PresentationDistrict.UpdateLevelBuild()            builds an AssetReferenceRepositoryRequest
                                                       (affinity + era + DistrictState/level + health + pollution)
  → PresentationLevelBuildComponent.SetChannel(0, req) GetGuid(request) → a material GUID, loaded onto channel 0
  → the loaded material is an FxEvolverMaterialLevelBuildSelector  ← THE SELECTOR (not a plain drawer)
       ├─ pairs[]              name → GUID table of building-variant sub-materials
       ├─ fxMaterialCacheEntries.Entries[]   { Guid, FxMaterial }  ← async-loaded sub-drawers
       ├─ defaultMaterial / invalidNameMaterial   fallback GUIDs
       └─ selects one sub-material per render by renderContext / era / level / deferredName
  → the SELECTED sub-material is an FxEvolverMaterialDrawer whose `mesh` (a [FakeAssetReference(FxMesh)] Guid)
  → AsynchEnsureMeshLoaded(mesh) → Amplitude.Graphics.Fx.FxComponentMeshContentManager → GPU-instanced draw
```

`FxComponentMeshContentManager` is the **same GPU mesh manager the unit path uses** ([Vertex-Budget.md](Vertex-Budget.md)),
and the building channel is separate from **terrain feedback** (faction tint / ground material / hex sculpting, via
`IWorldMapFeedbackProvider.AddDistrict` / `UpdateHexagonSculpting` / `UpdateGroundMaterial`). So a mesh swap does not
touch terrain. The selector lives in `Amplitude.Mercury.Terrain.Fx` (Amplitude.Mercury.Terrain.dll);
`FxMesh` / `FxEvolverMaterialDrawer` / the mesh manager in `Amplitude.Graphics.Fx`.

---

## What is BUILT and PROVEN (all verified in-game)

- **The Harmony hook** — `Hk_DistrictRepoint` on `PresentationDistrict.UpdateLevelBuild`, scoped to one district by
  `ConstructibleDefinitionName`. Fires correctly; correctly scoped (only the named district changes).
- **Reaching the presentation state** — reflect targets confirmed: `ConstructibleDefinitionName` (scoping key),
  base-class `presentationLevelBuildComponent`, `RenderMode`, static `mainLevelBuildComponantLayer` = 0, private
  `channels[]` (`PerChannelData` struct) with public `EvolverMaterialGuid`. The public seam
  `PresentationLevelBuildComponent.SetChannel(int, Guid, RenderMode, bool)` works.
- **Diagnostics** — `[District] saw district '<name>'` dumps every district's `ConstructibleDefinitionName`;
  `[DistrictMat] <name> -> material a,b,c,d` dumps each district's resolved channel-0 material GUID. Both invaluable.
- **The FxMesh baker** — `DistrictBaker.cs` "Bake District FxMesh" wraps a static-baked `_ModelMesh` into an
  `Amplitude.Graphics.Fx.FxMesh` ScriptableObject and logs its GUID. **Works** (baked an 18,984-vert reactor FxMesh).
- **The material swaps** — pointing a district's channel at a different material GUID demonstrably changes what it
  draws (color/ground changed; buildings vanished). The plumbing is sound.

---

## What we tried and why each FAILED

Chronological, all tested in-game:

1. **Affinity-swap** (prefix rewrites `visualAffinityName` to another vanilla affinity). Fires cleanly, scoped
   correctly — but: (a) the *sculpting* request also reads `visualAffinityName`, so it **re-sculpts the tile**; and
   (b) the building only resolves for **registered `(constructible + affinity + era)` combos**, so a foreign affinity
   returns nothing. `LuxuryExtractor` on the reactor → spawned the quarry's **worker pawns** + flattened terrain, no
   building. `Science` → empty tile.
2. **`SetChannel(guid)` with a foreign material GUID** (Train Station, City Center). Loads fine — but the loaded
   material is itself a *selector*, which picks nothing valid in the new district's context → a small gray box / a
   wasteland ground plane, never a real building.
3. **Mesh-swap** (set the loaded material's `mesh` field to our FxMesh, keeping its working context). **Failed**: the
   channel material is the **selector**, which `has no 'mesh' field` (logged: `FxEvolverMaterialLevelBuildSelector … name mesh`).
   The mesh is on the selector's chosen *sub-material*, not the selector.

Key negative result from step 2/3: overriding `Extension_Base_Industry`'s material made **every Makers Quarter building
disappear** — which *confirmed the building is reachable on this channel*, but also showed a foreign/edited material
won't render a building; it just removes the vanilla one.

---

## The remaining path (NOT built — the next deep chunk)

Reach the selector's async-loaded sub-drawers and rewrite their mesh:

1. On the target district's channel-0 material (the selector), read `fxMaterialCacheEntries.Entries[]` (each a
   `FxMaterialCacheEntry { Guid, FxMaterial }`) plus the loaded `defaultMaterial` / `invalidNameMaterial` drawers.
2. For each `.FxMaterial` that is an `FxEvolverMaterialDrawer` (has the `mesh` field), set `mesh` → our FxMesh GUID and
   reset `meshIndex` = 0.
3. Do this **continuously (per-frame, from `Plugin.Update`)**, not just in the `UpdateLevelBuild` postfix — the
   sub-materials load *asynchronously after* the hook returns, and the selector's `Refresh` re-resolves them.

Caveats that make this genuine engine-surgery, not a quick fix:
- **Shared sub-drawers** — a variant material may be shared across districts, so mutating it affects others. Isolating
  (Instantiate a private copy and swap the cache entry's `FxMaterial` reference) is more correct but more fragile.
- **The selector re-picks/re-loads**, so a one-shot swap gets overwritten; hence the per-frame reapply, or hook the
  selector's own resolve / `WriteToGPUData`.
- **Orientation / seating** — never observed in-game (nothing rendered), so the district up-axis vs the unit-bake
  up-axis is still unknown. Expect to tune Rotation / FxMesh `importAngles` once something draws.
- **Budget** — district meshes share the GPU mesh manager; measure with Shift+F8 before shipping many.

An alternative to the runtime surgery: author a **plain** `FxEvolverMaterialDrawer` (not a selector) that references our
FxMesh, and `SetChannel(ourDrawerGuid)` — a plain drawer renders its mesh directly, bypassing selection. The open
question is producing a valid drawer asset (its output-layer/subshader wiring); `DistrictBaker.cs` "Clone District
Material" attempts this by cloning a donor drawer, but a suitable donor drawer asset was never located in-project.

---

## Config + diagnostics reference

Plugin config section `[District]` (all off by default):

| Key | Meaning |
|---|---|
| `DistrictRepoint` | master enable |
| `DistrictName` | target `ConstructibleDefinitionName` (use a base quarter, e.g. `Extension_Base_Industry`) |
| `DistrictAffinityOverride` | mode 1 — swap `visualAffinityName` (re-sculpts; combo-sensitive; proof-only) |
| `DistrictEvolverGuid` | mode 2 — `SetChannel(guid)` to a material GUID `a,b,c,d` |
| `DistrictFxMeshGuid` | mode 3 — mesh-swap our FxMesh (blocked by the selector; the enhancement above targets this) |

**Gotcha:** these keys only appear in the `.cfg` after the *new* build has run once; adding a key by hand must go
**inside** the `[District]` section or the plugin reads the default. The FxMesh baked into the project only ships to the
game after a **mod rebuild/export**. The Factory static bake requires a dummy `pawnDescription`, so a district bake
leaves a throwaway unit registry entry to remove.

Decompiled reference: `C:\tmp\reactor\Selector.cs` (the selector), `FxMesh.cs`, `PLBC.cs`, `FxEvolverMaterialDrawer.cs`.
