using System.Runtime.Versioning;
using StepWind.Core.Journal;
using StepWind.Core.Storage;

// ============================================================================
// StepWind CLI — diagnostics + real-hardware E2E through the PRODUCTION classes
// (UsnJournalReader, OperationReconstructor, OperationReverser, VersionStore).
//
//   journal <C:> [n]   tail the USN journal and print the last n reconstructed ops
//   e2e                scripted create/rename/move/delete → reconstruct via the real
//                      reader → reverse the move → verify; plus a version-store round-trip
// ============================================================================

return args.FirstOrDefault()?.ToLowerInvariant() switch
{
    "journal" => Journal(args.ElementAtOrDefault(1) ?? "C:", int.TryParse(args.ElementAtOrDefault(2), out int n) ? n : 40),
    "e2e" => E2E(),
    "probe" => Probe(),
    "settings" => DumpSettings(),
    "recent" => DumpRecent(),
    "set-encryption" => SetEncryption(args.ElementAtOrDefault(1)),
    "purge" => Purge(args.ElementAtOrDefault(1)),
    "export-recovery-key" => ExportRecoveryKey(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2)),
    "recover-verify" => RecoverVerify(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2), args.ElementAtOrDefault(3)),
    _ => Help(),
};

// Exports a passphrase-protected copy of the store's encryption key, so encrypted history
// survives an OS reinstall / disk move (which makes the DPAPI-sealed live key unwrappable).
// Reads the key directly from the ACL-locked store — must run elevated (admin).
[SupportedOSPlatform("windows")]
static int ExportRecoveryKey(string? passphrase, string? outFile)
{
    if (string.IsNullOrEmpty(passphrase) || string.IsNullOrWhiteSpace(outFile))
    {
        Console.WriteLine("usage: stepwind-cli export-recovery-key <passphrase> <output-file>");
        return 1;
    }

    string storeRoot = StepWind.Core.Engine.StepWindSettings.DefaultStoreRoot;
    string keyPath = Path.Combine(storeRoot, "store.key");
    if (!File.Exists(keyPath))
    {
        Console.WriteLine("Encryption isn't enabled (no store key exists), so there's nothing to export. Enable encryption first.");
        return 1;
    }

    try
    {
        byte[] key = KeyProtector.LoadOrCreate(storeRoot);
        byte[] blob = KeyRecovery.Export(key, passphrase);
        File.WriteAllBytes(outFile, blob);
        Console.WriteLine($"Recovery key written to {outFile}. Keep it somewhere safe and OFF this machine — anyone with this file AND the passphrase can read your encrypted history.");
        return 0;
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine("Access denied reading the store key. Run this command as administrator.");
        return 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine("Export failed: " + ex.Message);
        return 1;
    }
}

// Verifies a recovery key file + passphrase actually unlock a store — the offline disaster-
// recovery check. With a store root, proves a real version reconstructs with the recovered key.
static int RecoverVerify(string? recoveryFile, string? passphrase, string? storeRootArg)
{
    if (string.IsNullOrWhiteSpace(recoveryFile) || string.IsNullOrEmpty(passphrase))
    {
        Console.WriteLine("usage: stepwind-cli recover-verify <recovery-file> <passphrase> [store-root]");
        return 1;
    }

    if (!File.Exists(recoveryFile))
    {
        Console.WriteLine("Recovery file not found: " + recoveryFile);
        return 1;
    }

    byte[] key;
    try
    {
        key = KeyRecovery.Import(File.ReadAllBytes(recoveryFile), passphrase);
    }
    catch (System.Security.Cryptography.CryptographicException)
    {
        Console.WriteLine("Wrong passphrase, or the recovery file is corrupt.");
        return 2;
    }

    string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(key))[..12].ToLowerInvariant();
    Console.WriteLine($"Recovery key is valid (fingerprint {fingerprint}).");

    string storeRoot = string.IsNullOrWhiteSpace(storeRootArg)
        ? StepWind.Core.Engine.StepWindSettings.DefaultStoreRoot : storeRootArg;
    string index = Path.Combine(storeRoot, "versions.jsonl");
    if (!File.Exists(index))
    {
        Console.WriteLine($"(No store at {storeRoot} to test against — the key itself is valid.)");
        return 0;
    }

    try
    {
        // Read both plain and cipher blobs (a store may be mid-migration) using the recovered key.
        var codec = new MigratingBlobCodec(new GzipBlobCodec(), () => new AesGcmBlobCodec(key), encryptNew: false);
        var store = new VersionStore(new BlobStore(storeRoot, codec), new VersionLog(index));
        FileVersion? sample = store.Log.All.FirstOrDefault();
        if (sample is null)
        {
            Console.WriteLine("Store has no versions to test, but the recovery key is valid.");
            return 0;
        }

        using var ms = new MemoryStream();
        store.WriteContent(sample, ms); // re-hashes every chunk; throws if the key can't decrypt
        Console.WriteLine($"Verified: '{sample.RelativePath}' ({ms.Length:N0} bytes) reconstructs with this key. Your store is recoverable.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine("The key imported but the store did not decrypt: " + ex.Message);
        return 2;
    }
}

