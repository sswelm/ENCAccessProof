# Animated Runtime — how an injected model is driven, frame by frame

The runtime companion to [Factory-Manual §16](Factory-Manual.md) (which covers converting a model into Amplitude's
dialect). This documents **what happens after the bake**: how the game's animation system consumes our Skeleton +
ClipCollection and how the plugin steers it. Everything here is grounded in decompiled, behavior-verified engine code
(`Amplitude.Mercury.Animation.dll` — editor bake AND game runtime; decompile with
`ilspycmd -t <TypeName> <dll>`), plus the litmus-rig verification of the composed result.

---

## 1. The cast

**Baked assets (per model, in the mod bundle):**
- `<name>_Skeleton.asset` — `BoneInfos[]`: per bone `Name`, `BindPose` (inverse-bind TRS), `Local` (parent-relative
  rest, derived `parentBind * bindInverse`), `ParentIndex`, `Depth`; plus `skinnedMeshInfos` (the FxMesh geometry with
  per-vertex bone indices, baked by `ImportMeshes`).
- `<name>_Clips.asset` — `ClipEntry[]` (clip guid, `Duration`, `FrameCount`, `BonesCount`, `CurveIndex`) and
  `ClipCurveEntry[]` (per bone: `EncodingFormat`, `BboxMin/Max`, `PoseDataIndex`).
- `<name>_ClipsPoseData.bytes` — the quantized pose stream (see §4).

**Runtime managers:** `AnimationManager` (owns the GPU buffers + the compute passes `CSAnimateFirstPass` /
`CSAnimateSecondPass`, which live in the game's `InstancingAndFx` asset bundle) and `PawnManager` (a `PawnEntry` per
rendered pawn: `SkeletonId`, `ObjectSpace` TRS, `Pose0..Pose8` blend slots, the `BoneRotation0..3` procedural layer).

**The plugin (`UniversalInject`):** a Harmony postfix at registration time (`AnimationLoad`) and one on
`PawnManager.AddPawnEntry` — the per-frame pose write.

## 2. Registration (once per session, at AnimationLoad)

1. The plugin loads each registry model's ClipCollection by GUID and **appends it to the private
   `loadedAnimationClipCollections` array *before* `Apply()` runs** — Apply's builder then bakes our clip into the
   GPU buffers exactly like vanilla content.
2. `Apply()` flattens every collection:
   - `gpuAnimationEntryBuffer[animBase + boneIndex]` — one `GPUAnimationEntry` **per bone per clip** (format, frame
     count, bbox, `StartPoseData`). A clip's runtime **animation id IS its base index** into this array — which is
     why a clip must carry exactly `BonesCount` curve entries in skeleton bone order (the bake guarantees it).
   - `gpuSkeletonBoneEntiesBuffer` — per bone: `Local`, `InverseBindPose`, globalized `ParentIndex`, `Depth`.
3. The plugin resolves our clip's id via `GetAnimationId(clipGuid)` and captures `GetAnimationDuration(id)` — needed
   because **pose time is NORMALIZED** (§3).
4. Each skeleton's runtime `SkeletonId` (its GPU slot, assigned during Apply) is captured for the pose hook.

## 3. The per-frame drive (the pose hook)

Every frame the game writes each pawn's `PawnEntry`; our postfix rewrites it for injected models:

- **Match & force:** pawns are matched by **PawnDescriptorId** (learned from the first correct pawn — NOT by
  SkeletonId, which differs across instances of the same unit type), and `entry.SkeletonId` is **forced to ours**.
- **Pose0 = our clip:** `AnimationId = animId`, `Weight = 1`,
  `Time = seconds / clipDuration` — the sampler computes `frame = (FrameCount-1) * Repeat(Time, 1)`, so feeding raw
  seconds plays `duration×` too fast. **Pose1..8 weights are zeroed — but never ALL poses**: the blender divides by
  `sumWeight`, and an all-zero pawn is `NaN` = invisible.
