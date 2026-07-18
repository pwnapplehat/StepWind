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
    _ => Help(),
};

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
          stepwind-cli journal <C:> [n]   print recent reconstructed file operations
          stepwind-cli e2e                real-hardware end-to-end validation
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
        for (int attempt = 0; attempt < 40; attempt++)
        {
            Thread.Sleep(250);
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
