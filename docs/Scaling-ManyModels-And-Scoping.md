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
