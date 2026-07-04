# Framework Review & Hardening Roadmap (2026-07-04)

A full code review of the Universal Model Factory (editor) + injection plugin (runtime), done as four parallel
component reviews (runtime plugin, baker, UI/registry, tooling) with the load-bearing claims **verified against the
actual code** afterward. Severities below are the *calibrated* ones — where verification contradicted a reviewer, the
change is noted.

**Verdict:** architecturally sound and works end-to-end, with an unusual amount of institutional knowledge captured
inline and real graceful-degradation infrastructure already present. The risks are not in the happy path — they're in
**how failure is detected (or isn't)**, **per-frame cost**, and **packaging hygiene**. Nothing here is "it's broken";
it's "here's what will bite the next modder or the next game patch."

---

## Cross-cutting themes

Fix these and most individual findings collapse.

### 1. Silent failure — "success" = "no exception thrown", not "the work happened"
The single biggest systemic risk, spanning both repos:
- **Baker** reflected `GetMethod(...)?.Invoke(...)` for `SetPrefab`/`Reimport`/`SetFromDirectory`: the `?.` swallows a
  null method, so a future Amplitude rename produces an **empty skeleton/clip asset that reports `ok=true`** with a valid
  Unity GUID. `AmplitudeGuid` returns `""` on failure and the caller still returns success.
- ~~**Plugin** `LoadSkeleton`/`LoadAtlas`/`LoadClipCollection` (`:223-224`): `var g = load?.MakeGenericMethod(...)` then
  `g.GetParameters()` with **no null-check**, and `LoadSkeleton` (unlike `LoadClipCollection`) isn't in a try/catch — so
  one renamed `LoadAsset` throws and aborts *all* registration.~~ **RESOLVED:** all three loaders now `if (g == null) return null;`
  with a clear log, **and** the registration loop wraps each entry in its own try/catch — one bad model is skipped (others
  still register + Apply), instead of a single failure taking down every custom model.
- **Plugin** `OnPawnAdded` bare `catch {}` per frame: a persistent field-rename fails silently on every pawn forever.

**Fix pattern:** capture each `MethodInfo`/GUID into a local; `return Fail(...)` / log-once when null; treat empty GUID as
a hard failure; wrap each per-entry registration body in its own try/catch. Centralize the Amplitude type/field/method
names as `const`s so a game patch is a one-line fix and the reflected API surface is self-documenting.

### 2. Per-frame cost that should be cached
- `Plugin.cs` `Update()` → `TickTexture()` → `foreach entries: TickOne` re-resolves render outputs via reflection and
  re-sets ~7 textures **every frame per model**.
- `OnPawnAdded` does ~15 string-keyed member lookups per pawn per frame.

**Fix:** apply textures once after repoint behind a dirty flag; resolve the fixed `PawnEntry`/`PawnEntryPose`/`TRS`
`FieldInfo`s once and reuse.

### 3. Packaging hygiene — "ENC" everywhere
Namespace `ENCAccessProof`, plugin GUID `community.humankind.encaccessproof`, registry filename `enc_models.json`,
EditorPrefs `ENC.blenderPath` / `ENC.bepinexConfig`, log tag `[Uni]`, a hardcoded `C:\Program Files (x86)` fallback, and
dead "Access Proof" scaffolding in `Plugin.cs`. All block shipping as a neutral package. **Fix:** one `Brand`/`PrefPrefix`
constant, derive keys + filename from it, strip dead scaffolding. Do it as one deliberate pass at the package push.

---

## Findings — Runtime plugin (`Patches/UniversalInjectPatch.cs`, `Plugin.cs`)

| Sev | Location | Issue → fix |
|---|---|---|
| ~~**Critical**~~ **RESOLVED** | `:223-224` (all 3 loaders) | `g.GetParameters()` NRE if `LoadAsset` not resolved; `LoadSkeleton` not in try/catch → cascades to abort **all** registration. **Verified.** **Fixed:** all 3 loaders `if (g == null) return null;` (clear log) **+** per-entry try/catch in the registration loop (one bad model skipped, others register + Apply). |
| **High** | `EnsureRegistered` `~124-158` | one bad model aborts the whole batch (single try/catch around the loop) and `registered` stays false → retries every hook. Wrap the per-entry body in its own try/catch. |
| **High** | `OnPawnAdded` `:590` | bare `catch {}` every frame hides a persistent reflection break with zero log. Log once behind a one-shot flag. |
| **High** | `Plugin.cs Update` → `TickOne` | textures re-applied every frame per model. **Verified.** Cache the `Material[]` + dirty flag. |
| **High** | pervasive | "ENC" branding (namespace, GUID, filename, prefs, log tag) blocks package reuse; dead "Access Proof" scaffolding in `Plugin.cs`. |
| **Medium** | `:566` | pose `Time = Time.time/duration` never wrapped → float precision coarsens after **many hours**. **Verified but mild** (reviewer said Critical — downgraded). Wrap in `Mathf.Repeat(…,1f)`. |
| **Medium** | `~534` | `anyAnimated` can cache `false` if a pawn is added before `entries` is populated (load race) → dead pose-hook all session. Don't cache the `false` while `entries == null`. |
| **Medium** | `~47 / ~119` | `loaded`/`registered` one-shots set before success → a transient IO failure permanently caches an empty registry. Set after a successful parse. |
| **Medium** | `~186` | always-on `DumpFields`/`DumpSkinned` diagnostic (heavy reflection, one-shot per entry) — gate behind a Verbose config flag. |
| **Medium** | `~644` | `GetMember`/`SetMember` do string-keyed `AccessTools` lookups every call in the per-pawn-per-frame path. Resolve fixed `FieldInfo`s once. |
| **Medium** | `~568` | hardcoded `Pose0..Pose8` (9). Higher poses on a struct change would keep stale weight. Derive count or document the constant. |
| **Medium** | `~215/443` | `LoadSkeleton`/`LoadAtlas`/`LoadClipCollection` near-identical — collapse into one `LoadAsset<T>(guid,tag)` (natural home for the null-guard). |
| **Medium** | pervasive | magic strings for Amplitude type/field names (`"skinnedMeshInfos"`, `"Pose"+i`, `"FxComponentMeshContentManager"`…) — a `const` registry makes a rename a one-line fix. |
| Low | `~13 vs ~51` | comment says `enc_models.txt`, code reads `enc_models.json`. |
| Low | `~235` | `EnsureUploaded` assumes sub-mesh 0 is the body (fine for single-body models). |
| Low | `~485` | `InjectClipCollections` replaces the array; timing-dependent if `Apply` cached the old reference earlier the same frame. |
| Low | `~603` | neutral overlay maps (`_flatN`/`_white`/…) allocated lazily inside the per-frame `TickOne` loop — move to one-time init. |
| Low | `stLogged` | single global one-shot → only the first entry's `_MainTex_ST` ever logs. |

---

## Findings — Baker (`UniversalBaker.cs`)

| Sev | Location | Issue → fix |
|---|---|---|
| **High** | `128/146/550` | `?.Invoke` no-ops on a null reflected method → silent empty bake reports `ok=true`. Capture the `MethodInfo`, `Fail` if null. |
| **High** | `142 / ~749` | `GetAssetGUID`/`AmplitudeGuid` return `""` silently → static path returns `ok=true` with an empty skeleton GUID. Treat empty GUID as hard failure. |
| **High** | `151-153` | empty/`0,0,0,0` GUID only *warns*; still `ok=true`. Promote to `Fail`. |
| **High** | `191-192` (+`661/709/736`) | Blender shell-out reads stdout-then-stderr before `WaitForExit` → **pipe-buffer deadlock** (Blender is verbose on stderr) hangs the editor. Read one stream async (`ReadToEndAsync`). |
| **High** | `~183` | no `File.Exists(src)` before shelling to Blender → a missing model surfaces only as "produced no FBX". Guard at the top of each shell-out. |
| **Medium** | `330-332` | `PackTextures` can return null rects → every submesh falls back to `Rect(0,0,1,1)`, scrambling the skin silently. Check for null and `Fail`. |
| **Medium** | `285-292` | FBX-vs-OBJ recovery is a heuristic `FirstOrDefault` scan — nondeterministic if both exist. Track the imported path explicitly. |
| ~~**Medium→High**~~ **RESOLVED** | `static bake` | static path `CreateAsset` without `DeleteAsset` (animated deletes first). Not cosmetic: the in-place overwrite made Unity serve a **stale cached mesh/prefab to the skeleton bake**, so a *re-bake* shipped a skeleton lagging a bake behind → the model rendered **90° off in-game** while the preview looked right (hit on the StealthCruiser). **Fixed:** static path now deletes the four outputs (`_ModelMesh`/`_Mat`/`_Model.prefab`/`_Skeleton`) up front **and** force-reimports mesh+prefab before the skeleton bake — a re-bake is now a clean slate, matching the animated path. |
| **Medium** | `~107` | animated Scale-Factor recompute reimports but never re-measures to confirm it hit `size`. Add a post-reimport `MeasureLongestAxis` assert/log. |
| **Medium** | `233/243/247` | temp GLB/FBX in `GetTempPath()` with fixed names — never cleaned, can reuse stale. Delete in `finally`. |
| **Medium** | `65/230` | `ENC.` EditorPrefs keys + `[Factory]`/ENC branding. |
| Low | `173 vs 544/747` | three copies of "scan assemblies for a type by FullName" — use `FindAmpType` everywhere. |
| Low | `69-71/209-211` | `CreateFolder` return unchecked; `resourceName` not sanitized for path-illegal chars. |
| Low | `479/484` | double-sided back-face uses `sn[i].normalized` on possibly-zero normals (degenerate tri → coincident vert). Guard. |
| Low | `191` etc. | stderr logged as `LogWarning` even on exit 0 → spurious warnings every successful bake. Only surface on non-zero exit. |

---

## Findings — UI + registry (`ModelFactoryWindow.cs`, `ModelRegistry.cs`)

| Sev | Location | Issue → fix |
|---|---|---|
| **High** | `EnsureAnimProbe ~369` | probe/inspect cached on file *path* only → stale clip/bone pickers + wrong Animated state after re-exporting the same `.glb` (the normal modeler loop). **Verified.** Also key on `File.GetLastWriteTimeUtc`. |
| **High** | `ReadGlbJson ~522` | GLB JSON chunk truncated at 16 MB → `HasGltfAnim`/parse can run on invalid/partial JSON on huge GLBs. Read full `clen` or treat truncation as "unknown". |
| **Medium** | `NamesInArray ~408` | fallback clip parse can over-collect names from nested objects (extensions/extras) — rare (fallback only); tighten or accept explicitly. |
| **Medium** | `RegistryPath/ConfigDir` | `OnGUI` re-reads `libraryfolders.vdf` (+regex) **every repaint** while Settings foldout is open. Cache the auto-detected dir. |
| **Medium** | donor-log path `~452` | `Path.Combine(ConfigDir,"..","LogOutput.log")` breaks when the config dir is overridden non-standardly. Derive the log path from the install root. |
| **Medium** | donor-log scan `~461` | up-to-300 MB streaming scan runs synchronously on the UI thread on Pick → editor appears frozen with no progress bar. Add a progress bar / wait cursor. |
| **Medium** | `ParseGuid ~158` | `int.TryParse` without `InvariantCulture` — safe for ints but pass the culture to document intent. |
| Low | pervasive | ENC branding: `ENC.bepinexConfig`/`ENC.blenderPath` prefs, `enc_models.json` filename. Derive from a `Brand` const. |
| Low | `FallbackConfigDir ~45` | hardcoded `C:\Program Files (x86)` — wrong on non-C:/non-English installs. Fall back to empty/prompt or `SpecialFolder`. |
| Low | `using Newtonsoft ~9` | hard dependency → compile break if the SDK/mod.io Newtonsoft is absent. The bracket-match fallback already covers parsing; guard or drop for a dependency-free package. |
| Low | `~303` | non-ASCII `⚠` can render as tofu in odd editor fonts/locales. |
| Low | `~561` | pawn gather compares `GetType().Name == "PresentationPawnDefinition"` (string) — reasonable decoupling but brittle to renames / cross-namespace name clashes. |
| ~~Critical~~ **Low** | VDF un-escape `~92` | reviewer flagged Critical — **downgraded after verification**: the `\\`→`\` un-escape is correct for standard `libraryfolders.vdf`; only a robustness edge for odd separators/layouts. |

---

## Findings — Tooling (`Tools/rig_anim.py`, `mesh_reduce.py`, `blend_export.py`, `glbconv/Program.cs.src`)

| Sev | Location | Issue → fix |
|---|---|---|
| **High** | `rig_anim.py:17` | `int(argv[2])` / positional args crash with a bare traceback on empty/missing input the C# side can't distinguish. Guard + `RIGANIM ERROR` + `sys.exit(1)`. |
| **High** | `rig_anim.py:43` | `actions.get(clip_name)` fails on Blender's `.001` name-collision suffix. Fall back to a `startswith` match, report the near-miss. |
| **High** | `rig_anim.py:58-59` | slotted-action binding always `slots[0]` and swallows failures → can bake **no animation**. Select the slot matching the armature; error if none binds. |
| **High** | `rig_anim.py:95` | degenerate/single-frame `frame_range` (`fs==fe`) unguarded → 1-frame bake / divide risk. Compute from keyframes; clamp `fe=max(fe,fs+1)`. |
| **High** | `rig_anim.py:137` vs `mesh_reduce.py:34` | tri-count basis differs (triangulated fans vs raw polygons) and DECIMATE `ratio` is face-based → ~2× overshoot on quad meshes. Standardize one tri helper; adjust ratio. |
| **High** | `rig_anim.py:81-91` | bone-prefix strip removes **object-level** transform curves too (no `pose.bones[` token) → can delete root motion silently; empty channelbags left. Only strip bone curves that don't match. |
| **High (latent)** | `glbconv:45,104` | **no V-flip** despite the README/Capabilities claim that "the converter flips V". **Verified: UVs copied raw.** Skins render correctly in-game, so the flip must happen at Unity's OBJ import (or the doc is stale) — **reconcile the doc** (see below). |
| **Medium** | `glbconv:21` | `int.Parse(grid)` throws on non-numeric. `TryParse` with default 140. |
| **Medium** | `glbconv:~126` | `CellKey` masks each axis to 21 bits → a large `grid` on a big model can alias/merge distant verts. Guard cell count. |
| **Medium** | `glbconv:~88` | `TriKey` treats tris as unordered sets → back-to-back thin faces collapse to one, deleting thin geometry (the thing it claims to preserve). Winding-aware key or skip dedupe. |
| **Medium** | `glbconv:~85` | grid>0 UV-cluster averaging scrambles textured models with no warning. Warn when `images>0 && grid>0`. |
| **Medium** | `rig_anim.py:32`/`mesh_reduce.py` | material-less-mesh removal deletes a legitimately untextured single-mesh model → "no mesh to export". Only drop when other material-bearing meshes exist. |
| **Medium** | `rig_anim.py:113` | albedo `img.save()` with no packing/`has_data` check → can write 0 bytes; only warns. Verify and error if albedo was requested. |
| Low | ratios | `mesh_reduce` min ratio `0.001` vs `rig_anim` `0.02` — unify + validate post-decimate tri count > 0. |
| Low | `blend_export.py:25` | assumes `.png` for extension-less image names. Try multiple extensions. |
| Low | all scripts | no `sys.stdout.reconfigure(encoding="utf-8")` → non-ASCII model/action names can mangle the diagnostics the C# side parses. |
| Low | `rig_anim.py:146` | dead `select_all` before `use_selection=False`; FBX unit scale left default (double-scale risk in some importers). |

---

## Strengths (preserve these)

- **Institutional-knowledge capture is exceptional** — nearly every non-obvious step documents the *why* (UV-seam weld
  bug, Instantiate-scrambles-UVs, double-sided halving the reduce budget, the `sumWeight` NaN trap, the clip-source
  `/anim` isolation). Exactly what a reusable package needs.
- **Graceful-degradation bones already exist** — `SafeTypes` guards `ReflectionTypeLoadException`; the tri-state anim
  probe never wrongly blocks a rigged model; Newtonsoft-primary/regex-fallback (plugin) and Newtonsoft/bracket-match
  (editor) layering. The gap is only that the *call sites* don't use them to fail loudly.
- **Genuinely clever, correct fixes** — the descriptor-learned wrong-skeleton "rescue"; the position-offset accumulation
  (reads fresh translation each frame); the multi-material atlas remap reading the mesh from the asset to dodge the
  UV-scramble bug; the `/anim` clip-source isolation.
- **Careful caching where it matters** — `EnsureAnimProbe`/`pawnCache` keep file IO out of most of `OnGUI`; one-shot log
  flags keep diagnostics out of the per-frame path.
- **Consistent, greppable tool error protocol** (`RIGANIM ERROR`, exit-code + output-file double-check) — the right shape
  for shelled-out tooling.

---

## Recommended fix order

1. **Plugin loader `g` null-check** (Critical, ~5 lines across 3 methods) — a real crash that cascades to abort all
   registration.
2. **Baker: fail loudly on missing reflected methods / empty GUIDs** (High) — prevents a silent broken bake shipping to
   an adopter.
3. **Blender shell-out deadlock** (High) — async stderr read; `File.Exists(src)` guards; stop logging stderr as warning
   on exit 0.
4. **Per-frame texture tick → dirty flag** (perf).
5. **Probe cache: add file mtime** (Med, easy, fixes a real iteration annoyance).
6. **Reconcile the glbconv V-flip doc** + wrap pose `Time` in `Repeat` (both tiny).
7. **Python tooling hardening** — arg guards, clip-name `startswith`, slot binding, frame-range clamp, unify tri-count.
8. **The "ENC → neutral" rebrand + dead-scaffolding removal** — one deliberate mechanical pass at the package push.

---

## Doc reconciliation — RESOLVED (2026-07-04)

The README + `Capabilities.md` stated the GLB→OBJ converter "flips V". Investigation confirmed the code does **not**:
`glbconv/Program.cs.src` copies UVs raw (`:45`, `:104`) and has **never** flipped V (no such change in its git history),
and the baker's only "flips" are winding (face-order), not UV. Since custom skins render right-side-up in-game, the
glTF-V-top ↔ OBJ/Unity-V-bottom convention is reconciled by **Unity's OBJ import**, not by our converter. The README and
Capabilities.md have been corrected to say so.

---

## Testing strategy — why (almost) no unit tests, deliberately (2026-07-04)

A .NET 8 SDK is now installed, so `dotnet build ENCAccessProof.csproj` compile-checks the plugin in ~3s (see the
compile-check note in memory). Compile-checking is high value. **Classic unit tests, for this codebase as an internal
tool, are not** — a deliberate decision, recorded here so it isn't re-litigated:

Every real defect we've hit lived in the **Unity/Amplitude integration seam or the runtime environment** — exactly where
a unit test can't reach:

| Real bug | Where it lived | Unit-testable? |
|---|---|---|
| Stale skeleton on re-bake (ship 90° off) | Unity `AssetDatabase` in-place-overwrite caching | No — needs the editor |
| `JsonUtility` returns empty in-game | Mono runtime quirk | **No — a Newtonsoft unit test would PASS while the game fails.** The bug was the environment, not the parse logic |
| Registration NRE → aborts all models | Reflection into Amplitude types | No — needs the game |
| Model orientation wrong | How Unity applies the baked transform | No — the euler math was already correct |

The genuinely pure logic (registry JSON parse, GUID nibble-swap) is **stable and low-churn**, so a suite there would be
green, reassuring, and would not have caught a single real defect. Robustness per hour comes instead from: the fast
**compile-check**, **fail-soft resilience** (per-entry try/catch, null-guards on reflected methods), and the
**rebuild → relaunch → verify-the-log** discipline (the skeleton is what ships, not the preview/parse).

**When this flips to worth it:** the distributable-package goal. Once *third parties* write `enc_models.json`, the parser
reads untrusted input, and "malformed/partial registry → degrades gracefully, never throws, defaults sanely" becomes a
real contract. At that point add **one** focused xUnit suite over an extracted `ParseRegistry(string) → List<ModelEntry>`
(feed it garbage, assert no-throw + sane defaults) — and nothing more. Until then, tests are ceremony, not safety.
