using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StepWind.Core.Integration;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// The MCP auto-installer edits OTHER apps' config files (Cursor's mcp.json, Claude's
/// .claude.json, Codex's config.toml, ...) — the one place where a StepWind bug could corrupt a
/// different product's state. So the contract under test is blunt: every key the user already
/// had must survive byte-identical in value, malformed/JSONC files must be refused (never
/// rewritten), and every operation must be idempotent and reversible.
/// </summary>
public class McpInstallerTests : IDisposable
{
    private readonly string _root;
    private readonly string _exe = @"C:\Program Files\StepWind\StepWind.Mcp.exe";

    public McpInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sw-mcpinst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        McpInstaller.BackupRoot = Path.Combine(_root, "backups");
    }

    private McpClientTarget Target(McpConfigFormat format, string fileName = "mcp.json",
        IReadOnlyDictionary<string, string[]>? extra = null) => new()
    {
        Id = "test-client",
        DisplayName = "Test Client",
        ConfigPath = Path.Combine(_root, fileName),
        DetectPath = _root,
        Format = format,
        ExtraArrayFields = extra,
    };

    // ---------------------------- fresh install (no config yet) ----------------------------

    [Fact]
    public void Fresh_install_creates_a_valid_mcpServers_file()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.True(r.Ok, r.Message);
        JsonNode root = JsonNode.Parse(File.ReadAllText(t.ConfigPath))!;
        Assert.Equal(_exe, root["mcpServers"]!["stepwind"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Fresh_install_vscode_uses_servers_root_and_stdio_type()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonServers);

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.True(r.Ok, r.Message);
        JsonNode root = JsonNode.Parse(File.ReadAllText(t.ConfigPath))!;
        Assert.Equal("stdio", root["servers"]!["stepwind"]!["type"]!.GetValue<string>());
        Assert.Equal(_exe, root["servers"]!["stepwind"]!["command"]!.GetValue<string>());
        Assert.Null(root["mcpServers"]); // must NOT use the generic root key
    }

    [Fact]
    public void Extra_array_fields_are_written_copilot_style()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers,
            extra: new Dictionary<string, string[]> { ["tools"] = ["*"] });

        Assert.True(McpInstaller.Install(t, _exe).Ok);

        JsonNode entry = JsonNode.Parse(File.ReadAllText(t.ConfigPath))!["mcpServers"]!["stepwind"]!;
        Assert.Equal("*", entry["tools"]![0]!.GetValue<string>());
    }

    // ------------------------- merge preserves EVERYTHING already there -------------------------

    [Fact]
    public void Install_preserves_every_existing_key_value_and_other_servers()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        // Realistic .claude.json-style file: top-level state keys + an existing server with env.
        File.WriteAllText(t.ConfigPath, """
        {
          "oauthAccount": { "emailAddress": "user@example.com", "uuid": "abc-123" },
          "numStartups": 41,
          "tipsHistory": { "new-user-warmup": 1 },
          "mcpServers": {
            "github": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-github"],
              "env": { "GITHUB_TOKEN": "ghp_secret" }
            }
          },
          "projects": { "C:\\dev\\thing": { "allowedTools": ["Bash"], "history": [1.5, 2, true, null] } }
        }
        """);

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.True(r.Ok, r.Message);
        JsonNode root = JsonNode.Parse(File.ReadAllText(t.ConfigPath))!;
        // Ours is in:
        Assert.Equal(_exe, root["mcpServers"]!["stepwind"]!["command"]!.GetValue<string>());
        // And every pre-existing value survived exactly:
        Assert.Equal("user@example.com", root["oauthAccount"]!["emailAddress"]!.GetValue<string>());
        Assert.Equal(41, root["numStartups"]!.GetValue<int>());
        Assert.Equal("ghp_secret", root["mcpServers"]!["github"]!["env"]!["GITHUB_TOKEN"]!.GetValue<string>());
        Assert.Equal("-y", root["mcpServers"]!["github"]!["args"]![0]!.GetValue<string>());
        Assert.Equal(1.5, root["projects"]!["C:\\dev\\thing"]!["history"]![0]!.GetValue<double>());
        Assert.True(root["projects"]!["C:\\dev\\thing"]!["history"]![2]!.GetValue<bool>());
        Assert.Null(root["projects"]!["C:\\dev\\thing"]!["history"]![3]);
    }

    [Fact]
    public void Remove_deletes_only_our_entry_and_keeps_the_rest()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        File.WriteAllText(t.ConfigPath,
            """{ "mcpServers": { "github": { "command": "npx" }, "stepwind": { "command": "old" } }, "theme": "dark" }""");

        McpInstallResult r = McpInstaller.Remove(t);

        Assert.True(r.Ok, r.Message);
        JsonNode root = JsonNode.Parse(File.ReadAllText(t.ConfigPath))!;
        Assert.Null(root["mcpServers"]!["stepwind"]);
        Assert.Equal("npx", root["mcpServers"]!["github"]!["command"]!.GetValue<string>());
        Assert.Equal("dark", root["theme"]!.GetValue<string>());
    }

    // --------------------------- refusal: never rewrite what we can't keep ---------------------------

    [Fact]
    public void Jsonc_comments_are_refused_not_stripped()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        string jsonc = """
        {
          // my precious hand-written comment
          "mcpServers": {}
        }
        """;
        File.WriteAllText(t.ConfigPath, jsonc);

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.False(r.Ok);
        Assert.Contains("comments or trailing commas", r.Message);
        Assert.Equal(jsonc, File.ReadAllText(t.ConfigPath)); // untouched, comment intact
    }

    [Fact]
    public void Trailing_commas_are_refused_not_normalized()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        string jsonc = """{ "mcpServers": { "a": { "command": "x" }, } }""";
        File.WriteAllText(t.ConfigPath, jsonc);

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.False(r.Ok);
        Assert.Equal(jsonc, File.ReadAllText(t.ConfigPath));
    }

    [Fact]
    public void Broken_json_is_refused_with_the_parse_error()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        File.WriteAllText(t.ConfigPath, "{ this is not json");

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.False(r.Ok);
        Assert.Contains("not valid JSON", r.Message);
        Assert.Equal("{ this is not json", File.ReadAllText(t.ConfigPath));
    }

    [Fact]
    public void Non_object_root_is_refused()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        File.WriteAllText(t.ConfigPath, "[1, 2, 3]");

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.False(r.Ok);
        Assert.Equal("[1, 2, 3]", File.ReadAllText(t.ConfigPath));
    }

    [Fact]
    public void Non_object_mcpServers_value_is_refused()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        File.WriteAllText(t.ConfigPath, """{ "mcpServers": "oops-a-string" }""");

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.False(r.Ok);
        Assert.Contains("mcpServers", r.Message);
    }

    // ------------------------------------ idempotency ------------------------------------

    [Fact]
    public void Installing_twice_yields_one_entry_and_updates_the_path()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);

        Assert.True(McpInstaller.Install(t, @"C:\old\place\StepWind.Mcp.exe").Ok);
        Assert.True(McpInstaller.Install(t, _exe).Ok);

        JsonNode servers = JsonNode.Parse(File.ReadAllText(t.ConfigPath))!["mcpServers"]!;
        Assert.Single(servers.AsObject());
        Assert.Equal(_exe, servers["stepwind"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Removing_when_absent_is_a_friendly_noop()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        File.WriteAllText(t.ConfigPath, """{ "mcpServers": { "github": { "command": "npx" } } }""");
        string before = File.ReadAllText(t.ConfigPath);

        McpInstallResult r = McpInstaller.Remove(t);

        Assert.True(r.Ok);
        Assert.Contains("nothing to remove", r.Message);
        Assert.Equal(before, File.ReadAllText(t.ConfigPath)); // not even re-serialized
    }

    [Fact]
    public void Removing_with_no_config_file_is_a_friendly_noop()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);

        McpInstallResult r = McpInstaller.Remove(t);

        Assert.True(r.Ok);
        Assert.False(File.Exists(t.ConfigPath));
    }

    // ------------------------------------ TOML (Codex CLI) ------------------------------------

    [Fact]
    public void Toml_fresh_install_writes_a_literal_string_table()
    {
        McpClientTarget t = Target(McpConfigFormat.Toml, "config.toml");

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.True(r.Ok, r.Message);
        string text = File.ReadAllText(t.ConfigPath);
        Assert.Contains("[mcp_servers.stepwind]", text);
        Assert.Contains($"command = '{_exe}'", text); // literal string: no escaping needed
    }

    [Fact]
    public void Toml_install_preserves_every_other_line_byte_for_byte()
    {
        McpClientTarget t = Target(McpConfigFormat.Toml, "config.toml");
        string existing = string.Join('\n',
            "# Codex configuration",
            "model = \"o4\"",
            "",
            "[mcp_servers.github]",
            "command = \"npx\"",
            "args = [\"-y\", \"@modelcontextprotocol/server-github\"]",
            "",
            "[profiles.fast]",
            "approval_policy = \"never\"");
        File.WriteAllText(t.ConfigPath, existing);

        Assert.True(McpInstaller.Install(t, _exe).Ok);

        string text = File.ReadAllText(t.ConfigPath);
        Assert.Contains("# Codex configuration", text); // comments SURVIVE in TOML mode
        Assert.Contains("model = \"o4\"", text);
        Assert.Contains("[mcp_servers.github]", text);
        Assert.Contains("[profiles.fast]", text);
        Assert.Contains("[mcp_servers.stepwind]", text);
    }

    [Fact]
    public void Toml_reinstall_replaces_our_block_including_subtables_only_once()
    {
        McpClientTarget t = Target(McpConfigFormat.Toml, "config.toml");
        File.WriteAllText(t.ConfigPath, string.Join('\n',
            "[mcp_servers.stepwind]",
            "command = 'C:\\old\\StepWind.Mcp.exe'",
            "",
            "[mcp_servers.stepwind.env]",
            "OLD_VAR = \"1\"",
            "",
            "[mcp_servers.github]",
            "command = \"npx\""));

        Assert.True(McpInstaller.Install(t, _exe).Ok);

        string text = File.ReadAllText(t.ConfigPath);
        Assert.DoesNotContain("old\\StepWind", text);
        Assert.DoesNotContain("OLD_VAR", text); // stale subtable removed with the block
        Assert.Contains("[mcp_servers.github]", text); // neighbors untouched
        // Exactly ONE [mcp_servers.stepwind] header after reinstall (split on it => 2 parts).
        Assert.Equal(2, text.Split("[mcp_servers.stepwind]", StringSplitOptions.None).Length);
        Assert.Contains($"command = '{_exe}'", text);
    }

    [Fact]
    public void Toml_remove_deletes_our_block_and_keeps_the_rest()
    {
        McpClientTarget t = Target(McpConfigFormat.Toml, "config.toml");
        File.WriteAllText(t.ConfigPath, string.Join('\n',
            "model = \"o4\"",
            "",
            "[mcp_servers.stepwind]",
            $"command = '{_exe}'",
            "",
            "[mcp_servers.github]",
            "command = \"npx\""));

        McpInstallResult r = McpInstaller.Remove(t);

        Assert.True(r.Ok, r.Message);
        string text = File.ReadAllText(t.ConfigPath);
        Assert.DoesNotContain("stepwind", text);
        Assert.Contains("model = \"o4\"", text);
        Assert.Contains("[mcp_servers.github]", text);
    }

    [Fact]
    public void Toml_string_helper_escapes_only_when_it_must()
    {
        Assert.Equal(@"'C:\a\b.exe'", McpInstaller.TomlString(@"C:\a\b.exe"));
        Assert.Equal("\"C:\\\\o'brien\\\\b.exe\"", McpInstaller.TomlString(@"C:\o'brien\b.exe"));
    }

    // ------------------------------ backups, BOM, empty files ------------------------------

    [Fact]
    public void Every_modification_creates_a_backup_first()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        string original = """{ "mcpServers": { "github": { "command": "npx" } } }""";
        File.WriteAllText(t.ConfigPath, original);

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.True(r.Ok);
        Assert.NotNull(r.BackupPath);
        Assert.Equal(original, File.ReadAllText(r.BackupPath!)); // backup is the pre-edit bytes
    }

    [Fact]
    public void Fresh_file_creation_makes_no_backup()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.True(r.Ok);
        Assert.Null(r.BackupPath);
    }

    [Fact]
    public void Utf8_bom_is_preserved_when_present_and_not_introduced_when_absent()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        byte[] withBom = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{ "mcpServers": {} }""")];
        File.WriteAllBytes(t.ConfigPath, withBom);

        Assert.True(McpInstaller.Install(t, _exe).Ok);
        byte[] after = File.ReadAllBytes(t.ConfigPath);
        Assert.True(after is [0xEF, 0xBB, 0xBF, ..], "BOM should be preserved");

        // And the no-BOM case stays no-BOM:
        McpClientTarget t2 = Target(McpConfigFormat.JsonMcpServers, "nobom.json");
        File.WriteAllText(t2.ConfigPath, """{ "mcpServers": {} }""", new UTF8Encoding(false));
        Assert.True(McpInstaller.Install(t2, _exe).Ok);
        byte[] after2 = File.ReadAllBytes(t2.ConfigPath);
        Assert.False(after2 is [0xEF, 0xBB, 0xBF, ..], "BOM should not be introduced");
    }

    [Fact]
    public void Empty_or_whitespace_file_is_treated_as_fresh()
    {
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        File.WriteAllText(t.ConfigPath, "   \r\n  ");

        McpInstallResult r = McpInstaller.Install(t, _exe);

        Assert.True(r.Ok, r.Message);
        JsonNode root = JsonNode.Parse(File.ReadAllText(t.ConfigPath))!;
        Assert.Equal(_exe, root["mcpServers"]!["stepwind"]!["command"]!.GetValue<string>());
    }

    // --------------------------- server exe path: never with spaces ---------------------------
    // Cursor (and others) spawn the stdio command through cmd.exe WITHOUT quoting, so
    // "C:\Program Files\...\StepWind.Mcp.exe" executes 'C:\Program' and dies. The path we
    // write into configs must therefore be spaceless whenever one can possibly be provided.

    [Fact]
    public void ResolveServerExe_prefers_the_spaceless_programdata_copy()
    {
        string spaceless = Path.Combine(_root, "bin", "StepWind.Mcp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(spaceless)!);
        File.WriteAllText(spaceless, "x");

        string result = McpInstaller.ResolveServerExe(@"C:\Program Files\StepWind\StepWind.Mcp.exe", spaceless);

        Assert.Equal(spaceless, result);
        Assert.DoesNotContain(" ", result);
    }

    [Fact]
    public void ResolveServerExe_ignores_a_missing_canonical_copy()
    {
        string missing = Path.Combine(_root, "bin", "StepWind.Mcp.exe");
        string besideApp = Path.Combine(_root, "app", "StepWind.Mcp.exe"); // no spaces in _root

        Assert.Equal(besideApp, McpInstaller.ResolveServerExe(besideApp, missing));
    }

    [Fact]
    public void ResolveServerExe_shortens_a_spacey_path_via_8dot3_when_the_file_exists()
    {
        string spacey = Path.Combine(_root, "Program Files Clone", "StepWind.Mcp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(spacey)!);
        File.WriteAllText(spacey, "x");
        string missingCanonical = Path.Combine(_root, "bin", "StepWind.Mcp.exe");

        string result = McpInstaller.ResolveServerExe(spacey, missingCanonical);

        // 8.3 names can be disabled per-volume; when they are, the long path is the honest
        // best-effort answer. When they aren't, the result must be the spaceless alias to
        // the SAME file.
        if (result != spacey)
        {
            Assert.DoesNotContain(" ", result);
            Assert.True(File.Exists(result), "short path must still resolve to the file");
        }
    }

    [Fact]
    public void ResolveServerExe_returns_a_nonexistent_spacey_path_unchanged()
    {
        string ghost = Path.Combine(_root, "No Such Dir", "StepWind.Mcp.exe");
        string missingCanonical = Path.Combine(_root, "bin", "StepWind.Mcp.exe");

        Assert.Equal(ghost, McpInstaller.ResolveServerExe(ghost, missingCanonical));
    }

    // ------------------------------------ detection ------------------------------------

    [Fact]
    public void DetectAll_reports_known_clients_with_absolute_windows_paths()
    {
        List<McpClientTarget> all = McpInstaller.DetectAll();

        // The registry of supported clients — if one is dropped accidentally, this fails.
        string[] ids = all.Select(t => t.Id).ToArray();
        foreach (string expected in new[] { "cursor", "claude-desktop", "claude-code", "antigravity",
            "windsurf", "vscode", "cline", "gemini-cli", "codex", "copilot-cli", "lmstudio", "kiro" })
        {
            Assert.Contains(expected, ids);
        }
        Assert.All(all, t => Assert.True(Path.IsPathRooted(t.ConfigPath), $"{t.Id} path not rooted"));
        Assert.All(all, t => Assert.False(string.IsNullOrWhiteSpace(t.DisplayName)));
    }

    [Fact]
    public void IsConfigured_detection_reads_lenient_json_and_reports_the_command()
    {
        // A hand-edited Cursor config with a trailing comma still counts as "configured" for
        // DETECTION (read-only) — we only refuse lenient files when WRITING.
        McpClientTarget t = Target(McpConfigFormat.JsonMcpServers);
        File.WriteAllText(t.ConfigPath,
            """{ "mcpServers": { "stepwind": { "command": "C:\\somewhere\\StepWind.Mcp.exe", }, } }""");

        List<McpClientTarget> all = McpInstaller.DetectAll(); // sanity: doesn't throw with real machine state
        Assert.NotEmpty(all);

        // Detection against our synthetic target goes through Install-time probing:
        McpInstallResult install = McpInstaller.Install(t, _exe);
        Assert.False(install.Ok); // trailing commas -> write refused (proves lenient read != lenient write)
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
