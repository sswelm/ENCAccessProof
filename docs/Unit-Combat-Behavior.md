# Unit combat behavior — what's data‑driven vs hardcoded

A modding reference for **how a Humankind unit fights and animates** — which behaviors you can set from data, and the handful that are baked into the engine by identity. Written after an afternoon of chasing a "why won't my custom monster charge into melee?" bug down the wrong hole; this is the map that would have saved it.

> **TL;DR.** A unit has **two definition assets** and you must look at both:
> - **`PresentationPawnDefinition`** — the *animation* side (how it looks/moves/fights).
> - **`UnitDefinition` / `UnitClass`** — the *simulation* side (stats, class, descriptors).
>
> **Combat animation lives on the PAWN. Stats/class live on the UNIT.** Almost everything is **data‑driven**. Only **Air/Naval unit type** and the **wild‑animal AI** are true engine hardcodes.

---

## 1. Combat *animation* — `Animation Capability Profile` (pawn def)

On the **`PresentationPawnDefinition`**, in the **Profile** section, is a dropdown: **`Animation Capability Profile`**. This is the single biggest lever for "what kind of thing is this, animation‑wise." Palette:

| Profile | Fights like |
|---|---|
| **Human** | a drilled foot‑soldier (stand‑and‑strike / disciplined melee) |
| **Human Mounted Fighter / Driver** | cavalry (mounted charge) |
| **Human Servant** | non‑combat human |
| **Animal Fighter** | a **beast — charges/lunges/mauls** into melee |
| **Animal Fighter Mount** | a rider *on* a beast |
| **Mount** | the mount itself |
| **Chariot Human Fighter/Driver, Chariot Mount, Chariot** | chariot crew/vehicle |
| **Boat / Plane** | naval / air animation sets |
| **Inanimate Object / Missile** | props, projectiles |
| **Custom** | **hand‑pick** the `Animation Capabilities` grid yourself |

Underneath is the **`Animation Capabilities`** grid — individual toggles: `Move, Strafe, Run, Rotate, Attack, Meta State, Charge, Charge Run, Counter, Be Countered, Protect, Prepared Attack Loop, Hit, Death, Idle, Idle Alt, Deployment Idle Alt, Disciplined Variation`. A preset profile enables a sensible set; **`Custom`** lets you turn them on/off individually. **`Charge` / `Charge Run` are the "advance into melee" animations.**

**Decompile note:** the profile ∈ {`Animal Fighter`, `Animal Fighter Mount`} sets the presentation `PresentationPawn.IsAnimal`, which swaps in the `Cavalry1Animal*` charge‑curve constants (animals charge like cavalry, with a beast‑shaped curve). It's a *curve variant*, entirely data‑selected.

**The profile also governs GROUP combat coordination (tested in‑game).** It's not only which *animation* plays — it changes how the whole stack fights:
- **`Animal Fighter`** → only the beasts *nearest* the target engage; the rest hang back. Animals were only ever given solo / loose‑pack behavior, never coordinated group melee.
- **`Human`** → the **entire stack advances and moves in for the kill** as a disciplined group. Switching a custom monster's pawn to `Human` makes all its units close in instead of just the closest.

So the profile choice is a trade‑off, not just cosmetic: `Animal Fighter` gives the beast‑charge look but weaker group engagement; `Human` gives coordinated all‑in group combat but the drilled‑soldier animation. (Related coordination knobs on the `PresentationUnitDefinition`: `Same Row Attack`, `Coordinate Attack Movement`, `Use Disciplined Variation` — these tune staggering/formation once a profile is chosen.)

*Open thread:* `Human` unlocks pairing with the `Melee` descriptor, but the descriptor alone was **not sufficient** to get full group engagement — the profile is doing the heavy lifting.

---

## 2. Combat *choreography* — advance vs fire‑in‑place (unit/pawn data)

Whether a unit **moves up to strike** or **attacks from where it stands** is chosen by `PresentationChoreographyController` from data:

```
ChoreographyOverride (on PresentationUnitDefinition):
  Cavalry → charge choreography
  Ranged  → fire in place
  Melee   → move-up-and-strike
  None    → auto-detect:
      IsRangedUnit?  → Ranged        // AttackRange > 1  OR  in water  OR  has Effect_Unit_HasRangedAttack
      IsCavalryUnit? → Cavalry       // mounted SubPawnComposition  OR  ChoreographyOverride == Cavalry
      else           → Melee
```