// Deletes stored history on the running service: purge "*" | unprotected | <prefix>.
static int Purge(string? selector)
{
    if (string.IsNullOrWhiteSpace(selector))
    {
        Console.WriteLine("usage: stepwind-cli purge \"*\"|unprotected|<folder-or-file-prefix>");
        return 1;
    }

    var client = new StepWind.Core.Ipc.PipeClient();
    StepWind.Core.Ipc.IpcResponse r = client.SendAsync(new StepWind.Core.Ipc.IpcRequest
    {
        Command = StepWind.Core.Ipc.IpcCommand.PurgeHistory,
        Arg1 = selector,
    }).GetAwaiter().GetResult();
    Console.WriteLine(r.Ok ? r.Json : "ERR: " + r.Error);
    return r.Ok ? 0 : 1;
}

// Toggles store encryption on the running service (diagnostic; the GUI has a switch).
static int SetEncryption(string? state)
{
    if (state is not ("on" or "off"))
    {
        Console.WriteLine("usage: stepwind-cli set-encryption on|off");
        return 1;
    }

    var client = new StepWind.Core.Ipc.PipeClient();
    StepWind.Core.Ipc.IpcResponse r = client.SendAsync(new StepWind.Core.Ipc.IpcRequest
    {
        Command = StepWind.Core.Ipc.IpcCommand.SetSettings,
        Arg1 = System.Text.Json.JsonSerializer.Serialize(new { EncryptionEnabled = state == "on" }),
    }).GetAwaiter().GetResult();
    Console.WriteLine(r.Ok ? r.Json : "ERR: " + r.Error);
    return r.Ok ? 0 : 1;
}

// Queries the running service's GetRecentFiles over the pipe (diagnostic).
static int DumpRecent()
{
    var client = new StepWind.Core.Ipc.PipeClient();
    StepWind.Core.Ipc.IpcResponse r = client.SendAsync(
        new StepWind.Core.Ipc.IpcRequest { Command = StepWind.Core.Ipc.IpcCommand.GetRecentFiles, Limit = 50 }).GetAwaiter().GetResult();
    Console.WriteLine(r.Ok ? r.Json : "ERR: " + r.Error);
    return r.Ok ? 0 : 1;
}

// Queries the running service's GetSettings over the pipe (diagnostic).
static int DumpSettings()
{
    var client = new StepWind.Core.Ipc.PipeClient();
    StepWind.Core.Ipc.IpcResponse r = client.SendAsync(
        new StepWind.Core.Ipc.IpcRequest { Command = StepWind.Core.Ipc.IpcCommand.GetSettings }).GetAwaiter().GetResult();
    Console.WriteLine(r.Ok ? r.Json : "ERR: " + r.Error);
    return r.Ok ? 0 : 1;
}

