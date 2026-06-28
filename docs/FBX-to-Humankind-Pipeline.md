# Custom 3D Models in Humankind — How It Works, and a Blueprint for an FBX→Asset Unity Package

*This is the authoritative "why it works" for rendering a fully custom 3D model (a real FBX — mesh **and** texture)
on a unit in Humankind via a BepInEx plugin + a mod-baked asset, **no executable patching**. It's written both as a
reference and as the design spec for a future Unity package that turns an arbitrary FBX into a Humankind-ready asset.*

---

## 0. TL;DR — the two halves

Rendering a custom model takes **two cooperating halves**:

| Half | Where | Job |
|---|---|---|
| **A. Asset generation** | Unity **editor** (the Mod Editor project) | Turn an FBX + textures into a baked Amplitude **Skeleton/MeshCollection** asset (holds the GPU `FxMeshContent`) + a **texture atlas**, shipped in the mod bundle. *This is what the future package automates.* |
| **B. Runtime injection** | **BepInEx** plugin (HarmonyX) | Load those baked assets and splice them into a **real vanilla unit**: overwrite the unit skeleton's mesh index with ours, and set the unit material's `_MainTex` to our atlas. |

**Why both are needed.** The engine's skeleton / mesh / material registries (`AnimationManagerContent`) are baked into
**compiled AssetBundles**, not the moddable data layer. Proven three ways (editor probe → Override-from-Archives → a
full 478 MB archive export): a custom skeleton's registration is **not** reachable from a data mod. So a pure data mod
cannot introduce a new animated model — **runtime code must do the splice.** The plugin is that runtime code.

---

## 1. The render pipeline (what the engine actually does)

Decompiled chain for drawing a unit (`Amplitude.Mercury.Animation` / `…Graphics.Fx`):

1. **`PresentationPawnDefinitionAddOn.Load`** resolves the unit's model:
   - `MeshCollection = AnimationManager.GetMeshCollection(Description.Template.Guid)`
   - `Skeleton = MeshCollection.SkeletonInstance`
2. **`FragmentEntry.Load`** turns the def's fragments into draw data:
   - mesh = `meshCollection.GetFxMeshIndex(SkinnedMeshPath)` — **looked up by NAME**, returns `skinnedMeshInfos[].MeshIndex`
   - output layer = `AnimationManagerContent.OutputLayerFromMaterialGuid(fragment.MaterialRef)`
   - `EncodedMeshAndVisualParticleCount = GetEncodedMeshAndVisualParticleCount(meshIndex, layerIndex, fxOutputLayer)`
3. **`PawnManager.Add`** builds a `GPUPawnDescriptor` from `AddOn.Skeleton` (BBox, `BonesCount`) + `FragmentEntries`.
4. **GPU-driven render**: `FxOutputLayer.RenderOutput.AddDrawCommand` issues `DrawProceduralIndirect` with the layer's
   **`currentRenderMaterial`**; bones come from `gpuSkeletonEntriesBuffer[SkeletonId]`, the mesh from `MeshIndex`.
5. **Shader**: *every* unit uses **`Amplitude/ParticleSkinnedMeshRender`** (confirmed across all 643 output layers).
   The look is the **material** (a 6-map PBR set — `_MainTex`=AlbedoTransparency, `_NormalMap`, `_MetallicMap`,
   `_RoughnessMap`, `_AmbiantOcclusionMap` [sic], `_EmissiveMap`), wrapped at runtime in a `<Unit>_OutputLayer_Proxy`.

**Consequences that make the whole thing possible:**
- Geometry is selected by a single integer **`MeshIndex`** on the skeleton's mesh entry.
- The skin is **just a material** on one shared shader — no per-unit shader.
- Bones/animation live entirely in the **skeleton** and its GPU buffers.

---

## 2. Why the naive approach fails — and the key insight

**Injecting a custom skeleton hangs the game.** Registering a new skeleton (`RegisterMeshCollection`/`Register`) and
redirecting the unit to it produces a **silent GPU hang** at load. The skinning/animation compute runs on a
runtime-injected skeleton whose bone data isn't in the **load-time-sized** GPU skeleton buffers
(`gpuSkeletonEntriesBuffer` / `gpuSkeletonBoneEntiesBuffer`, built from the engine-bundled `AnimationManagerContent`).
Proven **mesh-independent**: it hung identically whether the fragment mesh was empty (0) or a real index — so it was
the *skeleton object*, not the mesh.

**THE INSIGHT (credit: CalmBreakfast).** *Don't inject a skeleton at all.* Keep the unit's **real** skeleton — its
bones, animation, GPU buffers, material are all proven-good — and change only the two things you actually want
different: the **mesh** it draws (`MeshIndex`) and the **texture** (`_MainTex`). That sidesteps the hang entirely.