- **Which Time** comes from the model's behavior (`ComputePoseTime`): continuous loop (`Time.time/dur`), fire-once
  (rest at 0; one 0→1 pass, per-instance-matched to the nearest active fire by render position), or deploy-on-stop
  (a per-unit ramped hold, driven by `ProcessDeployState`'s settle-immune **render-position-delta** movement signal —
  deliberately not `IsAnyPawnMoving`, whose wait-to-idle settle reads as "moving").
- **BoneRotation layer policy:** the game **turns pawns** through `BoneRotation0..3` (each slot:
  `SkeletonBoneIndex`, `AxisIndex`, `Angle`), and vehicle donors also stream **wheel-spin** into it. The plugin
  clears the layer **only for artillery models** (fire/deploy behaviors — the game's aim would twist the barrel) and
  zeroes junk axis-0 slots elsewhere. Note: a slot whose `SkeletonBoneIndex` matches no bone's `LocalIndex` (e.g. the
  `0xFFFFFFFF` sentinel) is a **no-op** — `ApplyBoneRotation` fires only on an exact index match.
- **Runtime extras:** the registry `position` is applied **in the pawn's frame** (planar part rotated by
  `ObjectSpace.Rotation` each frame; z = world-up altitude), and `scale` multiplies `ObjectSpace.Scale`.

## 4. The pose math (decompiled — what actually gets computed)

Per bone, per pose slot (`ApplyPose` → `GetPoseTRS`):

1. `entry = gpuAnimationEntryBuffer[animationId + boneIndex]`; frame position
   `f = (FrameCount-1) * Repeat(Time,1)`; the two neighboring frames are decoded, then lerped (translation/scale) and
   fast-slerped (rotation).
2. **Decode by `EncodingFormat`** (all channels 16-bit quantized):
   - **`Rotation`** (the target format — bbox all zero): quaternions only, **pair-packed** (2 frames per 3 uints;
     oct-encoded direction + a `sqrt(1-w)` word); **translation is forced to zero** — the bone sits exactly at its
     rest offset.
   - **`RotationTranslation`**: 3 uints/frame — quat in the low 16 bits, translation in the high 16 bits,
     **normalized into the bone's `BboxMin..BboxMax`**.
   - **`RotationTranslationScale`**: + a uniform-scale word. **`Fixe`**: a single static frame.
   - The bake picks per bone: translation range within ±**0.01** (`MinTranslationToBeEncoded`) of the rest ⇒
     rotation-only.
3. `local = TRS.Mul(BoneInfos.Local, decodedPose)` — pose data is stored **relative to the rest** (the bake sampled
   `Local.Inverse * animatorLocal` through a real Unity Animator on the skeleton prefab), so this reconstructs the
   animated local transform.
4. Weighted accumulation across the pose slots (quaternion hemisphere-corrected), normalized by `sumWeight`; then the
   BoneRotation layer multiplies in.
5. **Hierarchy composition** (`GetBoneTRS`): walk the `ParentIndex` chain multiplying locals — bounded by
   `MaxBoneDepth = 15` — then apply `ObjectSpace`. Skinning uses `InverseBindPose` against the composed world.