// Isolates ResolveDirectory: create a dir, learn its file-reference-number, resolve it back.
[SupportedOSPlatform("windows")]
static int Probe()
{
    string dir = Path.Combine(Path.GetTempPath(), "sw-probe-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(dir);
    string volume = Path.GetPathRoot(dir)!.TrimEnd('\\');
    ulong frn = Interop.GetFileReferenceNumber(dir);
    Console.WriteLine($"dir={dir} frn={frn:x}");
    using var reader = new UsnJournalReader(volume);
    string? resolved = reader.ResolveDirectory(frn);
    Console.WriteLine($"resolved={resolved ?? "(null)"} lastError={reader.LastResolveError}");
    Directory.Delete(dir);
    return resolved is not null ? 0 : 1;
}

static int Help()
{
    Console.WriteLine("""
        StepWind CLI (admin required)
          stepwind-cli journal <C:> [n]              print recent reconstructed file operations
          stepwind-cli e2e                           real-hardware end-to-end validation
          stepwind-cli probe                         resolve-directory self-test
          stepwind-cli settings                      print the running service's settings
          stepwind-cli recent                        print recently-changed protected files
          stepwind-cli set-encryption on|off         toggle store encryption on the service
          stepwind-cli purge "*"|unprotected|<prefix>  delete stored history (destructive)
          stepwind-cli export-recovery-key <passphrase> <out-file>
                                                     export a passphrase-protected recovery key
                                                     (survives OS reinstall / disk move)
          stepwind-cli recover-verify <recovery-file> <passphrase> [store-root]
                                                     check a recovery key unlocks the store
        """);
    return 0;
}

[SupportedOSPlatform("windows")]
static int Journal(string volume, int count)
{
    using var reader = new UsnJournalReader(volume);
    (ulong journalId, long nextUsn) = reader.Query();
    long start = Math.Max(0, nextUsn - 8_000_000); // look back a little
    (List<UsnRecord> records, _) = reader.Read(journalId, start);
    var ops = new OperationReconstructor(reader.ResolveDirectory).Reconstruct(records);
    foreach (FileOperation op in ops.TakeLast(count))
    {
        Console.WriteLine($"{op.TimestampUtc:HH:mm:ss}  {op.Kind,-7} {op.NewPath ?? op.OldPath ?? op.Name}");
    }

    return 0;
}

[SupportedOSPlatform("windows")]
static int E2E()
{
    string sandbox = Path.Combine(Path.GetTempPath(), "stepwind-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(sandbox);
    string volume = Path.GetPathRoot(sandbox)!.TrimEnd('\\');
    bool pass = true;

    try
    {
        using var reader = new UsnJournalReader(volume);
        (ulong journalId, long startUsn) = reader.Query();
        Console.WriteLine($"volume {volume}, journal {journalId:x}, start USN {startUsn}");

        // --- scripted incident ---
        string projectX = Path.Combine(sandbox, "ProjectX");
        string archive = Path.Combine(sandbox, "Archive");
        Directory.CreateDirectory(projectX);
        Directory.CreateDirectory(archive);
        string file = Path.Combine(projectX, "thesis.docx");
        File.WriteAllText(file, "important work");
        File.Move(file, Path.Combine(projectX, "thesis-final.docx"));
        Directory.Move(projectX, Path.Combine(archive, "ProjectX")); // the mistaken move
        File.Delete(Path.Combine(archive, "ProjectX", "thesis-final.docx"));

        // --- reconstruct through the production reader, waiting for the async journal ---
        var reconstructor = new OperationReconstructor(reader.ResolveDirectory);
        IReadOnlyList<FileOperation> ops = [];
        for (int attempt = 0; attempt < 100; attempt++)
        {
            Thread.Sleep(300);
            (List<UsnRecord> records, _) = reader.Read(journalId, startUsn);
            ops = reconstructor.Reconstruct(records);
            if (ops.Any(o => o.Kind == OperationKind.Delete && o.Name.Equals("thesis-final.docx", StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }
        }

        foreach (FileOperation o in ops.Where(o => o.Name.Contains("thesis", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("ProjectX", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"   {o.Kind,-7} {o.OldPath ?? "(new)"} -> {o.NewPath ?? "(gone)"}");
        }

        FileOperation? move = ops.FirstOrDefault(o => o.Kind == OperationKind.Move && o.Name == "ProjectX");
        bool sawRename = ops.Any(o => o.Kind == OperationKind.Rename && o.Name == "thesis-final.docx");
        bool sawDelete = ops.Any(o => o.Kind == OperationKind.Delete && o.Name == "thesis-final.docx");
        Console.WriteLine($"reconstructed: rename={sawRename} move={move is not null} delete={sawDelete}");
        pass &= sawRename && move is not null && sawDelete;

        // --- reverse the move through the production reverser ---
        if (move is not null)
        {
            ReverseResult rr = OperationReverser.Reverse(move, new RealFs());
            bool back = Directory.Exists(projectX) && !Directory.Exists(Path.Combine(archive, "ProjectX"));
            Console.WriteLine($"reverse move: success={rr.Success} folderBack={back} ({rr.Message})");
            pass &= rr.Success && back;
        }

        // --- version-store round-trip through production classes ---
        string storeRoot = Path.Combine(sandbox, "store");
        var vs = new VersionStore(new BlobStore(storeRoot, new GzipBlobCodec()), new VersionLog(Path.Combine(storeRoot, "versions.jsonl")));
        string doc = Path.Combine(sandbox, "novel.txt");
        File.WriteAllText(doc, "the good version");
        FileVersion good = vs.Capture(doc, "novel.txt");
        File.WriteAllText(doc, "ruined");
        vs.Capture(doc, "novel.txt");
        File.Delete(doc);
        string restored = vs.RestoreToSafePath(good, doc);
        bool exact = File.ReadAllText(restored) == "the good version";
        Console.WriteLine($"version restore after overwrite+delete: byteExact={exact}");
        pass &= exact;
    }
    catch (Exception ex)
    {
        Console.WriteLine("E2E ERROR: " + ex.Message);
        pass = false;
    }
    finally
    {
        try { Directory.Delete(sandbox, true); } catch { }
    }

    Console.WriteLine(pass ? "\nSTEPWIND E2E PASS: production journal + reverse + version store verified on hardware."
                           : "\nSTEPWIND E2E FAIL");
    return pass ? 0 : 2;
}

[SupportedOSPlatform("windows")]
file static class Interop
{
    // All DWORD/FILETIME fields → 4-byte members, so CLR alignment matches the native
    // 4-aligned layout (using `long` here would 8-align and misread FileIndex).
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(IntPtr h, out BY_HANDLE_FILE_INFORMATION info);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    public static ulong GetFileReferenceNumber(string path)
    {
        IntPtr h = CreateFileW(path, 0x0080 /*READ_ATTRIBUTES*/, 7, IntPtr.Zero, 3, 0x02000000 /*BACKUP_SEMANTICS*/, IntPtr.Zero);
        try
        {
            GetFileInformationByHandle(h, out BY_HANDLE_FILE_INFORMATION info);
            return ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}

/// <summary>Real filesystem actions for the reverser (the app uses the same shape).</summary>
file sealed class RealFs : IFileSystemActions
{
    public bool Exists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void Move(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }
}