Plus the pawn def's **`Has Range Weapon`** toggle (part of `IsRangedPawn = HasRangeWeapon && IsRangedUnit`). A ranged pawn spawns a projectile sequence instead of advancing into melee.

**Recipe:** want a melee charger? Give it `AttackRange = 1`, no range weapon, a non‑mounted composition — or just force **`ChoreographyOverride = Melee`**.

---

## 3. The real hardcodes — what you **cannot** data‑drive

| Behavior | Gated by | Modding reality |
|---|---|---|
| **Air unit** (`IsAir`/`IsAerial`) | the unit's DEFINITION must **be an `AirUnitDefinition`** (a class, not a tag/descriptor) | You must declare the air definition class; no tag makes a land unit fly. |
| **Naval visual** (`IsNaval`) | `def is NavalUnitDefinition/NavalTransportDefinition` (or embarked state) | True naval unit needs the naval definition class. |
| **Wild‑animal combat AI** (`AttackOnSight`) | `ArmyFlags.IsAnimal`, stamped **only** by `AnimalMinorFactionSpawner` — never by a unit definition | A **player‑built** unit can *never* be a "wild animal" (and shouldn't be — it'd auto‑attack everyone). This is the one thing about "animal" that's genuinely unreachable — but it's the **AI**, not the charge animation. |

Everything else in the `Is<Type>` family is data/context, not a unit‑type identity gate:

| Check | Driven by | Note |
|---|---|---|
| `IsRangedUnit` / `IsRangedPawn` | `AttackRange > 1` / water / `Effect_Unit_HasRangedAttack` / `HasRangeWeapon` | data |
| `IsCavalryUnit` / `IsCavalryChoreography` | mounted `SubPawnComposition` or `ChoreographyOverride == Cavalry` | data |
| `IsAnimal` (presentation) | `AnimationCapabilityProfile ∈ {Animal Fighter, Animal Fighter Mount}` | data (charge‑curve variant) |
| `IsSettler` | the `Settler` tag‑ability | data (UI/cursor only) |
| `IsSiege` / `IsSiegeDefender` | the *battle* is a city siege (`battle.Siege != null`) | context, not a unit type |
| `IsNavalBattle` | both combatants in water | positional |
| `IsFortification` | the district/wall battle entity | structural, not a unit |
| `IsMeleeAttackTransitionValid` | pathfinding (target reachable) | benign |
| "siege unit" (breach) | unit `Family == Siege` + `CanBreach` tag | data |

---

## 4. The debugging trap (learn from ours)

We spent an afternoon convinced the melee‑charge was gated by the engine **checking the `Effect_UnitPrototype_Animal` descriptor by identity** — because a *content‑identical clone* of it didn't charge while the original did.

**That was wrong.** The charge is driven by the **pawn's `Animation Capability Profile = Animal Fighter`**, not the unit‑side prototype descriptor. The Animal‑vs‑`Melee` descriptor swap was a **confound** (the `Melee` prototype nudged `AttackRange`/detection so the unit read as non‑charging), while the constant pawn profile was doing the real work.

**The lesson:** when a behavior seems tied to one asset, **check the *other* side of the unit** — animation lives on the `PresentationPawnDefinition`, stats/class on the `UnitDefinition`. And the misleadingly‑named `Effect_UnitPrototype_Melee` is a **human‑military stat block**, not "the melee charger."

**Confirmed red herrings (don't re‑chase):** the Nomad tag, `LandSiegeWorksNet`/SiegeWorks (cavalry carries it *and* charges), vision range, descriptor *contents*.

---

## 5. Quick recipes

- **Custom melee monster that charges:** pawn `Animation Capability Profile = Animal Fighter` (with `Charge` enabled); unit `AttackRange = 1`, no range weapon.
- **Custom human melee soldier:** profile `Human`; `AttackRange = 1`.
- **Add a combat bonus** (e.g. bonus vs air/gunships) *without* touching behavior: add the standalone `BattleAbility_StrengthFromTargetClass*` reference to the unit's ability list — pure data, safe to make custom.
- **Don't** clone a specialized `Effect_UnitPrototype_*` and expect its behavior — reuse the base‑game one, or (better) set the behavior via the pawn profile + choreography fields above.

*Investigation method: decompile with `ilspycmd` (`~/.dotnet/tools/ilspycmd`) against `.../Humankind_Data/Managed/Assembly-CSharp.dll` (presentation) and `Amplitude.Mercury.Firstpass.dll` (simulation).*
