# Scaling to Many Custom Models — The Scoping Problem & the Deep-Clone Architecture

*Status: research in progress (2026-06-29). This captures the hard-won knowledge from making a 2nd custom model
(an LCAC hovercraft replacing the Era-6 transport) and discovering the real architecture needed to add **many**
custom static models (ships, planes, siege engines) across all eras that **coexist on the same map**.*

This builds on [FBX-to-Humankind-Pipeline.md](FBX-to-Humankind-Pipeline.md) (the single-model "why it works").
Read that first for the render-pipeline basics.

---

## 1. The goal

Add **lots** of custom **static** models (ships, airplanes, siege weapons) to **specific** units, throughout all
eras, such that:
- only the intended unit changes (no collateral on units that share plumbing),
- many custom models coexist on one map,
- it's a **repeatable recipe**, not a per-case hack — the basis of an FBX→asset import package.

Static models are the easy case: they're root-anchored with trivial motion, so a single-bone ("Base") rig is ideal.

---

## 2. The render pipeline facts that matter for scoping

(Decompiled from `Amplitude.Mercury.Animation.dll`.)

- **One AddOn per pawn definition.** `PresentationPawnDefinitionAddOn.Load(AnimationManager)` does:
  `MeshCollection = ampliAnimationManager.GetMeshCollection(description.Template.Guid)`,
  then `Skeleton = MeshCollection.SkeletonInstance`, then loads `FragmentEntries`, then
  `PawnManager.Instance.RegisterPawnDefinition(definition, …)`.
- **`GetMeshCollection(Guid)` is a shared, GUID-keyed lookup** (linear scan of `skeletons`/`meshCollections` by
  `SourcePrefab`). **Two pawn-defs with the same `Template.Guid` get the SAME skeleton instance.** This is the root
  of the scoping problem.
- `class Skeleton : MeshCollection`, `SkeletonInstance => this` — for a skinned unit, the "mesh collection" **is**
  the skeleton object. The body mesh lives in `skeleton.skinnedMeshInfos[0]` (a `SkinnedMeshInfo` with
  `MeshName`, `FxMeshContent`, and `[NonSerialized] uint MeshIndex`).
