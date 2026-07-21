using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using StepWind.Core.Ipc;

namespace StepWind.Mcp.Tools;

/// <summary>
/// The AI/MCP surface, exposed to coding agents (Cursor, Claude, etc.) over stdio. Deliberately
/// read-only + additive: nothing here can delete history or change settings (that stays a human
/// decision in the StepWind GUI) — an agent gets the time machine, not the shredder.
///
/// The intended loop for an agent about to make a risky change: <see cref="CheckpointFile"/>,
/// make the edit, <see cref="DiffVersions"/> ("latest:path" vs "current:path") to see exactly
/// what changed, and <see cref="RestoreVersion"/> to undo if it went wrong — a restore always
/// lands beside the current file, so it can never destroy work by overwriting it.
/// </summary>
[McpServerToolType]
public static class StepWindTools
{
    // ── Read: protection status, machine-wide timeline, protected folders, browse/search ──

    [McpServerTool(Name = "stepwind_get_status", ReadOnly = true, Destructive = false),
     Description(
        "Current StepWind protection status: which folders are protected, how many versions " +
        "are stored, and whether the whole-machine flight recorder and encryption are on. Call " +
        "this first to check whether a path is actually protected before relying on the other " +
        "StepWind tools for it — unprotected paths have no version history, only (at best) " +
        "move/rename/delete entries in the timeline.")]
    public static async Task<string> GetStatus(StepWindGateway gateway, CancellationToken cancellationToken)
        => await gateway.CallAsync(IpcCommand.GetStatus, ct: cancellationToken);

