# Framework Review — Universal Model Factory

Living hardening roadmap. Four review passes so far, most recent first:

1. **2026-07-12 — architectural / structural review** (see "Architectural findings" below): stepped back from
   line-bugs to *system structure*, given the stated direction (many more models; eventual distributable
   package). Surfaced the per-pawn god-method (**A1, now fixed**), the four-place registry-schema duplication
   (A3), and the `ModelEntry` god-object (A2), plus two suspected baker bugs (A4) and the package-readiness gap (A5).
2. **2026-07-07 — full critical re-review**: the runtime plugin read line-by-line,
   the editor pipeline and all tools swept by independent reviewers, and the two headline Blender findings
   **empirically verified on Blender 5.1.2** (the same install the pipeline auto-detects).
3. **2026-07-07 — external review** (received by mail): five findings + three comment-drift items, all
   verified against the code and fixed same day (see "Fixed to date").
4. **2026-07-05 — self-review**: registry safety, process timeouts, dead code. Fixed or consciously deferred.

**Overall verdict (unchanged and reinforced):** disciplined code for what it is — a reflection-heavy
BepInEx mod poking at a closed engine. Verified strengths: per-hook and per-model failure isolation,
fail-loud resolution of required SDK methods, empty-GUID validation, atomic registry writes with a
versioned git-tracked backup, bounded shell-outs with dual-stream draining, one-shot error logging on hot
paths, correct boxed-struct mutate/write-back in the pose hook. The gaps below are mostly *silent-failure
edges* and *surviving-a-stranger's-machine* items, not broken-today bugs — everything the author uses
daily works.

Severity key: 🔴 fix soon (silent data loss / silent no-inject) · 🟡 worth hardening · 🟢 cleanup.

---

## Fixed to date (condensed history)