- **The body mesh is read LIVE from the shared skeleton at draw time** (via the pawn's `SkeletonId`). It is **not**
  copied into any per-instance (`PawnEntry`) or per-definition (`GPUPawnDescriptorEntry`) struct — those carry only
  BBox/bone counts/fragment ranges. **So there is no per-pawn body-mesh override point.**
- `AnimationManager.Register(Skeleton)` assigns `SkeletonId` (only if `< 0`), calls `LoadIFN`, adds to `skeletons`.

**Consequence:** to give one unit a different body mesh, that unit must be on a **different skeleton object**. You
cannot scope a body-mesh change at the pawn/instance level.

---

## 3. What works, what fails, and why

### ✅ Global mesh swap on the shared skeleton (the robust, working approach)
Harmony postfix on `GetMeshCollection`; when the returned skeleton matches (by name), keep the **real** skeleton
(bones/animation/material) and only repoint `skinnedMeshInfos[0].MeshIndex` to our uploaded mesh; set the skin via
the shared `FxOutputLayer` material's `_MainTex`. Renders perfectly and is reload-robust.
**Limitation:** it hits **every** unit on that skeleton. Fine when the skeleton is used by exactly one unit; wrong
when two units share it (e.g. Era-6 has a **barge** transport and a **hovercraft** transport that BOTH ride
`Unit_Era6_Common_LandingCrafts_01_Skeleton` — the swap turns both into the LCAC).

### ❌ Per-pawn runtime skeleton clone (`Instantiate` + `Register`)
Postfix `PresentationPawnDefinitionAddOn.Load`; for the target pawn-def, `UnityEngine.Object.Instantiate(skeleton)`,
`Register` the clone, point its mesh at ours, set `addOn.Skeleton/MeshCollection = clone`.
**Result: BROKEN GEOMETRY** — the mesh collapses to slabs/spikes for some unit facings. Tried both (a) grafting
`FxMeshContent` onto the clone + re-uploading, and (b) the proven mesh-index swap on the clone — **both slab**.

**Precise root cause (confirmed by decompile):** the GPU skeleton + per-bone matrix buffers
(`gpuSkeletonEntriesBuffer`, `gpuSkeletonBoneEntiesBuffer`, built from each `skeleton.BoneInfos[]`) are populated
**only in `AnimationManager.Apply()`**, which runs **once** from `AnimationLoad()`. `Register(clone)` bumps
`meshAndSkeletonRevisionIndex`, but **nothing ever reads it** to re-run `Apply()` (`applyedMeshAndSkeletonRevisionIndex`
is dead code, `Apply()` is `protected`). So the clone's freshly-assigned `SkeletonId` indexes into bone buffers that
were never extended → garbage `InverseBindPose`/`Local` → collapsed geometry. Second, independent failure:
animations are bound to a skeleton via a **`ClipCollection` that references it** (`Apply()` stamps
`GPUAnimationEntry.SkeletonIndex` per clip collection) — no clip collection references the clone → zero animation
rows → no posed motion. **The only runtime cure is `AnimationUnloadIFN()`+`AnimationLoadIFN()` after the clone is in
`loadedContent` — far more fragile than baking.** This is conclusive: the clone must exist as a loaded asset
**before** `Apply()` runs ⇒ the **data/asset route is the correct one**, runtime cloning is a dead end.

### ❌ "Cheap trick" — borrow a different existing skeleton
Redirect the unit onto some other real skeleton (e.g. an Era-2 Carrack/galley) that's rarely co-present, then swap
that one. Renders (no clone), but: (a) it still converts anything else on the borrowed skeleton, (b) animation
compatibility is luck, and (c) **it does not scale** — with many models across eras, collisions are inevitable.
Rejected as an architecture.

---

## 4. The target architecture: a DEEP CLONE baked as a real asset

The fix for the slab is the difference between a **shallow runtime clone** and a **deep asset clone loaded through
the engine's normal path**:

> **Deep-clone a working vanilla skeleton into a NEW asset (new GUID)** — copying all its parts (mesh content,
> bones, bind poses, animations, material) — then **swap in our mesh + our own material/texture**, and point the
> target unit's `Description.Template` at the clone.

Why this works where the runtime clone didn't: when the clone is a **loaded asset**, the engine includes it when
`Apply()` builds the GPU bone buffers (§3), so skinning is correct. Because the clone has its own `SourcePrefab`
GUID, `GetMeshCollection` returns a **separate** skeleton instance with its own `SkeletonId` — only the unit
pointing at it is affected. Many clones ⇒ many models that coexist with zero collateral. **This is the import
package.**

### Confirmed anatomy (decompile)
Everything **load-bearing in a `Skeleton` is `[SerializeField]`** and survives a deep asset copy:
`prefab` (→ `SourcePrefab`, **the `GetMeshCollection` lookup key**), `BoneInfos[]` (bone hierarchy + bind poses — the
GPU buffer source), `BBoxMin/Max`, `animatorController` + `animatorOverrideController` GUIDs, and
`skinnedMeshInfos[].FxMeshContent` (mesh holds **raw `verticesBytes` inline** + its own `Guid`, not a reference).
Rebuilt at load (don't worry about them): `SkeletonId`, `MeshIndex`, `loadingStatus`, `boneNameToInt`.

**Material/texture is NOT on the skeleton** — it's on the fragment:
`PresentationPawnFragmentSkinnedMesh.MaterialRef (Guid)` → `AnimationManagerContent.OutputLayerFromMaterialGuid` →
matches an `OutputLayerEntry { Guid Material; Guid OutputLayer; }` → loads the `FxOutputLayer` asset (shader+texture).
So a unique texture needs a **new material GUID + `FxOutputLayer` + `OutputLayerEntry`**, never a skeleton edit.

**Editor access:** `Amplitude.Framework.Asset.AssetDatabase.LoadAsset<Skeleton>(guid)` resolves vanilla skeletons by
GUID (bundle mounted). It is **read-only at runtime** — no `CreateAsset`/`Duplicate`; asset creation is a Unity
mod-tools **Build** operation.

**CONFIRMED in-editor (2026-06-29, `HovercraftProbe.cs`):** the mod-tools editor has all game bundles mounted
(`mercury.data.units.assetbundle`, `LoadedAssetBundleFlags = 0xFFFFFFFF`). `FindAssetPathsOfType<Skeleton>()` only
walks the *Project Assets* provider (sees the mod's own 2 skeletons), but you can reach vanilla assets by pulling the
provider's `AssetBundle` (`unityObject`/`UnityObject` field) and `GetAllAssetNames()` (21,575 unit assets), then
`bundle.LoadAsset(name, typeof(Skeleton))`. Loaded `Unit_Era6_Common_LandingCrafts_01_Skeleton`:
- `prefab` (SourcePrefab) = **`2bb20c2003488a04d84cf3b90917e764`** (the GetMeshCollection key)
- `animatorOverrideController` = **`d3e591815e0d5b6468d590d47fc12273`**, `animatorController` = none
- `BoneInfos` (4) = **`Dummy_Root, Base, Drapeau_00, Trappe_00`** — has a **`Base`** bone (our LCAC rigs to Base ✓)
- `skinnedMeshInfos` = **null on the raw asset** (body mesh is built from the prefab at load) → the clone must
  supply its own populated `skinnedMeshInfos` (we already have one in our baked `Hovercraft_Skeleton`).

### Wiring — CONFIRMED constraints (2nd investigation)
- **`AnimationManagerContent` is a single vanilla asset** (`AnimationManager.InstanceGUID =
  f81c148cff973af4ca02dcc2f617f781` → its `content`), holding **fixed `Guid[]` arrays**: `MeshCollections`,
  `AnimationClipCollections`, `AnimatorOverrideControllers`, `OutputLayerEntries`. `AnimationLoad()` registers each
  `MeshCollections[]` entry (`RegisterMeshCollection`) then `Apply()` builds GPU bone buffers over `skeletons[]`.
- **A mod CANNOT edit those arrays.** Asset GUID collisions resolve with `AssetDuplicateSolvingPolicy.Error` →
  **vanilla always wins**, so a mod can't override the vanilla `AnimationManagerContent` (or any vanilla asset by
  reusing its GUID). ⇒ **Shipping a skeleton asset in the mod does NOT auto-register it**, and a mod can't add an
  `OutputLayerEntry`.
- ⇒ **The skeleton MUST be registered at runtime by the plugin:**
  `AnimationManager.Instance.RegisterMeshCollection(ourSkeleton)`. GPU bone buffers only (re)build in `Apply()`,
  which runs inside `AnimationLoad()` — so register **before** `AnimationLoad`/`Apply` (hook it) or force
  `AnimationUnloadIFN()`+`AnimationLoadIFN()` after registering. (This is why the runtime *Instantiate* clone slabbed
  — it was never in `skeletons[]` when `Apply` ran. A registered REAL asset is fine.)
- **The Description repoint IS pure mod data** (and is the scoping mechanism): `PresentationPawnDefinition` elements
  are **name-keyed** `DatatableElement`s, so the mod overrides `Era6_Common_Hovercrafts_01` per-element without
  touching the barge. `PresentationPawnDescription.Template` is a `GameObjectReference` whose `.Guid` is the skeleton
  SourcePrefab matched by `GetMeshCollection`. The pawn-def's `Description` is a `PresentationPawnDescriptionReference`
  (`AssetReference<PresentationPawnDescription>`, serialized `Description: { guid }`).

### The recipe (per model) — corrected to the confirmed wiring
1. **Bake the skeleton asset** → unique `SourcePrefab` GUID (ours: `{a:-161743038,b:1281290275,c:-429521739,
   d:-847522509}`, no vanilla collision). Reuse the vanilla rig's `Base` bone (our LCAC rigs to it); for animation,
   copy the vanilla `animatorOverrideController` GUID (`d3e591…`). Our `Hovercraft_Skeleton.asset` already exists.
2. **Register at runtime (plugin):** `RegisterMeshCollection(ourSkeleton)` timed before `Apply()` (hook
   `AnimationLoad`), so `GetMeshCollection(ourSourcePrefabGuid)` finds it AND its bones are in the GPU buffer.
3. **Repoint ONLY the Hovercraft** (data OR runtime): make a new mod `PresentationPawnDescription` (copy the vanilla
   Hovercraft Description's `Slots`/rotations/collider, set `Template.Guid` = our SourcePrefab) and point
   `Era6_Common_Hovercrafts_01`'s `Description.guid` at it (the byte at `PresentationPawnDefinition_Era6_ENC.asset`
   ~line 12939). The barge (different element, same vanilla Description) is untouched. *(Or do the repoint at runtime
   in the Hovercraft AddOn.Load postfix: `addOn.Skeleton = addOn.MeshCollection = GetMeshCollection(ourSourcePrefab)`
   — a reference swap to our REGISTERED skeleton, not a clone.)*
4. **Texture:** the mod can't add an `OutputLayerEntry`, so reuse a vanilla output layer and set `_MainTex` from the
   plugin (our existing technique). For true per-unit texture, pick an output layer the barge doesn't use.

Scoping comes from the per-element Description repoint (unique SourcePrefab ⇒ unique skeleton ⇒ only that unit).
**N models coexist** by construction. Plugin involvement shrinks to: register the skeleton(s) + set `_MainTex`.

Key source: `MeshCollection.cs:25-40`; `Skeleton.cs:27-58`; `FxMeshContent.cs:109-152`;
`AnimationManager.cs:261-301,372-391,495-501,557-685` (Apply/Register/GetMeshCollection);
`PresentationPawnDefinitionAddOn.cs:55-83,210-279`; `AnimationManagerContent.cs:17-114`; `OutputLayerEntry.cs:13-42`;
`PresentationPawnFragmentSkinnedMesh.cs:23,30`; `AssetDatabase.cs:399-694`.

---

## 5. Static-model baking specifics (from the LCAC hovercraft)

Baker: `ENCReload/Assets/Scripts/Editor/HovercraftModel.cs`. Source: CC-BY "LCAC Hovercraft croqui" (LM3D),
a raw CAD GLB with **no textures** (vertex-colors only).

- **GLB → OBJ + decimation** (`scratchpad/glbconv`, SharpGLTF, vertex-clustering): grid≈180 took 405k verts/702k
  tris → ~13k/27k (game-range). Untextured CAD models need this; high-poly + no UVs is the norm for free CAD.
- **Combine** all submeshes into one mesh (`Mesh.CombineMeshes`, UInt32 index format).
- **Normalize:** recenter, uniform-scale longest axis to a target length, align longest axis → Y. Tunable
  `OrientEuler` for the final orientation (LCAC needed `(0,90,0)` — a 90° roll about the long axis to get deck-up;
  the airship needed `(0,180,0)` for upside-down).
- **Hover height:** world-up maps to **mesh +Z** here (via the bind pose), so raise the model by `-min.z + HoverGap`
  so the hull bottom sits just above the water (centered-on-surface looks half-sunk).
- **Winding fix:** CAD normals are unreliable → wind every triangle outward from the centroid (`Dot(geoNormal,
  a+b+c) < 0 ⇒ flip`). Fixes backface culling without trusting authored normals.
- **No-texture skin:** the plugin applies a **procedural** skin. Trick for arbitrary CAD UVs: **override the UVs in
  the baker with height-based mapping** (U = position along length, V = normalized height) so a vertical-gradient
  texture reads correctly (dark skirt low, light naval-gray hull high) regardless of the source UVs.
- **Rig:** skin 100% to a single "Base" bone, set bind pose, `RecalculateTangents` (required for
  VertexEncodingFormat 6), bake the Amplitude `Skeleton` via `SetPrefab` + `Reimport`.

---

## 6. Era-6 transport specifics (the test case)

- Two distinct Era-6 transport units — a **barge** and a **hovercraft** — both ride
  `Unit_Era6_Common_LandingCrafts_01_Skeleton`. The hovercraft pawn-def is `Era6_Common_Hovercrafts_01`.
- Embarked-army transports and standalone naval transports both use era-appropriate skeletons; earlier eras
  (e.g. `Unit_Era2_Common_TransportCarracks01_Skeleton`) are **different** skeletons, unaffected by a LandingCrafts
  swap.
- Finding which skeleton a unit borrows: runtime discovery — a Harmony postfix on `GetMeshCollection` logging each
  distinct skeleton name (`HovercraftDiscovery.cs`), then load a save with the unit visible.
- Our baked LCAC skeleton GUID (Amplitude): `a=-1153397905 b=1134277020 c=577920438 d=-573259371`.

---

## 7. Current state

- **✅ WORKING (2026-06-29): native, scoped custom model.** The Hovercraft renders our LCAC from our own registered
  skeleton; the barge transport (same vanilla skeleton) is untouched. Proven in-game.

### The working runtime recipe (`HovercraftInjectPatch.cs`)
Hard-won; the order and the struct write-back matter. Two Harmony hooks:

**A. Hook `AnimationManager.AnimationLoad` (POSTFIX)** — register our skeleton + rebuild GPU buffers:
1. `AssetDatabase.LoadAsset<MeshCollection>(ourAssetGuid)` our baked skeleton.
2. Reset its `loadingStatus`→NotLoaded, `SkeletonId`→-1; rename `skinnedMeshInfos[0].MeshName` to the host body-mesh
   name (`Unit_Era6_Common_LandingCrafts_01` — the name the Description's body fragment looks up).
3. `AnimationManager.RegisterMeshCollection(ourSkeleton)`. **Must be POSTFIX** (the FX manager doesn't exist in the
   prefix → NullRef). Then **invoke `Apply()` via reflection** — it's the only thing that builds
   `gpuSkeletonBoneEntiesBuffer` from `skeletons[]`, and it normally runs once; re-invoking it builds OUR bones
   (SkeletonId 70) into the buffer (skip this → mesh collapses to a line).

**B. Hook `PresentationPawnDefinitionAddOn.Load` (POSTFIX)** — repoint ONLY the Hovercraft:
4. If `Definition.name` contains "Hovercraft":
5. **Explicitly `LoadIFN`** our mesh (RegisterMeshCollection doesn't upload at AnimationLoad time → `MeshIndex` stays
   0; upload here, when the unit presents and FX is loaded → `MeshIndex`=115).
6. `addOn.Skeleton = addOn.MeshCollection = ourSkeleton` (so pawns use our SkeletonId/bones).
7. **Re-resolve the body fragment.** `FragmentEntry` is a **struct** with its OWN `private MeshCollection
   meshCollection`; `FragmentEntry.Load` resolves the mesh via *that* field, not its `skeleton` arg. So per fragment:
   box it, set its `meshCollection` field = ourSkeleton, invoke `Load`, **write the struct back to the array**
   (a `foreach` mutates a copy → no-op; that bug cost hours). The body fragment's `EncodedMeshAndVisualParticleCount`
   then flips to our mesh; fragments whose mesh we lack (the barge floor) resolve to 0 → not drawn.
8. Texture via `_MainTex` on the shared LandingCrafts output layer (per-frame from `Plugin.Update`).

Scoping is automatic: only the Hovercraft AddOn is repointed; the barge AddOn keeps the vanilla skeleton.

### Remaining / next
- **Texture polish** — the skin currently shares the LandingCrafts output layer (also touches the barge's material).
- **Move the repoint to data** — per §3-4, a mod `PresentationPawnDescription` with `Template`→our SourcePrefab would
  drop the runtime repoint (registration still needs the plugin, as `AnimationManagerContent` is vanilla-locked).
- **Generalize** into the import package: one FBX/OBJ → baked skeleton + this register/repoint, parameterized per
  unit. The mechanism above is the template for every ship/plane/siege model.

---

## Potential plan (candidate, NOT decided): data-driven `AnimationManagerContent` merge

*Proposed by shakee (Discord, 2026-06-30). Recorded as a possible direction — not committed; needs validation, and
we're not yet sure it beats a thin per-model plugin config.*

**Idea.** Instead of per-model runtime surgery, let mods **ship their own `AnimationManagerContent` asset** (listing
their skeletons / output-layers / clip-collections) and have a **single generic plugin merge** them into the game's
loaded singleton at load. A modder then just uploads an FBX in modtools, which emits the baked skeleton + a
material/`FxOutputLayer` + an `AnimationManagerContent` + a `Description` repoint — **zero per-model plugin code**.

**Why it's attractive.** It would dissolve the fragile parts of the working runtime recipe (§7):
- No manual `Apply()` re-invoke, no `LoadIFN` timing, no `addOn.Skeleton` repoint, and — crucially — **no body
  `FragmentEntry` struct surgery**, because the unit points at the mod skeleton via pure data
  (`Description.Template` → mod `SourcePrefab`), so fragments resolve against it **from the start**.
- **Solves per-unit textures**: merging `OutputLayerEntries` (`{Material, OutputLayer}`) lets a mod ship its own
  `FxOutputLayer` → `OutputLayerFromMaterialGuid` finds it → scoped custom skin (the one thing the runtime path
  can't scope cleanly).
- One generic plugin serves every mod.

**Mechanism (sketch).**
- The singleton is `AnimationManager.InstanceGUID = f81c148cff973af4ca02dcc2f617f781` → its `content`
  (`AnimationManagerContent`, with `Guid[] MeshCollections / AnimationClipCollections / AnimatorOverrideControllers /
  OutputLayerEntries`). `AnimationResolveDependencies` loads it into `loadedContent` and walks `MeshCollections[]` →
  `loadedMeshCollections[]`; `AnimationLoad` registers those + `Apply()` builds GPU buffers.
- Mods **can't override** that asset (`AssetDuplicateSolvingPolicy.Error` → vanilla wins), so **merge, don't
  override**: postfix `AnimationResolveDependencies`, enumerate mod `AnimationManagerContent` assets in mounted
  bundles (`FindAssetPathsOfType<AnimationManagerContent>` minus vanilla), append their entries into the loaded
  arrays / `loadedMeshCollections`. `AnimationLoad`/`Apply` then handle them natively.
- Unit repoint stays pure mod data (name-keyed `PresentationPawnDefinition` → new `Description.Template`).

**Open questions / why it's not a settled choice.**
- Exact merge seam + timing (prefix can't touch `loadedContent` before it loads; postfix must append to BOTH
  `loadedContent.*` and the already-built `loadedMeshCollections` — verify `AnimationLoad` reads the latter).
- Re-merge robustness on save load / content invalidation (`ContentRevision`).
- Whether modtools can author/emit a valid `AnimationManagerContent` + `FxOutputLayer` easily (the "easy workflow"
  shakee rightly insists on) — authoring `FxOutputLayer` shaders may be the real friction.
- Static models (districts, idle bodies) reportedly already work as **plain data mods** and need none of this — so
  this plan is only for the **animated** case.
- Alternative still on the table: keep the proven runtime register/repoint but drive it from a small JSON/asset
  manifest (per-model config), avoiding a deeper engine-content merge.

### ⚠️ Partial result (2026-06-30): the merge REGISTERS a skeleton — display is NOT proven
**Do not read this as "shakee's method works."** What was actually shown is narrow:

1. **Data file is authorable in modtools** (`ShakeeMethodProbe.cs`): `AnimationManagerContent` is a plain
   `ScriptableObject`; `CreateInstance` + set `MeshCollections = [skeleton asset GUID]` + `CreateAsset` round-trips.
   Created `ENC_ModAnimationContent.asset` listing the zeppelin skeleton (asset GUID `e7ad…`). ✅
2. **Generic merge hook** (`ShakeeMergePatch.cs`): postfix on `AnimationManager.AnimationResolveDependencies` loads
   the mod content by GUID and appends its skeletons into the private `loadedMeshCollections[]`
   (AnimationManager.cs:463-468); `AnimationLoad` then `RegisterMeshCollection`s them (497-501). ✅
3. **Result (log):** `merged 1 mod skeleton(s) (110 -> 111)` + `zeppelin skeleton SkeletonId=70`. This proves the
   skeleton **object landed in the registry list** — nothing more.

**What `SkeletonId=70` does NOT prove (and what actually makes a model display):**

| # | Requirement to render on a unit | Shown by the merge? |
|---|---|---|
| 1 | Skeleton registered (`SkeletonId`) | ✅ |
| 2 | **Mesh uploaded to GPU** (`skinnedMeshInfos[0].MeshIndex ≠ 0`) | ❓ UNTESTED — the hovercraft needed an explicit `LoadIFN`; `RegisterMeshCollection` did not upload at `AnimationLoad` time |
| 3 | GPU bone buffers built (`Apply` over `skeletons[]`) | ⚠️ likely (registered before `Apply`) but unverified |
| 4 | A unit points at it (`Description.Template` → SourcePrefab) | ❌ not done |
| 5 | Body fragment resolves the mesh by name (`GetFxMeshIndex`) | ❓ untested — the part that took hours on the hovercraft |
| 6 | Material / output-layer for the body | ❌ not done; `OutputLayerEntries` is vanilla-locked |

So **2, 4, 5, 6 — the things that make it visible — are unproven**, and they're exactly the walls the runtime
hovercraft hit. The correction earlier in this doc claiming "no `Apply`/`LoadIFN`/fragment surgery" was premature:
registration avoided those, but display has not been attempted.

**Skepticism worth keeping (it doesn't add up if it's "easy"):** registration has always been the understood part;
the years-long blocker is the **display chain** (GPU mesh upload + by-name fragment resolution + the vanilla-locked
material/output-layer). shakee *proposed* the merge as an idea — proposing ≠ implementing+shipping; if a `SkeletonId`
were enough this would have been solved long ago. The real friction (which shakee himself flagged) is also the
**modtools authoring** of the content + `FxOutputLayer` materials.

**Next, to actually prove or disprove it:** verify `MeshIndex` on the merged skeleton (if 0, it cannot draw — case
closed for now), then do the `Description.Template` repoint and *look at the screen*. Only a unit visibly rendering
the model counts. Expect to hit at least one of rows 2/5/6; if so, that obstacle is the honest finding.

### Update — MeshIndex passes, but the Description wall is real (2026-06-30)
- **Row 2 (mesh upload): PASSES natively.** Checking the merged zeppelin skeleton across `FxLoadIFN` passes, its
  `skinnedMeshInfos[0].MeshIndex` went `0 → 115` — the mesh uploaded to the GPU through the engine's *normal* FX
  load, **no explicit `LoadIFN`** like the runtime hovercraft needed. So registration **and** GPU upload (rows 1-2)
  are genuinely clean via data + the generic hook. That part of shakee's idea holds.
- **Rows 4-6 (display): blocked by a vanilla-locked Description.** The unit's `PresentationPawnDescription`
  (`PresentationAirUnit_Era5_Common_Zeppelins_Default`) — which carries the skeleton `Template`, the body fragment
  (`SkinnedMeshPath`), AND the body material — is **vanilla**; the mod only references it by name. There is no
  `Template`/`SkinnedMeshPath`/`GameObjectReference` anywhere in the mod data. So you **cannot** repoint by editing it.
- **Honest conclusion:** the merge cleanly solves the *registration half* (skeleton + GPU mesh). The *display half*
  still requires the unit's Description, and because it's vanilla you must **author a NEW mod
  `PresentationPawnDescription`** (Template → your SourcePrefab `{a:-1151233186,b:1095993008,c:-1749390190,
  d:-1274340553}`, a body fragment with `SkinnedMeshPath = 'Zeppelin_ModelMesh'` and a `MaterialRef`) and repoint the
  (mod-owned, name-keyed) pawn-def's `Description.guid` at it. For a custom texture the `MaterialRef`→`OutputLayer`
  must also be authored (reuse a vanilla one, or merge an `OutputLayerEntry` via the same AMC).
- **So "it doesn't add up as easy" is correct:** a `SkeletonId`/`MeshIndex` is not a rendered unit. shakee's merge
  shifts the work, it doesn't remove it — the unsolved "easy workflow" is authoring the **Description + material**,
  which is exactly the friction shakee himself named. The runtime hovercraft path sidesteps Description authoring by
  reusing the vanilla Description's fragment and repointing it live (the fragment-struct surgery) — that's the
  trade-off: shakee's path is cleaner-but-more-to-author; the runtime path is hackier-but-reuses-vanilla.

---

## PLAN (2026-06-30): combine merge + thin runtime repoint — target the Zeppelin Bomber

**Goal:** render the Zeppelin Bomber with our custom zeppelin model on screen, via the *combination* — shakee's merge
does the registration + GPU upload natively, a *thin* runtime repoint does the display by reusing the vanilla
Description. Success = the model on screen with the fragile `Apply()`/`LoadIFN` hacks **removed** from the runtime code.

**Why this combo (recap):** the merge provably removes the two flakiest runtime hacks — the manual `Apply()` re-invoke
(it registers *before* `Apply`, so bones build natively → no slab) and the explicit `LoadIFN` (FX upload happens
natively → `MeshIndex=115`). The runtime half shrinks to: point the unit at the already-registered skeleton +
re-resolve the body fragment — reusing the vanilla Description, so **no Description to author**.

### Steps
1. **Data / merge (mostly done):** `ENC_ModAnimationContent.asset` already lists the zeppelin skeleton; the
   `AnimationResolveDependencies` postfix merges it → registered (`SkeletonId=70`) + uploaded (`MeshIndex=115`).
   Keep `Shakee/MergeModContent` on.
2. **Discover the zeppelin's body-mesh name** (the name the vanilla Description's body fragment looks up) — via a
   fragment dump on the zeppelin AddOn (like the hovercraft's `Unit_Era6_Common_LandingCrafts_01`). Needed so we can
   rename our skeleton's `skinnedMeshInfos[0].MeshName` to match → `GetFxMeshIndex` resolves to our mesh.
3. **Thin runtime repoint** — new hook (or reuse `HovercraftInject` generalized): postfix
   `PresentationPawnDefinitionAddOn.Load`, match the zeppelin pawn-def (`Era5_Common_Zeppelins_01`), then:
   `addOn.Skeleton = addOn.MeshCollection = <merged skeleton>` and re-resolve the body fragment (box struct, set its
   `meshCollection` field to ours, `Load`, write back). **Omit** `EnsureRegistered` / `Apply()` / `LoadIFN` — the merge
   already did those (this is the robustness win to verify).
4. **Disable the old global swap** for the zeppelin (`ZeppelinInject` / its config) so it doesn't fight the new path.
5. **Texture:** start by reusing the vanilla zeppelin material (the body fragment's existing `MaterialRef` → its
   output layer). A scoped custom skin via a merged `OutputLayerEntry` is a follow-up, not part of this proof.

### Unknowns / risks to watch
- The zeppelin's body-mesh name (step 2) — discover before renaming.
- The zeppelin is currently entangled with the global-swap setup (it borrows the cruise-missile skeleton); make sure
  the old path is fully off so we're testing the new one.
- Whether the thin repoint renders **without** the `Apply()`/`LoadIFN` calls — that's the exact thing being proven.
- Mesh orientation/scale of the existing zeppelin bake may need the same `OrientEuler`-style tuning as before.

### Success / failure criteria
- **Success:** Zeppelin Bomber visibly shows our zeppelin model in-game, and the runtime code path contains **no**
  `Apply()`/`LoadIFN`/`EnsureRegistered` (only the repoint + fragment re-resolve).
- **Failure (still informative):** if it needs `Apply()`/`LoadIFN` back, or slabs/half-renders, that tells us the
  merge's native registration isn't sufficient for an animated unit and the combo's robustness claim is weaker than
  hoped — documented as the honest finding.

### ✅ RESULT (2026-06-30): combo proven for the MESH; texture via re-applied atlas
First run *looked* fine but was **contaminated** — the global swap was still on (`RepointOnLoad=true`) and had
clobbered our skeleton's `SkeletonId` to `48` (the cruise-missile slot), so the swap was doing the work. Smoking gun:
the merge logged `SkeletonId=70` but the combo logged `SkeletonId=48`. Lesson: a render that "looks good" proves
nothing while another path is live.

**Clean run (global swap OFF, `redirect count: 0`):**
- `before: Skeleton='…CruiseMissile…' SkeletonId=48` → `repointed → MERGED skeleton (SkeletonId=70, MeshIndex=115)`
  → `after: Skeleton='Zeppelin_Skeleton' SkeletonId=70`, and the body fragment's
  `EncodedMeshAndVisualParticleCount` changed `134683904 → 3842569472` (re-resolved to OUR mesh).
- **No `Apply()`/`LoadIFN`/register in the runtime path.** The merge did registration + GPU upload natively; the
  runtime shrank to the deterministic repoint + fragment re-resolve. **The combo's robustness claim holds for the
  mesh — proven on screen.** The `Apply()`/`LoadIFN` hacks (the slab and `MeshIndex=0` sources from the hovercraft)
  are gone.

**Texture:** with no skin applied, the mesh sampled the **cruise-missile material** → a red **stain** on the top.
Fixed by re-applying our zeppelin atlas `_MainTex` on the (shared) cruise-missile output layer per-frame — the same
trick the old swap used (`ShakeeZeppelinCombo.ApplyTexture`/`TickTexture`, hooked from `Plugin.Update`). Texture is
**not yet scoped** (shared output layer); the pure-data `OutputLayerEntry` merge is the eventual scoped path.

**Net:** combination = shakee's merge (robust native registration + upload) + a thin runtime repoint (reuse the
vanilla Description, no Description authoring). Genuinely more robust than runtime-only; remaining runtime bits are the
repoint, the fragment re-resolve, and (until `OutputLayerEntry` scoping) the per-frame texture.

## 8. ✅ Stealth Cruiser — first TEXTURED + first naval-COMBAT unit (USS Zumwalt) (2026-06-30)

The 3rd working custom model and the most complete: **USS Zumwalt (DDG-1000)** onto the Era-6 **StealthCruisers**
("Stealth Missile Cruiser") unit. First model with a **real texture**, first **naval combat** unit, and **verified in
battle** (renders + behaves correctly mid-combat, not just on the map). Source: Yakudami, Sketchfab, CC-BY.

It uses the **native-scoped runtime recipe** (same family as `HovercraftInjectPatch`), not the merge/combo —
self-contained and proven. Patch: `StealthCruiserInjectPatch.cs` (`StealthCruiserInject`, hooks
`CruiserRegisterHook` on `AnimationLoad` + `CruiserRepointHook` on `AddOn.Load`); config gate `Plugin.CruiserInject`
(section `Cruiser`, default true, independent of `RepointOnLoad`).

### What was new (and reusable)

1. **Textured pipeline.** Keep the model's own UVs through the bake and apply its albedo. The baker bakes the
   extracted albedo into `StealthCruiser_Atlas.asset` (alpha forced opaque); the plugin applies it as `_MainTex` on
   the host output layer per-frame (`TickTexture`). No procedural skin like the hovercraft.

2. **Faithful UV conversion (the texture-scramble fix).** The GLB→OBJ converter's default vertex-clustering
   decimation merges coincident **UV-seam** verts and **averages their UVs** → the skin smears (red waterline bleeds
   onto the deck). Added a **faithful mode (`grid 0` = no merging)** that preserves every vertex + its exact UV. Use
   it for any low-poly textured model (decimation isn't needed there anyway). Unity's "Weld Vertices" is safe — it
   only merges fully-identical verts, so seams survive import.

3. **Angular look from a smooth-shaded source.** The model ships smooth normals → set the OBJ import to
   `Normals = Calculate` with a low `SmoothingAngle` (~20°) so hull facets become hard edges (radar-defeating look).
   Importing the model's own normals faithfully = rounded; flat-shading everything = wrong. Calculate+angle matches
   the artist's intended creases.

4. **Positioning is per-vehicle-type — do NOT inherit the hovercraft's.** A hovercraft hovers (a small gap *above*
   the water read correctly). A **displacement ship sits IN the water at its painted waterline** — sink it with a
   **negative Z offset** so the red boot-topping submerges and the surface lands on the red/grey line. Naval baseline
   `≈ -0.2`. Orientation `OrientEuler = (180,0,0)` = deck-up **and bow-forward**; `(0,180,0)` kept the deck up but
   reversed the heading (the ship "moved backwards").

5. **Self-discovering scoped patch (key robustness win).** At repoint, the patch reads the host AddOn's OWN
   `FragmentEntries[].meshName`, picks the hull (a `Unit_*` mesh that isn't Water/Wake/Foam/Proof), and **renames our
   mesh to match** so the on-map body fragment resolves to ours (otherwise: INVISIBLE — the "truly stealth" bug). The
   skin layer is `<bodyMeshName>_OutputLayer`, matched by the discovered name. **Zero hardcoding** → works for any
   borrowed naval unit. Here StealthCruisers borrows the **Swedish Visby Corvette**
   (`Unit_Era6_Sweden_VisbyCorvettes_01`, `AnimationCapabilityProfile = Boat`).

6. **Persistent config dialog.** `Tools → StealthCruiser → Configure Stealth Ship` (`StealthShipConfigWindow`) —
   offset X(sway)/Y(fore-aft)/Z(waterline)/size multiplier, persisted in **EditorPrefs** (survive recompiles +
   restarts). Build button bakes with the current values; no const-edit/recompile loop.

### How to find what a mod unit borrows (lesson)
A mod `PresentationPawnDefinition` stores `Description`/`Attachements` as **bare GUIDs** in the `.asset` YAML — text
grep for the borrowed name finds **nothing**. **Resolve the GUID** instead: the Unity Inspector shows the name
(`Description`/`Body` fields), or `AssetDatabase.GUIDToAssetPath` in a script. (Confirmed: StealthCruisers → Visby.)

### Files
- Baker + dialog: `ENCReload/Assets/Scripts/Editor/StealthCruiserModel.cs`
- Assets: `ENCReload/Assets/Resources/StealthCruiser/` (OBJ + albedo) → `StealthCruiser_Skeleton.asset`,
  `StealthCruiser_Atlas.asset`
- Patch: `ENCAccessProof/Patches/StealthCruiserInjectPatch.cs`; gate `Plugin.CruiserInject`
- Converter faithful mode: scratchpad `glbconv` (`grid 0`)

### Known UX cost — the real issue: it should work FIRST time
The core problem isn't tuning *speed*, it's that the pipeline still needs **multiple in-game passes** (orientation,
size, waterline) before a model is right. The config dialog removed the *recompile* wait, not the
*bake → rebuild-mod → relaunch* round-trip. The goal is **first-time-right**: get the model correct in the editor so
the first launch is a *confirmation*, not another iteration. Path:
- **Calibrated editor preview** — render the baked prefab against a water plane at the calibrated surface height and at
  the correct relative scale, so orientation / size / waterline are judged in the Scene, not in-game.
- **Auto-detect to remove the guesses entirely**: waterline from the red/grey boot-topping line in the atlas; forward
  axis from the longest bbox dimension; deck-up + target size from the borrowed unit (we already discover its mesh and
  can read its bbox at runtime).
Bow-vs-stern and final taste may still need one quick editor confirm — but **zero in-game iteration**.

## 9. ✅ Universal Model Factory — data-driven, any model onto any unit (2026-07-01)

Generalized everything above into a reusable tool: **Tools > Universal Model Factory**. Pick (or create) a 3D
resource, pick a target pawn definition, pick a model file, set rotation / position / size / normals / smoothing /
convert-grid, press **Bake** → it bakes a skeleton + atlas and writes a JSON registry that the runtime reads. **Adding
a model is now zero new code.** Proven end-to-end in-game: the StealthCruiser (Zumwalt) is driven entirely by the
registry with all the old per-unit patches OFF. Intended to ship as a **distributable Unity package** for any modder.

### Pieces
- **Editor:** `ModelFactoryWindow` (the window) + `UniversalBaker` (the bake engine, every knob a parameter) +
  `ModelRegistry` (writes `enc_models.json`). A searchable **Pick** lists all `PresentationPawnDefinition`s; picking
  one auto-suggests the resource name. A bundled GLB→OBJ converter (`Tools/glbconv`, invoked via `dotnet`) handles GLB.
- **Runtime:** `UniversalInject` (`UniRegisterHook` on `AnimationLoad`, `UniRepointHook` on `AddOn.Load`) reads the
  registry, registers every skeleton, and repoints each listed pawn-def with the same **self-discovery** as the
  cruiser (read host body-mesh name → rename ours → resolve; skin via `<bodyMesh>_OutputLayer`). One patch, N models.
  Config gate `[Factory] UniversalInject`; the per-unit gates (`CruiserInject`, `MergeModContent`, `RepointOnLoad`)
  go OFF when the registry drives things.

### Registry (`BepInEx/config/enc_models.json`)
`{ "models": [ { resourceName, pawnDescription, modelFile, rotation, position, size, normalsMode, smoothingAngle,
convertGrid, skel:[a,b,c,d], atlas:[a,b,c,d] } ] }`. The runtime only needs `pawnDescription` + `skel` + `atlas`
(offsets are baked into the skeleton). Re-baking an existing resource keeps the same asset GUIDs (same path), so the
registry entry stays valid.

### ⚠️ Gotcha that cost an hour: `JsonUtility` is unreliable here
`UnityEngine.JsonUtility.FromJson<Wrapper>(json)` returned **`models = null`** for a perfectly valid file (no
exception, no BOM, full-mirror class) inside the BepInEx plugin — it silently fails to populate a `List<T>` of a
nested-object class in this context. **Fix: parse the known fields directly** (regex for `resourceName` /
`pawnDescription` / `skel[4]` / `atlas[4]`; the i-th match of each belongs to model i, since each appears once per
entry in document order). Lesson: don't trust `JsonUtility` for non-trivial structures in a plugin — hand-parse.
(Diagnosed by instrumenting every checkpoint: hook fired → `EnsureRegistered` fired → `read N chars; parsed
models=NULL` was the smoking gun.)

### ✅ Texture IS scoped now — a private FxOutputLayer clone per model (2026-07-01)
The mesh swap was always scoped, but the skin was painted on the host's *shared* output layer, so the vanilla unit on
that layer wore our skin (Zumwalt + Visby Corvette are both Era 6, fielded together). **Solved** by giving each model
its own output layer.

**The mechanism (decompiled `AnimationManagerContent` / `FragmentEntry` / `OutputLayerEntry` with ilspycmd):**
- The output layer is resolved from the **fragment asset's `MaterialRef`**:
  `Content.OutputLayerFromMaterialGuid(materialRef)` → the `OutputLayerEntry` whose `Material == materialRef` → its
  `OutputLayerInstance`. We don't own that fragment asset (it's the borrowed unit's), so we can't change the resolution.
- BUT the runtime `FragmentEntry` holds a **settable `fxOutputLayer` field**, and its `Load()` just calls
  **`fxComponentRenderer.GetLayerIndexAddItIFN(fxOutputLayer)`** — which allocates a **fresh GPU layer index for
  whatever `FxOutputLayer` instance you hand it**. And `FxOutputLayer` is a plain `ScriptableObject`.

**The fix (runtime, in `UniversalInject.ReloadFragments`):** for OUR body fragment (the one whose `meshName` == the
discovered hull name), `UnityEngine.Object.Instantiate` the host's `FxOutputLayer` → a fresh clone (its `Loaded`/GPU
state is `[NonSerialized]`, so it initialises clean). Set the fragment's `fxOutputLayer` to the clone, then call
`Load()` — `GetLayerIndexAddItIFN(clone)` gives it its **own slot + render outputs/materials**. Paint our atlas on the
**clone only** (per-frame `_MainTex`, cached per entry as `isolatedLayer`). The host layer — and every real unit still
on it — keeps its own skin.

**Proven:** log shows cloned `StealthCruiser_OutputLayer` / `Hovercraft_OutputLayer` / `Zeppelin_OutputLayer`, no
errors; in-game the Visby reverted to its Swedish splinter camo while the Zumwalt kept grey/red. No editor changes, no
`AnimationManagerContent` merge, no resizing shared arrays — a self-contained runtime clone, mirroring the skeleton
deep-clone but at the output-layer level. (The earlier "resize OutputLayerEntries + re-Apply" plan was unnecessary.)

**Future (modder request): culture-specific texture overrides.** Now that each model owns its own layer/material
(above), a unit can show a different skin per empire/culture — that foundation is in place. Per-culture variants are
the extension: keep a small set of cloned layers (one per culture atlas) and pick which the fragment points at by the
rendering unit's culture. Design the registry/atlas handling to allow multiple atlases keyed by culture.

### ✅ Neutralise the host's overlay maps (2026-07-01)
Isolating the layer stops our skin leaking onto the vanilla unit, but the *reverse* still bit us: the cloned material
kept the **host's** overlay maps (`_NormalMap`, `_AmbiantOcclusionMap`, `_ColorMask`, `_RoughnessMap`, `_MetallicMap`),
and those are sampled through **our** UVs. Result: the host's panel detail and team/camo mask smeared across our model
— worst at the stern ("front fits, back drifts"). **Fix (runtime, `TickOne`):** alongside setting `_MainTex` to our
atlas, point every overlay map at a flat 1×1 texture — normal = `(0.5,0.5,1)`, AO = white, ColorMask = black,
Roughness = grey, Metallic = black (`Solid(r,g,b)` helper). Only our albedo shows; no borrowed detail, no camo bleed.

### ✅ Fixing a bad extracted texture — hand-edit + "Reuse extracted files" (2026-07-01)
The Zumwalt GLB shipped with a stray yellow fill baked into its albedo. **Chosen model: the modder fixes the extracted
texture in whatever image editor they like, then re-bakes without re-importing over the fix.** The Factory has one
checkbox for this — **Reuse extracted files**: when on, `UniversalBaker` skips the model-import/convert step if the
OBJ/albedo already exist (`haveObj`), so a hand-edited `*_albedo.png` survives the bake; `BuildAtlas` reads the raw
`.png` bytes off disk, so the edit flows straight into the atlas. Workflow: bake once (extracts the albedo) → edit
`Assets/Resources/<name>/<name>_..._albedo.png` in e.g. paint.net → tick **Reuse extracted files** → bake again.
Caveat: baking with Reuse **off** re-extracts the GLB and clobbers the edit.

> We briefly built a generic in-Factory "Replace a colour" tool (eyedrop → replace, per-pixel in `BuildAtlas`) and
> **removed it** — hand-editing + Reuse is simpler, fully universal, and puts no image-editing logic in the tool. The
> baker keeps only `reuseExtracted`; no colour-match code.

### ⚠️ The albedo is found by NAME-SCAN, not a stored reference — keep the scan robust (2026-07-01)
`BuildAtlas` doesn't hold a reference to a texture asset; it **scans the resource folder** for a `.png` and loads its
raw bytes, because the GLB→OBJ converter names the albedo after the *model's own material* (`<Material>_albedo.png`),
which the baker can't know ahead of time. Cost us ~an hour: a hand-made `..._albedo-backup.png` sitting in the folder
got baked **instead** of the real albedo — `FirstOrDefault(name.Contains("albedo"))` returned it because Windows lists
files alphabetically and `-` (ASCII 45) sorts before `.` (46), so `…albedo-backup.png` came first. The clean backup
silently masked the real (yellow) file, so bakes looked fine for the wrong reason. **Fix:** exclude `backup`/`orig`
sidecars and prefer the shortest matching name (`…_albedo.png` beats `…_albedo-backup.png`). Rule for modders: don't
leave extra `*albedo*.png` files in a model's resource folder. (Cleaner long-term: have the converter record the exact
extracted filename in the registry and read *that* directly — kills this bug class.)

### Debugging orientation/texture: know WHICH asset reflects WHAT (2026-07-01)
Hours were lost tweaking the rotation offset and "seeing no change" — because we kept looking at assets that don't
reflect the setting under test. Map it once:
- **The flat `*_albedo.png` texture** — never changes with rotation (rotation moves the mesh, not the UVs). Comparing
  textures tells you nothing about orientation.
- **The raw imported OBJ** (`Assets/Resources/<name>/<name>`, the *subfolder*) — the pre-bake import; `rotationEuler` is
  **not** applied to it. It looks the same no matter the rotation.
- **The baked `<name>_Model.prefab` / `<name>_ModelMesh`** (loose in `Resources/`) — these DO carry `rotationEuler`
  (baked into the verts, `rot = Euler(rotationEuler) * align`). This is the source of truth for orientation.
- **In game** — loads the baked skeleton/atlas by GUID. Confirm fresh bakes actually reach the game before diagnosing:
  bake a deliberately visible change (e.g. the yellow patch) and check it appears. If it doesn't, it's a deploy/stale-
  bundle problem, not a bake problem — chasing rotation is wasted effort until the pipeline is proven live.

**Source-model quality is a real limit.** If the skin looks stretched along the hull with faithful UVs (`convertGrid=0`)
and correct orientation, the source GLB's UVs are simply poor — no bake setting fixes it. The pipeline is faithful; a
bad source stays bad. Options: repair UVs in Blender, or **swap to a better upload** — the Factory makes model-swap a
2-minute op (point Model file at the new GLB, bake, rebuild), which is the pragmatic fix.

### ✅ `.blend` import — auto-convert via installed Blender (2026-07-01)
The Factory now accepts `.blend` directly. `.blend` isn't a transfer format, so `UniversalBaker.ConvertBlend` shells out
to **headless Blender** (`blender file.blend --background --python Tools/blend_export.py -- out.glb`) to produce a GLB,
then the normal GLB→OBJ→bake path takes over. **Zero config:** `FindBlender()` locates `blender.exe` (EditorPrefs
`ENC.blenderPath` override → newest `C:\Program Files\Blender Foundation\Blender*` → `blender` on PATH), so it "just
works" whenever Blender is installed; the Model-file field shows a warning if a `.blend` is picked with no Blender found.
`Tools/blend_export.py` also **recovers textures**: many blends (e.g. Sketchfab's `source/*.blend` + `textures/*`) store
dead absolute image paths, so the script re-points each missing image to a same-named file in the blend folder / a
sibling `textures/` dir before export. **Caveat:** a very old material (pre-Principled-BSDF, e.g. a 2019 asset) may
export **untextured** because the glTF exporter can't read its node setup — supply the albedo manually (drop it in the
resource folder, bake with Reuse) or fix the material. Modern blends embed fine. Like the `dotnet` GLB converter, this
adds a **Blender dependency** — fine for a modding tool, and to be surfaced as an install note when packaged.

### ✅ RESOLVED: detailed atlas scrambled in-game = a missing UV V-flip in `glbconv` (2026-07-01)
**Root cause, one line:** the GLB→OBJ converter never flipped the V coordinate. **glTF/GLB store texture coords with
V=0 at the TOP; OBJ (and Unity) use V=0 at the BOTTOM.** So every UV was vertically mirrored — the skin mapped
upside-down in V, landing the deck markings on the superstructure and the hull numbers in the wrong place. **Fix:**
`glbconv/Program.cs` writes `vt {U} {1 - V}` instead of `vt {U} {V}`. That's the entire bug.

**Why only the Stealth Cruiser:** it's the **first GLB-sourced model**. The Hovercraft and Zeppelin were baked from OBJ
directly (no glTF→OBJ step), so they never hit the flip. Any GLB/glTF/`.blend` model would have shown it — the Cruiser
just had a detailed, asymmetric skin that made the mirrored V obvious (a uniform skin would hide it).

**Why it took so long (and the debugging lessons that matter):**
- We kept "proving each stage clean" by **rendering meshes in Blender** — but Blender's OBJ import and our upside-down
  bake orientation combined to *mask the V-flip* from certain camera angles. Rendering from a consistent angle, or
  better, comparing raw data, is essential. **Never trust a textured 3D render across two different tools to judge a
  V-flip** — the tools disagree on V origin, which is the very thing under test.
- The **decisive test was data, not pixels**: `diff` the `vt` lines of the imported vs baked OBJ dumps → **byte-identical**.
  That proved the *bake* preserved UVs perfectly and sent the hunt to the render/convention layer instead of the mesh.
- The **confirming test**: render the baked mesh with a **V-flipped atlas** → clean. Flipping the atlas ≡ flipping the
  UVs, so this pinned it to a V-convention mismatch introduced before Unity ever saw the mesh — i.e. `glbconv`.
- Earlier I *wrongly* "ruled out" a V-flip (a flipped-atlas render "didn't match" in-game) and chased Amplitude's
  GPU mesh-upload via ilspycmd (encoding bbox, `FxMeshContent`, quadification) and a real-but-unrelated **double-injection**
  bug. All dead ends for *this* symptom. Lesson: when mesh UVs are provably intact but the engine renders them wrong,
  suspect **texture-coordinate origin/convention** before deep engine internals.

**Fixes that landed during the hunt (correct, kept even though not the culprit):**
- `weldVertices = false` on the OBJ importer (stops Unity re-merging split seams).
- Manual UV-preserving mesh combine (replaced `Mesh.CombineMeshes`) + read the mesh from the imported **asset**, not an
  `Instantiate`'d copy (Unity has a known "duplicated model → broken UVs" bug; belt-and-suspenders).
- Runtime `_MainTex` scale/offset reset to identity.
- **Double-injection fix:** the old per-unit `StealthCruiserInject` and `UniversalInject` were BOTH live on the Cruiser
  (config defaults `CruiserInject=true` + `UniversalInject=true`); gated the old one off under UniversalInject.

**Tooling from this session (kept):** `baker/MeshDumper.cs` (dump imported + baked mesh AND the atlas to files for
external comparison); headless **Blender** render/export scripts (blend→GLB with material repair + texture recovery,
OBJ+atlas render); the whole GLB→OBJ→Unity→bake→skeleton→GPU path decompiled and understood.

### Toward a Unity package (gaps)
Decouple hardcoded paths (`ModelRegistry.ConfigDir`, the `dotnet`/converter path) into settings; neutral naming (drop
"ENC", namespace `ENCAccessProof`); ship the editor package + the companion BepInEx plugin together with docs; consider
a Unity-native glTF importer (glTFast) instead of the `dotnet` converter. Mirror of editor scripts lives in
`ENCAccessProof/baker/` (ENCReload git tracks only `Assets/Databases`).

---

## 10. Heavy / CAD models in the Factory + the engine's shared mesh buffer (2026-07-02)

Baking a raw, high-poly, untextured **CAD** model (the LCAC hovercraft) through the *Universal Model Factory* forced
three capabilities into the baker and surfaced a hard engine limit. It also cost most of a day to a chain of wrong
guesses — the post-mortem is below, because the mistakes are instructive.

### The engine's shared mesh buffer — the real ceiling (decompiled)
From `Amplitude.Mercury.Animation.FxComponentMeshContentManager` (`ilspycmd`):
```csharp
private int baseVertexBufferSize = 100000;   // vertex budget
private int baseIndexBufferSize  = 250000;   // index budget  (÷3 ≈ 83,333 triangles)
private ReadWriteBuffer1D<uint> indexBuffer; // 32-bit indices -> NO 65,535 cap
private Vector3 encodingBBoxPosMin = (-8,-16,-8);  encodingBBoxPosMax = (8,16,8);   // position must fit this box
...FillMeshVertexAndBufferContent(..., maxMeshTriangleCount, minAreaTriangleToKeepIt, ref currentVertexIndex, ...)
```
- **~100k vertices / ~250k indices (~83k triangles)** is the budget; the encoder **skips the overflow** past it (missing
  / see-through geometry — "it dropped vertices").
- Indices are **32-bit** — the earlier "16-bit / 65,535" hunch was wrong.
- `currentVertexIndex` / `currentIndexIndex` are **running offsets by ref → the buffer is SHARED** across *all* injected
  custom meshes **and the game's own fx meshes**. So the ceiling is the **total**, not per-model, and the effective
  headroom is less than the raw number. **Double-sided counts twice.** Plan every model against this shared budget.
- Position must fit `[-8,8]×[-16,16]×[-8,8]` (the Factory's size-normalize already keeps models inside it).
- (Escape hatch, untried: `baseVertexBufferSize`/`baseIndexBufferSize` are `[SerializeField]` — a plugin could reflect
  them larger and recreate the buffers to raise the ceiling.)

### Three baker options this added
- **Vertex reducer** (`targetTris`, `Tools/mesh_reduce.py` via Blender): per-object quadric **collapse** to ~N triangles
  so heavy models fit the shared buffer. Use **collapse**, NOT planar dissolve — planar merges near-coplanar faces and
  **flattens gently-curved features (it destroyed the skirt)**; collapse preserves distinct curved features. Reduce the
  *whole model* toward the budget; sharp detail (fans) degrades before big surfaces (hull, skirt).
  - **It's a CEILING, not a quota.** A model already under the target passes through **untouched** — the decimate ratio
    clamps to 1.0 and collapse never *adds* geometry, so light models are never upscaled or altered. Only heavy models
    get trimmed. That's why a non-zero default is safe for everyone.
  - **Default `24000`, and double-sided auto-halves it.** The Factory now defaults `targetTris` to **24000** instead of
    off — chosen so that under double-sided it halves to **12000**, the confirmed best-looking LCAC bake (just under the
    ~25k per-model vertex ceiling; 12500 from a 25000 default was a hair worse in fine detail on close inspection).
    Because double-sided doubles the baked geometry, the baker **halves the effective target when it's on** (24000 →
    12000) and logs it. So the field is a single "budget" you set once — flip Double-sided on/off and the baked result
    stays under it automatically. `0` still fully disables the reducer.
- **Winding fix** (`windingFix`): the documented CAD fix from §5 — `Dot(geoNormal, a+b+c) < 0 ⇒ flip`, run **after the
  raise** so the origin sits *below* the model (→ "outward" is horizontal for a low skirt, not downward). Rewinds faces
  outward so a single-sided/CAD mesh renders **single-sided** (no culling holes) with **no extra geometry**. This is the
  right tool for convex hulls (hovercraft, ships) and is what makes the skirt render.
- **Double-sided** (`doubleSided`): appends a reversed, slightly-inset copy of every face → renders from both sides
  regardless of winding. Topology-independent but **doubles the vertex cost** (budget!). Fallback for genuinely
  non-convex thin shells; prefer the winding fix for hulls.

### Untextured models
No albedo → the Factory bakes a flat-grey atlas and the plugin applies it. (An experiment to bake *no* atlas — so the
plugin leaves the host material alone — was tried and reverted; it made models dark and solved a problem that was really
the buffer overflow, not the atlas.)

### ⚠️ Post-mortem — how this got broken, so it doesn't happen again
The hovercraft **already worked** at the last git push, baked by the retired `HovercraftModel.cs` (§5) whose crucial
step is the **winding fix**. Migrating it to the Factory — which never had that fix — is what broke it, and then a chain
of wrong diagnoses piled on:
1. **Baked over a working asset.** Re-baking through the Factory overwrote the working `Hovercraft_Skeleton.asset` (and
   the ~27k grid-180 `Hovercraft.obj`); neither is in git, so there was no clean undo. **Lesson: don't overwrite a
   working baked asset when migrating; keep the known-good input.**
2. **Re-derived instead of re-reading.** The winding fix was *already documented in §5*. Instead of applying it, it got
   re-derived badly (measured from the model **centre** instead of the below-model **origin** → flipped the wrong faces
   → "mostly transparent"). **Lesson: read the existing docs before re-inventing.**
3. **Chased symptoms.** The missing skirt was blamed in turn on a 16-bit cap, backface culling, coincident-face alpha,
   and the grey atlas — each "fix" a detour. The actual causes were only two: **no winding fix** (skirt culled) and
   **double-sided pushing over the shared buffer** (skirt truncated). **Lesson: the skirt lived in the baked skeleton,
   not the code — a code revert can't fix a bad bake; re-bake with the right recipe.**
4. **Edited shared tools mid-experiment, then a blunt revert.** Editing `UniversalBaker.cs` while the user was
   experimenting polluted their tests; a subsequent `git checkout` (uncommitted work) wiped the good double-sided +
   reducer features along with the bad edit. **Lesson: commit working features incrementally so a revert can be
   surgical, and don't touch shared tooling mid-experiment.**

**Resolution:** the ~27k grid-180 OBJ was regenerated, the old builder restored the working skirt, and the winding fix
was then ported into the Factory (correctly, from the below-model origin) — so the Factory now bakes CAD hulls
single-sided with the skirt, no old builder required.

### Height-based UVs + a gradient skin (2026-07-02)
A fourth CAD option: **height-based UVs** (`heightUV`) overrides the mesh UVs with `U = position along length (Y after
align)`, `V = normalized height (Z)` — the §5 recipe, now in the Factory. A **vertical-gradient albedo** then maps by
HEIGHT regardless of the model's (arbitrary/absent) UVs: put a PNG named `*albedo*.png` in the resource folder with a
black band at the **bottom** (V=0 → lowest geometry → skirt) fading to grey at the **top** (V=1 → hull/deck). BuildAtlas
picks it up; the plugin applies it; the skirt reads black, the hull grey — matching the LCAC reference. Make the texture
with ImageMagick, e.g.:
```
magick \( -size 64x176 xc:'#767d84' \) \( -size 64x24 gradient:'#767d84'-'#0d0d0d' \) \( -size 64x56 xc:'#0d0d0d' \) -append Hovercraft_albedo.png
```
Honest caveat: this is a **color gradient, not a real skin** — no panel lines, dots, or weathering. It's the cheap
"good at RTS zoom" path; a detailed skin needs a proper UV-unwrap + paint on the model's real geometry.

### Winding fix vs double-sided — the mixed-model rule (illustrated)
- **Winding fix** rewinds faces outward from the below-model origin — perfect for a **convex hull** (the skirt), zero
  extra geometry. But it can't orient **non-convex** parts (the LCAC's fan housings): from behind they show their culled
  backfaces → see-through.
- **Double-sided** gives every face a back side → fills those non-convex holes, at **2× vertices AND 2× triangles**.
- A **mixed** model (convex hull + non-convex fans) legitimately wants **both** on: winding fix cleans the hull's
  normals, double-sided fills the fans. The cost is the 2× (from double-sided) on the whole mesh, so pair it with a
  **lower reduce target**. Don't guess the number — **bisect down until the in-game model matches the editor preview**
  (no missing fan vertices). For the LCAC, 20000 double-sided still dropped fan detail and 15000 was clearly better, so
  the sweet spot is at or below ~15000; keep lowering until it renders exactly like the preview. (Future optimization:
  a *selective* double-sided that only duplicates faces the winding fix left inward-facing, so you pay ~1.2× instead of
  2×, letting you keep a higher target.)
- **Always verify from *behind*, not the flattering front-quarter shot.** Single-sided renders more *unique* detail for
  the same budget (double-sided spends half on mirrored back-faces), so from the front a single-sided bake can look
  **sharper** — and pass. But swing the camera to the **rear of the non-convex parts** (fan housings, open frames): that
  is where single-sided culls to invisible. Confirmed on the LCAC — single-sided 24000 looked crisper head-on but the
  fan housings were **see-through from behind**; double-sided 12000 stayed solid from every angle. In-game the camera
  *does* orbit behind units, so the rear view is the deciding test — **sharper-but-hollow loses to softer-but-solid.**
  This is why the double-sided toggle earns its keep: for non-convex thin parts it's the *only* thing that closes them —
  the winding fix (convex-only) can't.
- **Counterintuitively, a *lower* reduce target can render *more* complete.** In-game, `targetTris=15000` double-sided
  gave visibly **better fan housings than 20000** — fewer missing vertices. **Observed, not yet explained.** The leading
  hypothesis is the shared ceiling: 20000×2 ≈ 40k indices from the hovercraft alone, and when the total (all injected
  models + the game's own fx meshes) crowds the ~250k-index buffer, the *last* geometry written — the fine fan detail —
  gets silently truncated; 15000×2 ≈ 30k leaves more headroom. But **the real cutoff hasn't been measured** — we don't
  yet know at what total it starts dropping, whether it's the index buffer, the vertex buffer, or something else. Treat
  the ~250k number as the decompiled buffer size, not a confirmed in-practice limit. Practical takeaway regardless: when
  fine detail goes see-through, try reducing the target *further* before reaching for a bigger budget — empirically it
  helps, even if we can't yet say exactly why.
- **This is truncation, not decimation quality — proven by geometry *appearing*.** Dropping the LCAC to 12000
  double-sided made the **interior well-deck walls suddenly render** (they were absent at 15000/20000). If lowering the
  target were merely coarsening the mesh, those walls would get *simpler or vanish*, not appear. Geometry appearing when
  you *reduce* the budget can only mean it was being **silently dropped** at the higher total — strong corroboration that
  the cause is the shared buffer ceiling (later-written geometry truncated), not the reducer's quality. Rule of thumb:
  if parts *appear* as you lower the target, you were over the ceiling; keep going until nothing new shows up.
- **Observed practical ceiling: ~25k *vertices* per model (estimate, to refine).** Watching where the LCAC's parts stop
  dropping, truncation seems to kick in around **~25,000 vertices** for this model in a typical scene — far below the
  decompiled ~100k *vertex* buffer. That's consistent with the buffer being **shared**: the game's own fx meshes plus the
  other injected models (cruiser + Zeppelin) already consume most of it, leaving only ~1/4 for any one new model. So plan
  a per-model budget around **~25k vertices, not 100k** — and remember double-sided doubles the vertex count, so a
  double-sided model wants ~half that triangle target. This is a felt estimate from bisecting, **not a measured cutoff**;
  the exact number will vary with how crowded the scene is.

## 11. Animated donors & donor-matching — the drone case study (2026-07-03)

Injecting a **quadcopter drone** (a free CC-BY Sketchfab model) onto a new `Era6_Common_Drone_01` pawn that borrows the
**attack-helicopter** animation. It surfaced the single most important rule for picking a donor, plus the first thing the
injection approach genuinely **cannot** do.

### What worked (and proves the Factory scales)
- **A 77-material model baked with a correct skin.** The drone GLB had 78 objects / 77 materials / ~84k tris. The
  reducer crushed it to ~10k tris; the bake produced a single correct atlas. Turns out all 77 materials **shared one
  texture** (the author's UV atlas), so the GLB→OBJ material-flatten cost nothing here — the skin came out right (white
  body, black props). *Lesson: many-material CAD models often share one atlas; don't assume the flatten ruins them.*
- **`glbconv` emits one albedo per material** even when they're byte-identical → a folder of 77 identical PNGs. Harmless
  (the bake uses one), but a **texture-dedup** in `glbconv` (hash → write once) is a worthwhile cleanup.
- **Orientation** was a simple bake `Rotation X≈90` to lay the flat/Z-up drone level.

### What did NOT work — the animated-donor ceiling
The drone rendered correctly **but the helicopter's spinning rotor stayed on top of it.** Chasing it mapped the exact
boundary of the injection approach. The rotor is **not** a fragment and **not** a separate sub-mesh — the helicopter
skeleton has **one** skinned mesh (the body) whose rotor-blade verts are weighted to `Helix` / `Helix_back` bones. It's
drawn by the **animated skinned-mesh path**, which is *separate* from the fragment path our injection swaps. Every lever
we have runs at `AddOn.Load`, which is **too late**:

| Attempt | Why it failed |
|---|---|
| Swap the body **fragment** (our normal mechanism) | Replaced the body ✓ but not the animated draw → rotor survives |
| **Hide** the rotor as a fragment (`hideMeshes`) | The rotor isn't a fragment — nothing to hide |
| **Collapse** the `Helix` bones via `BindPose` scale | Animated bones read their transform from the **animation clip**, not bind pose → no effect |
| **Redirect** the donor skeleton's skinned-mesh index to our mesh | The animated draw's geometry is **encoded once at pawn-spawn** → late edits don't re-encode |

**The ceiling, stated plainly:** the injection can replace a donor's **body mesh**, but it **cannot remove animated
skinned sub-parts** (a rotor, spinning wheels) that the engine bakes into the pawn's render encoding at spawn time. Our
`AddOn.Load` hook is downstream of that encoding. Removing them would mean Harmony-patching the spawn-time encode path —
deep in the GPU skinning the whole architecture deliberately avoids. `hideMeshes` was kept (it works for **fragment**-based
extras); the `hideBones`/bind-pose/redirect experiments were removed as dead ends. *(Diagnostics were kept: on each
model's first load the plugin logs the donor's fragment names and skinned sub-mesh count — `[Uni] <name> donor.Skeleton:
N skinned sub-mesh(es)` — which is exactly how you vet a candidate donor.)*

### The real lesson: **donor matching** (and the rotor is a *feature*)
The rotor isn't a bug — it's a mismatch. **Match the donor's animated silhouette to your model:**

- **Rotorcraft model → helicopter donor.** A custom **scout/stealth helicopter** body, modeled *without* its own rotor,
  swapped onto a helicopter donor **keeps the borrowed spinning rotor** → a fully-animated custom helicopter for **free**,
  no rotor rigging. The exact thing that ruined the drone makes rotorcraft trivial. (Model the body rotor-less so you
  don't get a double rotor; the donor supplies it.)
- **Non-rotorcraft (drone, UGV, ground vehicle) → a donor with no animated sub-parts.** 

Two hard requirements for a donor when your model has **no** matching moving part:
1. **No animated skinned sub-parts** (no rotor, no spinning propeller — so avoid helicopters *and* propeller planes).
2. **A complete animation set** — idle + move + stop. A **cruise-missile** donor is rotor-free but *fire-and-forget*: it
   lacks an idle/end-of-move animation and looks broken standing still. (This is why the Zeppelin-on-missile works only
   because a zeppelin is "always travelling" in feel.)

**Best non-rotorcraft donors:** a **land vehicle** (APC/IFV/recon) — rigid or near-rigid, and it has the full ground
animation set, so the unit never looks frozen. Add a small **Z position offset** at bake to float the model just above the
ground so a "flying" drone reads as hovering low rather than sitting in the dirt. (Watch for animated wheels/tracks as
separate sub-parts — but even if present they're small and low, nowhere near as glaring as a rotor; the sub-mesh log tells
you before you commit.) Fixed-wing **jets/bombers** are also rotor-free, but confirm they have a sane hovering/idle
animation for a stationary air unit.

**Donor-picking checklist:** (1) does my model have a moving part? → pick a donor that animates the same part and borrow
it. (2) otherwise → pick a donor with **no** animated sub-parts **and** a full idle/move animation set; check the sub-mesh
log shows `1 skinned sub-mesh`.

## 12. Animated custom models — proven feasible; the editor pipeline works (2026-07-03)

**The big one: a custom model's own skeletal animation CAN be baked into Amplitude's format and bound to an injected
model.** Prompted by the ReconDrone — the Sketchfab drone GLB ships a real **`hover`** skeletal animation (spinning
props). Static injection can't spin props (see §11's articulation ceiling), but the animation *exists in the file*, so
the question became: can we feed it through Amplitude's own tooling? **Answer: yes** — Phases 1–2 below are proven; Phase
3 (runtime playback) is planned but not yet built.

### What the model must have
Inspect the source with Blender: it needs an **armature + a skeletal AnimationClip** (bone animation, not object/turntable).
The drone had `armature 'skeletal.1'` (87 bones incl. `motor_1..4_jnt` prop spinners) + 3 actions (`hover`, `exploded_view`,
`step_by_step`). Sketchfab auto-plays the embedded clip — that's the tell that a real animation is in there.

### Phase 1 — bake a bone-preserving Amplitude `Skeleton` ✅ PROVEN
The Factory's normal bake **destroys** rigs (GLB→OBJ strips armature; it rebuilds a rigid 2-bone rig). So the animated
path bypasses the Factory and uses the **SDK's own editor tooling**, which is all reflectable:
1. **Produce a rigged FBX** from the GLB via Blender: strip junk (the material-less Icosphere), keep only the wanted
   action (`hover`), export FBX with `bake_anim=True`, `add_leaf_bones=False`, `object_types={ARMATURE,MESH}`.
2. **Import into Unity** (`Rig → Generic`, `Import Animation ✓`).
3. Select it → **`Assets ▸ Create ▸ Amplitude ▸ Animation ▸ AnimationSkeleton`** (menu runs `Skeleton.SetPrefab` +
   `Reimport`). *Gotcha:* the menu can grab the wrong prefab from selection context — open the new `*_Skeleton` asset and
   set its **`Prefab`** field to the rigged FBX, then click **`Reimport`**.
4. **Result:** `BoneInfos` filled with the real rig — **88 bones** (root + all 87 joints, prop bones present),
   `SkinnedMeshInfos` = 77, non-zero `CheckSum`. A bone-preserving Amplitude skeleton. *(That the SDK ships this tooling
   at all is why "inject a custom animated skeleton" — long feared to hang the GPU skinning — is actually supported.)*

### Phase 2 — bake the clip into an Amplitude `ClipCollection` ✅ PROVEN
A `ClipCollection` is a **pre-baked** format (`skeleton` GUID + `poseDataAsset` bytes + `ClipEntry`/`ClipCurveEntry` tables
+ checksums), *not* a Unity AnimationClip. Build it with the SDK:
1. Select the **folder** containing the rigged FBX → **`Assets ▸ Create ▸ Amplitude ▸ Animation ▸ AnimationCollection`**.
2. On the new ClipCollection: set **`Skeleton`** = our `*_Skeleton`; **drop the folder** into "Fill from directory content"
   (it scans `*.fbx`, pulls their AnimationClips) → `Animation Clip Entries` becomes 1.
3. Click **`Reimport`** → samples the clip against the skeleton and writes the pose data.
4. **Result:** `Pose Data Asset` set, Statistics `1 clip / 21,912 poses / 130 kb / Duration 10.4 s / 249 frames / 88
   bones`, curves **98.9% rotation-only** (= spinning props). Done.

*(Harmless SDK noise: pressing Reimport with the mod-build `ModuleEditor` window open throws an IMGUI
"control 2's position…" `ArgumentException` + a null-ref in `ModuleEditor.OnModuleBuildGUI` — a repaint-vs-layout glitch
in the build window, NOT a bake failure. Close/reopen the window to clear it.)*

### Phase 3 — runtime playback ⏳ PLANNED (not yet built)
The remaining integration, with one real unknown:
1. **Register the ClipCollection** so the engine assigns `hover` an `animationId`. Clip collections load from an
   **`AnimationManagerContent`** asset — the mod already has one (`ENC_ModAnimationContent`, currently empty
   `MeshCollections`/`AnimationClipCollections`). Two routes: add the skeleton+clip to that asset (data, loads at startup),
   or inject into the live `loadedAnimationClipCollections` + rebuild the GPU animation buffer at runtime (like we already
   do for skeletons via `RegisterMeshCollection` + `Apply`).
2. **Repoint the pawn to the animated skeleton** — existing mechanism (`UniversalInject`), just a different GUID.
3. **⚠️ Force the pawn to PLAY `hover`.** The one frontier: a pawn's current animation is set in the
   **Presentation/PawnManager layer (a different DLL)** from unit state → OverrideController → clip. We'd make it always
   resolve to our clip — a per-frame animation-state override, or a custom `OverrideController` (also bakeable via
   `ReimportAnimatorOverrideControllers`). Expect a few build→test cycles here.

### Facts banked (Amplitude animation format)
- **`Skeleton : MeshCollection`** — `BoneInfo[] { Name, TRS BindPose, TRS Local, ParentIndex, Depth }`; built from a Unity
  rigged prefab via editor `SetPrefab`/`Reimport`. `SkeletonInstance => this` (a Skeleton's own `Skeleton` field is unused).
- **`ClipCollection`** — `skeleton` GUID + `poseDataAsset` (TextAsset bytes) + `animationClipEntries`/`animationClipCurveEntries`
  + `sourceDirectory`. `SetFromDirectory()` pulls clips from `*.fbx`; `Reimport()` bakes poses.
- **`AnimationManagerContent`** — lists `MeshCollections` + `AnimationClipCollections` the manager loads/registers.
- Editor tooling is all reflectable (menus + `Reimport` methods), same trick the Factory uses for `Skeleton`.
- Cost note: 88 bones + 77 skinned sub-meshes + 83k tris is **heavy** — a shipping version should slim to prop-bones-only
  and decimate the *skinned* mesh (weight-preserving) to fit the shared buffer (§10).

### Phase 3 runtime — IN PROGRESS: renders + animates in-game; the final lever found (2026-07-03)

Took the animated ReconDrone from assets to on-screen. Status: **it renders, it's full-size, it's textured, and the
animation pipeline is provably live** — the only thing left is making it play *our* clip instead of the donor's.

**Increment 1 — renders without the GPU-skinning hang ✅ (the fear is dead).** Pointed the ReconDrone registry entry's
`skel` at the animated skeleton's Amplitude GUID (get it via a tiny reflection menu — `AmplitudeGuidLogger`, since
`Amplitude.Framework.Asset.AssetDatabase.GetAssetGUID` only resolves in-editor). First try was **invisible**: the raw
skeleton was **83,712 tris across 77 separate meshes** — it overflows the shared buffer (§10) *and* the 1-fragment
injection only maps one mesh. **Fix: re-export a *slim* FBX** — in Blender, `join()` all 77 objects into one mesh, collapse
to a single material (they shared one texture anyway), and decimate to ~12k tris, **keeping the armature + `hover`**. Then
re-point the skeleton asset's `Prefab` at the slim FBX and `Reimport` (same asset file → **same GUID**, so the registry
needs no edit). Result: a **1-mesh / 12k-tri / 88-bone** animated skeleton that renders (matches the static bake's shape
that we know works).

**Increment 2 — full-size + textured ✅.** The SDK-built skeleton uses the FBX's **native scale** (~0.1 u), *not* the
Factory `size` field (that only applies to Factory bakes). So it came in as a 1-pixel speck. Fix: bump the FBX's
**Model → Scale Factor** (~30) → Apply → Reimport skeleton + clip. Renders full-size and correctly textured (the
registry atlas applies fine).

**Increment 3 — the animation is LIVE, but plays the DONOR's clip ⏳ (the finish line).** The drone's bones visibly
animate — but into a *contorted* pose, because the pawn is playing the **donor APC's** animation on our skeleton: the
APC clip's curves (its ~16 turret/axle bones) get applied **by bone index** to our drone's first ~16 bones, so the
underside/arms twitch while the props (higher indices, untouched by the APC clip) stay still. **This is the proof the
pipeline works — it's just the wrong clip.**

**The exact final lever (decompiled `AnimationManager.GetLocalBoneTRS(ref PawnManager.PawnEntry, boneIndex)`):** each pawn
carries **`Pose0…Pose4`** — up to five blended animation poses, each with a `.Weight` and an animation id. Therefore:
- **All pose weights 0 → the bone uses its rest `Local`** = drone sits in its correct **rest pose, no contortion** (a clean
  *static* drone).
- **`Pose0` = our `hover` animation-id, weight 1 → the props spin** (the goal).

Both fixes are the same lever: **override the pawn's `Pose0…4`**. The remaining work: these live in
**`PawnManager.PawnEntry`** (the *Presentation* DLL, not yet decompiled/hooked), and the game re-sets them each frame — so
it needs a **PawnManager hook** that finds our pawn's entry and overrides its poses per frame, plus **registering our
ClipCollection** (via the empty `ENC_ModAnimationContent`, or injecting into `loadedAnimationClipCollections` + `Apply`)
so `GetAnimationId(hoverGuid)` resolves. That's the scoped finish line.

**Net:** the first **animated injected model in Humankind renders, textures, scales, and drives its skeleton in-game**.
Only the last step — pointing the pawn's pose at our own clip — remains, and its mechanism is now fully mapped.

### Phase 3 COMPLETE — the props spin in-game ✅ (2026-07-03)

**Done. The first animated custom model in Humankind plays its own baked animation in-game** — the ReconDrone's
propellers spin. The runtime chain (all in `UniversalInject`):

1. **Register the clip.** The clip-collection builder lives inside `AnimationManager.Apply()` (it reads
   `loadedAnimationClipCollections`, copies each `PoseDataBytes` into the GPU buffer, and fills `animationIds`). So we
   **load our ClipCollection by Amplitude guid, append it to `loadedAnimationClipCollections`, then let the existing
   `Apply()` (already called to register skeletons) bake it in.** Its animation id is then resolved via
   `GetAnimationId(clipEntry.UnityAnimationClip)`. Registry field: `"clip": [a,b,c,d]` (the ClipCollection guid; static
   models leave it `[0,0,0,0]`).
2. **Play it per pawn.** Harmony **postfix on `PawnManager.AddPawnEntry(ref PawnEntry)`** — the game writes
   `pawnEntries[pawnCount-1]` once per pawn per frame. If that entry's `SkeletonId` matches our animated skeleton, we set
   **`Pose0.AnimationId` = our clip id, `Pose0.Weight` = 1, `Pose0.Time` = `Time.time`** (advancing ⇒ it plays), and
   **zero `Pose1..8`**. This overrides the donor's animation with ours.

**The one non-obvious trap (cost an invisible drone):** `GetLocalBoneTRS` divides the blended pose by `sumWeight`
(line ~1148). **Zeroing *all* pose weights ⇒ divide-by-zero ⇒ NaN ⇒ the whole mesh vanishes.** There is no "rest pose by
zeroing weights" — you must keep at least one pose (`Pose0`) at weight 1 pointing to a *valid registered* clip.

**Log of a clean run:** `loaded clipCollection` → `injected clipCollection at [106]` →
`ReconDrone(skel 73, anim 256959)` → `pose hook: 'ReconDrone' -> Pose0 anim 256959`.

**Recap of the full recipe for an animated custom model:**
1. Model with an armature + skeletal clip → **slim FBX** (join to 1 mesh, 1 material, decimate, keep armature+clip).
2. SDK editor: **AnimationSkeleton** (SetPrefab the FBX → Reimport) + **AnimationCollection** (set Skeleton, fill from
   folder → Reimport). Scale via the FBX **Model→Scale Factor**.
3. Registry: point `skel` at the skeleton guid + add `"clip"` = the ClipCollection guid (both via `AmplitudeGuidLogger`).
4. Plugin: registers the skeleton (existing), **injects the clip + resolves its id (Apply)**, and **forces `Pose0` via
   the `AddPawnEntry` hook**. No GPU-skinning hang; props spin.

**Known minor artifact:** the drone's camera-gimbal pan is baked into the `hover` clip (strip those keyframes + re-bake
to remove). Cosmetic.
