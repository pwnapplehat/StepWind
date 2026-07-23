---
name: stepwind-safety-net
description: >-
  Uses StepWind's MCP tools (stepwind_*) as a local file safety net: checkpoint files before risky
  or bulk edits, diff what an edit actually changed, restore earlier versions of overwritten or
  deleted files, and undo accidental file moves or renames. Use before large refactors, code-mods,
  regex replaces across files, config/lockfile rewrites, or file deletions; after an edit produced
  unexpected results; and whenever the user says a file was lost, overwritten, corrupted, broken
  by an edit, or asks to roll back, recover, or undo file changes.
---

# StepWind safety net

StepWind keeps automatic version history for the user's protected folders and a machine-wide
timeline of file operations. Its MCP tools are read + additive only — nothing here can delete
history or overwrite a file — so prefer using them over guessing.

## Before a risky edit

1. `stepwind_checkpoint_file` on each file about to change (relative or absolute path). This is a
   free, additive no-op if the file is already captured.
2. Make the edit.
3. `stepwind_diff_versions` with oldSelector=`latest:<path>`, newSelector=`current:<path>` to see
   exactly what changed on disk. Judge the edit from the real diff, not from memory.

Checkpoint first whenever an edit is large, generated, cross-file, or hard to reverse.

## Recover a bad or lost change

- See what a file used to contain: `stepwind_get_file_history <relativePath>`, then
  `stepwind_read_version <versionId>`.
- Bring a version back: `stepwind_restore_version <versionId>` — the restored copy is written
  ALONGSIDE the current file, never over it. Move it into place only if the user wants replacement.
- Deleted files work the same way: history survives deletion; restore recreates the content.

## Undo file moves and renames

`stepwind_list_timeline` lists recent operations across all drives, naming the responsible
process. Entries with `Reversible: true` can be undone via `stepwind_undo_operation` (one) or
`stepwind_undo_operations` (bulk). An undo is refused if the original location is now occupied.

## Finding paths

Most tools take store-relative paths like `Project/src/main.cs`. Discover them with
`stepwind_browse` (tree listing; add `query` for a recursive name search) or
`stepwind_recent_files` (recently changed protected files, newest first).

## Selector forms

- Exact version: `<relativePath>|<ticks>` (a VersionId from history or checkpoint results)
- Most recent saved version: `latest:<relativePath>`
- The live file on disk right now: `current:<relativePath>`

## Boundaries

- Content history exists only for folders the user chose to protect. When unsure, check
  `stepwind_get_status` / `stepwind_list_protected_folders` first. If an important folder isn't
  protected, tell the user to add it in the StepWind app — agents cannot change protection.
- The machine-wide timeline records move/rename/delete/create events, not file contents.
- Restores and undos never overwrite existing files; occupied destinations are refused.
