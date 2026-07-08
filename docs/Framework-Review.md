# Framework Review — Universal Model Factory

Living hardening roadmap. Three review passes so far, most recent first:

1. **2026-07-07 — full critical re-review** (this document's body): the runtime plugin read line-by-line,
   the editor pipeline and all tools swept by independent reviewers, and the two headline Blender findings
   **empirically verified on Blender 5.1.2** (the same install the pipeline auto-detects).
2. **2026-07-07 — external review** (received by mail): five findings + three comment-drift items, all
   verified against the code and fixed same day (see "Fixed to date").
3. **2026-07-05 — self-review**: registry safety, process timeouts, dead code. Fixed or consciously deferred.

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

#### E2 🟡 Remove button deletes by the *edited* name and always reports success
`ModelFactoryWindow.cs:109-117`: removal key is the live `cur.resourceName` text field, not the selected
entry (`existing[selected]`), and `Remove()`'s bool is discarded. Edit the name field first and Remove
deletes a *different* model — or nothing — while the status says "Removed".
- **Fix:** key on `existing[selected]`; branch the status on the returned bool.

#### E3 🟡 Static bake of a skinned-only model ships a 0-vertex (invisible) unit
`UniversalBaker.cs` static combine iterates only `MeshFilter`; a rigged FBX that imports as pure
`SkinnedMeshRenderer`s yields `cVerts.Count == 0`, yet the bake completes, the GUID check passes (the
asset exists — it's just empty), and a registry entry for an invisible unit is written. Only trace:
`verts=0` in a Debug.Log. (The animated path's `MeasureLongestAxis` handles skinned meshes; the static
combine does not.)
- **Fix:** `if (cVerts.Count == 0) return Fail(...)` after the combine (and/or harvest `SkinnedMeshRenderer.sharedMesh`).

#### E4 🟡 `RunBounded` isn't fully bounded: unbounded pipe-drain after `WaitForExit`
`UniversalBaker.cs:758-760`: if the child exits but a grandchild (Blender helper) inherited the stdout
handle, `WaitForExit(timeout)` returns true and the `ReadToEnd` tasks never see EOF —
`GetAwaiter().GetResult()` then hangs the editor main thread forever, the exact freeze the cap exists to
prevent.
- **Fix:** `Task.WaitAll(new[]{outTask, errTask}, remaining)` and bail with partial output on timeout.

#### E5 🟡 Delete-first re-bake destroys the last-good assets with no rollback
Static and animated paths delete `_Skeleton/_Atlas/_ModelMesh/_Mat/_Model.prefab` *before* the fallible
steps run. If the re-bake then fails, the registry still points at now-deleted assets: "re-bake failed,
old model still works" became "re-bake failed, old model destroyed". (Delete-first is the deliberate
stale-skeleton fix — the gap is only the missing rollback.)
- **Fix:** rename-aside + restore-on-failure, or delete only after the new skeleton passes the GUID check.

#### E6 🟢 Corrupt project backup + missing registry = permanent Save lockout with a misleading error
`ModelRegistry.Load`: the backup restore parses inside the same `try` whose catch sets `lastLoadCorrupt`
and names `RegistryPath` — a file that *doesn't exist* in this scenario. Save then refuses forever with
instructions that can't be followed.
- **Fix:** parse the backup in its own try/catch; treat a corrupt backup as "no backup".

#### E7 🟢 Stale `_Preview.prefab` shadows a static re-bake in the window preview
Bake animated → re-bake static: the static delete-first list omits `<name>/<name>_Preview.prefab`, and
`LoadPreview` prefers the anim path whenever it exists — the preview forever shows the old animated model
while the game gets the new static one.

#### E8 🟢 Texture leak per multi-material bake
The per-material albedos loaded for `PackTextures` (up-to-4096² RGBA32 each) are never `DestroyImmediate`d;
Unity objects don't GC. An iterating modder strands tens of MB per bake until the next domain reload. Same
class as the known rig-block GameObject leak, larger per occurrence.

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

#### T3 🟡 `rig_anim.py` albedo grab takes the *first* `TEX_IMAGE` node, not Base Color
Node order is creation order; a PBR material with normal/roughness maps can hand the Factory a **normal
map as the atlas albedo** — purple/garbled skin, no error.
- **Fix:** trace the Principled BSDF's Base Color input link; fall back to any TEX_IMAGE only if unlinked.

#### T4 🟡 `rig_anim.py` `join()` keeps only the active object's modifiers
Active is `meshes[0]` (scene order). If that happens to be a bone-parented prop without an Armature
modifier, the joined mesh exports with **no skin binding** — the whole model rigid/frozen, all green logs.
- **Fix:** pick a mesh that has an `'ARMATURE'` modifier as the join target (or re-add one bound to the armature).

#### T5 🟡 glbconv: negative-scale (mirrored) nodes don't flip triangle winding
Mirroring half a symmetric vehicle via scale (-1,1,1) is routine; those halves come out inside-out —
invisible under backface culling, silently "fixed" by users reaching for Double-sided without knowing why.
- **Fix:** if `M.GetDeterminant() < 0`, emit `(A,C,B)`.

#### T6 🟢 Silent-empty outcomes in the Blender scripts
- `rig_anim.py`: a typo'd bone-prefix filter strips **all** fcurves → a frozen 1-frame clip bakes and
  ships with exit 0. Should hard-fail on `kept == 0`.
- `prep_model.py`: strip-only config with an over-broad substring exports an **empty** GLB "successfully"
  (the no-meshes guard only runs when reduce is on). Failure surfaces later with no pointer to the strip list.

#### T7 🟢 Tool lows (recorded, not urgent)
glbconv: normals not inverse-transpose under non-uniform scale; the legacy single-material texture path
can overwrite on duplicate material names (no `mat{i}` prefix); TGA descriptor byte omits alpha-depth bits
(Unity tolerates); whole-OBJ built in memory (~2× peak); skinned GLBs double-transform through the static
path. `prep_model.py`: comma-containing object names can't be targeted (CSV split). `blend_export.py`:
texture recovery is by basename only (can rebind a same-named wrong texture) and guesses `.png` for
extension-less images.

**Verified clean** (checked and explicitly cleared): glbconv's `mat_none` handling, TriMat/outTri
bookkeeping, MatName collision-proofing (`mat{i}_` prefix), the UV V-flip (including repeat-wrapped UVs),
0-material/null-name paths, Sanitize's filename safety; `prep_model.py`'s victim-set materialization (no
iterate-while-removing) and case handling; Blender 5.x slotted-action fallback in `rig_anim.py`; window
probe caching, GLB chunk parsing, and log scanning; `Plugin.cs` wholesale; `Prober.cs` (diagnostics,
defensively guarded throughout).

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
- Minors: bake temp files not cleaned; `libraryfolders.vdf` parsing only decodes `\\`; no hard guard at
  the ~25k shared-vertex ceiling; `RespawnDelayFrames` effective granularity is 5 frames (scan throttle).

---

## Recommended fix order

1. ~~**E1**~~ ✅ done · ~~**T1**~~ ✅ done · ~~**T2**~~ ✅ done — tier 1 complete.
2. **E2** (Remove key + honest status) · **E4** (bound the pipe drain — completes the 07-05 timeout work).
3. **T3 / T4 / T5** — the silent-corruption class (wrong albedo, lost skinning, inside-out mirror halves).
4. **E3, E5, T6** — honest failures for empty/destroyed outputs.
5. Deferred list + lows as the package push approaches (Blender PATH discovery first among them).
