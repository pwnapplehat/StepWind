using System.IO.Pipes;

namespace StepWind.Core.Ipc;

/// <summary>
/// Who is on the other end of an IPC request. The elevated service acts on behalf of whichever
/// local user connects to its pipe, so every privileged or private operation is authorized
/// against this identity — otherwise any local user could reverse another user's file moves as
/// SYSTEM, read another user's file versions, or purge everyone's history.
///
/// <see cref="LocalTrusted"/> is the in-process / CLI identity: full trust, used by unit tests
/// and the diagnostics CLI that run inside the same trust boundary as the engine. Real pipe
/// connections always carry a resolved <see cref="UserSid"/> and are authorized for real.
/// </summary>
public sealed record CallerContext
{
    /// <summary>The caller's user SID (SDDL string), or null for the in-process trusted caller.</summary>
    public string? UserSid { get; init; }

    /// <summary>Human-readable caller name for logs (e.g. "MACHINE\\alice").</summary>
    public string? UserName { get; init; }

    /// <summary>The caller is an elevated Administrator or SYSTEM — allowed machine-wide actions.</summary>
    public bool IsAdministrator { get; init; }

    /// <summary>In-process/CLI caller inside the engine's own trust boundary — skips all checks.</summary>
    public bool FullTrust { get; init; }

    /// <summary>
    /// The live pipe connection for this request, used to impersonate the caller for on-demand
    /// "can this user actually read that folder?" checks. Server-side only; never serialized.
    /// </summary>
    public NamedPipeServerStream? PipeStream { get; init; }

    /// <summary>True when the caller may perform machine-wide actions (config/toggles, purge-all, see everyone's data).</summary>
    public bool IsPrivileged => FullTrust || IsAdministrator;

    /// <summary>The engine's own trust boundary: unit tests, the CLI, and direct in-process calls.</summary>
    public static readonly CallerContext LocalTrusted = new() { FullTrust = true, IsAdministrator = true, UserName = "(local)" };
}
