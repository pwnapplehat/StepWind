using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StepWind.Core.Integration;

/// <summary>Outcome of one install/remove operation, with an honest human-readable message.</summary>
public sealed record McpInstallResult(bool Ok, string Message, string? BackupPath = null);

/// <summary>
/// Installs the StepWind MCP server into AI tools' config files (Cursor, Claude, VS Code, ...)
/// without ever corrupting them. The rules, in order of importance:
///
///  1. NEVER lose user data: existing config is parsed and merged, never overwritten. If a file
///     isn't strict JSON (comments / trailing commas), we REFUSE to auto-edit and tell the user
///     to paste manually — rewriting a JSONC file through a JSON serializer would silently strip
///     their comments.
///  2. Every modification first copies the file to a timestamped backup under LocalAppData.
///  3. Writes are atomic (temp file + rename) so a crash can't leave a half-written config.
///  4. After writing we re-read and re-parse the file; if that somehow fails the backup is
///     restored immediately.
///  5. Idempotent: install twice = update in place; remove when absent = friendly no-op.
/// </summary>
public static class McpInstaller
{
    /// <summary>The server key we write into every client's config.</summary>
    public const string ServerKey = "stepwind";

    /// <summary>Where pre-modification backups go (unelevated-writable, per-user).</summary>
    public static string BackupRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StepWind", "mcp-backups");

    private const int MaxBackupsPerClient = 10;

