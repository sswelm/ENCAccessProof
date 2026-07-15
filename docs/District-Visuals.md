# District Visuals — replacing a district's on-map model

**Status: investigated & judged feasible (2026-07-15); first experiment in progress.**

This is the second injection axis, alongside units. The goal: let a pack replace a **district's** on-map
building with a custom static 3D model — e.g. give ENC's *Stone Quarry* a real quarry model instead of the
shared common-district visual it currently borrows.

## What a district's visual actually is

A district (a `ConstructibleCommonExtensionDefinition`, e.g. `Villages_StoneQuarry`) ships **no geometry of
its own**. Its entire on-map appearance comes from one named handle:

```yaml
ConstructibleVisualAffinity:
  serializableElementName: DistrictVisualAffinity_Base_LuxuryExtractor
```

That `DistrictVisualAffinity_*` string is the **district-side analogue of a unit's `pawnDescription`** — a
slot name the game resolves to geometry at runtime. The affinity → geometry definition lives in the *game
bundle*, not the mod (a mod's `ConstructibleVisualAffinityDefinition.asset` is an empty marker). So
"represented using a common district" simply means the district borrows a shared visual by name — exactly
like a unit borrowing a donor mesh.

Note the affinity is **shared**: several districts point at `LuxuryExtractor`. Repointing must therefore be
scoped to the *specific district* (match on the district identity), not the affinity string, or every
district using that visual changes with it.

## The runtime pipeline (decompiled)

```
district def ConstructibleVisualAffinity  ("DistrictVisualAffinity_Base_LuxuryExtractor")
  → PresentationDistrict.UpdateLevelBuild()            builds an AssetReferenceRepositoryRequest
                                                       (criteria: affinity + era + DistrictState + health + pollution)
  → PresentationLevelBuildComponent.SetChannel()       GetGuid(request) → an FxEvolverMaterial GUID
  → FxEvolverMaterialDrawer.mesh                        a single Guid, [FakeAssetReference(typeof(FxMesh))]
  → AsynchEnsureMeshLoaded(mesh)
  → Amplitude.Graphics.Fx.FxComponentMeshContentManager   GPU-instanced draw
```

The last stage — `FxComponentMeshContentManager` — is the **same GPU mesh-manager class the unit path uses**
(the one measured in [Vertex-Budget.md](Vertex-Budget.md)). Districts and units draw through the same
GPU mesh-buffer *family*. A district's building is a separate channel from the **terrain feedback**
(faction tint / ground material / hex sculpting, via `IWorldMapFeedbackProvider.AddDistrict`), so a model
swap doesn't touch terrain.

**Simpler than a unit:** a district's geometry is a single *static* `FxMesh` GUID — no skeleton, no
animation clip, no skinning. All the animation machinery that makes units hard is absent here.

## The injection seam

Direct analogue of the unit repoint hook (`UniRepointHook`). Best Harmony targets, narrowing:

1. `PresentationDistrict.UpdateLevelBuild` — rewrite the affinity/request before it's built (district-scoped).
2. `PresentationLevelBuildComponent.SetChannel` — swap the resolved material/mesh GUID at load.
3. `AssetReferenceRepository.GetGuid` — narrowest choke: remap the GUID for a chosen key.

Match on the district's own identity so only the target district changes and the shared visual is left
intact for every other district that borrows it.

## Two open unknowns (both empirical, not more decompiling)

1. **Can the baker emit a static `FxMesh` / `FxEvolverMaterial`?** Today the Factory bakes *unit* assets
   (Skeleton + ClipCollection + mesh collections + atlas). The district channel wants a static `FxMesh`
   plus an evolver wrapper. The geometry half is familiar (mesh collections are already baked); the
   question is whether the Amplitude SDK exposes the static-`FxMesh` asset type directly.
2. **Budget / layer.** The static drawer uses `DrawerMeshLayerIndex = 0`; layer 0 (`Visual`) read *99%
   full* in the live measurement. That may be a different internal index than the global pawn layer, but
   it's the one thing to measure live (Shift+F8) before committing — silent overflow drops geometry (the
   vanished-mesh class of bug; see [Vertex-Budget.md](Vertex-Budget.md)).

## Smallest-first experiment (in progress)

Prove the runtime half before touching the baker: a `SetChannel`/`UpdateLevelBuild` prefix matched on the
**Stone Quarry** district specifically, repointing it at a custom static mesh — **without altering the
shared `LuxuryExtractor` visual** (other districts that borrow it stay unchanged). If a custom mesh renders
on the Stone Quarry tile at all, the whole axis is de-risked.