| Date | Fix |
|---|---|
| 07-05 | Registry atomic write (`.tmp` + `File.Replace`), corrupt-file guard (`.corrupt.json`, Save refuses), versioned project backup + auto-restore |
| 07-05 | `RunBounded` 3-min cap on all five shell-outs (but see **E4** below — one unbounded path remains) |
| 07-05 | `[SerializeField]` form persistence; dead `LoadHookPatch` removed; F8 window un-broken |
| 07-06 | Resource-name whitespace/char guard (window gate + baker `Fail`), quoted glbconv arg |
| 07-06 | Multi-material GLB: glbconv emits `usemtl`/`.mtl`/solid swatches; baker matches `.jpg` albedos |
| 07-07 | *(external #1)* `LoadRegistry` no longer latches `loaded=true` before the read — retries transient failures up to 3× |
| 07-07 | *(external #2)* regex fallback parses `respawnAfterLoad` (parity with Newtonsoft path) |
| 07-07 | *(external #3)* `ModelRegistry.Save` atomic swap guarded; `Save`/`Upsert`/`Remove` return bool; `DoBake` reports "Baked, but REGISTRY SAVE FAILED" instead of a false ✓ |
| 07-07 | *(external #5)* `Prober.TestWrite` labelled: leaves an inert placeholder in the live registry until restart |
| 07-07 | Comment drift: `enc_models.txt`→`.json`; ModelRegistry header no longer claims JsonUtility works at game runtime (it silently returns empty there — the plugin **must** keep Newtonsoft); `ReadDonorFragments` header matches its whole-log-scan body |
| 07-07 | *(this review)* `EnsureRegistered` latched `registered=true` on empty entries, permanently defeating the new load-retry (retry would succeed, registration never ran again → injection silently dead for the session). Now latches only when the load actually succeeded. |
| 07-07 | csproj: `DefaultItemExcludes baker\**` — glbconv's .NET 8 publish output was being swept in as candidate assemblies, making the net471 build fail with 271 phantom type errors |
| 07-12 | **A1**: `OnPawnAdded` decomposed from a ~190-line per-pawn-per-frame god-method into a dispatcher + one named handler per behavior (verified in-game + 12/12 bake smoke test) |
| 07-12 | Registry kept **alphabetical** on load AND save (`ModelRegistry.SortByName`) — a stable Factory dropdown and no more meaningless reorder churn in the git-tracked backup on every bake |
| 07-12 | **A3**: `check_schema_parity.sh` rewritten — Newtonsoft==regex read paths, read⊆written, and read-cast==declared-type; verified to fail on each drift type (Option A: verify, don't merge) |
| 07-12 | **T3/T4** (`rig_anim.py`): albedo grab traces the Principled Base Color (not the first image node); join re-binds the armature modifier so a bone-parented-prop-first model can't export rigid. **T5** (glbconv mirrored-node winding): source + **exe rebuilt** (SharpGLTF pinned 1.0.6, verified geometry-identical across all 11 registry models); build now documented in `Tools/glbconv/BUILD.md` |
| 07-12 | **E5**: `Build`/`BuildAnimated` snapshot the baked outputs (asset + .meta) before a re-bake and restore them on any failure — a partway-failed re-bake no longer destroys the last-good model. Fail-safe (restore runs only on failure); 12/12 smoke test |
| 07-12 | **E2/E3/E4** editor hardening: Remove keys on the selected entry + honest status; static bake fails loud on 0 vertices (no silent invisible unit); `RunBounded` bounds the pipe drain so a grandchild-held pipe can't hang the editor. 12/12 smoke test |
| 07-12 | **Bake Feature Test** (`Tools ▸ ENC ▸ Bake Feature Test`): new integration test proving each baker *feature knob* does what it claims — bakes a self-contained synthetic cube per-knob and asserts a feature-specific invariant (doubleSided 2× tris, Faceted unweld, atlasMaxDim cap, heightUV, size/position, brightness/saturation, …). Non-destructive; **Tier 1** 12/12 |
| 07-12 | **Bake Feature Test — Tier 2** (`… Tier 2 — Blender + animated`): `targetTris` decimates a generated high-poly grid (5000→600), `stripParts` drops a generated named object (24→12 tris), and the animated pipeline (`BuildAnimated` → skeleton + clip) is exercised by borrowing ReconDrone + TowedGunHowitzers from the registry. **4/4** |
| 07-12 | **A4**: `BuildMultiAtlasAndRemap` submesh→rect match now tries EXACT before the loose substring — an animated multi-material model with prefix-colliding material names (`Body`/`Body_Trim`) no longer maps the wrong texture. (`MeasureLongestAxis` half verified-and-dismissed: `rig_anim` joins to one mesh, so the per-node transform is moot.) |
| 07-12 | **E6/E8** (🟢): backup restore parses in its own try/catch (a corrupt backup + missing registry no longer locks Save with a misleading error); both multi-material `PackTextures` sites free their source albedos so a bake no longer strands tens of MB until domain reload |
| 07-13 | **E7** (🟢): the static re-bake's delete-first now clears the stale animated `_Preview.prefab`/`_PreviewMesh`/`_PreviewMat` from FactorySource, so the window preview shows the fresh static model instead of the old animated one |
| 07-13 | **T6** (🟢): `rig_anim.py` hard-fails when a bone-prefix filter matches nothing (frozen-clip trap; lists the real bones); `prep_model.py` hard-fails when a strip/reduce leaves no mesh (empty-GLB trap). Both syntax-checked. **T7** lows reviewed + consciously deferred (each needs a glbconv rebuild or changes output for ~zero benefit) |
| 07-13 | **2nd critical review** (independent adversarial pass, 3 reviewers): confirmed the `OnPawnAdded` refactor faithful and E5's core sound. Fixed a `heightUV` feature-test **false-green** (now asserts V *tracks vertex height*, not merely spans [0,1]); added an **E5-rollback test** that forces a failed re-bake and proves the assets restore with original GUIDs + content. Feature Test Tier 1 now **13/13**. |
| 07-14 | **HAF multi-mod (phase 1)**: `LoadRegistry` is now a multi-**pack** loader — discovers ENC's base `enc_models.json` + any `haf_packs/*.json`, merges by `pawnDescription` (first-loaded wins on an undeclared clash, logged loud — no silent overrides), and writes `haf_load_report.txt`. The old Newtonsoft-primary + regex-fallback parse was lifted **verbatim** into `ParseModels` (transient-retry/3×-latch preserved). Baker writes a `RegistryFile` wrapper (`schemaVersion`/`modId`/`dependsOn`/`loadAfter`/`overrides`); `check_schema_parity.sh` gained wrapper-key parity (and its ModelDef-body terminator was fixed for the class rename). `dependsOn`/`loadAfter`/`overrides`/`patches` are **reserved-and-logged**, not yet enforced. Verified in-game (13 models, 1 pack, 0 conflicts). Spec: [Multi-Mod.md](Multi-Mod.md). |
| 07-16 | **Vertex budget: per-mesh cap mapped, all limits overridable**: `ContentLayer.maxMeshTriangleCount` is a per-mesh triangle cap that **silently truncates** excess quads at encode (holes, no log) — but ships **0 = unlimited on every layer**, so there is no hard per-unit cap; the practical limit is the pawn layer's shared pool (1M verts, ~70% baseline). Measured: a FRESH game reads the same fill as a Contemporary save → the pool is filled **roster-wide at load**, not per-era (kills the era-clustering theory; both Vertex-Budget open questions answered). New generic `[Buffers] BufferOverrides = <layer>:verts=+N,idx=+N,meshes=+N,maxtris=N` (same creation seam as DistrictBufferHeadroom, any layer); budget readout is now an F8-window **"Mesh Budget" button** incl. maxTris/mesh. Docs: [Vertex-Budget.md](Vertex-Budget.md) rewritten. |
| 07-16 | **Pawn PROP axis — SOLVED (third injection axis)**: custom weapons/gear on pawn attachment slots — Slingers now carry an actual sling. Rides the game's own data path (pawn def `Attachements[] → PresentationPawnFragmentMesh {ModelPrefab, ModelName, MaterialRef}` = rigid mesh glued to the slot's bone); the one gate is the fragment's MeshCollection needing registration with the AnimationManager. Editor: **Tools ▸ ENC ▸ Prop Lab** (`PropBaker.cs`) — dump any vanilla fragment by GUID (accepts the Asset Picker's 32-hex form; nibble-swap encoding) as the authoring template, then one Bake = static bake (no dummy pawn) → bone-free FxMesh (`mergeSubMeshes`: the fragment GPU encoder draws only submesh 0) → MeshCollection + fragment assets. Runtime: `Hk_PropRegister` postfix on `AnimationManager.AnimationLoad` registers our collections via the public `RegisterMeshCollection` before pawn resolution (an Update-tick loses that race → the pawn def fails Load and units render as pawn definition 0 — a herd of mammoths); Amplitude's catalog misses mod MeshCollections by GUID → by-name fallback from the mounted Unity bundles. Full recipe + traps: [Pawn-Props.md](Pawn-Props.md). |
| 07-16 | **District pipeline productized (dialog + registry)**: districts got their own Factory. Editor: **Tools ▸ ENC ▸ District Factory** (`DistrictFactoryWindow`) — pick a district (searchable dropdown over the project's district definitions), Browse a model, set Size/Rotation/import-angles/Target-tris/Isolate, one **Bake** runs the static bake core (**no dummy pawn** — `pawnDescription` was registry-only in `UniversalBaker.Build`), wraps a bone-free FxMesh (`DistrictBaker.BakeFxMesh`, now callable), and writes the entry; `DistrictRegistry` mirrors `ModelRegistry` (corrupt-guard, atomic write, git-tracked backup). Runtime: `enc_districts.json` registry — **any number of districts at once**, per-entry `{district, fxMeshGuid, isolate}` with private-leaf machinery per entry (`DistrictModel`/`distModels`); legacy `[District]` single-model keys remain as fallback when the registry is empty; F8 "Dump District" is entries-aware. Reactor migrated (dummy `Era0_Common_Bears_01` unit entries removed). **Verified in-game end-to-end.** |
| 07-16 | **District model injection — SOLVED (custom mesh on ONE tile)**: the axis works end-to-end. Recipe: give the district a renderable `ConstructibleVisualAffinity` + **clear its Additional Visual Levels** (the `DistrictState` criterion, not the district class, was why swaps resolved to null); at runtime walk `Selector(culture) → pairs → Emitter.levelBuildItems[].loadedEvolverMaterial → FxEvolverMaterialLevelBuildElement`, set each leaf's `fxMesh` to our baked FxMesh and call the leaf's `Load()` to re-resolve `meshIndex` (reusing the game's own leaf keeps the selector GPU context — a foreign `SetChannel` material drew nothing). The mesh must be **bone-free** (static shader can't read skinned verts) and **lean** (the shared 'Visual' GPU buffer, 3,000,000 verts, runs ~99.85% full late-game; oversize meshes get a slot but silently overflow). `DistrictBufferHeadroom` (opt-in) enlarges that buffer (3M→5M verified). `DistrictIsolate` **scopes to one tile**: build a private `Instantiate`d leaf, point only that district's own channel + Shuriken particle at it via `RefreshChannel`. All off by default. Full recipe: [District-Visuals.md](District-Visuals.md). |
| 07-15 | **District visuals — axis investigated & judged feasible**: decompiled the full district-visual pipeline (`ConstructibleVisualAffinity` → `PresentationDistrict.UpdateLevelBuild` → `PresentationLevelBuildComponent.SetChannel` → `FxEvolverMaterialDrawer.mesh` (static `FxMesh` GUID) → the same `FxComponentMeshContentManager` the unit path uses). A district's visual is a named slot (the district-side `pawnDescription`) resolving to a single **static** mesh — simpler than a unit (no skeleton/clip). Clean Harmony seam exists (analogue of `UniRepointHook`), scoped per-district so the shared affinity is untouched. First experiment started: repoint ENC Stone Quarry at a custom quarry model. Spec: [District-Visuals.md](District-Visuals.md). |
| 07-17 | **PROJECTILE axis — PARTIAL (fourth injection axis)**: a custom model as a unit's fired munition — the Era-6 Anti Tank FPV throws a kamikaze drone. A `ProjectileAsset` has no mesh; the flying mesh is its `trail` → `FxEvolverMaterialDrawer.mesh` (a swappable `FxMesh` GUID the drawer auto-loads — no runtime registration). Editor: **Tools ▸ ENC ▸ Projectile Lab** (`ProjectileBaker.cs`) — Dump prints a **VERDICT** (usable donor = a mesh drawer with a **non-null mesh**; sprite drawers render our mesh **invisible**), then Bake = static bake → bone-free FxMesh → `CopySerialized`-clone the donor drawer with our mesh + clone the ProjectileAsset. Wired via the unit's `Projectile` field (data; plugin fallback `[Projectiles] ProjectileOverrides` / `Hk_ProjectileOverride`, off). Findings: **only thrown-solid projectiles are meshes** (all modern/siege/naval ones are sprites), and **all mesh donors share one brown atlas** — no cheap recolor (tint is ignored for textured meshes; the only fix is a custom FX atlas, shelved). Split-donor: mesh trail (ThrownSpear, stable) + **impact donor** (Mortar → real explosion + audio). Orientation: the drawer **welds mesh +Z to velocity** → `ImportAngles (0,−90,−90)`, avoid the X=90 gimbal pole. `WriteAssetKeepingGuid` keeps the ProjectileAsset's GUID across re-bakes (else the unit's field blanks silently → fires nothing). Shipped brown + legibility-limited (a simpler/chunkier model would silhouette better miniaturized). Full recipe: [Projectiles.md](Projectiles.md). |

---

## Open findings — 2026-07-07 critical review

### Editor pipeline (UniversalBaker / ModelFactoryWindow / ModelRegistry)

#### E1 🔴 Bake uses trimmed fields, registry stores the untrimmed original — silent no-inject — ✅ FIXED (2026-07-07)
`ModelFactoryWindow.cs` `DoBake`: `cfg` got `cur.pawnDescription.Trim()` but `Upsert(cur)` persisted the
raw string. Paste a pawn description with a trailing space → bake succeeds, registry entry looks valid,
but the plugin's substring match (`name.IndexOf(pawnDescription)`) never fires. "Baked ✓", model never
appears, no error anywhere. (`hideMeshes` is safe — the plugin trims per-token; `pawnDescription` is not.)
> **Fixed:** `DoBake` now trims every text field on `cur` itself before building the bake config, so what's
> baked and what's registered are identical.

#### E2 🟡 Remove button deletes by the *edited* name and always reports success — ✅ FIXED (2026-07-12)
`ModelFactoryWindow.cs`: removal keyed on the live `cur.resourceName` text field, not the selected entry
(`existing[selected]`), and `Remove()`'s bool was discarded. Editing the name field then Remove deleted a
*different* model — or nothing — while the status still said "Removed".
> **Fixed:** keys on `existing[selected]` and branches the status on `Remove()`'s actual bool ("Removed '…'"
> vs "'…' was not in the registry — nothing removed").

#### E3 🟡 Static bake of a skinned-only model ships a 0-vertex (invisible) unit — ✅ FIXED (2026-07-12)
`UniversalBaker.cs` static combine iterates only `MeshFilter`; a rigged FBX that imports as pure
`SkinnedMeshRenderer`s yielded `cVerts.Count == 0`, yet the bake completed, the GUID check passed (the
asset exists — it's just empty), and a registry entry for an invisible unit was written. Only trace:
`verts=0` in a Debug.Log.
> **Fixed:** a `cVerts.Count == 0` guard after the combine returns `Fail(...)`, pointing to the Animated path
> (where skinned meshes belong). Verified it does NOT false-trigger on valid models: 12/12 bake smoke test.

#### E4 🟡 `RunBounded` isn't fully bounded: unbounded pipe-drain after `WaitForExit` — ✅ FIXED (2026-07-12)
If the child exited but a grandchild (Blender helper) inherited the stdout handle, `WaitForExit(timeout)`
returned true and the `ReadToEnd` tasks never saw EOF — `GetAwaiter().GetResult()` then hung the editor main
thread forever, the exact freeze the cap exists to prevent.
> **Fixed:** the drain is bounded too — `Task.WaitAll(new[]{outTask, errTask}, remaining)`; on timeout it takes
> whatever completed (`TaskStatus.RanToCompletion`), observes any late fault, logs a warning, and continues (the
> process already exited cleanly). Normal bakes drain in ms — unchanged (12/12 smoke test).

#### E5 🟡 Delete-first re-bake destroys the last-good assets with no rollback — ✅ FIXED (2026-07-12)
Static and animated paths delete `_Skeleton/_Atlas/_ModelMesh/_Mat/_Model.prefab` *before* the fallible
steps run. If the re-bake then failed, the registry still pointed at now-deleted assets: "re-bake failed,
old model still works" became "re-bake failed, old model destroyed". (Delete-first is the deliberate
stale-skeleton fix — the gap was only the missing rollback.)
> **Fixed:** `Build`/`BuildAnimated` now snapshot the existing outputs (each `.asset` + its `.meta`, so the
> GUIDs survive) to a temp dir **outside** the project before the bake, and on any failure (exception or
> `ok:false`) restore them — wiping any partial new outputs first. Fail-safe by construction: the success path
> is unchanged (backup → bake → discard) and restore runs **only** on an already-failed bake, so it can't harm a
> good bake. Backing up outside `Assets/` avoids a duplicate-GUID import.
> **Runtime-verified (2026-07-13):** a dedicated Bake Feature Test case now bakes a cube, captures the `_Skeleton`/
> `_Atlas` Unity GUIDs + mesh content, forces a failed re-bake of the same resource (missing model file), and asserts
> the assets return with their **original GUIDs and content** — closing the earlier "restore path never triggered"
> gap and catching a non-atomic/GUID-losing restore. Passed (`skelGuidKept=True, atlasGuidKept=True, verts 24→24`).

#### E6 🟢 Corrupt project backup + missing registry = permanent Save lockout — ✅ FIXED (2026-07-12)
`ModelRegistry.Load` parsed the backup restore inside the same `try` whose catch set `lastLoadCorrupt`
and named `RegistryPath` — a file that *doesn't exist* in this scenario — so Save refused forever with
instructions that couldn't be followed.
> **Fixed:** the backup parse is now in its own try/catch; a corrupt backup is treated as "no backup" (a
> warning that names the *backup* path, `lastLoadCorrupt` stays clear, Save is not locked — there's no live
> registry to protect, and the next Save rewrites the backup).

#### E7 🟢 Stale `_Preview.prefab` shadows a static re-bake in the window preview — ✅ FIXED (2026-07-13)
Bake animated → re-bake static: the static delete-first list omitted `<name>/<name>_Preview.prefab`, and
`LoadPreview` prefers the anim path whenever it exists — so the preview kept showing the old animated model
while the game got the new static one.
> **Fixed:** the static path's delete-first now also removes the stale animated-preview assets
> (`_Preview.prefab` / `_PreviewMesh.asset` / `_PreviewMat.mat`) from FactorySource, so `LoadPreview` falls
> through to the fresh `_Model.prefab`. (The reverse, static→animated, was already fine: the animated bake
> regenerates `_Preview`, which `LoadPreview` then correctly prefers.)

#### E8 🟢 Texture leak per multi-material bake — ✅ FIXED (2026-07-12)
The per-material albedos loaded for `PackTextures` (up-to-4096² RGBA32 each) were never `DestroyImmediate`d;
Unity objects don't GC, so an iterating modder stranded tens of MB per bake until the next domain reload.
> **Fixed:** both `PackTextures` sites (static `BuildInner` + animated `BuildMultiAtlasAndRemap`) now
> `DestroyImmediate` the source albedos right after packing (they're copied into the atlas; the animated path
> keeps using only the `orderedAlb` *keys* afterward, not the textures).

### Tools — Blender scripts + glbconv (key items verified on Blender 5.1.2)

#### T1 🔴 `prep_model.py` reduce hard-fails on instanced (multi-user mesh) models — ✅ FIXED (2026-07-07)
`modifier_apply` on linked-duplicate data raises `RuntimeError: Modifiers cannot be applied to multi-user
data` (reproduced on 5.1.2). glTF/FBX importers create linked duplicates whenever nodes share a mesh —
common in Sketchfab models (wheels, missiles, rotor blades). Any such model with Reduce on failed the whole
bake, and the surfaced error was the misleading "Blender prep produced no GLB (exit 0)".
> **Fixed:** each object gets `o.data = o.data.copy()` when its data is multi-user, before the decimate
> modifier is applied — also the *correct* semantics (applying through each user would re-decimate the
> shared datablock). Verified end-to-end: a GLB whose import produces two objects sharing one mesh
> (`users=2`) now reduces cleanly.

#### T2 🟡 Reduce ratio counts polygons, but COLLAPSE ratio operates on triangles — ✅ FIXED (2026-07-08)
A quad-heavy model under-reduced by up to 2×: 20k quads (40k tris) with target 24000 computed ratio 1.0 →
*no reduction at all* → 40k tris shipped into the ~25k-ceiling engine buffer this feature exists to protect.
GLB inputs were immune (glTF is triangle-only); OBJ/FBX/.blend sources were exposed. The post-reduce log
also under-reported (counted polys, not tris).
> **Fixed:** ratio and the before/after report now count real triangles (`len(p.vertices) - 2` per polygon,
> like `rig_anim.py`). Verified end-to-end: a 10,000-quad OBJ (20,000 real tris) with target 5000 now
> computes ratio 0.25 and lands at 4,999 output triangles (independently re-imported and counted); the old
> code would have produced ~10,000. Triangle inputs are byte-identical in behaviour (each tri counts 1).

#### T3 🟡 `rig_anim.py` albedo grab takes the *first* `TEX_IMAGE` node, not Base Color — ✅ FIXED (2026-07-12)
Node order is creation order; a PBR material with normal/roughness maps could hand the Factory a **normal
map as the atlas albedo** — purple/garbled skin, no error.
> **Fixed:** `base_color_image()` now traces the Principled BSDF's **Base Color** input upstream to the nearest
> image node (through a mix/gamma node if present); falls back to any TEX_IMAGE only when there's no Principled or
> the input is unlinked. Blender-syntax-checked.

#### T4 🟡 `rig_anim.py` `join()` keeps only the active object's modifiers — ✅ FIXED (2026-07-12)
Active was `meshes[0]` (scene order); if that was a bone-parented prop without an Armature modifier, the joined
mesh exported with **no skin binding** — the whole model rigid/frozen, all green logs.
> **Fixed two ways:** the join target is now chosen as a mesh that *has* an armature modifier (fallback `meshes[0]`),
> and — the real guarantee — after the join the code re-adds an armature modifier bound to `arm` if none survived.
> The joined mesh keeps every source mesh's vertex groups regardless, so re-binding fully restores skinning.

#### T5 🟡 glbconv: negative-scale (mirrored) nodes don't flip triangle winding — ✅ FIXED, source + exe (2026-07-12)
Mirroring half a symmetric vehicle via scale (−1,1,1) is routine; those halves came out inside-out — invisible
under backface culling, silently "fixed" by users reaching for Double-sided without knowing why.
> **Fixed:** `Program.cs.src` now emits the triangle as `(A,C,B)` when `node.WorldMatrix.GetDeterminant() < 0`, and
> `glbconv.exe` was **rebuilt** (pinned SharpGLTF.Core 1.0.6, untrimmed + single-file-compressed so it stays ~35 MB;
> trimming was rejected — it changed OBJ/MTL output on 4 of 11 models). The rebuild was verified **geometry-identical
> across all 11 registry models**; its only other effect is a SharpGLTF UV-decode change (raw tiled UVs → pre-folded),
> which the baker's per-vertex fold makes equivalent *except* at tile seams (**3 of 208,198** Cobra verts, 0.0014%).
> The prior exe is backed up + in git. Build recipe + reproducibility note now in `Tools/glbconv/BUILD.md` (previously
> there was **no** build documentation at all — that gap is closed). Recommend a one-time in-game Cobra glance.

#### T6 🟢 Silent-empty outcomes in the Blender scripts — ✅ FIXED (2026-07-13)
- `rig_anim.py`: a typo'd bone-prefix filter stripped **all** fcurves → a frozen 1-frame clip baked and shipped
  with exit 0. **Fixed:** hard-fails on `kept == 0`, listing the model's actual animated bones so the prefix can
  be corrected.
- `prep_model.py`: a strip-only config with an over-broad substring exported an **empty** GLB "successfully" (the
  no-meshes guard only ran when reduce was on). **Fixed:** a no-mesh guard now runs before *every* export, failing
  loudly with a pointer to the strip list. Both Blender-syntax-checked.

#### T7 🟢 Tool lows — reviewed 2026-07-13, consciously deferred
Each was re-examined and left as-is: fixing any needs a glbconv rebuild and/or *changes output* on current models
(regression risk), for negligible benefit — none is reachable or impactful in the current pipeline. Revisit only if
one actually bites.
- glbconv **normals not inverse-transpose under non-uniform scale** — would change shading output on any
  non-uniform-scaled source (rebuild + regression risk); unreported, and the runtime flattens PBR anyway.
- glbconv **legacy single-material dup-material-name overwrite** — effectively unreachable: multi-material models
  take the *grouped* path (already `mat{i}`-prefixed); a single-material model has one material.
- glbconv **TGA alpha-depth byte / ~2× peak memory / skinned double-transform** — Unity tolerates the TGA byte,
  memory is fine for these small models, and skinned models take the *animated* path, not the static one.
- `prep_model.py` **comma-in-object-name** — inherent to the CSV `stripParts` interface the baker relies on; rare,
  and it fails *visibly* (the part just isn't stripped), not silently.
- `blend_export.py` **basename texture recovery / `.png` guess** — only affects rare `.blend` inputs; minor.

**Verified clean** (checked and explicitly cleared): glbconv's `mat_none` handling, TriMat/outTri
bookkeeping, MatName collision-proofing (`mat{i}_` prefix), the UV V-flip (including repeat-wrapped UVs),
0-material/null-name paths, Sanitize's filename safety; `prep_model.py`'s victim-set materialization (no
iterate-while-removing) and case handling; Blender 5.x slotted-action fallback in `rig_anim.py`; window
probe caching, GLB chunk parsing, and log scanning; `Plugin.cs` wholesale; `Prober.cs` (diagnostics,
defensively guarded throughout).

---

## Architectural findings — 2026-07-12

Where 07-07 hunted line-bugs, this pass treats *system structure* as the risk. The framework has the signature
of software that grew one feature-flag at a time; at its current size that accretion — not any single bug — is
the dominant risk, especially given the goal of many more models and an eventual shippable package.

#### A1 🔴 `OnPawnAdded` was a ~190-line per-pawn-per-frame god-method — ✅ FIXED (2026-07-12, `9b956a7`)
The hottest hook (runs per pawn-add per frame once any animated/freeze model is registered) inlined five
behaviors — skeleton rescue, freeze, loop, fire-once, deploy + recoil overlay. Each feature bolted on another
branch, and the branches interact (`deployOnStop && fireOnAttack`). Untestable as a unit; the interactions
lived only in the author's head.
> **Fixed:** decomposed into a thin dispatcher + one named handler per concern — `TryReadLastPawn`,
> `ForceOurSkeleton`, `ApplyFreeze`, `ApplyAnimatedPose`, with the pose-time logic split into a strategy
> dispatch (`ComputePoseTime` → `DeployPoseTime`/`RecoilOverlay`, `FireOncePoseTime`, loop) and the tail steps
> (`ClearAimLayer`/`ApplyPositionOffset`/`ApplyScale`/`LogPoseHookOnce`). Strictly behavior-preserving: same
> reflection reads, write-backs, ordering, thresholds, and log-gate fields; the boxed `PawnEntry` is threaded
> through a small `PawnCtx` and shared by reference so every `SetMember` lands on the same box. **Adding a new
> model behavior is now a new method, not another branch on the hot path.** Verified in-game across all three
> behaviors (drones loop, howitzer deploy+recoil, zeppelins freeze), each with multiple instances, plus a
> 12/12 bake smoke test.

#### A2 🟡 `ModelEntry` is a ~35-field bag mixing four concerns
Registry data, runtime-resolved handles (`skeleton`, `animId`, `skeletonId`), per-instance concurrent state
(`fireGuidQueue`, `deployProgress`, `deploySamples`), and behavior config all live in one class — so every new
feature touches the parser, the POCO, and the (now-decomposed) hot loop in lockstep. Natural next split, pairing
with A3: a plain serialized config record vs. a runtime-state object.

#### A3 🟡 The cross-repo registry schema is defined in FOUR places — now guarded by a real check (2026-07-12)
`ModelDef` (editor writer, JsonUtility) · `ModelEntry` (runtime) · the Newtonsoft parse · the regex-fallback
parse — a new field is edited in all four. **Mitigated (Option A — verify, don't merge):** `check_schema_parity.sh`
was rewritten to make drift loud. It now asserts (1) the Newtonsoft and regex read paths read the **same** key set,
(2) every read key is a `ModelDef` field the baker writes (minus a runtime-only allowlist), and (3) each read cast's
type matches `ModelDef`'s declared type; bake-time-only fields are listed as INFO. Proven to fail on all three drift
types (dropped regex key, unwritten key, type mismatch). The duplication still **exists** — this monitors it rather
than removing it. The deeper Option B (auto-deserialize into one POCO, drop the manual mapping, retire the regex
fallback) was deliberately not taken, per the standing "keep the two sides separate, just verify compatible"
preference; revisit B only if the manual mapping starts costing more than the guard saves.

#### A4 ✅ RESOLVED (2026-07-12) — one dismissed after verification, one fixed
- `UniversalBaker.MeasureLongestAxis` (animated path) — **DISMISSED (not a bug).** It reads mesh-local
  `sharedMesh.bounds` without a per-node transform, *but* `rig_anim.py` **joins all meshes into one** before the
  animated path measures (there is only ever a single skinned mesh), and size is position-invariant. The asymmetry
  with the static combine — which reads many *un-joined* `MeshFilter`s and so genuinely needs `rootInv *
  localToWorldMatrix` — is therefore justified, not a defect. Deliberately **not** "defensively fixed": transforming
  the single mesh's bounds by a non-identity SMR transform could rescale every current animated model for zero gain.
- `BuildMultiAtlasAndRemap` submesh→atlas-rect name match — **FIXED.** The loose `Contains`-both-ways `SimplifyMat`
  match collided: material `Body` (simplified `body`) matched `Body_Trim` (`bodytrim`) via `"bodytrim".Contains("body")`,
  and if the longer name sorted first, `FindIndex` returned the wrong rect → wrong texture on that submesh. Now tries an
  **exact** simplified-name match first, then substring, then the submesh index. Behaviour is identical for
  non-colliding models (only the collision case changes). Affects only the animated multi-material path — the static
  path matches by exact material *reference*.

#### A5 🟢 Package-readiness (the stated goal) — not close yet
ENC branding hardcoded (`enc_models.json`, `ENC.*` EditorPrefs); Blender discovery Windows/Program-Files-only;
machine-specific paths (D: backup, `C:\GameData` junction); the ENCReload README sends a package consumer to a
*different* repo for all docs. None are bugs — just the gap between "works on my machine" and "shippable to
strangers." Overlaps the deferred list's ENC-branding, Blender-PATH-discovery, and Blender-free-static items.

---

## Still deferred (from earlier passes — unchanged)

- **#3 Blender discovery** is Program-Files-only and `BlenderAvailable()` never probes PATH — the biggest
  real gap for adopters (winget/Steam/portable/macOS/Linux installs refused up front).
- **#4 `registered` never reset across a session** — latent for a main-menu → different-game round-trip
  *if* the game rebuilds AnimationManager. (Distinct from the empty-latch bug fixed 07-07.) Cheap
  insurance: cache the animMgr instance, reset on change.
- **#5 `anyAnimated` can cache `false` permanently** if the pawn hook fires before registration.
- **#6 `TryLoadAsset` return-value trap** in the asset loaders (prefer `LoadAsset` explicitly).
- **#7 `canBake` doesn't check the model file exists.**
- **#8 rig-block GameObject leak** (no try/finally around the prefab build).
- *(external #4)* **`OnPawnAdded` perf**: once any animated model is registered, the full hook body runs
  for every pawn in the game; member caching makes it survivable, but cost scales with total unit
  activity. Gate on a skeleton-id HashSet if it ever bites.
- **ENC branding** hardcoded (`enc_models.json`, `ENC.*` EditorPrefs, fallback path) — package-readiness.
- **Blender-free static pipeline** (package-readiness, pairs with #3): Strip could move into `glbconv`
  (it already walks named GLB nodes — skip stripped nodes' triangles in C#), and Reduce could use an
  in-process C# quadric simplifier (UnityMeshSimplifier-class, MIT) on the imported mesh. That removes the
  Blender dependency for ALL static bakes *and* the process-startup + GLB round-trip cost (the bulk of a
  ~33s prep pass). Blender then remains only for `.blend` inputs and the animated pipeline, shrinking the
  audience exposed to Blender discovery (#3). Rationale documented in `Building.md`.
- Minors: bake temp files not cleaned; `libraryfolders.vdf` parsing only decodes `\\`; no hard guard at
  the ~25k shared-vertex ceiling; `RespawnDelayFrames` effective granularity is 5 frames (scan throttle).

---

## Recommended fix order

**Architectural (2026-07-12):** ~~**A1**~~ ✅ · ~~**A3**~~ ✅ · ~~**A4**~~ ✅ (SimplifyMat collision fixed; MeasureLongestAxis verified-and-dismissed). Next: **A2** (`ModelEntry` config/state split), when Option B is tackled.

1. ~~**E1**~~ ✅ done · ~~**T1**~~ ✅ done · ~~**T2**~~ ✅ done — tier 1 complete.
2. ~~**E2**~~ ✅ · ~~**E4**~~ ✅ done (Remove key + honest status; bounded pipe drain).
3. ~~**T3 / T4 / T5**~~ ✅ done — the silent-corruption class (wrong albedo, lost skinning, inside-out mirror halves).
4. ~~**E5**~~ ✅ · ~~**E3**~~ ✅ done · **T6** — honest failures for empty/destroyed outputs.
5. Deferred list + lows as the package push approaches (Blender PATH discovery first among them).