    /// <summary>
    /// Builds the full target list for this machine and probes each one: does the tool look
    /// installed, and does its config already contain our entry?
    /// </summary>
    public static List<McpClientTarget> DetectAll()
    {
        List<McpClientTarget> targets = BuildTargets();
        foreach (McpClientTarget t in targets)
        {
            t.Installed = Directory.Exists(t.DetectPath) || File.Exists(t.DetectPath) || File.Exists(t.ConfigPath);
            t.Configured = t.Installed && IsConfigured(t);
        }
        return targets;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static List<McpClientTarget> BuildTargets()
    {
        var list = new List<McpClientTarget>
        {
            new()
            {
                Id = "cursor", DisplayName = "Cursor",
                ConfigPath = Path.Combine(Home, ".cursor", "mcp.json"),
                DetectPath = Path.Combine(Home, ".cursor"),
                Format = McpConfigFormat.JsonMcpServers,
                Note = "Picks up changes automatically — no restart needed.",
            },
            BuildClaudeDesktopTarget(),
            new()
            {
                Id = "claude-code", DisplayName = "Claude Code",
                ConfigPath = Path.Combine(Home, ".claude.json"),
                DetectPath = Path.Combine(Home, ".claude"),
                Format = McpConfigFormat.JsonMcpServers,
                Note = "Applies to new Claude Code sessions.",
            },
            BuildAntigravityTarget(),
            new()
            {
                Id = "windsurf", DisplayName = "Windsurf",
                ConfigPath = Path.Combine(Home, ".codeium", "windsurf", "mcp_config.json"),
                DetectPath = Path.Combine(Home, ".codeium", "windsurf"),
                Format = McpConfigFormat.JsonMcpServers,
                Note = "Refresh from Windsurf's MCP panel or restart it.",
            },
            new()
            {
                Id = "vscode", DisplayName = "VS Code (Copilot)",
                ConfigPath = Path.Combine(Roaming, "Code", "User", "mcp.json"),
                DetectPath = Path.Combine(Roaming, "Code", "User"),
                Format = McpConfigFormat.JsonServers,
                Note = "Uses VS Code's own mcp.json ('servers' key).",
            },
            new()
            {
                Id = "cline", DisplayName = "Cline (VS Code extension)",
                ConfigPath = Path.Combine(Roaming, "Code", "User", "globalStorage",
                    "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json"),
                DetectPath = Path.Combine(Roaming, "Code", "User", "globalStorage", "saoudrizwan.claude-dev"),
                Format = McpConfigFormat.JsonMcpServers,
                Note = "Reloads live in Cline's MCP servers panel.",
            },
            new()
            {
                Id = "gemini-cli", DisplayName = "Gemini CLI",
                ConfigPath = Path.Combine(Home, ".gemini", "settings.json"),
                DetectPath = Path.Combine(Home, ".gemini", "settings.json"),
                Format = McpConfigFormat.JsonMcpServers,
                Note = "Shared settings file — StepWind only touches the mcpServers entry.",
            },
            new()
            {
                Id = "codex", DisplayName = "Codex CLI",
                ConfigPath = Path.Combine(Home, ".codex", "config.toml"),
                DetectPath = Path.Combine(Home, ".codex"),
                Format = McpConfigFormat.Toml,
                Note = "TOML config — StepWind adds a [mcp_servers.stepwind] table.",
            },
            new()
            {
                Id = "copilot-cli", DisplayName = "Copilot CLI",
                ConfigPath = Path.Combine(Home, ".copilot", "mcp-config.json"),
                DetectPath = Path.Combine(Home, ".copilot"),
                Format = McpConfigFormat.JsonMcpServers,
                ExtraArrayFields = new Dictionary<string, string[]> { ["tools"] = ["*"] },
                Note = "Applies to new Copilot CLI sessions.",
            },
            new()
            {
                Id = "lmstudio", DisplayName = "LM Studio",
                ConfigPath = Path.Combine(Home, ".lmstudio", "mcp.json"),
                DetectPath = Path.Combine(Home, ".lmstudio"),
                Format = McpConfigFormat.JsonMcpServers,
                Note = "Available to local models that support tool use.",
            },
            new()
            {
                Id = "kiro", DisplayName = "Kiro",
                ConfigPath = Path.Combine(Home, ".kiro", "settings", "mcp.json"),
                DetectPath = Path.Combine(Home, ".kiro"),
                Format = McpConfigFormat.JsonMcpServers,
                Note = "Reloads from Kiro's MCP panel or on restart.",
            },
        };
        return list;
    }

    /// <summary>
    /// Claude Desktop's documented path is %APPDATA%\Claude, but MSIX-packaged installs (the
    /// current claude.ai/download and Store builds) read a VIRTUALIZED copy under
    /// %LOCALAPPDATA%\Packages\Claude_&lt;hash&gt;\LocalCache\Roaming\Claude — writing only to the
    /// documented path would be silently ignored by the app (github.com/anthropics/claude-code
    /// issues #25579 / #26073). If the virtualized dir exists, that's the real one.
    /// </summary>
    private static McpClientTarget BuildClaudeDesktopTarget()
    {
        string classic = Path.Combine(Roaming, "Claude");
        string configDir = classic;
        try
        {
            string packages = Path.Combine(Local, "Packages");
            if (Directory.Exists(packages))
            {
                foreach (string pkg in Directory.GetDirectories(packages, "Claude_*"))
                {
                    string virtualized = Path.Combine(pkg, "LocalCache", "Roaming", "Claude");
                    if (Directory.Exists(virtualized))
                    {
                        configDir = virtualized;
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Enumeration failure (permissions, broken junctions) — fall back to the classic path.
        }

        return new McpClientTarget
        {
            Id = "claude-desktop", DisplayName = "Claude Desktop",
            ConfigPath = Path.Combine(configDir, "claude_desktop_config.json"),
            DetectPath = configDir,
            Format = McpConfigFormat.JsonMcpServers,
            Note = "Restart Claude Desktop to pick it up.",
        };
    }

    /// <summary>
    /// Antigravity 2.0's installer migrates config to ~/.gemini/config and drops a zero-byte
    /// ".migrated" marker there; pre-2.0 installs read ~/.gemini/antigravity. Both the IDE and
    /// the Antigravity CLI share whichever file is active.
    /// </summary>
    private static McpClientTarget BuildAntigravityTarget()
    {
        string migratedDir = Path.Combine(Home, ".gemini", "config");
        string legacyDir = Path.Combine(Home, ".gemini", "antigravity");
        bool migrated = File.Exists(Path.Combine(migratedDir, ".migrated"));
        string dir = migrated ? migratedDir : legacyDir;

        return new McpClientTarget
        {
            Id = "antigravity", DisplayName = "Antigravity",
            ConfigPath = Path.Combine(dir, "mcp_config.json"),
            DetectPath = dir,
            Format = McpConfigFormat.JsonMcpServers,
            Note = "Restart Antigravity (or refresh Manage MCP Servers).",
        };
    }

    // ---------------------------------------------------------------------------------------
    // Read side: is our entry already in this client's config? (Lenient parse is fine here —
    // we're only LOOKING, never rewriting through a lenient parse.)
    // ---------------------------------------------------------------------------------------

    private static bool IsConfigured(McpClientTarget target)
    {
        try
        {
            if (!File.Exists(target.ConfigPath))
            {
                return false;
            }

            string text = File.ReadAllText(target.ConfigPath);
            if (target.Format == McpConfigFormat.Toml)
            {
                (int start, int _) = FindTomlBlock(text.Split('\n'));
                if (start < 0)
                {
                    return false;
                }
                target.ConfiguredCommand = ExtractTomlCommand(text);
                return true;
            }

            JsonNode? root = JsonNode.Parse(text, documentOptions: LenientRead);
            JsonNode? entry = (root as JsonObject)?[RootKey(target)]?[ServerKey];
            if (entry is null)
            {
                return false;
            }
            target.ConfiguredCommand = entry["command"]?.GetValue<string>();
            return true;
        }
        catch (Exception)
        {
            return false; // unreadable/unparseable — treat as not configured; install will explain
        }
    }

    private static readonly JsonDocumentOptions LenientRead = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string RootKey(McpClientTarget target) =>
        target.Format == McpConfigFormat.JsonServers ? "servers" : "mcpServers";

    // ---------------------------------------------------------------------------------------
    // Write side: install / remove. All mutations flow through the same safety pipeline:
    // strict parse -> backup -> atomic write -> verify (restore backup on failure).
    // ---------------------------------------------------------------------------------------

    /// <summary>Adds (or updates) the stepwind entry in this client's config.</summary>
    public static McpInstallResult Install(McpClientTarget target, string serverExePath)
    {
        try
        {
            return target.Format == McpConfigFormat.Toml
                ? MutateToml(target, serverExePath, remove: false)
                : MutateJson(target, serverExePath, remove: false);
        }
        catch (Exception ex)
        {
            return new McpInstallResult(false, $"Could not update {target.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>Removes the stepwind entry from this client's config (no-op if absent).</summary>
    public static McpInstallResult Remove(McpClientTarget target)
    {
        try
        {
            if (!File.Exists(target.ConfigPath))
            {
                return new McpInstallResult(true, $"{target.DisplayName} has no config file — nothing to remove.");
            }
            return target.Format == McpConfigFormat.Toml
                ? MutateToml(target, "", remove: true)
                : MutateJson(target, "", remove: true);
        }
        catch (Exception ex)
        {
            return new McpInstallResult(false, $"Could not update {target.DisplayName}: {ex.Message}");
        }
    }

    private static McpInstallResult MutateJson(McpClientTarget target, string serverExePath, bool remove)
    {
        string rootKey = RootKey(target);
        bool existed = File.Exists(target.ConfigPath);
        string original = existed ? File.ReadAllText(target.ConfigPath) : "";
        bool hadBom = existed && FileStartsWithBom(target.ConfigPath);

        JsonObject root;
        if (string.IsNullOrWhiteSpace(original))
        {
            root = new JsonObject();
        }
        else
        {
            // STRICT parse for anything we intend to rewrite. If the file only parses leniently
            // (comments / trailing commas), rewriting it would silently delete those comments —
            // that is corruption from the user's point of view, so we refuse and say why.
            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(original);
            }
            catch (JsonException strictError)
            {
                bool lenientOk = false;
                try
                {
                    JsonNode.Parse(original, documentOptions: LenientRead);
                    lenientOk = true;
                }
                catch (JsonException) { }

                return new McpInstallResult(false, lenientOk
                    ? $"{target.DisplayName}'s config contains comments or trailing commas, which auto-edit would destroy. Please paste the config manually (use the Copy button below)."
                    : $"{target.DisplayName}'s config file is not valid JSON ({FirstLine(strictError.Message)}). Fix or delete '{target.ConfigPath}' first — StepWind won't touch a file it can't safely parse.");
            }

            if (parsed is not JsonObject obj)
            {
                return new McpInstallResult(false,
                    $"{target.DisplayName}'s config root is not a JSON object — StepWind won't rewrite it. Please add the entry manually.");
            }
            root = obj;
        }

        JsonNode? serversNode = root[rootKey];
        if (serversNode is not null && serversNode is not JsonObject)
        {
            return new McpInstallResult(false,
                $"{target.DisplayName}'s config has an unexpected \"{rootKey}\" value (not an object) — StepWind won't rewrite it. Please add the entry manually.");
        }

        var servers = serversNode as JsonObject;
        if (remove)
        {
            if (servers is null || !servers.ContainsKey(ServerKey))
            {
                return new McpInstallResult(true, $"StepWind wasn't configured in {target.DisplayName} — nothing to remove.");
            }
            servers.Remove(ServerKey);
        }
        else
        {
            if (servers is null)
            {
                servers = new JsonObject();
                root[rootKey] = servers;
            }
            servers[ServerKey] = BuildServerEntry(target, serverExePath);
        }

        string updated = root.ToJsonString(WriteOptions);
        return CommitWithVerify(target, updated, existed, original, hadBom, remove);
    }

    private static JsonObject BuildServerEntry(McpClientTarget target, string serverExePath)
    {
        var entry = new JsonObject();
        if (target.Format == McpConfigFormat.JsonServers)
        {
            entry["type"] = "stdio"; // VS Code's schema wants the transport spelled out
        }
        entry["command"] = serverExePath;
        if (target.ExtraArrayFields is not null)
        {
            foreach ((string key, string[] values) in target.ExtraArrayFields)
            {
                var arr = new JsonArray();
                foreach (string v in values)
                {
                    arr.Add(v);
                }
                entry[key] = arr;
            }
        }
        return entry;
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // ---------------------------------------------------------------------------------------
    // TOML (Codex CLI). We deliberately do NOT parse TOML — we do line-conservative surgery:
    // remove any existing [mcp_servers.stepwind] block (header through the line before the next
    // unrelated [table] header), then append a fresh block at the end. Every other line in the
    // user's file is preserved byte-for-byte.
    // ---------------------------------------------------------------------------------------

    private static McpInstallResult MutateToml(McpClientTarget target, string serverExePath, bool remove)
    {
        bool existed = File.Exists(target.ConfigPath);
        string original = existed ? File.ReadAllText(target.ConfigPath) : "";
        bool hadBom = existed && FileStartsWithBom(target.ConfigPath);

        var lines = new List<string>(original.Length == 0 ? [] : original.Replace("\r\n", "\n").Split('\n'));

        bool found = true;
        bool removedAny = false;
        while (found)
        {
            (int start, int end) = FindTomlBlock(lines.ToArray());
            found = start >= 0;
            if (found)
            {
                lines.RemoveRange(start, end - start);
                removedAny = true;
            }
        }

        if (remove && !removedAny)
        {
            return new McpInstallResult(true, $"StepWind wasn't configured in {target.DisplayName} — nothing to remove.");
        }

        if (!remove)
        {
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }
            if (lines.Count > 0)
            {
                lines.Add(""); // blank separator before our table
            }
            lines.Add($"[mcp_servers.{ServerKey}]");
            lines.Add($"command = {TomlString(serverExePath)}");
        }

        string updated = string.Join(Environment.NewLine, lines).TrimEnd('\r', '\n') + Environment.NewLine;
        return CommitWithVerify(target, updated, existed, original, hadBom, remove);
    }

    /// <summary>
    /// Finds our server's table block: [start, end) line range covering the
    /// [mcp_servers.stepwind] header, its keys, and any [mcp_servers.stepwind.*] subtables.
    /// Returns (-1, -1) if absent. Accepts both bare and quoted key forms.
    /// </summary>
    internal static (int Start, int End) FindTomlBlock(string[] lines)
    {
        static bool IsOurHeader(string trimmed) =>
            trimmed.StartsWith($"[mcp_servers.{ServerKey}]", StringComparison.Ordinal) ||
            trimmed.StartsWith($"[mcp_servers.{ServerKey}.", StringComparison.Ordinal) ||
            trimmed.StartsWith($"[mcp_servers.\"{ServerKey}\"]", StringComparison.Ordinal) ||
            trimmed.StartsWith($"[mcp_servers.\"{ServerKey}\".", StringComparison.Ordinal);

        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart().TrimEnd('\r');
            if (start < 0)
            {
                if (IsOurHeader(trimmed))
                {
                    start = i;
                }
                continue;
            }
            // Inside our block: it ends at the first header that is NOT one of our subtables.
            if (trimmed.StartsWith('[') && !IsOurHeader(trimmed))
            {
                return (start, i);
            }
        }
        return start < 0 ? (-1, -1) : (start, lines.Length);
    }

    private static string? ExtractTomlCommand(string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        (int start, int end) = FindTomlBlock(lines);
        for (int i = start; i >= 0 && i < end; i++)
        {
            string trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("command", StringComparison.Ordinal))
            {
                continue;
            }
            int eq = trimmed.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            string value = trimmed[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '\'' || value[0] == '"') && value[^1] == value[0])
            {
                string inner = value[1..^1];
                return value[0] == '"' ? inner.Replace("\\\\", "\\") : inner;
            }
        }
        return null;
    }

    /// <summary>
    /// TOML literal strings ('...') take backslashes verbatim — ideal for Windows paths. Only if
    /// the path itself contains a single quote do we fall back to a basic string with escaping.
    /// </summary>
    internal static string TomlString(string value) =>
        !value.Contains('\'')
            ? $"'{value}'"
            : $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    // ---------------------------------------------------------------------------------------
    // The shared commit pipeline: backup -> atomic write -> verify-or-rollback.
    // ---------------------------------------------------------------------------------------

    private static McpInstallResult CommitWithVerify(
        McpClientTarget target, string updated, bool existed, string original, bool bom, bool remove)
    {
        string? backupPath = null;
        if (existed && original.Length > 0)
        {
            backupPath = CreateBackup(target);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target.ConfigPath)!);
        WriteAtomic(target.ConfigPath, updated, bom);

        // Verify: the file on disk must read back and parse. If this ever fails we restore the
        // user's original bytes before reporting the error.
        try
        {
            string readBack = File.ReadAllText(target.ConfigPath);
            if (target.Format != McpConfigFormat.Toml)
            {
                JsonNode.Parse(readBack);
            }
            bool present = target.Format == McpConfigFormat.Toml
                ? FindTomlBlock(readBack.Replace("\r\n", "\n").Split('\n')).Start >= 0
                : (JsonNode.Parse(readBack) as JsonObject)?[RootKey(target)]?[ServerKey] is not null;
            if (present == remove)
            {
                throw new InvalidOperationException(remove
                    ? "entry still present after removal"
                    : "entry missing after write");
            }
        }
        catch (Exception verifyError)
        {
            if (backupPath is not null)
            {
                File.Copy(backupPath, target.ConfigPath, overwrite: true);
            }
            else if (!existed)
            {
                try { File.Delete(target.ConfigPath); } catch (IOException) { }
            }
            return new McpInstallResult(false,
                $"Write verification failed for {target.DisplayName} ({FirstLine(verifyError.Message)}) — the original config was restored untouched.");
        }

        string what = remove
            ? $"Removed StepWind from {target.DisplayName}."
            : $"StepWind is now connected to {target.DisplayName}.";
        if (!remove && target.Note is not null)
        {
            what += $" {target.Note}";
        }
        return new McpInstallResult(true, what, backupPath);
    }

    private static string? CreateBackup(McpClientTarget target)
    {
        try
        {
            string dir = Path.Combine(BackupRoot, target.Id);
            Directory.CreateDirectory(dir);
            string name = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Path.GetFileName(target.ConfigPath)}";
            string backupPath = Path.Combine(dir, name);
            File.Copy(target.ConfigPath, backupPath, overwrite: true);

            // Keep the newest MaxBackupsPerClient, prune the rest (name sorts chronologically).
            string[] all = Directory.GetFiles(dir);
            Array.Sort(all, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Length - MaxBackupsPerClient; i++)
            {
                try { File.Delete(all[i]); } catch (IOException) { }
            }
            return backupPath;
        }
        catch (Exception ex)
        {
            // A backup failure must not be silent — without one we refuse to modify the file.
            throw new IOException($"could not create a safety backup ({FirstLine(ex.Message)})", ex);
        }
    }

    private static void WriteAtomic(string path, string content, bool bom)
    {
        string tmp = path + ".stepwind-tmp";
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom);
        File.WriteAllText(tmp, content, encoding);
        File.Move(tmp, path, overwrite: true);
    }

    private static bool FileStartsWithBom(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            return fs.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string FirstLine(string s)
    {
        int nl = s.IndexOfAny(['\r', '\n']);
        return nl < 0 ? s : s[..nl];
    }
}
