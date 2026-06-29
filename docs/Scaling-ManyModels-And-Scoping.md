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

### The recipe (editor-baker route — per model)
1. **Clone the skeleton asset** → new asset GUID. Keep `BoneInfos`, `BBox`, `animator*` GUIDs **unchanged** (reuses
   the original's working bones + animation). Replace `skinnedMeshInfos[i].FxMeshContent` with our baked mesh and
   give that `FxMeshContent.Guid` a **unique** value. Set `prefab`/`SourcePrefab` to a **new unique GUID**.
2. **Register it for `Apply()`** — add the clone's GUID to the mod's `AnimationManagerContent.MeshCollections[]` so
   `AnimationLoad()` registers it **before** `Apply()` builds the GPU buffers. Add it to the relevant
   `AnimationClipCollections[]` so it gets animation rows (for a static prop, reuse the original's idle).
3. **Unique texture** — author a `Material` (unique GUID) + matching `FxOutputLayer`; add an
   `OutputLayerEntry { Material, OutputLayer }` to `AnimationManagerContent.OutputLayerEntries[]`.
4. **Body fragment** — a `PresentationPawnFragmentSkinnedMesh` with `MaterialRef =` your material GUID and
   `SkinnedMeshPath =` your mesh name.
5. **Point only the target unit** — clone its `PresentationPawnDescription` (new GUID), set `Template.Guid` = the
   clone skeleton's new `SourcePrefab` GUID, set its slots to your fragment, and repoint just that unit definition.

Every link is GUID-keyed and additive, so **N models coexist** with zero cross-talk. The whole thing is **native
data — no runtime plugin swap needed** once baked.

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

- **Deployed:** the robust **global swap** (both Era-6 transports → LCAC). Renders correctly; not scoped. Good
  fallback / quick-and-dirty option for skeletons used by exactly one unit.
- **Recipe known (§4).** Next: implement the editor-baker that clones the LandingCrafts skeleton asset + wires the
  `AnimationManagerContent` entries + repoints only the hovercraft's Description. First milestone = the hovercraft
  rendering natively from its own cloned skeleton (scoped, no plugin swap). Then generalize into the import package
  (one FBX/OBJ → the full GUID-keyed asset set, automated).
