# Humankind Custom 3D Asset Injection — Progress, Mechanism + Asset Spec

*Status: **✅ WORKING (2026-06-28). A custom mesh renders in the live game.** A hand-built cube now draws on the
zeppelin/cruise-missile unit and bombards with no hang. THE KEY: do NOT inject a custom skeleton (that's what hung).
Keep the unit's REAL skeleton + animation + material, and only repoint its mesh entry's GPU `MeshIndex` to a custom
mesh we upload at runtime. Custom GEOMETRY is solved; custom TEXTURE/MATERIAL is the next frontier (the box wears the
missile's material — the "amplitude passable material+shader" problem shakee is also stuck on).*

## ✅ THE WORKING RECIPE (custom geometry on an animated unit)

1. Bake a mesh into an Amplitude `Skeleton`/`MeshCollection` asset in the Mod Editor, ship it inert in the mod.
2. BepInEx plugin, Harmony postfix on `AnimationManager.GetMeshCollection(Guid)`:
   a. Load our baked collection by `Amplitude.Framework.Guid`.
   b. **Reset its `loadingStatus` to NotLoaded** (it ships `Loaded`, which makes `LoadIFN` a no-op).
   c. **`LoadIFN(fxComponentMeshContentManager, FXMeshLayerIndex, anySlot)`** → runs `Load` → `GetMeshIndex` uploads
      our mesh to the GPU mesh-content manager and returns a valid `MeshIndex` (e.g. 683). (The upload is
      skeleton-index-independent; we only pass a slot because the signature wants one.)
   d. When `GetMeshCollection` returns the target unit's skeleton, **mutate that REAL skeleton's
      `skinnedMeshInfos[0].MeshIndex` = our uploaded MeshIndex** (struct in an array → box, set, write back). Do NOT
      replace the skeleton object. The unit keeps its real bones/animation/material and draws our mesh.
3. **Make it reload-robust.** The engine re-runs `LoadIFN` on the unit skeleton (re-present, end-of-turn, **save
   load**), which resets `MeshIndex` back to the original mesh — so a one-time swap **reverts**. Fixes:
   - **Re-apply the swap on every `GetMeshCollection`** for that skeleton (idempotent; only acts/logs when the index
     was reset). Catches re-presents.
   - **Re-upload when the FX manager instance changes.** A save load rebuilds `FxComponentMeshContentManager` and
     drops our mesh, leaving a stale index. Cache the manager we uploaded against; if it differs, reset our
     collection's `loadingStatus` and `LoadIFN` again to get a fresh `MeshIndex`. Catches save loads.
4. Result: custom mesh renders, animates, casts shadows, survives loads/turns, no crash. (Swaps globally for that
   mesh — fine for a POC; for a single unit, gate by which pawn def is loading.)

**Proven model (procedural, no FBX):** the baker `ZeppelinModel.cs` builds a real airship — an elongated ellipsoid
hull + 3 engine gondolas + a 4-fin tail cross — by `Mesh.CombineMeshes` of scaled primitives, skinned 100% to the
single `Base` bone. Mesh-space axes that matched in-game: **+Y/-Y = long axis (forward = -Y, tail fins at +Y),
-Z = underside** (gondolas). It wears the borrowed unit's material (texture is the open frontier).

**Why injecting a custom skeleton failed:** the GPU skinning/animation compute on a runtime-injected skeleton hangs
silently (obfuscated, untraceable). Reusing the real skeleton avoids that path entirely. Proven by: it hung
identically whether our fragment mesh was empty (0) or real (683) — i.e. independent of the mesh — so it was the
skeleton object; swapping only the mesh on the real skeleton then worked.

**Next frontier — custom material/texture:** the mesh wears the borrowed unit's material. A custom texture needs an
Amplitude-passable material + `FxOutputLayer` (`OutputLayerFromMaterialGuid`), which shakee flags as "#3 of the
AnimationManager / the hard part." Likely solvable by a BepInEx script that dumps usable material refs / authors a
material+shader (shakee's plan), or by reusing a vanilla material whose look is acceptable.

---

### (historical) earlier status — superseded by the WORKING recipe above

*Was: NOT working yet — injecting a custom skeleton CRASHED at render. The notes below trace how that wall fell.*

---

## TL;DR — what is and isn't true

**What runs (log-confirmed):** a standard BepInEx plugin loads a custom Skeleton (baked in the Mod Editor) from the
mod bundle, *calls* `RegisterMeshCollection`/`Register`, and its Harmony redirect hands that skeleton to a real unit.
So the runtime asset-load + registration + redirect *path is reachable* from a normal plugin — no exe patching.

**What does NOT work:** the game then **hangs / NullRefs in the render pipeline** when it tries to draw the custom
skeleton. **No custom model has rendered in-game.** The goal is unmet.

**Unknown:** whether a properly-authored model clears the render crash, or whether there are deeper blockers. The
render code is obfuscated and untraceable, so this is unproven either way. Treat "a real mesh will fix it" as a
hypothesis, not a fact.

---

## What's confirmed vs. what failed

In-game log from the running plugin:
```
zeppelin skeleton injected: Zeppelin_Skeleton, SkeletonInstance=Zeppelin_Skeleton   <- our skeleton loaded; register() called
redirecting 'Unit_Era6_CruiseMissile_01_Skeleton' -> zeppelin                        <- redirect hook fired, returned our skeleton
```
These prove the plugin **executed** each step. They do **not** prove the registration "took" correctly or that the
skeleton is renderable — because immediately after, **the game crashes at load and never finishes** (NullRef in
obfuscated render code). So: the API path is reachable; the end result is a crash.

Also proven earlier in the same plugin: **READ** the registry (`AnimationManager.Content.MeshCollections`),
**WRITE** to it (`RegisterMeshCollection` — empty collection raised the live count), and **reach a specific mod's
assets** (found ENC's `Era5_Common_Zeppelins_01` in the `PresentationPawnDefinition` database).

---

## The injection mechanism (for plugin devs / shakee)

**Plugin side** (BepInEx 5 / net471 / HarmonyX — repo: `C:\Repo\ENCAccessProof`):

1. **Hook** `AnimationManager.GetMeshCollection(Guid)` (Harmony postfix, reflection-resolved type).
2. On first call, **load the baked skeleton**:
   `Amplitude.Framework.Asset.AssetDatabase.LoadAsset<MeshCollection>(guid)`.
   - The GUID type is **`Amplitude.Framework.Guid`** — a struct of **4 Int32 fields `{a,b,c,d}`**. Its `ToString()`
     equals the Unity hex GUID. Build it by setting those 4 fields (dump the exact values at editor time via
     `AssetDatabase.GetAssetGUID(asset)` then reflect its fields — don't guess the Unity→Amplitude encoding).
3. **Register** it: `AnimationManager.RegisterMeshCollection(mc)` + `AnimationManager.Register(skeleton)` (gives the
   GPU pawn a SkeletonId).
4. **Redirect**: in the `GetMeshCollection` postfix, when the result is the target unit's skeleton (match by name,
   e.g. `Unit_Era6_CruiseMissile_01_Skeleton`), return our skeleton instead. **No pawn-def mutation** → no
   re-presentation. Narrow the match to the *skeleton* only (not the unit's projectile/effect collections, or you
   break the bomb FX and hang the load).

Why redirect instead of repointing the Description: mutating the pawn def at load makes the game *re-present* the
unit (you get a duplicate). The GetMeshCollection redirect leaves the def alone → the unit presents once, drawing
our skeleton.

**Why a custom skeleton at all** (the data-mod wall): a unit's animated model = a Description's rigged `Template`,
whose skeleton must be in `AnimationManagerContent.MeshCollections[]`. That registry is baked into the engine
bundles — not in the project, not in Override-from-Archives, not in a full archive export. So a data mod can't
register a skeleton; only runtime code (BepInEx) can. (Static idle-Body fragments and districts *do* work as data
mods — different, supported path.)

---

## The editor-side bake (for whoever builds the asset into the mod)

Baker script: `ENCReload/Assets/Scripts/Editor/ZeppelinModel.cs`. It:
1. Builds a prefab rig: `root -> Dummy_Root -> Base` (single bone — **matches the cruise-missile rig** so it binds
   the same way), a `SkinnedMeshRenderer` skinned 100% to `Base`, a generic `Avatar`, and the **missile's bindpose**
   (`[1,0,0,0; 0,0,1,0.101; 0,-1,0,-0.069; 0,0,0,1]`) so the hull orients nose-forward.
2. Bakes an **`Amplitude.Mercury.Animation.Skeleton`** asset (it's `[CreateAssetMenu]`, `: MeshCollection`):
   `skel.SetPrefab(prefab)` then `skel.Reimport()` → bakes bones + skinned mesh into the engine's `FxMeshContent`.
3. Ships the `Skeleton` + prefab **inert** (unreferenced) in `Assets/Resources` so they bundle but don't crash a
   no-plugin install.

To swap the mesh: replace the prefab's mesh, re-bake, rebuild the mod. The plugin loads it by GUID — so log the new
GUID's `{a,b,c,d}` after baking and update the plugin constant (or read it from a manifest).

---

## FULL PIPELINE MAP + FINAL WALL (2026-06-28, deepest pass)

Decompiled the entire unit-render path and fixed every CPU-side blocker. Sequence the plugin now performs and what
each fixed:

1. **Load** the baked Skeleton by Amplitude.Guid. ✅
2. **Reset `loadingStatus` to NotLoaded.** Our asset ships `Loaded`, which makes `MeshCollection.LoadIFN` a NO-OP
   (`if (loadingStatus != Loaded) Load()`). Without the reset, `Load()` never runs, so the GPU `MeshIndex` stays 0
   and `SkeletonId` is never assigned → the earlier NullRef/hang. After reset, `MeshIndex` came back **683**. ✅
3. **Reuse the missile's GPU skeleton slot.** A freshly-`Register`ed skeleton gets a new index (70) with no entry in
   the load-time-sized `gpuSkeletonEntriesBuffer` / `gpuSkeletonBoneEntiesBuffer` (built from the engine-bundled
   `AnimationManagerContent`) → bone walk reads garbage. Instead we `LoadIFN(ourSkeleton, fxMgr, FXMeshLayerIndex,
   missileSkeleton.SkeletonId)` so `Skeleton.Load` sets our `SkeletonId` = the **missile's slot (48)**, reusing its
   valid, already-uploaded bone data (our rig is the same single `Base` bone). ✅
4. **Rename our mesh entry to the missile's mesh name.** The render mesh isn't taken from the skeleton directly — it
   comes from `PresentationPawnDefinitionAddOn` → `FragmentEntry.Load`, which looks it up **by name**:
   `meshCollection.GetFxMeshIndex(SkinnedMeshPath)` where `SkinnedMeshPath` = the missile's mesh name. Our entry was
   `Zeppelin_ModelMesh` → no match → index 0. Renamed at runtime to `Unit_Era6_CruiseMissile_01`. (The fragment's
   `FxOutputLayer` comes from the **pawn def's** `MaterialRef` = the missile's material, which is valid — so material
   was never our blocker.) ✅
5. **Redirect** `AnimationManager.GetMeshCollection(Template.Guid)` (called in `AddOn.Load`) to return ourSkeleton,
   so `AddOn.MeshCollection`/`AddOn.Skeleton` become ours. ✅

**Result: still a silent hang during load — and it hangs IDENTICALLY whether the fragment resolves to an empty mesh
(index 0) or our mesh (683).** That is the decisive datapoint: the freeze is **independent of the rendered mesh**.
It is not the mesh, the material, the output layer, the mesh-name lookup, or the bone-slot — all fixed/ruled out.
What remains common to every variant is **the GPU skinning/animation compute running on a runtime-injected
skeleton**. That code is obfuscated compute-shader / native render territory with no exceptions and no diagnostics —
**not instrumentable or traceable via remote Harmony/reflection.** This is the practical wall.

**Most promising un-tried direction:** the cruise-missile fragment is a *skinned* mesh (goes through the skinning
compute that hangs). The pawn fragment system also supports a **static** mesh fragment
(`PresentationPawnFragmentMesh`, attaches a model to a bone with **no skinning**). A zeppelin is rigid and needs no
skinning. Injecting a *static* fragment (register the model collection — `RegisterMeshCollection` is proven to work —
and add a `PresentationPawnFragmentMesh` to the pawn def, hiding the skinned one) would take a **different GPU path**
and may sidestep the skinning hang entirely. This is the recommended next experiment if the work continues; it's a
real redesign, not a tweak.

---

## CORRECTED FINDING (2026-06-28, late): the asset is VALID — it's a runtime GPU-integration problem

Dumped our baked collection vs the cruise missile's, field by field. **They match:** same `Skeleton` type, 1
`skinnedMeshInfos` entry with `FxMeshContent` **set**, valid `SkeletonInstance`, `LoadingStatus=Loaded`,
`BonesCount=2`, and identical `FxMeshContent` internals — **same `encodingFormat=PosUVNormalTangentBones`**, same
`versionIndex=3`, consistent vertex/quad/bytes counts and bbox. So the **mesh data the bake produces is correct and
complete.** A "needs a properly-authored model" conclusion is therefore **wrong** — a hand-built cube is a valid
asset here.

**What actually moved the needle:** `RegisterMeshCollection` only *lists* the collection; it does NOT upload the
mesh to the GPU. Adding **`MeshCollection.LoadIFN(FxComponentMeshContentManager, FXMeshLayerIndex, skeletonIndex)`**
(skeletonIndex = the return of `AnimationManager.Register(skeleton)`) **cleared the NullRef** — the unit then
presents far enough to play audio.

**Where it's now stuck:** a **silent hang** in the obfuscated GPU render path — no exception, no diagnostic. Most
likely a **mesh-index / skeleton-index coordination** mismatch: we `Register` our skeleton (got index 70) and
`LoadIFN` the mesh under that index/layer, but the renderer looks it up via
`FxComponentMeshContentManager.ContentLayer.GetFxMeshStructIndex` under a *different* index (the pawn re-registers
the skeleton in its own `AddOn.Load`, getting its own id). So: mesh uploaded, but the renderer can't find it where
it expects → hangs.

**Next investigation (needs deeper engine access / deobfuscation):** decompile
`FxComponentMeshContentManager.ContentLayer.GetFxMeshStructIndex` to learn the exact lookup key, and coordinate our
`Register`/`LoadIFN` with the index the pawn actually renders under (or hook the pawn's `AddOn.Load` to LoadIFN at
the right time/index instead of doing it eagerly). This is the genuine remaining problem — runtime GPU/skeleton
index plumbing, not asset quality.

---

## RENDER-PIPELINE REQUIREMENTS — historical (superseded by the corrected finding above)

The game's skinned-mesh format is **`VertexEncodingFormat 6`**. A mesh must satisfy *all* of these or it hangs /
NullRefs in the render pipeline:

1. **Tangents — REQUIRED.** Missing tangents → silent GPU hang on load. (`RecalculateTangents()` cleared the hang;
   an authored mesh must export tangents.)
2. **A material that maps to a game `FxOutputLayer`.** This is the current blocker: a plain Unity `Standard`
   material has no output layer → `NullReferenceException` at render
   (`AnimationManagerContent.OutputLayerFromMaterialGuid(materialGuid)` returns null). The mesh needs a material the
   game's FX system recognizes — i.e. one whose GUID is in the engine's output-layer registry, *or* the plugin must
   assign a valid `FxOutputLayer` at runtime. **This is the next thing to crack.**
3. **Normals + UVs** (needed for lighting and for tangent generation).
4. **Rig:** skinned to bone(s) that match what we register. For a drop-in replacement of an existing unit, mirror
   that unit's skeleton (the cruise missile is a *single* `Base` bone — simplest case).

**What to deliver (artist):** an airship/zeppelin FBX, sane poly count, **rigged to a single root bone** (or the
target unit's skeleton), with **proper UVs, normals, and tangents**, and a **material compatible with the game's
shader/FX** (the trickiest — see #2; may require reusing a known game material or runtime output-layer assignment).

---

## Remaining unknown (next investigation)

The **material → `FxOutputLayer`** mapping for a custom mesh. Two candidate fixes:
- Author the mesh's material so its GUID is one the game already has an output layer for (reuse a vanilla unit
  material), or
- Have the plugin set the `FxOutputLayer` on the registered collection at runtime
  (`AnimationManagerContent.OutputLayerFromMaterialGuid` / the `OutputLayerEntries`).

Once a mesh renders, the framework is complete: any modder ships a baked skeleton + a manifest, and the (single,
optional) injection plugin makes it appear — with a crash-safe vanilla fallback for players without the plugin.

---

## Artifacts
- **Plugin:** `C:\Repo\ENCAccessProof` (BepInEx 5, net471; `Prober.cs` + `Patches/ZeppelinInjectPatch.cs`).
- **Baker:** `ENCReload/Assets/Scripts/Editor/ZeppelinModel.cs`.
- **Reference HK BepInEx mods:** `C:\Repo\Humankind-GUI-Tools`, `C:\Repo\shakee.Humankind.FameByScoring`.
- **Full saga / decompile notes:** `C:\GameData\ModTools\UnitPreview-Findings.md`,
  `C:\GameData\ModTools\Custom3DModels-Findings-Shareable.md`.
