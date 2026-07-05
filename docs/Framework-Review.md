# Framework Review — Universal Model Factory

Prioritized, **verified** findings from a code review of the runtime plugin, the bake pipeline, and
the editor UI (2026-07-05). Each finding was checked against the actual code; two review claims were
corrected (noted inline). This is a hardening roadmap toward shipping the Factory as a distributable
package — it is not a list of things that are broken today for the author's own use.

**Overall:** the framework is solid where it counts. Verified strengths, all done right:
per-model **and** per-hook failure isolation, `InvokeReq` for required SDK methods (fails loud on an
Amplitude API rename instead of silently no-op'ing), empty-GUID validation, delete-first +
force-synchronous reimport (defeats the stale-skeleton bug), pipe-buffer-deadlock avoidance on all
shell-outs, one-shot error logging on the pose hot path, and correct boxed-struct mutate/write-back.
The gaps below are mostly about *surviving a stranger's machine*.

Severity key: 🔴 fix before distributing · 🟡 worth hardening · 🟢 cleanup / package-readiness.

---

## 🔴 Fix before distributing

### 1. Registry write can silently wipe a modder's whole registry — ✅ FIXED (2026-07-05)
`baker/ModelRegistry.cs` (`Save` ~130-135, `Load` ~118-128) — **confirmed.**
> **Fixed:** `Save` now writes to `.tmp` and atomically `File.Replace`s it in (no truncated file on an
> interrupted write). `Load` flags an unparseable file, copies it to `.corrupt.json`, and `Save` refuses to
> run while that flag is set — so a corrupt/half-edited registry is preserved and never overwritten. The
> original finding is kept below for the record.
`Save` does a non-atomic `File.WriteAllText` (truncate-then-write). `Load` swallows a parse error and
returns an **empty** list. `Upsert` then Loads → empty → adds one → Saves, **overwriting all prior
models**. A crash/lock mid-write, or one hand-edit typo in the JSON, and every previously baked model
vanishes from the registry with only a `Debug.LogError` the user may never see.
- **Failure:** bake model #5, the write is interrupted (or the file was already corrupt) → the next
  bake starts from an empty list → models #1-4 are gone.
- **Fix:** write to `RegistryPath + ".tmp"` then `File.Replace`/atomic move; on parse failure, surface
  a blocking error and rename the bad file to `enc_models.corrupt.json` — never let a subsequent Save
  overwrite an unreadable-but-present file.

### 2. No timeout on any external process — ✅ FIXED (2026-07-05)
`baker/UniversalBaker.cs` — **confirmed** (5× `WaitForExit()` with no argument: ~238, 740, 791, 823, 855).
> **Fixed:** added a `RunBounded(process, timeoutMs, out stdout, out stderr)` helper — both streams drain
> on pool threads and the wait is bounded (`WaitForExit(ms)`; `Kill()` + fail on timeout). All five
> shell-outs use it with a 3-min cap, so a stuck child fails the bake cleanly instead of freezing the editor.
A hung Blender or glbconv (bad model, a modal dialog it shows despite `--background`, a driver stall,
an infinite Python loop) wedges the Unity **main thread forever** — Task Manager is the only way out.
The existing comments handle pipe-buffer deadlock, not a genuinely stuck child.
- **Fix:** `p.WaitForExit(timeoutMs)`; on timeout `p.Kill()`, drain, and `return Fail(...)`. Applies to
  all four shell-outs (glbconv, blend export, mesh reduce, rig anim).

### 3. Blender discovery is Windows/Program-Files-only, and "available" never probes PATH
`baker/UniversalBaker.cs` (~723 `glbconv`, ~756 `FindBlender`, ~767 `BlenderAvailable`) — **confirmed.**
Blender search is limited to `C:\Program Files\Blender Foundation` / `(x86)`. A Steam / winget
(`%LOCALAPPDATA%`) / portable / macOS / Linux install yields only the bare `"blender"` PATH fallback,
and `BlenderAvailable()` returns **false** for a genuine PATH install (it never probes PATH) — so
strip-parts / reduce-to-tris / animated bakes are refused up front for a large slice of adopters.
- **Fix:** also search `%LOCALAPPDATA%\Programs\Blender*`, the Steam common path, `/Applications/Blender.app`,
  `/usr/bin/blender`; confirm availability by launching `blender --version` once (short timeout) and cache it.

---

## 🟡 Worth hardening

### 4. `registered` static never reset across a session
`Patches/UniversalInjectPatch.cs:121` — **confirmed pattern, but not biting save-loads.**
`EnsureRegistered` early-returns on `registered==true` and the flag is never reset. Save-load reloads
work today (the skeletons persist across a save-load — verified all session), so the symptom does not
appear there. The latent risk is a **main-menu → different-game** round-trip *if* the game rebuilds
`AnimationManager` — the custom skeletons would not be in the new instance and models would vanish with
no error.
- **Fix (cheap insurance):** cache the `animMgr` instance; when a different instance is seen, reset
  `registered`/`loaded` and re-register.

### 5. `anyAnimated` can cache `false` permanently
`Patches/UniversalInjectPatch.cs:544` — if `entries` is null the first time `OnPawnAdded` fires (pawn
added before registration — not guaranteed impossible across a game update), `anyAnimated` (a `bool?`)
caches `false` for the whole session and all animation is silently disabled.
- **Fix:** don't cache `anyAnimated` until `entries != null` (or `LoadRegistry()` at the top of the hook).

### 6. `TryLoadAsset` return-value trap
`Patches/UniversalInjectPatch.cs` (`LoadSkeleton` / `LoadAtlas` / `LoadClipCollection`) — the loader
picks `LoadAsset || TryLoadAsset` by first match and uses the **return value**. If a game update adds
`TryLoadAsset` (returns `bool`, asset via `out`) and it sorts first, every load becomes a boxed `false`
and downstream field lookups miss with confusing logs.
- **Fix:** prefer `LoadAsset` explicitly; only fall back to `TryLoadAsset` and read the `out` arg.

### 7. `canBake` doesn't check the model file exists
`ModelFactoryWindow.cs:344` — a moved/typo'd path passes the gate and fails deep inside `Build` with a
cryptic error.
- **Fix:** in `canBake`, when `modelFile` is non-empty require `File.Exists`; show "Model file not found".

### 8. GameObject leak on an exception in the rig block
`baker/UniversalBaker.cs:573-605` — (correcting the review: there are **no** `return Fail` in this
range, so no early-return leak) but there's no `try/finally` around `root`, so an exception from
`SaveAsPrefabAsset` / `Shader.Find` leaves orphan GameObjects in the open scene.
- **Fix:** wrap the rig build in `try/finally` that `DestroyImmediate(root)`s.

---

## 🟢 Cleanup / package-readiness

- **`Patches/LoadHookPatch.cs` was dead code** — ✅ **removed (2026-07-05).** It was never applied
  (`Plugin.Awake` patches only the three `Uni*` hooks, no `PatchAll`). Deleting it also un-broke the F8
  window: `Prober.AnimMgr` is now set from `UniRegisterHook` (which *is* applied), so the AnimMgr-dependent
  scans work again.
- **ENC branding is hardcoded** — the `enc_models.json` filename, the `ENC.*` EditorPrefs keys
  (`ENC.bepinexConfig`, `ENC.blenderPath`), and the `C:\Program Files (x86)` fallback const. For a
  neutral package, derive all of these from one namespace constant (and coordinate the JSON filename
  rename between baker and plugin).
- **`RespawnDelayFrames` effective granularity is 5 frames** — the `%5` scan throttle means config
  values 1-5 behave identically. Minor; document it or compare in scan-ticks.
- **Minor:** bake temp files (`*_stripped/_reduced/_fromblend.glb`) aren't cleaned up; `libraryfolders.vdf`
  parsing only decodes the `\\` escape; no hard guard against the ~25k shared-vertex ceiling (a huge
  model bakes an over-budget skeleton with only a `Debug.Log`).

---

## Closed during review
- **Field consistency (baker `ModelDef` ↔ plugin reader): no mismatch.** The plugin reads the runtime
  subset (`resourceName`, `pawnDescription`, `skel`, `atlas`, `clip`, `position`, `hideMeshes`,
  `respawnAfterLoad`) by exact name via Newtonsoft, ignoring the bake-only fields. Verified.

## Recommended fix order
1. ~~**#1 registry atomicity**~~ ✅ done · ~~**#2 process timeouts**~~ ✅ done · **#3 Blender discovery** —
   the last one matters only for distributing to strangers (the author's own machine already has Blender).
2. **#8 rig-block `try/finally`**, **#5 `anyAnimated`**, **#7 model-file check** — cheap correctness.
3. **#4 `registered` reset**, **#6 `TryLoadAsset`** — resilience to a future game update.
4. Cleanup: ~~delete `LoadHookPatch.cs`~~ ✅ done · neutralize ENC branding.

> **Status (2026-07-05):** #1 (the only finding that could destroy the author's own work — a hand-edit
> typo wiping the registry) and the dead-code cleanup are fixed. The rest are documented as
> distribute-to-strangers hardening / future-game-update resilience, deliberately deferred.
