namespace StepWind.Core.Integration;

/// <summary>How a client stores its MCP server list on disk.</summary>
public enum McpConfigFormat
{
    /// <summary>JSON with a root <c>"mcpServers"</c> object (Cursor, Claude, Windsurf, most others).</summary>
    JsonMcpServers,

    /// <summary>JSON with a root <c>"servers"</c> object (VS Code's mcp.json).</summary>
    JsonServers,

    /// <summary>TOML with <c>[mcp_servers.NAME]</c> tables (Codex CLI).</summary>
    Toml,
}

/// <summary>
/// One AI tool StepWind can auto-configure as an MCP client. Paths are resolved for the current
/// user at construction. <see cref="Installed"/> means the tool appears to be present on this
/// machine (its config dir exists); <see cref="Configured"/> means our "stepwind" entry is
/// already in its config.
/// </summary>
public sealed class McpClientTarget
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>The MCP config file this client reads.</summary>
    public required string ConfigPath { get; init; }

    /// <summary>Directory whose existence indicates the tool is installed (usually ConfigPath's app dir).</summary>
    public required string DetectPath { get; init; }

    public required McpConfigFormat Format { get; init; }

    /// <summary>Extra key/value pairs some clients require in each server entry (e.g. Copilot's tools).</summary>
    public IReadOnlyDictionary<string, string[]>? ExtraArrayFields { get; init; }

    /// <summary>A short note shown in the UI (e.g. "restart the app to pick it up").</summary>
    public string? Note { get; init; }

    public bool Installed { get; set; }
    public bool Configured { get; set; }

    /// <summary>When configured, the command path the entry currently points at (may be stale).</summary>
    public string? ConfiguredCommand { get; set; }
}
