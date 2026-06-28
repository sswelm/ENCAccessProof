# Humankind Unit Preview — Investigation Findings

**Goal:** build an editor tool to view a unit (`PresentationPawnDefinition`) fully assembled —
upright, dressed, all attachments, freely rotatable — because the native preview is unusable.

**Date:** 2026-06-26 · **Project:** ENCReload (Humankind mod, Unity 2021.3.1f1)

---

## Practical workarounds for the broken preview camera (recommended)
The native "Open in Editor" preview renders the unit correctly; only its camera misbehaves (resets on
every repaint). Two cheap tricks make it perfectly usable:
1. **Freeze the view:** drag to rotate the unit, then move the mouse **outside** the preview window.
   Repaints stop, so the camera stays at your chosen angle until you move the cursor back over it —
   letting you study the unit from any angle.
2. **Screenshot it:** with the view frozen, take a screen capture. Repeat at a few angles to build a
   full reference of the unit. Simplest reliable way to "see what it looks like in-game."

---

## TL;DR
A standalone GameObject-based unit viewer is **not feasible**. Humankind renders units through a
**GPU pipeline** (`PawnManager` + `AnimationManager`), not as posed GameObjects. The only ways to
*see* a unit correctly are tools that drive that pipeline:
- **Native "Open in Editor"** pawn editor — renders correctly, but the camera is buggy/limited.
- **`PawnTester`** (their MonoBehaviour) in **Play mode** — untested-to-success here; needs the full
  play-mode bootstrap, but bypasses prefab instantiation so it *could* work.

Use the native preview. Accept the clunky camera.

---

## Why the native preview looks broken
The native inspector / "Open in Editor" preview frames the unit at its feet, upside-down, with limited
rotation that snaps back. The **rendering is correct** (the unit IS drawn, dressed); only the **camera**
is broken, and it lives in Amplitude's compiled `Amplitude.Mercury.Production.dll`, so it can't be patched.

A separate red herring: running `PawnTester` in Play mode first failed with *"Missing
AssetBundleLoadingConfiguration."* That's a **different** path. The inspector preview loads each pawn's
assets on demand and renders fine without it.

## Why a custom GameObject viewer is impossible (proven 3 ways)
1. **Decompiled code** (`Amplitude.Mercury.Animation.PawnTesterHelper.EnsureLoaded`):
   `PawnManager.Instance.RegisterPawnDefinition(...)` + `AnimationManager.Instance.Register(skeleton)` —
   the body, clothing fragments, and skeleton are uploaded to **GPU buffers**.
2. The pawn **`Instance`** struct is just GPU indices: `PawnDescriptorIndex`, `SkeletonId`,
   `AnimationId0/1`, `PalettePixels`, `Position` — **no GameObject, no Transform hierarchy.**
3. **Instantiating the fragment prefabs** in the editor yields exploded skinned meshes, because the
   prefabs' driver components are missing scripts: `PresentationSubPawn`, `AlterationController`,
   `MecanimEventInterpreter`, `PawnAudioScaleSlave`. Those components do the assembly/posing; without
   them every clothing `SkinnedMeshRenderer` collapses toward the origin (the spiky mess).

## How a unit is actually structured (data side)
- `PresentationPawnDefinition` (in `Amplitude.Mercury.Data.World`) = `SubPawnDefinitions[]` +
  `Attachements[]` (each `Attachement` = `Name` + `PresentationPawnFragmentReference Fragment`),
  plus `AnimatorOverrideController`, `CharacterPalette`, `Offset`, `MaxHeight`.
- Fragments are `PresentationPawnFragment(Mesh|SkinnedMesh)` referencing a `ModelPrefab`/`Prefab` and a
  `MaterialRef`. The body + all clothing share **one skeleton**; fragments are mesh-only, bound to that
  rig at runtime by the GPU pipeline.