**The contracts that fall out of this math** (and that §16's conversion enforces):
- **Rotation-only clips** — translations are dropped (`Rotation` format) or bbox-quantized; a rig whose animation
  *moves* bones (location keys) cannot survive as-is.
- **Uniform scale ≈ 1 everywhere** — `TRS.Scale` is a single float; a file-scale "sandwich" (0.01 bindposes + ×100
  root) degrades composition, worst on deep chains.
- **Parents must sort before children** — the Skeleton bake **sorts bones alphabetically**
  (`BuildBoneEntry.Compare`: roots first, then `string.Compare`); consumers assume topological array order. Hence
  the conversion's `b###_` rename.
- **Depth ≤ 15** — no-op root bones are collapsed to preserve budget.

## 4b. State-driven playback facts (Phase 2, 2026-07-19 — decompiled + experimentally proven)

- **The full pawn array uploads to the GPU EVERY FRAME**: `PawnManager.DoComputation()` runs per evolve pass and does
  a whole-array `pawnEntriesBuffer.SetData(pawnEntries)`. There is **no id latching** — every field the pose hook
  writes (AnimationId included) reaches the GPU each frame, so **per-frame `AnimationId` switching on Pose0 is safe**
  and is how the state machine (idle / run / after-move) is implemented.
- **The secondary pose slots (Pose1/Pose2) misbehave** in the GPU pass: driving states by weight-switching constant-id
  slots rendered the pawn **invisible while moving** (most plausibly a garbage id sampling an arbitrary buffer entry —
  a scale-0 entry collapses the mesh to a point). The C# mirror (`GetLocalBoneTRS`/`ApplyPose`) is slot-agnostic, so
  the divergence lives in the compute shader; the state machine simply avoids the secondary slots.
- **Rotation-format clip data cannot explode a mesh**: `GetPoseTRS` forces translation to zero and scale to 1 for
  `Rotation`-encoded curves. This yields a sharp diagnostic dichotomy: a pawn rendering **invisible** ⇒ a wrong
  *animation id* (sampling foreign entries); a pawn rendering **frozen** ⇒ *constant clip data* (see the
  frozen-runner bug in Factory-Manual §16: Blender's bone rename syncs fcurve paths only for the ASSIGNED action, so
  dormant state-role clips exported as statues until patched explicitly).
- **Byte-level clip forensics**: the `_Clips*PoseData.bytes` layout is per-curve blocks (Rotation format:
  `ceil(frames/2) × 3` uints per curve). A healthy clip shows a MIX of varying and constant curve blocks (animated
  vs still bones); ALL-constant blocks = a frozen bake. This check runs from PowerShell in seconds and settled in
  minutes what in-game observation could not.

## 5. Multi-instance & lifecycle notes

- Same-unit instances get **different SkeletonIds** — hence descriptor keying + SkeletonId forcing (a second
  instance left on a vanilla skeleton renders mis-skinned).
- **Save-load spawn race** (models borrowing a donor's animated sub-part, e.g. a rotor): fixed by re-running the
  game's own `PresentationUnit.UpdatePawns` shortly after load (`respawnAfterLoad`, per model).
- A corrupted skeleton state can disrupt **more than the pose**: while the soldier's rig was broken, the unit's
  projectile visual also vanished (attack sim + audio unaffected); it returned with the clean rig.

## 6. Verifying the whole chain

- **Litmus rig** (`Tools/make_litmus.py`): a 12-deep chain of colored cubes through the full pipeline — renders as a
  straight chain in-game when everything above holds. The fastest "is it the pipeline or the model?" answer: one
  launch.
- **Baked-asset greps** (plain YAML): `<name>_Skeleton.asset` — every `Scale:` must be 1 and every bone's
  `ParentIndex` smaller than its own index; `<name>_Clips.asset` — `EncodingFormat: 1` with zero bboxes on every
  bone is the healthy rotation-only profile.
- **Plugin logs**: `[Uni]` registration lines (clip injected, animId + duration), the pose-hook one-shot, and the
  temporary `[Uni][facing]` dump (ObjectSpace rotation + all BoneRotation slots, 3s period).
- **Decompile refresh**: editor-bake code = the SDK's `Amplitude.Mercury.Animation.dll` (Unity project, AnyCPU
  plugins folder); runtime code = the same-named DLL in `Humankind_Data/Managed`. `ilspycmd -t <type>` suffices. The
  compute shaders themselves live in the `InstancingAndFx` bundle (not extracted — the C# mirrors
  `GetBoneTRS`/`ApplyPose` have matched observed behavior everywhere tested, litmus included).
