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

### ⚠️ UNRESOLVED: detailed atlas maps scrambled in-game — full diagnosis (2026-07-01)
The Stealth Cruiser's detailed atlas renders **scrambled in-game** (helipad marking lands on the superstructure, deck
numbers on the hull, etc.), while the **Zeppelin (simpler texture) maps correctly**. A long, methodical hunt narrowed
it down by *proving each stage clean* rather than guessing:

**Ruled OUT, each with a rendered proof:**
- **Source model UVs** — the artist's `.blend` renders perfectly in Blender (clean grey hull, helipad aft, red+black
  waterline). The UVs are a normal single [0,1] map. Not the source. *(The Sketchfab GLB, by contrast, arrives with
  UVs already scrambled by Sketchfab's auto-conversion — always re-export from the `.blend`/original, not the GLB.)*
- **Our GLB→OBJ converter (`glbconv`, grid 0)** — ran it on the clean GLB, rendered the resulting OBJ + extracted
  albedo in Blender: **clean**. It reports `verts=1270 (UVs preserved)`, unified `v/vt/vn` indices. Converter is fine.
- **Unity's OBJ import welding** — Unity re-splits the mesh (1270→1439 verts) on import. Set `weldVertices = false` in
  the baker's `ModelImporter` so it stops merging seams; the re-split preserves UVs regardless.
- **The baked mesh itself** — added `MeshDumper` (menu *Tools/Dump StealthCruiser Meshes*) to export the Unity-imported
  mesh AND the final `_ModelMesh` back to OBJ. Rendered both with the atlas: **clean**. The baked mesh UVs are correct.
- **The atlas** — 0 yellow, correct layout, `StealthCruiser_Atlas 2048x1024` loads fine (runtime log).
- **Host material `_MainTex` crop** — runtime logs `host _MainTex_ST scale=(1,1) offset=(0,0)`. Identity; not a crop.
  (Added a reset anyway — harmless, correct.)
- **Texture V-flip** — rendered the mesh with a vertically-flipped atlas; the resulting scramble does **not** match the
  in-game one. And logically **no texture transform (flip/crop/rotate) can move a flat-deck marking onto the raised
  superstructure** — that needs genuinely wrong UV *coordinates*, not a shifted texture.

**Conclusion:** correct mesh UVs + correct atlas + identity transform go in, yet the game samples *wrong* UVs at render.
The scramble is introduced **inside Amplitude's mesh upload / GPU-skinning path**, downstream of everything the bake and
the atlas control. The hopeful sign: the **Zeppelin renders textured correctly**, so the mesh-swap *can* carry UVs —
something about how this particular mesh uploads differs (candidate leads: the 1270→1439 re-split, or `skinnedMeshInfo`
carrying UV/attribute data separate from the swapped `MeshIndex`).

**Next step (open):** decompile Amplitude's `skinnedMeshInfo` + mesh-upload (`LoadIFN`/`GetFxMeshIndex`) path and add a
runtime probe that reads back the uploaded mesh's UVs at skin time, to see where our UVs get replaced. This is the last
mile for detailed position-specific textures; simpler/uniform textures already work.

**Tooling from this session (kept):** `baker/MeshDumper.cs` (mesh→OBJ dump); headless **Blender** render/export scripts
(blend→GLB with material repair + texture recovery, OBJ+atlas render) — Blender is installed and drives cleanly from the
command line. See also the `.blend` import path above.

### Toward a Unity package (gaps)
Decouple hardcoded paths (`ModelRegistry.ConfigDir`, the `dotnet`/converter path) into settings; neutral naming (drop
"ENC", namespace `ENCAccessProof`); ship the editor package + the companion BepInEx plugin together with docs; consider
a Unity-native glTF importer (glTFast) instead of the `dotnet` converter. Mirror of editor scripts lives in
`ENCAccessProof/baker/` (ENCReload git tracks only `Assets/Databases`).
