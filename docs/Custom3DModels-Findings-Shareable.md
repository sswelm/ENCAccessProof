# Custom 3D Models in Humankind — Findings & Plan

*A focused write-up of what's possible, what isn't, and why — from a long decompile + trial-and-error
session. Safe to share or feed to an AI model.*

---

## TL;DR

- **Static models** (districts, props, a unit's idle "Body", textures) → **work as a plain data mod** via
  editor scripts / the database. No BepInEx.
- **Animated / skeletal unit models** (a unit's *rigged attack visual*) → **cannot ship as a data mod.** The
  skeleton must be registered in a runtime-only engine registry that no mod-data layer can write. Confirmed by
  decompiling the animation pipeline and by three independent checks.
- **Proposed solution:** a community **3D-asset injection** framework — data mods ship inert baked assets + a
  small manifest; an **optional BepInEx plugin** registers them at runtime and repoints the unit's Description.
  **Graceful fallback:** without the plugin, the data mod shows a vanilla stand-in (no crash); with it, the
  custom model appears. Opt-in, zero risk to other players.

---

## The two cases (this is the key distinction)

| | Registration path | Mod-shippable? |
|---|---|---|
| **Static** mesh — district, prop, unit idle Body, textures | directory-scan (`FindAssetPaths("t:PresentationPawnFragment", fragmentDirectories)`) | ✅ yes — editor/database works |
| **Skeletal** mesh — a unit's Description `Template` (attack visual) | entry in `AnimationManagerContent.MeshCollections[]`, populated at runtime | ❌ no — registry isn't in any mod-data layer |

So it's **not "editor scripts vs BepInEx"** — it's **editor/database for static, BepInEx for animated.**

---

## The animated wall (core finding)

A unit's attack visual is a `PresentationPawnDescription`'s **rigged `Template`**. At pawn instantiation:

```
PresentationPawnDefinitionAddOn.Load():
    guid          = description.Template.Guid          // the rig prefab
    MeshCollection = AnimationManager.GetMeshCollection(guid)   // null if the skeleton isn't registered
    Skeleton       = MeshCollection.SkeletonInstance           // → NullReferenceException when null
```

`GetMeshCollection(guid)` only returns a hit if a `MeshCollection` whose `SourcePrefab == guid` was registered in
**`AnimationManagerContent.MeshCollections[]`**. A custom skeleton is never in that list → null → crash.

### Why editor scripts CAN'T register it (but CAN for static)
- You *can* bake a skeleton in-editor: `Skeleton` is `[CreateAssetMenu] class Skeleton : MeshCollection`, and
  `Skeleton.Reimport(prefabGuid)` builds the bone hierarchy from a rig prefab.
- The engine even has a one-call bake+register: `AnimationManagerContent.UpdateSkeletonsFromDatabase()` →
  `CreateSkeletonIfNecessary()` (creates a `<name>_Skeleton.asset`, `Reimport`s it, appends its GUID to
  `MeshCollections[]`).
- **But that edits the vanilla `AnimationManagerContent`, which a mod can't ship a change to** (see proof below).
- Static fragments avoid this entirely — they register through the `fragmentDirectories` directory-scan that the
  mod build supports. That's why custom idle-Body meshes and districts work with no plugin.

### The registry is not in any mod-data layer (proven 3 ways)
1. **Editor probe:** `AnimationManagerContent.Instance` is **null** in the Mod Editor (its fixed asset GUID
   `0a88cc6e70fe12a489eb3ebddb8ffb97` resolves to nothing — it isn't a project asset). The registry only
   populates at game runtime, from the vanilla bundle.
2. **Override from Archives:** searching for `AnimationManagerContent` / that GUID returns **nothing** — not
   exposed for override.
3. **Full archive export:** a complete export (478 MB, 811 `.asset`, all Databases/Resources/SDK) contains
   **zero** `AnimationManagerContent` / `Skeleton` / `MeshCollection` assets. The registry is baked into the
   compiled engine `AssetBundles`, outside everything a mod can touch.

---

## The BepInEx opening

The registry exposes **public runtime methods**:
```csharp
AnimationManager.Instance.RegisterMeshCollection(MeshCollection mc);  // adds to the live registry
AnimationManager.Instance.Register(Skeleton skel);                    // returns a skeletonId
AnimationManager.Instance.Content;                                    // the live AnimationManagerContent
```
A BepInEx/Harmony plugin can call these at load — exactly what a data mod can't. **Verified hook point:** postfix
on **`AnimationManager.AnimationResolveDependencies()`** (the method that assigns `loadedContent` and registers
every vanilla collection — fires right when the registry becomes populated, before pawns instantiate).

### ✅ PROVEN with a running plugin (2026-06-27)
A first BepInEx 5 plugin (`ENCAccessProof`, net471, HarmonyX) was **built, deployed, and run in-game**. Its
postfix on `AnimationManager.AnimationResolveDependencies` fired, and an F8 feedback window reported:
```
AnimationManager registry reachable: MeshCollections=110, AnimatorOverrideControllers=112, AnimationClipCollections=106
PresentationPawnDefinition DB: 1306 entries; 1 match "Zeppelin": Era5_Common_Zeppelins_01
>>> Confirmed access to 'ENCReload' assets. <<<
```
Key point: the registry was **null/empty in the Mod Editor but populated (110 collections) at runtime** — confirming
the registry exists only at runtime, exactly where BepInEx operates. We can **read** it (`AnimationManager.Content`),
so we can **write** to it (public `RegisterMeshCollection`), and we can **locate the target unit**
(`Era5_Common_Zeppelins_01`, found via `Amplitude.Framework.Databases.GetDatabase(typeof(PresentationPawnDefinition))`)
to repoint its Description. Every primitive the injection needs is now proven running, not just decompiled.

**WRITE also proven:** a "Test Write" button creates an empty `MeshCollection` and calls the public
`RegisterMeshCollection` on the live `AnimationManager`; the runtime `meshCollections` *list* count ticked up by 1
per press (pressed twice → +2). So both core primitives are confirmed in-game: **READ** the live registry and
**WRITE** to it. The empty placeholders are harmless (unreferenced; the registry rebuilds from the bundle each load).

**Timing rule (important for the injection):** `AnimationResolveDependencies` fires **several times** during load.
`Content.MeshCollections` (the source `Guid[]`, =110) is set early, but the **runtime `meshCollections` list** (what
`RegisterMeshCollection` feeds) is built **later** — it reads as null/`-1` on the first hook pass and `106` once
populated. So the injection must register **when the live list is populated and before the pawn instantiates** —
either guard on `meshCollections.Count > 0` (skip the early empty passes, run once), or — most robust — register
**just-in-time** via a prefix on `PresentationPawnDefinitionAddOn.Load` (fires right before a pawn's skeleton lookup,
i.e. the exact method that NullRef-crashes, so the registry is guaranteed populated there).

---

## Proposed framework

**Data-mod side (standard, ships to everyone, crash-safe):**
- Keep the unit wired to a **vanilla stand-in** Description (e.g. zeppelin → cruise missile) → works for all,
  never crashes. This is the no-plugin fallback.
- Ship, **inert/unreferenced** (just files in the bundle, nothing loads them):
  - the baked `Skeleton`/`MeshCollection` (editor-baked via `Skeleton.Reimport`),
  - the custom rig prefab,
  - the custom Description.
- A small **manifest**: `{ "skeletonGuid": "...", "targetUnit": "...", "rigGuid": "..." }`.

**Plugin side (optional opt-in, BepInEx 5 / Mono):**
- Harmony postfix on `AnimationManager.AnimationResolveDependencies`:
  - for each loaded mod's manifest entry: `LoadAsset<MeshCollection>(skeletonGuid)` →
    `RegisterMeshCollection(mc)` → `Register(mc.SkeletonInstance)`,
  - then repoint the target unit's `Description.Template` GUID → `rigGuid` (in memory).

**The safety rule:** shipped data must **never** reference the custom skeleton (that's what crashes). The plugin
is the only thing that registers + wires it.

**Durability:** Humankind is EOL (scenario content only, no engine/code changes) → the Harmony patches stay valid
indefinitely. Write once.

---

## Open concerns (both valid, flagged for prototyping)

- **Cultures (`UnitVisualAffinity`):** a unit's visual is per-culture/era (e.g. `UnitVisualAffinity_Era5_Germany`),
  so a model swap must target the right affinity. **Good first test:** the zeppelin is an Era5 **Common** unit
  (culture-agnostic), so it sidesteps this for a PoC.
- **UV / materials:** custom meshes route through the `FxOutputLayer` / material pipeline
  (`AnimationManagerContent.OutputLayerFromMaterialGuid(materialGuid)`). A mesh needs a **valid material's output
  layer** or it renders garbage — and because fragments live in a shared collection, a bad material can corrupt
  neighbouring units. (Hit exactly this with a null `MaterialRef`.) Custom textures/UVs are worth prototyping
  early; for static fragments materials register via `materialDirectories`, similar to meshes.

---

## Key decompiled reference

- **Crash site:** `Amplitude.Mercury.Animation.PresentationPawnDefinitionAddOn.Load(AnimationManager)`.
- **`Skeleton : MeshCollection`**, both `[CreateAssetMenu]`. `Skeleton.Reimport(Guid prefab)` builds bones from a
  prefab (handles `AdditionalBones`, strips the root parent).
- **Registry:** `AnimationManagerContent` (asset GUID `0a88cc6e70fe12a489eb3ebddb8ffb97`), `Guid[] MeshCollections`
  / `AnimationClipCollections` / `AnimatorOverrideControllers`, runtime-only.
- **Rig matching:** a custom Template rig must mirror the vanilla skeleton it stands in for — same bone hierarchy
  + names (e.g. `Dummy_Root → Base`), a generic **Avatar**, and the mesh's **bindpose** — or you get an earlier
  `IndexOutOfRangeException` (the animation system indexes bones that don't exist). Matching it clears that and
  leaves only the registration gap above.
- **ilspycmd gotcha:** the SDK DLLs live under `Assets/[Amplitude.Mercury]/Plugins/AnyCPU/` — the **square
  brackets are a glob wildcard**, so ilspycmd reports "file does not exist." Copy the DLLs to a bracket-free
  folder first, then `ilspycmd <dll> -t <FullTypeName>`.

---

## Proof-of-concept plugin

A buildable scaffold exists (modeled on shakee's `FameByScoring` project):
- **`.csproj`** — `net471`, SDK-style, references `BepInEx.dll` + `0Harmony.dll` (from `BepInEx\core\`) +
  `UnityEngine.CoreModule` + `Amplitude.Mercury.Animation/.Data` + `Amplitude.Framework` (from a local
  `References\` folder). Builds with just the .NET SDK via `Microsoft.NETFramework.ReferenceAssemblies`.
- **`Plugin.cs`** — `[BepInPlugin]` + `BaseUnityPlugin` + `Awake()` → `new Harmony(GUID).PatchAll()`.
- **`AnimationManagerPatch.cs`** — postfix on `AnimationManager.AnimationResolveDependencies`, logs that the hook
  fired + the live `MeshCollections` count (proves runtime registry access), with the
  `RegisterMeshCollection()`/`Register()` injection point marked.
- Drop the built DLL in `<Humankind>\BepInEx\plugins\`; check `BepInEx\LogOutput.log` for the `[PoC]` lines.

If that postfix prints a non-zero count in-game, the whole approach is **proven running** — live, writable access
to the registry the engine refuses to expose to data mods.

---

*Environment: BepInEx 5 (Unity Mono), HarmonyX, .NET `net471`. Humankind is a managed-Mono game, so Harmony
patches the methods directly — the easy case.*
