using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StepWind.Core.Journal;

/// <summary>Snapshot of a volume's USN journal header (ids + the valid USN range).</summary>
public readonly record struct UsnJournalState(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn, long MaxUsn);

/// <summary>
/// Reads the NTFS USN change journal for a volume (e.g. "C:"). Requires elevation, so this
/// lives in the background service, not the GUI. Parses both V2 and V3 physical record layouts
/// (modern Windows emits V3 with 128-bit ids) and resolves parent references to directory
/// paths via OpenFileById. Persisting the last-read USN + detecting a journal-id change/wrap
/// (so a gap triggers a full rescan instead of silently missing changes) is handled by the
/// caller that owns the cursor; this class is the raw reader.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UsnJournalReader : IDisposable
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;
    private static readonly IntPtr InvalidHandle = new(-1);

    private readonly IntPtr _volume;

    /// <summary>Last Win32 error from a failed <see cref="ResolveDirectory"/> (diagnostics).</summary>
    public int LastResolveError { get; private set; }

    public UsnJournalReader(string volume)
    {
        string trimmed = volume.TrimEnd('\\', ':') + ":";
        _volume = CreateFileW($@"\\.\{trimmed}", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (_volume == InvalidHandle)
        {
            throw new InvalidOperationException($"Cannot open volume {trimmed} (administrator rights required).");
        }
    }

    /// <summary>Journal id + the current end USN (a fresh tail should start here).</summary>
    public (ulong JournalId, long NextUsn) Query()
    {
        UsnJournalState s = QueryState();
        return (s.JournalId, s.NextUsn);
    }

    /// <summary>
    /// Full journal state — including <c>LowestValidUsn</c>, which the caller compares against its
    /// cursor to detect a wrap/overflow (records purged past the cursor) and resync loudly rather
    /// than silently missing operations. See <see cref="UsnResyncPolicy"/>.
    /// </summary>
    public UsnJournalState QueryState()
    {
        int size = Marshal.SizeOf<USN_JOURNAL_DATA_V0>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(_volume, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, buf, size, out _, IntPtr.Zero))
            {
                throw new InvalidOperationException("USN journal is not active on this volume.");
            }

            var data = Marshal.PtrToStructure<USN_JOURNAL_DATA_V0>(buf);
            return new UsnJournalState(data.UsnJournalID, data.FirstUsn, data.NextUsn, data.LowestValidUsn, data.MaxUsn);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>
    /// Reads records from <paramref name="startUsn"/> forward. Returns the parsed records and
    /// the next USN to resume from (persist it as the cursor). Stops when the journal has no
    /// more records past the cursor.
    /// </summary>
    public (List<UsnRecord> Records, long NextUsn) Read(ulong journalId, long startUsn, int maxBytes = 1 << 20)
    {
        var results = new List<UsnRecord>();
        var input = new READ_USN_JOURNAL_DATA_V0
        {
            StartUsn = startUsn,
            ReasonMask = 0xFFFFFFFF,
            ReturnOnlyOnClose = 0,
            Timeout = 0,
            BytesToWaitFor = 0,
            UsnJournalID = journalId,
        };

        int inSize = Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>();
        IntPtr inBuf = Marshal.AllocHGlobal(inSize);
        IntPtr outBuf = Marshal.AllocHGlobal(maxBytes);
        long nextUsn = startUsn;
        try
        {
            for (int round = 0; round < 256; round++)
            {
                Marshal.StructureToPtr(input, inBuf, false);
                if (!DeviceIoControl(_volume, FSCTL_READ_USN_JOURNAL, inBuf, inSize, outBuf, maxBytes, out int returned, IntPtr.Zero)
                    || returned <= 8)
                {
                    break;
                }

                nextUsn = Marshal.ReadInt64(outBuf);
                int offset = 8;
                while (offset < returned)
                {
                    int recLen = Marshal.ReadInt32(outBuf, offset);
                    if (recLen <= 0)
                    {
                        break;
                    }

                    UsnRecord? parsed = Parse(outBuf, offset);
                    if (parsed is not null)
                    {
                        results.Add(parsed.Value);
                    }

                    offset += recLen;
                }

                if (nextUsn <= input.StartUsn)
                {
                    break;
                }

                input.StartUsn = nextUsn;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }

        return (results, nextUsn);
    }

    private static UsnRecord? Parse(IntPtr buf, int offset)
    {
        short major = Marshal.ReadInt16(buf, offset + 4);
        ulong frn, parent;
        uint reason, attrs;
        long usnValue, timestamp;
        short nameLen, nameOff;

        if (major == 2)
        {
            frn = (ulong)Marshal.ReadInt64(buf, offset + 8);
            parent = (ulong)Marshal.ReadInt64(buf, offset + 16);
            usnValue = Marshal.ReadInt64(buf, offset + 24);
            timestamp = Marshal.ReadInt64(buf, offset + 32);
            reason = (uint)Marshal.ReadInt32(buf, offset + 40);
            attrs = (uint)Marshal.ReadInt32(buf, offset + 52);
            nameLen = Marshal.ReadInt16(buf, offset + 56);
            nameOff = Marshal.ReadInt16(buf, offset + 58);
        }
        else if (major == 3)
        {
            frn = (ulong)Marshal.ReadInt64(buf, offset + 8);   // low 64 of 128-bit id
            parent = (ulong)Marshal.ReadInt64(buf, offset + 24);
            usnValue = Marshal.ReadInt64(buf, offset + 40);
            timestamp = Marshal.ReadInt64(buf, offset + 48);
            reason = (uint)Marshal.ReadInt32(buf, offset + 56);
            attrs = (uint)Marshal.ReadInt32(buf, offset + 68);
            nameLen = Marshal.ReadInt16(buf, offset + 72);
            nameOff = Marshal.ReadInt16(buf, offset + 74);
        }
        else
        {
            return null; // V4 records precede a V3 with the name; we key off V3
        }

        string name = nameLen > 0 ? Marshal.PtrToStringUni(buf + offset + nameOff, nameLen / 2) ?? "" : "";
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        DateTime ts;
        try { ts = DateTime.FromFileTimeUtc(timestamp); } catch { ts = DateTime.UtcNow; }

        return new UsnRecord(frn, parent, usnValue, (UsnReason)reason, ts, name, (attrs & FILE_ATTRIBUTE_DIRECTORY) != 0);
    }

    /// <summary>Best-effort directory path for a file reference number (opens it by id).</summary>
    public string? ResolveDirectory(ulong frn)
    {
        var id = new FILE_ID_DESCRIPTOR
        {
            dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
            Type = 0, // FileIdType
            FileId = (long)frn,
            FileIdHigh = 0,
        };

        // FILE_READ_ATTRIBUTES + full sharing (incl. DELETE) lets us open a directory purely
        // to name it, even while it's in use — GetFinalPathNameByHandle needs no read access
        // to the contents. Requesting 0 access made open-by-id fail for in-use dirs, which is
        // why paths came back null in the first hardware run.
        IntPtr h = OpenFileById(_volume, ref id, FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
        if (h == InvalidHandle)
        {
            LastResolveError = Marshal.GetLastWin32Error();
            return null;
        }

        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint len = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
            if (len == 0)
            {
                return null;
            }

            string p = sb.ToString();
            return p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public void Dispose()
    {
        if (_volume != InvalidHandle)
        {
            CloseHandle(_volume);
        }
    }

    // -------------------------------------------------------------------- interop

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    // Must match the native FILE_ID_DESCRIPTOR size exactly (24 bytes: the union is a 16-byte
    // GUID). dwSize is validated by the OS, so an undersized struct makes OpenFileById fail —
    // the bug that made path resolution silently return null. FileId occupies the first 8
    // bytes of the union; the trailing 8 bytes pad out the GUID's remaining size.
    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public int Type;
        public long FileId;
        public long FileIdHigh; // union padding to reach the 16-byte GUID size
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenFileById(IntPtr volumeHandle, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr sec, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr h, System.Text.StringBuilder path, uint size, uint flags);
}