    [McpServerTool(Name = "stepwind_list_timeline", ReadOnly = true, Destructive = false),
     Description(
        "Recent file operations (create/modify/move/rename/delete) across ALL drives, recorded " +
        "by StepWind's whole-machine flight recorder — not limited to protected folders. Each " +
        "entry names the process that performed it. Move/Rename entries with Reversible=true " +
        "can be undone with stepwind_undo_operation using their OperationId.")]
    public static async Task<string> ListTimeline(
        StepWindGateway gateway,
        [Description("Max entries to return, most recent first.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        string json = await gateway.CallAsync(IpcCommand.GetTimeline, limit: limit, ct: cancellationToken);
        return json == "[]" ? "No file operations recorded yet (or the flight recorder is off — check stepwind_get_status)." : json;
    }

    [McpServerTool(Name = "stepwind_list_protected_folders", ReadOnly = true, Destructive = false),
     Description(
        "Lists the folders StepWind is protecting with continuous version history, plus " +
        "related settings (encryption, auto-update). Files outside these folders have no " +
        "saved content versions — only the machine-wide timeline covers them, and only for " +
        "move/rename/delete, never content changes.")]
    public static async Task<string> ListProtectedFolders(StepWindGateway gateway, CancellationToken cancellationToken)
        => await gateway.CallAsync(IpcCommand.GetSettings, ct: cancellationToken);

    [McpServerTool(Name = "stepwind_browse", ReadOnly = true, Destructive = false),
     Description(
        "Browses the version store like a file tree, or searches it by name. Without 'query': " +
        "lists the immediate subfolders and files under 'path' (each with file/version counts) " +
        "— pass an empty path to list the top-level protected folders. With 'query': " +
        "recursively searches file names containing that text under 'path' (or everywhere, if " +
        "path is empty). Use this to find a file's exact relative path before calling the other " +
        "tools, which all expect that relative path (e.g. 'Project/src/main.cs').")]
    public static async Task<string> Browse(
        StepWindGateway gateway,
        [Description("Folder to list/search under, e.g. 'Project/src'. Empty = the protected-folders root.")] string path = "",
        [Description("Optional: recursively search file names containing this text under 'path'.")] string? query = null,
        CancellationToken cancellationToken = default)
        => await gateway.CallAsync(IpcCommand.BrowseVersions, arg1: path, arg2: query, ct: cancellationToken);

    [McpServerTool(Name = "stepwind_get_file_history", ReadOnly = true, Destructive = false),
     Description(
        "Lists every saved version of one file, newest first — timestamp, size in bytes, and " +
        "why it was captured (change/checkpoint/baseline/catch-up). Use the relative path shown " +
        "by stepwind_browse. Pass a result's VersionId to stepwind_read_version or " +
        "stepwind_restore_version.")]
    public static async Task<string> GetFileHistory(
        StepWindGateway gateway,
        [Description("Relative path within a protected folder, e.g. 'Project/src/main.cs'.")] string relativePath,
        CancellationToken cancellationToken)
    {
        string json = await gateway.CallAsync(IpcCommand.GetHistory, arg1: relativePath, ct: cancellationToken);
        return json == "[]" ? $"No saved versions for '{relativePath}'. Check the path with stepwind_browse, or it may not be in a protected folder." : json;
    }

    // ── Read content + diff — the core "what did I just change" workflow ──────────────────

    [McpServerTool(Name = "stepwind_read_version", ReadOnly = true, Destructive = false),
     Description(
        "Reads the text content of one version of a protected file — or the live content on " +
        "disk right now. Selector forms: 'relativePath|ticks' (an exact VersionId from " +
        "stepwind_get_file_history), 'latest:relativePath' (the most recently saved version), " +
        "or 'current:relativePath' (what's on disk right now — which may be newer than any " +
        "saved version if nothing has captured it yet). Binary files and files over 4 MB report " +
        "their size but not content.")]
    public static async Task<string> ReadVersion(
        StepWindGateway gateway,
        [Description("A selector: 'relativePath|ticks', 'latest:relativePath', or 'current:relativePath'.")] string selector,
        CancellationToken cancellationToken)
    {
        string json = await gateway.CallAsync(IpcCommand.GetVersionContent, arg1: selector, ct: cancellationToken);
        ContentResult r = Deserialize<ContentResult>(json);
        if (r.IsBinary)
        {
            return $"{r.Label}\n({r.Size:N0} bytes, binary — content not shown)";
        }

        if (r.Truncated)
        {
            return $"{r.Label}\n({r.Size:N0} bytes — too large to read in full; content not shown)";
        }

        return $"{r.Label}\n---\n{r.Content}";
    }

    [McpServerTool(Name = "stepwind_diff_versions", ReadOnly = true, Destructive = false),
     Description(
        "Unified diff (same format as 'git diff') between two versions of the SAME file. Use " +
        "oldSelector='latest:path' and newSelector='current:path' to see exactly what YOU (the " +
        "agent) have changed since StepWind's last saved checkpoint — call this after editing a " +
        "file whenever you're unsure what actually changed, or before deciding whether to keep " +
        "an edit. Selector forms are the same as stepwind_read_version's.")]
    public static async Task<string> DiffVersions(
        StepWindGateway gateway,
        [Description("The 'before' selector, e.g. 'latest:Project/src/main.cs'.")] string oldSelector,
        [Description("The 'after' selector, e.g. 'current:Project/src/main.cs'.")] string newSelector,
        CancellationToken cancellationToken)
    {
        string json = await gateway.CallAsync(IpcCommand.DiffVersions, arg1: oldSelector, arg2: newSelector, ct: cancellationToken);
        DiffResult r = Deserialize<DiffResult>(json);
        return $"--- {r.OldLabel} ({r.OldSize:N0} bytes)\n+++ {r.NewLabel} ({r.NewSize:N0} bytes)\n\n{r.Diff}";
    }

    // ── Actions: checkpoint, restore, undo — all additive or non-destructive by design ─────

    [McpServerTool(Name = "stepwind_checkpoint_file", ReadOnly = false, Destructive = false),
     Description(
        "Forces an immediate saved version of a file RIGHT NOW, instead of waiting for " +
        "StepWind's automatic ~2-second capture. Call this BEFORE making a risky or large edit " +
        "so there's a known-good point to diff against or restore to afterward. Purely " +
        "additive — it only ever adds a version, never removes or overwrites anything, and is " +
        "a safe no-op if the content already matches the most recent saved version.")]
    public static async Task<string> CheckpointFile(
        StepWindGateway gateway,
        [Description("Relative path (e.g. 'Project/src/main.cs') or absolute path of the file to checkpoint.")] string path,
        CancellationToken cancellationToken)
    {
        string json = await gateway.CallAsync(IpcCommand.CaptureNow, arg1: path, ct: cancellationToken);
        VersionEntry v = Deserialize<VersionEntry>(json);
        return $"Checkpointed {v.RelativePath} at {v.CapturedUtc:yyyy-MM-dd HH:mm:ss} UTC " +
               $"({v.Size:N0} bytes, {v.Reason}). VersionId: {v.VersionId}";
    }

    [McpServerTool(Name = "stepwind_restore_version", ReadOnly = false, Destructive = false),
     Description(
        "Restores a saved version of a file to disk. NEVER overwrites: if the destination " +
        "already exists, the restored file is written ALONGSIDE it with a '(restored ...)' " +
        "suffix in the name, so restoring can never destroy whatever is currently there — you " +
        "(or the user) decide whether to replace the current file with the restored one " +
        "afterward. Use the VersionId from stepwind_get_file_history or stepwind_checkpoint_file.")]
    public static async Task<string> RestoreVersion(
        StepWindGateway gateway,
        [Description("The VersionId to restore, e.g. 'Project/src/main.cs|638000000000000000'.")] string versionId,
        CancellationToken cancellationToken)
    {
        string json = await gateway.CallAsync(IpcCommand.RestoreVersion, arg1: versionId, ct: cancellationToken);
        using JsonDocument doc = JsonDocument.Parse(json);
        string restoredPath = doc.RootElement.GetProperty("RestoredPath").GetString() ?? "(unknown path)";
        return $"Restored to: {restoredPath}\n(this is a NEW file next to the original — nothing was overwritten; " +
               "move it into place yourself if you want it to replace the current file)";
    }

    [McpServerTool(Name = "stepwind_undo_operation", ReadOnly = false, Destructive = false),
     Description(
        "Reverses a move or rename from stepwind_list_timeline in one step, moving the file or " +
        "folder back to where it was. Only works for entries with Reversible=true, and only if " +
        "the original path is still free (it refuses rather than overwriting something new " +
        "there). Deletes can't be undone this way — use stepwind_get_file_history + " +
        "stepwind_restore_version to recover a deleted file's last saved content instead.")]
    public static async Task<string> UndoOperation(
        StepWindGateway gateway,
        [Description("The OperationId from a stepwind_list_timeline entry.")] string operationId,
        CancellationToken cancellationToken)
    {
        string json = await gateway.CallAsync(IpcCommand.ReverseOperation, arg1: operationId, ct: cancellationToken);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("Message", out JsonElement msg) ? msg.GetString() ?? "Reversed." : "Reversed.";
    }

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidOperationException($"StepWind returned an empty {typeof(T).Name}.");
}
