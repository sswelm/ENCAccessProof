# HAF multi-mod — shipping your own pack

The runtime (this plugin) is a **Humankind Asset Framework** host: it loads not just ENC's registry but **any number of packs**,
so a modder augments their own units with a custom 3D model, texture, and sound **without touching ENC's files or code**. You
ship a config file plus your assets; the runtime discovers, merges, and reports.

This is the *loader* contract. For how to **bake** a model into the assets a pack references, see [`Factory-Manual.md`](Factory-Manual.md).

## What a pack is

A pack is one JSON file with the schema wrapper around the familiar `models` array:

```json
{
  "schemaVersion": 1,
  "modId": "yourmod",
  "dependsOn": [],
  "loadAfter": ["enc"],
  "overrides": [],
  "models": [ /* the same model entries the Factory writes — see Factory-Manual.md */ ]
}
```

A copy-ready starting point is [`haf-pack.example.json`](haf-pack.example.json).

| Key | Meaning |
|---|---|
| `schemaVersion` | The HAF schema version this file targets. Currently `1`. Evolves **additively** — new keys are added, old files keep loading. |
| `modId` | Your pack's unique id. Keep it stable; it's how you're named in the load report and how others depend on you. |
| `models` | Your model entries — identical to what the Factory bakes. Runtime-only entries (a retexture/tint/sound with no baked mesh) need no GUIDs. |
| `dependsOn` | modIds your pack **requires**. A missing dependency means your pack is **skipped** (loudly, in the log + report). Also orders you after them. |
| `loadAfter` | modIds your pack must load **after** (soft: an absent modId is ignored, your pack still loads). |
| `overrides` | explicit `{modId, pawnDescription}` replacements: your entry **replaces** that pack's entry on that pawn. Declared = consensual; without it, the clash is a conflict and the first-loaded entry wins. |

**Backward compatible:** a legacy bare `{ "models": [...] }` with no wrapper still loads — it just gets default metadata
(`modId` = the filename, `schemaVersion` = 0). ENC's own `enc_models.json` is treated as the base pack `enc`.

## Where it goes

- **ENC's base registry:** `BepInEx/config/enc_models.json` (loaded first, as `modId` `enc`).
- **Your pack:** drop your `*.json` in **`BepInEx/config/haf_packs/`**. Every `.json` there is discovered, sorted by filename.

> **Assets:** baked mesh/skeleton/atlas resolve by Amplitude **GUID**, so they work from any mod's bundle the game loads —
> the runtime doesn't care which mod shipped them. *File-based* assets (custom WAVs, PNG skins) currently resolve from the
> shared `enc_sounds/` and `enc_skins/` folders by filename; per-pack asset folders are a planned refinement.

## How packs merge (resolution ENFORCED since 2026-07-19)

1. **Discovery** — the base registry, then every `haf_packs/*.json` (filename order).
2. **Duplicate `modId`s** — the first file keeps the id; later same-id packs are **skipped** (log + report).
3. **`dependsOn` validation** — a pack whose dependency isn't loaded is **skipped** (iterated: skipping one pack can
   invalidate a pack that depended on it).
4. **Load order** — a **stable topological sort** over `dependsOn` + `loadAfter`: with no declared constraints the
   order is exactly the old base-first + filename order; a dependency **cycle** is broken loudly by falling back to
   file order for its members.
5. **Merge** — all `models` are combined. A model's identity is its **`pawnDescription`** (the physical pawn slot — two skins
   can't ride one pawn).
6. **Declared overrides** — an entry whose pack declares `{modId, pawnDescription}` for the current owner **replaces**
   that entry (logged + reported as an override, not a conflict).
7. **Undeclared conflicts** — the **first-loaded pack wins** (so ENC's own units are protected), and it's logged loud.
   *No implicit overrides* — declare it in `overrides` if the replacement is intentional.

**Not built (deliberately):** a `patches` concept — field-level modification of another pack's entry, as opposed to
`overrides`' whole-entry replacement. It would let compatibility packs tweak one knob without duplicating a full model
definition; queued until a real compatibility pack needs it, so its shape is driven by a real use case.

## The load report

Every load writes **`BepInEx/config/haf_load_report.txt`** — the first thing to check after adding your pack:

```
HAF load report  (regenerated every load)
packs=2  models=14  conflicts=0  overrides applied=0

[enc]      schemaVersion=1  models=13  file=enc_models.json
[yourmod]  schemaVersion=1  models=1   file=yourmod.json
    loadAfter: enc
```

If your pack isn't listed, it wasn't discovered (wrong folder, a parse error — check the BepInEx log), **or it was
skipped by resolution** (duplicate `modId`, missing `dependsOn` — the `RESOLUTION` section says which and why). An
`OVERRIDES APPLIED` section lists declared replacements that took effect; a `CONFLICTS` section means two packs are
fighting over a pawn undeclared.