## Asset bundles
- The editor's `Assets/Resources/AssetBundleLoadingConfiguration.asset` lists only `ENCReload.Manifest`.
  Vanilla art lives in the game install:
  `C:\Program Files (x86)\Steam\steamapps\common\Humankind\AssetBundles\` — key bundles
  `Mercury.Data.Units\mercury.data.units.assetbundle` (bodies/clothes) and
  `MercuryDatabases\mercurydatabases.assetbundle`.
- The inspector preview loads what it needs on demand, so it renders without registering vanilla bundles.
  `PawnTester` / Play mode is the path that wants the bundle config.

## A real (but heavy) custom viewer would require
Driving `PawnManager`'s GPU render into a `RenderTexture` with our own camera — i.e. reimplementing the
native preview's render setup (`CameraGraphicService` init, descriptor/skeleton registration,
render-to-texture). Possible, but a large, fragile reverse-engineering effort. Not worth it for a preview.

## Tooling notes (for next time)
- **Decompiler is installed:** .NET SDK 8.0 at `%USERPROFILE%\.dotnet`, `ilspycmd 8.2.0.7535` at
  `%USERPROFILE%\.dotnet\tools\ilspycmd.exe`. Decompile e.g.:
  `ilspycmd "<dll>" -t <FullTypeName>` (set `$env:DOTNET_ROOT` to the .dotnet dir first).
  Key DLLs: `Assets/[Amplitude.Mercury]/Plugins/AnyCPU/Amplitude.Mercury.Animation.dll`,
  `Assets/[Amplitude.Mercury]/Plugins/Editor/Amplitude.Mercury.Production.dll`.
- The native editor's preview entry points: `PresentationPawnDefinitionCustomInspector.OpenEditor()` →
  `PresentationPawnDefinitionEditor` (EditorWindow) → `OnDrawEditorPreview()` / `RefreshPreview()`.

---

# Recipe: add a custom 3D model (static prop / vehicle, e.g. a zeppelin)

**Verified 2026-06-27.** Proven end-to-end on the *referencing* side; the final *render* needs a Build
(see the MeshCollection gotcha). Adding a 3D model is officially a **Minor** mod change.

### The one rule that explains everything
**Referencing ≠ rendering.**
- A model prefab in `Assets/Resources` is **immediately referenceable** in Amplitude's asset picker
  (shows up with `Owner: Project Assets`) — no build needed to *point at* it.
- But the GPU renderer can only **draw** meshes that are in a **MeshCollection**, which is only created
  when you **Build the mod**. Until then the preview throws
  `PresentationPawnDefinitionAddOn.AddMeshPreviewData` NullRefs and shows *"MeshCollection not found."*
  That message = "you haven't built yet," not a real error.

### Where things live
- **Visual assets (models, textures) → `Assets/Resources`** (`ModuleEditor.ResourcesFolderPath`). Everything
  there is bundled into the mod on build. "Visual Data → Select Resources Folder" just designates this folder.
- **Data (fragments, definitions) → `Assets/Databases`** (git-tracked; ships in the build).

### Steps
1. **Model** → import your FBX + texture, make a prefab, drop it under `Assets/Resources`
   (e.g. `Assets/Resources/MyModel.prefab`). A Unity primitive works as a stand-in to test the pipeline.
2. **Fragment** → create a `PresentationPawnFragmentMesh` (the *static* mesh fragment — no skeleton needed):
   - `Model Prefab` → set via the **`...` picker** (it's an `Amplitude.Framework.Asset.GameObjectReference`,
     NOT a drag-and-drop Unity slot). Your `Resources` prefab appears in the picker.
   - `Model Name` → the prefab name. `Cast Shadow` → Cast.
   - `Material Ref` → a real **unit/pawn material** (e.g. the one your placeholder unit uses).
     ⚠️ NOT a font material like `arial-monospaced` (the picker lists those too).
3. **Pawn** → do NOT build a `PresentationPawnDefinition` from scratch: it's a **humanoid** type
   (hair/cape/dress slots) and *requires a skeleton `Description`* — a vehicle doesn't fit it, and it errors.
   Instead **reuse an existing vehicle/air-unit pawn** and swap its body. In ENC, `Era5_Common_Zeppelins_01`
   already exists and uses `Unit_Era6_CruiseMissile_01` as both its `Description` (skeleton/template) and
   placeholder `Body`. Just change its **Attachements → Body** fragment (`...` picker) to your custom fragment;
   leave `Description` as the missile (it provides the attach point — your static mesh rides on it).
4. **Build** → Mod Editor → Build (run `C:\GameData\Clean-ENCReload-Export.bat` first if you hit
   "access denied" — see [[humankind-folder-junction]]). The build bakes your `Resources` model into the
   bundle/MeshCollection.
5. **Run the game** → the unit now renders your model. Swapping in a *real* mesh later = just replace the
   prefab in the fragment's `Model Prefab`; every unit using that fragment updates, no other changes.

### Helper used
`Assets/Scripts/Editor/ZeppelinSetup.cs` (temporary) created the stand-in prefab + the
`PresentationPawnFragmentMesh` programmatically via `Tools > Zeppelin > …`. Delete it once done.

---

# Custom unit MODEL — full experiment log & hard limits (the zeppelin saga, 2026-06-27)

**Goal:** get a custom 3D model to show on a live unit — ideally the zeppelin, *during bombardment*.
We tried many angles. Here is exactly what works, what doesn't, and why. All experiments were reverted
clean via `git checkout` (only `Assets/Databases` is tracked).

## ✅ What works
- **Custom static mesh as a Body fragment → renders on the unit, no corruption** — *provided it has a valid
  material*. Proven in-game (a capsule rendered on the zeppelin).
- **Referencing**: a prefab in `Assets/Resources` is immediately pickable in the Amplitude `...` asset picker
  (`Owner: Project Assets`). No build needed to *reference* it (build needed to *render* — see MeshCollection).

## ⛔ Hard limits (each independently proven, the game crashed/corrupted to confirm)
1. **A valid `MaterialRef` is MANDATORY.** Decompiled `PresentationPawnFragmentMesh.RuntimeMaterial`:
   `if (MaterialRef.IsNull) LogError("no Material is defined")` → it then renders **garbage**. Because the
   fragment sits in a **shared multi-unit collection** (`PresentationPawnDefinition_Era5_ENC.asset`), that
   garbage **corrupts neighbouring units** (patrol fighters rendered as exploded/“corruption” flying around).
   - **Fix:** copy a real material's GUID off an existing fragment. We read the cruise-missile's
     `MaterialRef` (`ba004f54131e750499b306fdab2cd1a1`) and set it on ours → no corruption.
   - Materials are NOT pickable in the editor (only Unity built-ins show, which Amplitude rejects → the field
     goes **red**) because the vanilla material bundles aren't loaded in the editor.
2. **The bombardment / attack visual is the Description's `Template`, NOT the Body.** Changing the Body only
   affects the *idle* look. The zeppelin bombards showing **cruise missiles** because its
   `Description = Unit_Era6_CruiseMissile_01` and that Template's mesh plays the attack animation. (Confirmed
   baseline: zeppelin bombard = cruise-missiles diving.)
3. **A CUSTOM `PresentationPawnDescription` CRASHES the game** — `Index was outside the bounds of the array`
   flood, breaks save loading. Tried three ways, all crash:
   - custom Description + a fake 1-bone skinned-brick Template,
   - custom Description with **NO** Body (Template-only),
   - custom Description **cloning the missile's** Template + Slots.
   **Conclusion: you cannot make a working custom Description.** Only vanilla Descriptions work — the pawn
   pipeline indexes the real skeleton, and anything homemade is out of bounds. So the attack model is
   effectively **locked** unless you override a *vanilla* Description's Template with a *properly rigged* model.
4. **`PresentationUnitDefinition` does NOT control the model** — only formation, behaviour, sound, projectile,
   and unit-data references. (Verified by inspection.)
5. **A static-mesh Template is impossible** — Templates require a real rig (`Animator` + bones matching the
   slots). A capsule/brick "minimal rig" crashes.

## 🪤 The animation trap (why the cruise-missile recon idea froze)
A cruise missile is a **one-shot projectile**: its animation set is launch → fly → impact, with **no
idle / move-loop / move-end / turn** clips. Reusing the cruise-missile pawn for a unit that *moves* makes the
unit **freeze after its first move** — it waits for a "settle to idle" animation that doesn't exist. So a
recon/scout unit needs a pawn built to move & hover.

## 🛩️ The viable (not-yet-built) path: clone the ATTACK HELICOPTER
`AirUnit_Era6_Common_AttackHelicopters` is the right base because a helicopter:
- **moves, hovers, turns, idles in the air** → has the full animation set (no freeze),
- **stays visible** in the sky (unlike bombers that vanish to a hangar),
- uses a **working vanilla Description** (no crash).
Plan: clone it → make it terrible at attacking + give it reveal/recon → then swap only the **Body** mesh.
Open question still untested: does a *hovering/patrolling* unit show its **Body** (changeable) or its
**Template** (locked)? Idle shows Body; attack-dive shows Template; hover/patrol = unknown.

## Practical rules learned
- **Never wire a half-built asset into a shared collection** — a null-material fragment in
  `PresentationPawnDefinition_Era5_ENC.asset` corrupted other Era5 units. Make the asset complete first.
- **Revert is always `git checkout <file>`** on the touched Databases asset(s) (Resources is gitignored).
  After a revert, a residual "modified" flag is usually just **LF↔CRLF** from Unity re-saving — `git diff -w`
  shows 0 real changes; `git checkout` again clears it.
- **2D icon ≠ 3D model.** The unit's `ZEPPELIN.PNG` icon is a painted image; it does NOT give a 3D mesh —
  that's why a real zeppelin needs an actual rigged model, not just the existing art.
- Decompiler is set up (ilspycmd) — see "Tooling notes" above. Key types read: `PresentationPawnFragment(Mesh)`
  (material logic), `PresentationPawnDescription` (`Template` + `Slots`), `PawnTesterHelper`.

## 🔑 BREAKTHROUGH (2026-06-27 pt.2): the REAL missing piece for a custom attack model = bake + register a Skeleton
We got a custom Description to load **past the IndexOutOfRange** by replicating the missile rig exactly
(`Dummy_Root → Base`, mesh skinned to `Base`, **reusing the missile's Avatar + Animator controller** — dumped
via `Tools > Zeppelin > Dump Missile Rig`). The user's "match the animation anchors" theory was correct and
necessary. The new failure became a **`NullReferenceException`**, and decompiling pinned it precisely:

`Amplitude.Mercury.Animation.PresentationPawnDefinitionAddOn.Load(AnimationManager)`:
```csharp
Guid guid = description.Template.Guid;          // our custom rig prefab's GUID
MeshCollection = GetMeshCollection(guid);        // returns NULL — our rig has no baked/registered MeshCollection
Skeleton = MeshCollection.SkeletonInstance;       // ← NullReferenceException (crashes 1 line before the graceful check)
```
The very next lines are the intended handler: `LogError("Skeleton <X> of pawn ... was not registered to
AnimationManager, please add it to AnimationManagerContent")`. **That error message is the whole secret.**

**How skeletons/meshes actually become drawable (the pipeline):**
- `MeshCollection` is a **`[CreateAssetMenu]` ScriptableObject** (ns `Amplitude.Mercury.Animation`) with
  `SetPrefab(GameObject/Guid)`, `Reimport()`, `SkeletonInstance`, `SourcePrefab`.
- `Skeleton` is **`[CreateAssetMenu] class Skeleton : MeshCollection`** with **`Reimport(Guid prefab)` that BUILDS
  the bone hierarchy from a prefab** (reads its transforms, handles `AdditionalBones`, strips the root parent).
- `AnimationManager.GetMeshCollection(guid)` returns a hit only if a baked MeshCollection whose
  `SourcePrefab == guid` was registered via `RegisterMeshCollection`, sourced from **`AnimationManagerContent`**
  (a `ScriptableObject` holding `Guid[] MeshCollections` / `AnimationClipCollections` / `AnimatorOverrideControllers`).

**So the missing step for ANY custom attack/skeleton model is NOT the rig prefab — it's:**
1. Create a **`Skeleton`** asset (Assets ▸ Create), `SetPrefab(customRig)` + `Reimport()` → bakes the bone hierarchy
   **and** the skinned meshes (Skeleton IS a MeshCollection, so `SourcePrefab = rig GUID`, `SkeletonInstance = itself`).
2. **Register** that Skeleton/MeshCollection in **`AnimationManagerContent.MeshCollections[]`** so `GetMeshCollection`
   resolves the Description's `Template` GUID.
3. Point a (vanilla-cloned) Description's `Template` at the custom rig.

Both `Skeleton` and `MeshCollection` being `[CreateAssetMenu]` strongly implies this is **intended to be doable** —
the likely "secret" behind a modder's quiet success. **REMAINING UNKNOWN:** whether a mod can append to the vanilla
`AnimationManagerContent.Instance` (it loads from the base bundle) — i.e. does the mod build auto-register mod
MeshCollections, or must you override/merge AnimationManagerContent? That's the next thing to crack.
Contrast: a **static Body fragment** (`PresentationPawnFragmentMesh`) already works because the mod build DOES bake
its (non-skeleton) MeshCollection — that's why the capsule Body rendered but the skinned Template did not.

**RESOLVED (probe, 2026-06-27): the skeleton registry is NOT mod-accessible.** `Tools>Zeppelin>Probe
AnimationManagerContent` reported **`AnimationManagerContent.Instance = null`** in the Mod Editor (its fixed
`AssetGUID` resolves to nothing — the content asset isn't in the mod and isn't loadable in edit mode), while
`AnimationManager.Instance` IS live and `UpdateSkeletonsFromDatabase` exists. So although the engine *has* a
one-call bake+register (`UpdateSkeletonsFromDatabase → CreateSkeletonIfNecessary`), it operates on the **vanilla
`AnimationManagerContent`**, which a mod neither owns nor can ship a change to. The two registration paths:
- **Static fragment mesh (idle Body):** directory-scan (`FindAssetPaths("t:PresentationPawnFragment",
  fragmentDirectories)`) — **mod build supports it** → custom idle Bodies work.
- **Skeleton (attack Template):** `UpdateSkeletonsFromDatabase` editing vanilla `AnimationManagerContent` —
  **not mod-accessible** → custom attack skeletons can't be shipped.
**Conclusion:** a custom *animated/attack* unit model is effectively blocked for mods (would require modifying
vanilla files). A custom *static* model — an idle Body fragment, or (cleanest) a **district/building** — registers
through the mod-supported path. The "modder who quietly succeeded" almost certainly shipped a **static district
model**, not an animated unit. → Pursue the district path (next section); it sidesteps this entire wall.

**FINAL (2026-06-27): confirmed not shippable as a standard mod.** Checked **Override from Archives** for
`AnimationManagerContent` and GUID `0a88cc6e70fe12a489eb3ebddb8ffb97` — **neither appears**, so a data mod cannot
override the skeleton registry. Probe also showed `AnimationManager.Content`/`meshCollections` are **empty in the
Mod Editor** (the registry only populates at game runtime from the vanilla bundle), and the public
`RegisterMeshCollection`/`Register(Skeleton)` are runtime-only. So the ONLY ways to register a custom skeleton are
(a) a shipped **BepInEx runtime plugin** calling `RegisterMeshCollection` (rejected — players won't install code
mods), or (b) overriding vanilla `AnimationManagerContent` (not exposed → impossible). BepInEx (the user already
runs **GUItool** for fast bombardment testing — spawn unit + force battle) is still useful as a **dev-only** reader,
but there's nothing shippable to produce. **DEFINITIVE: custom animated unit models cannot ship as a standard
Humankind data mod; only static models (idle Body fragment, or districts) can.**

**CLINCHING PROOF (2026-06-27): a FULL archive export does not contain the registry.** The user produced a complete
"Override from Archives" export (`D:\Humankind\Dante`, 478 MB, 811 `.asset`, full Databases/Resources/SDK folders).
Searched it exhaustively: **no `AnimationManagerContent` instance, no `Skeleton` assets, no `MeshCollection`/
`ClipCollection` assets, and nothing containing GUID `0a88cc6e…`.** So the animation/skeleton registry is **not part
of the moddable data layer at all** — it's baked into the compiled engine bundles (`Assets/AssetBundles`, binary),
outside everything the archive system exposes. That's the cleanest possible confirmation: *extract everything a mod
can touch, and the skeleton registry still isn't in it.* A standard mod cannot register a custom skeleton because the
registry exists in no layer a mod can write. **Animated custom unit model as a standard mod = settled NO** (confirmed
three independent ways: editor probe → Override-from-Archives search → full archive export).
NOTE: the Dante full export is still very useful — it holds the complete editable **database**, ideal source for the
district path below (`BuildingVisualAffinityDefinition`, `ConstructibleVisualAffinityDefinition`, all constructibles).

## ✅ VIABLE ROUTE for animated models: two-tier (standard base mod + OPTIONAL BepInEx plugin)
A pure data mod can't register a custom skeleton — but an **optional** BepInEx plugin layered over a standard,
crash-safe base mod can, with a **free graceful fallback**. Design:
- **Base data mod (ships to everyone, standard, no dependency):** the zeppelin stays wired to the **vanilla
  cruise-missile Description** → works for all players, never crashes. This is literally the current working state.
  The custom zeppelin `Skeleton`/`MeshCollection`/Description/rig prefab **ship as INERT, unreferenced assets**
  (just files in the bundle — nothing loads them, so no crash path).
- **Optional BepInEx plugin (opt-in "HD model"):** at load, BEFORE the zeppelin pawn instantiates (Harmony postfix
  on `AnimationManager.Load`, or a hook right after DB load / before presentation), it (1) loads the inert baked
  `Skeleton` by GUID and calls the public `AnimationManager.Instance.RegisterMeshCollection(...)` /
  `Register(Skeleton)`, then (2) **repoints just `Era5_Common_Zeppelins_01.Description`** (in memory) to the custom
  Description. Repoint the zeppelin's Description (zeppelin-only) rather than globally swapping the cruise-missile
  mesh (which would change real missiles too).
- **THE SAFETY RULE:** shipped data must NEVER reference the custom skeleton (that's exactly what NullRef-crashed).
  Keep the vanilla Description wired; the plugin is the ONLY thing that registers + repoints, at runtime.
- **Durability:** Humankind is effectively EOL (only scenario content, no engine/code changes), so the Harmony
  patches stay valid indefinitely — write once, no per-patch maintenance. The version-fragility caveat is largely
  moot. The base mod is unaffected by patches regardless.
- **Distribution:** base mod on mod.io (standard, no deps) + the plugin as a separate optional download.
This is almost certainly the only way to get a custom *animated* unit model into the running game, and EOL makes it
a durable one-time build rather than a treadmill.

**✅ PROVEN (2026-06-27) — the plugin half works, running in-game.** Built a first BepInEx 5 plugin
(`C:\Repo\ENCAccessProof`, net471 + HarmonyX, manual `References\` since BepInEx isn't on nuget.org), deployed to
`Humankind\BepInEx\plugins\`, ran it. Its Harmony postfix on `AnimationManager.AnimationResolveDependencies` fired,
and an F8 feedback window reported: **`MeshCollections=110, AnimatorOverrideControllers=112, AnimationClipCollections=106`**
and found ENC's **`Era5_Common_Zeppelins_01`** among 1306 `PresentationPawnDefinition`s (via
`Amplitude.Framework.Databases.GetDatabase(typeof(PresentationPawnDefinition))`). Crucial: the registry was
null/empty in the Mod Editor but **populated (110) at runtime** — it exists only at runtime, exactly where BepInEx
operates. So reading the live registry, writing to it (public `RegisterMeshCollection`), and locating the target
unit to repoint are all now proven running, not just decompiled. (Other HK BepInEx references cloned to `C:\Repo\`:
`Humankind-GUI-Tools`, `shakee.Humankind.FameByScoring`, `BepInEx-src`.)

**WRITE proven (same session):** a "Test Write" button creates an empty `MeshCollection` and calls the public
`RegisterMeshCollection` on the live `AnimationManager` — the runtime `meshCollections` *list* count rose +1 per press
(two presses → +2). So READ and WRITE are both confirmed in-game. (Empty placeholders are harmless; the registry
rebuilds from the bundle each load.)

**TIMING RULE for the injection (learned empirically):** `AnimationResolveDependencies` fires **several times** per
load. `Content.MeshCollections` (source `Guid[]`, =110) is set early, but the runtime `meshCollections` **list** (what
`RegisterMeshCollection` feeds) is built **later** — it reads null/`-1` on the first hook pass and `106` once
populated. So register the custom skeleton **only when `meshCollections.Count > 0` (skip early empty passes, run
once)**, or — most robust — **just-in-time via a prefix on `PresentationPawnDefinitionAddOn.Load`** (fires right
before a pawn's skeleton lookup — the exact NullRef-crash site — so the registry is guaranteed populated there, and
it's the latest point before the crash). MILESTONES so far: (1) READ ✅, (2) WRITE ✅, (3) REPOINT+RENDER — pending:
bake a real `Skeleton` inert in ENC (3a) → plugin loads+registers it (3b) → repoint `Era5_Common_Zeppelins_01`'s
Description + bombard (3c). Injection work lives in `C:\Repo\Community3DInjection`; the proven access plugin is
`C:\Repo\ENCAccessProof`.

Decompiler gotcha: the SDK DLLs live under `Assets/[Amplitude.Mercury]/Plugins/AnyCPU/` — the **square brackets are a
glob wildcard**, so `ilspycmd` reports "file does not exist." **Copy the DLLs to a bracket-free folder first**, then
`ilspycmd <dll> -t <FullTypeName>`. (This silently broke every `-t` decompile earlier this session.)

## 🏛️ ALTERNATIVE PROMISING PATH: custom DISTRICT / building models
**Why this is the easy case** (a modder reportedly succeeded quietly): a district/building is a **static prop**
— **no rig, no `Animator`, no `Description`/`Template`, no idle/move animations.** So **every wall that killed
the unit attempt simply does not exist for a building:** no `IndexOutOfBounds` crash, no animation freeze, no
rig requirement. It's just a static mesh + material placed at a spot — the genuinely supported case.

**The system to target:** buildings get their models through a **`BuildingVisualAffinityDefinition`** system
(`BuildingVisualAffinityDefinitionCollection`, `BuildingVisualAffinityPerEra`, `BuildingVisualAffinityReference`)
— per-era, per-culture model sets. There's also `ConstructibleVisualAffinityDefinition` in
`Databases/SettlementPresentation/`. A custom district model would be referenced through that affinity system.

**Next steps (focused dig, not yet done):** decompile `BuildingVisualAffinityDefinition` /
`BuildingVisualAffinityPerEra` to find the exact model/prefab reference field (hit a namespace/assembly snag —
it's referenced in `Amplitude.Mercury.Data.dll` but may be defined elsewhere or nested). Then: put a custom
building mesh in `Assets/Resources`, point a building's visual-affinity at it, build, place the district in-game.
Expectation: far higher chance of success than units, because none of the unit blockers apply.
