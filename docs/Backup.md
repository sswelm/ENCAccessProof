# Backup &amp; Restore

**Menu:** `Tools ▸ HAF ▸ Backup and Restore` (window title *Backup &amp; Restore*).

A safety net for everything the ENCReload git repo does **not** track. That repo versions only
`Assets/Databases`; the rest of the working set — the editor tooling, the licensed source models, the baked
assets, the `Tools/` scripts, and the **live BepInEx runtime config the plugin reads** — otherwise lives on
disk with no version control. This window snapshots all of it to a timestamped folder on `D:`.

## What it captures

Each group is an independent toggle with a live size readout:

| Group | Source |
| --- | --- |
| Editor scripts | `Assets/Scripts/Editor` |
| Source models | `Assets/FactorySource` (the bake *inputs* — never shipped in the mod) |
| Baked assets | `Assets/Resources` (skeletons, atlases, clip collections, PNGs) |
| ENC Databases | `Assets/Databases` |
| Tools | `Tools/` (Blender rig/convert scripts, `glbconv`) |
| Runtime config | `BepInEx/config/enc_*.json` + `enc_skins/` + `enc_sounds/` (the regenerable `enc_atlas_dump/` is skipped) |

## How a backup is stored

**Back up now** writes a new folder `D:\HAF_Backups\<yyyy-MM-dd_HHmmss>\` (destination is configurable and
remembered). Inside, each source is copied under `<group>/<name>`, alongside a **`manifest.txt`** that records
every source's *original absolute path*, file count, and byte size. Backups are **never overwritten** (each is a
fresh timestamp) and **never auto-deleted**. After copying, the file count is re-verified against the manifest
and reported — a mismatch is flagged loudly.

## Restore — guarded three ways

Restore reads a backup's manifest and copies each source back to its original path, but never at the cost of
current work:

1. **Auto pre-restore snapshot.** Before touching anything, the *current* state of exactly the paths about to be
   overwritten is saved to a `_prerestore_<timestamp>` backup. A wrong restore is always undoable — just restore
   that snapshot.
2. **Additive only.** Files present in the backup overwrite their current versions, but any file you have **added
   since** (not in the backup) is left untouched. New work can't vanish. There is deliberately no destructive
   "mirror/clean" mode.
3. **Explicit confirmation.** A dialog lists exactly which original paths will be written before anything happens,
   and the restored file count is reported afterward. `AssetDatabase.Refresh()` reimports the restored assets.

`Delete` removes a backup folder only (live files untouched), after a confirm.

## Notes

- The window itself lives in the un-tracked `Assets/Scripts/Editor`, so its own **Editor scripts** group backs
  *itself* up — and captures any other unversioned editor-tool edits along with it.
- Unity `.meta` files are copied with their assets, so restored assets keep their GUIDs/import settings.
- Directory sizes are cached; hit **↻ sizes** to recompute after big changes.