This is the cornerstone. Everything below follows from it.

---

## 3. Runtime injection — every step and *why* (`Patches/ZeppelinInjectPatch.cs`)

Harmony **postfix on `AnimationManager.GetMeshCollection(Guid)`**:

1. **Load our baked `MeshCollection`/`Skeleton` by GUID.**
   `AssetDatabase.LoadAsset<MeshCollection>(guid)`. The GUID type is **`Amplitude.Framework.Guid`** — a struct of 4
   `Int32` `{a,b,c,d}`; dump those at bake time (`AssetDatabase.GetAssetGUID`) so the plugin doesn't guess the
   Unity→Amplitude encoding.
2. **Reset its `loadingStatus` → `NotLoaded`.** *Why:* `MeshCollection.LoadIFN` no-ops when `loadingStatus == Loaded`,
   and the baked asset ships `Loaded`. Without the reset, the GPU upload (`Load` → `GetMeshIndex`) never runs, leaving
   `MeshIndex = 0` → render NullRef / hang. **This single stale flag was the original crash.**
3. **`LoadIFN(FxComponentMeshContentManager, FXMeshLayerIndex, anySlot)`.** Runs `Load` → `GetMeshIndex` uploads our
   mesh to the GPU mesh-content manager and returns a valid **`MeshIndex`**. (The upload is skeleton-index-independent;
   the slot arg only matters for skeletons we don't register.)
4. **Repoint the REAL unit skeleton's mesh.** When `GetMeshCollection` returns the target unit's skeleton, mutate
   **`skinnedMeshInfos[0].MeshIndex = ourMeshIndex`** (it's a struct in an array → box, set, write back). *Why by
   MeshIndex:* the fragment resolves its mesh by name via `GetFxMeshIndex`, which returns `skinnedMeshInfos[].MeshIndex`;
   overwriting it makes the unit draw **our** mesh while keeping its skeleton, animation, and material.
5. **Texture.** Set our atlas `Texture2D` on the unit's `FxOutputLayer.RenderOutputs[].currentRenderMaterial._MainTex`
   (the albedo slot, found via `AnimationManagerContent.OutputLayerEntries` → match by name/matRef). *Why per-frame:*
   `useProxys = True` → the proxy textures load **asynchronously** and rebind `_MainTex` over ours if set once; re-apply
   from **`Plugin.Update`** (the BaseUnityPlugin is a MonoBehaviour) so we win after the proxy settles.
6. **Reload-robustness.** Two engine behaviours fight a one-shot swap:
   - The engine **re-`LoadIFN`s** the unit skeleton on re-present / end-of-turn / **save load**, resetting `MeshIndex`
     to the original → **re-apply the swap on every `GetMeshCollection`** (idempotent).
   - A **save load rebuilds `FxComponentMeshContentManager`**, dropping our uploaded mesh (stale index) → **re-upload
     when the manager instance changes** (cache the bound manager; on change, reset `loadingStatus` + `LoadIFN` again).

That's the entire runtime contract: **load → reset status → LoadIFN → repoint MeshIndex → set _MainTex → keep
re-applying.** No skeleton injection, no def mutation, no exe patching.

---

## 4. The FBX→asset pipeline — every step and *why* (`baker/ZeppelinModel.cs`)

**Input:** an FBX + its textures. **Output:** a baked Amplitude **Skeleton** asset (contains the `FxMeshContent`) and a
**texture atlas** asset, both shipped in the mod's `Resources`, plus their GUIDs.

| Step | What | Why |
|---|---|---|
| **A. Combine to one mesh** | Instantiate the FBX; for every `MeshFilter`/submesh, copy verts + that submesh's triangles and add a `CombineInstance` with the part's local matrix; `Mesh.CombineMeshes(merge=true)`. | The proven engine path bakes a **single** `skinnedMeshInfo`. Multi-submesh/multi-material is unproven and risks multiple mesh entries. One mesh = one `MeshIndex` = one `_MainTex`. |
| **B. Atlas + remap UVs** | `Texture2D.PackTextures` the part albedos; remap each submesh's UVs into its atlas rect (by material-name → rect). | One mesh → one albedo. The model's parts use different textures, so pack them and rewrite UVs so each part samples its region. |
| **C. Opaque + clean dead-zone** | Force atlas alpha = 255; repaint near-black pixels to a neutral. | "Albedo**Transparency**" alpha can read as **cutout** (see-through hull). And the model's albedo had a large **black UV dead-zone** the hull top mapped onto → rendered black; repaint it. |
| **D. Normalize scale + orientation** | Recenter; uniform-scale the longest axis to the unit's size; auto-align longest axis → **Y** (`Quaternion.FromToRotation`); apply a configurable `OrientEuler` tweak. | Third-party models have arbitrary scale/orientation. The reused unit's bindpose expects the long axis on **Y**, forward **−Y**. (Our airship needed a 180° roll for right-side-up.) |
| **E. Fix winding (radial-outward)** | For each triangle, flip it if its geometric normal points *toward* the centred origin: `dot(cross(edges), centroid) < 0`. | **Backface culling uses winding, not normals.** The model had **~65 % inside-out faces** *and* unreliable authored normals → see-through/dark surfaces. For a convex hull, "outward" = direction from the centred origin. Geometry-based, model-normal-independent. After this, `RecalculateNormals` gives clean outward normals. |
| **F. Rig + bake** | Skin 100 % to one **`Base`** bone; set the reused unit's bindpose; `RecalculateNormals` + **`RecalculateTangents`**; bake the Amplitude **Skeleton** (`SetPrefab` + `Reimport` → bakes bones + skinned mesh into `FxMeshContent`); save the atlas as a `Texture2D` asset; emit skeleton + atlas Amplitude GUIDs. | Single rigid bone matches the reused unit's single-bone rig (a rigid prop needs no skinning). **Tangents are REQUIRED** — the engine's `VertexEncodingFormat 6` silently hangs without them. |

**Net:** an arbitrary FBX becomes a single, correctly-wound, correctly-textured, tangent-bearing, single-bone skinned
mesh baked into the engine's own `Skeleton`/`FxMeshContent` format, plus a ready `_MainTex` atlas.

---

## 5. Blueprint for the Unity package

**Goal:** a developer drops in an FBX + textures, picks a target vanilla unit, clicks **Generate**, and gets a baked
asset in their mod + a config the companion plugin reads — no per-model coding.

**Components:**
1. **Editor window / scripted importer** — fields for the FBX, textures (or auto-extract from FBX), a **target-unit**
   picker (the vanilla unit whose skeleton/animation/material to reuse — e.g. an air unit for a flyer), and **scale +
   orientation** controls with a live preview. The **Generate** button runs the pipeline of §4.
2. **Output manifest (JSON), not hardcoded GUIDs** — emit `{ targetUnit, skeletonGuid, atlasGuid, settings }`. The
   plugin reads the manifest at runtime, so it stays **generic** (no recompile per model). This is the single biggest
   upgrade over the current hardcoded constants.
3. **Generic companion plugin** — the §3 injector, driven by the manifest: for each entry, hook `GetMeshCollection`,
   match the target unit's skeleton by name, swap `MeshIndex` + `_MainTex`, with the reload-robustness of §3.6.
4. **Target-unit catalog** — ship the **render dump** (`DumpRenderInfo`: skeleton + animation + the full
   `matRef → FxOutputLayer → material/shader` catalog + texture slots) so developers can pick a suitable host unit and
   see exactly what material/shader a fully-custom skin would need.

**Config the package should expose:** target unit · mesh scale · orientation (Euler) · texture atlas/cleanup on/off ·
(future) per-unit gating · (future) multi-bone / real custom animation.

---

## 6. Known limitations (and where to push next)

- **Global swap.** The swap keys on the host skeleton/matRef, so it reskins **all** units sharing it. Per-unit needs
  gating by which pawn def is loading.
- **Single rigid bone → inherited animation.** Great for rigid props/vehicles; a model that needs its *own* animation
  would require solving the skeleton-injection hang (the deep GPU-buffer problem we deliberately sidestepped).
- **Convex-hull winding fix.** Non-convex/offset parts (gondolas, fins) may have a few mis-wound faces; a per-component
  centroid (or trusting good authored normals when present) would tighten it.
- **Borrowed material/shader.** The skin rides the host unit's material on the shared shader. A *fully* custom material
  needs registering a new `FxOutputLayer` in `AnimationManagerContent.OutputLayerEntries` (the one engine-baked step the
  plugin can still do at runtime) — see the texture section of `Custom3DInjection-Spec.md`.
- **EOL game = durable.** Humankind is end-of-life (scenarios only), so the Harmony patches won't break on a future
  update — a one-time build, not a treadmill.

---

## 7. Artifacts

- **Runtime injector:** `Patches/ZeppelinInjectPatch.cs` (load → reset status → LoadIFN → MeshIndex swap → `_MainTex` →
  reload-robust; plus `DumpRenderInfo` — the render catalog).
- **FBX→asset baker:** `baker/ZeppelinModel.cs`.
- **Deeper mechanism notes:** `Custom3DInjection-Spec.md` (the full decompiled pipeline + the texture/output-layer
  details) and `UnitPreview-Findings.md` (the data-mod wall, the skeleton registry, the BepInEx route).
- **Example model:** "Дирижабль HD" by **MMD_SonicNewYear** (Sketchfab, **CC-BY**) — download into
  `Assets/Resources/Airship/`; not committed.
