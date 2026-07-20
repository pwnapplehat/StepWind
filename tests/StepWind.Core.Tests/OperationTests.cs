using StepWind.Core.Journal;
using Xunit;

namespace StepWind.Core.Tests;

public class OperationReconstructorTests
{
    private const ulong DirA = 100, DirB = 200, FileFrn = 500, FolderFrn = 600;

    private static UsnRecord Rec(ulong frn, ulong parent, UsnReason reason, string name, bool dir = false, long usn = 0)
        => new(frn, parent, usn, reason, DateTime.UtcNow.AddSeconds(usn), name, dir);

    private static string? Resolve(ulong frn) => frn switch
    {
        DirA => @"C:\Work\ProjectX",
        DirB => @"C:\Work\Archive",
        _ => null,
    };

    [Fact]
    public void Reconstructs_a_rename_from_the_old_new_record_pair()
    {
        var records = new[]
        {
            Rec(FileFrn, DirA, UsnReason.RenameOldName, "thesis.docx", usn: 1),
            Rec(FileFrn, DirA, UsnReason.RenameNewName | UsnReason.Close, "thesis-final.docx", usn: 2),
        };

        FileOperation op = Assert.Single(new OperationReconstructor(Resolve).Reconstruct(records));
        Assert.Equal(OperationKind.Rename, op.Kind);
        Assert.EndsWith(@"ProjectX\thesis.docx", op.OldPath);
        Assert.EndsWith(@"ProjectX\thesis-final.docx", op.NewPath);
    }

    [Fact]
    public void Reconstructs_a_cross_directory_move()
    {
        var records = new[]
        {
            Rec(FolderFrn, DirA, UsnReason.RenameOldName, "ProjectX", dir: true, usn: 1),
            Rec(FolderFrn, DirB, UsnReason.RenameNewName | UsnReason.Close, "ProjectX", dir: true, usn: 2),
        };

        FileOperation op = Assert.Single(new OperationReconstructor(Resolve).Reconstruct(records));
        Assert.Equal(OperationKind.Move, op.Kind);
        Assert.True(op.IsDirectory);
        Assert.True(op.IsReversible);
        Assert.EndsWith(@"ProjectX\ProjectX", op.OldPath);   // was inside ProjectX dir
        Assert.EndsWith(@"Archive\ProjectX", op.NewPath);    // now inside Archive dir
    }

    [Fact]
    public void Detects_posix_unlink_delete_via_frn_not_marker_name()
    {
        // Real modern sequence: file lives, then is renamed into $Deleted with a hex marker,
        // and the delete flag lands on that marker record for the SAME frn.
        var records = new[]
        {
            Rec(FileFrn, DirA, UsnReason.FileCreate | UsnReason.Close, "secret.docx", usn: 1),
            Rec(FileFrn, DirA, UsnReason.RenameOldName, "secret.docx", usn: 2),
            Rec(FileFrn, DirA, UsnReason.RenameNewName, "00012a4500000000:secret.docx", usn: 3),
            Rec(FileFrn, DirA, UsnReason.FileDelete | UsnReason.Close, "00012a4500000000:secret.docx", usn: 4),
        };

        IReadOnlyList<FileOperation> ops = new OperationReconstructor(Resolve).Reconstruct(records);
        FileOperation del = Assert.Single(ops, o => o.Kind == OperationKind.Delete);
        Assert.Equal("secret.docx", del.Name); // real name, not the $Deleted marker
        Assert.DoesNotContain(ops, o => o.Kind is OperationKind.Rename or OperationKind.Move); // marker rename ignored
    }

    [Fact]
    public void Posix_delete_is_emitted_at_marker_rename_even_if_filedelete_never_arrives()
    {
        // Windows 26200 reality (measured with a held shared-delete handle): the marker is
        // bare hex embedding the file's own FRN, and the FileDelete record only lands when
        // the last handle closes — potentially minutes later. The user saw the file vanish
        // at the marker rename, so the Delete op must exist WITHOUT the FileDelete record.
        // FileFrn = 500 = 0x1F4 → marker embeds "00000000000001F4".
        var records = new[]
        {
            Rec(FileFrn, DirA, UsnReason.FileCreate | UsnReason.Close, "report.xlsx", usn: 1),
            Rec(FileFrn, DirA, UsnReason.RenameOldName, "report.xlsx", usn: 2),
            Rec(FileFrn, 29, UsnReason.RenameNewName, "00000000000001F4175DB297", usn: 3),
            // no FileDelete record at all in this read window
        };

        IReadOnlyList<FileOperation> ops = new OperationReconstructor(Resolve).Reconstruct(records);
        FileOperation del = Assert.Single(ops, o => o.Kind == OperationKind.Delete);
        Assert.Equal("report.xlsx", del.Name);
        Assert.EndsWith(@"ProjectX\report.xlsx", del.OldPath);
        Assert.DoesNotContain(ops, o => o.Kind is OperationKind.Rename or OperationKind.Move);
    }

    [Fact]
    public void Late_filedelete_after_marker_rename_does_not_duplicate_the_delete()
    {
        var records = new[]
        {
            Rec(FileFrn, DirA, UsnReason.RenameOldName, "report.xlsx", usn: 1),
            Rec(FileFrn, 29, UsnReason.RenameNewName, "00000000000001F4175DB297", usn: 2),
            Rec(FileFrn, 29, UsnReason.FileDelete | UsnReason.RenameNewName | UsnReason.Close, "00000000000001F4175DB297", usn: 3),
        };

        IReadOnlyList<FileOperation> ops = new OperationReconstructor(Resolve).Reconstruct(records);
        Assert.Single(ops, o => o.Kind == OperationKind.Delete); // exactly one, not two
    }

    [Fact]
    public void A_users_hex_named_file_is_not_mistaken_for_a_delete_marker()
    {
        // 24 hex chars but the first 16 don't match the record's FRN — e.g. a git-object-like
        // name. Must be treated as a normal rename, never as a POSIX delete.
        var records = new[]
        {
            Rec(FileFrn, DirA, UsnReason.RenameOldName, "temp.bin", usn: 1),
            Rec(FileFrn, DirA, UsnReason.RenameNewName | UsnReason.Close, "AABBCCDDEEFF001122334455", usn: 2),
        };

        IReadOnlyList<FileOperation> ops = new OperationReconstructor(Resolve).Reconstruct(records);
        FileOperation op = Assert.Single(ops);
        Assert.Equal(OperationKind.Rename, op.Kind);
        Assert.DoesNotContain(ops, o => o.Kind == OperationKind.Delete);
    }

    [Fact]
    public void Emits_create_for_a_simple_new_file()
    {
        var records = new[] { Rec(FileFrn, DirA, UsnReason.FileCreate | UsnReason.DataExtend | UsnReason.Close, "new.txt", usn: 1) };
        FileOperation op = Assert.Single(new OperationReconstructor(Resolve).Reconstruct(records));
        Assert.Equal(OperationKind.Create, op.Kind);
    }

    [Fact]
    public void A_created_then_moved_file_is_one_move_not_a_duplicate()
    {
        var records = new[]
        {
            Rec(FileFrn, DirA, UsnReason.FileCreate | UsnReason.Close, "a.txt", usn: 1),
            Rec(FileFrn, DirA, UsnReason.RenameOldName, "a.txt", usn: 2),
            Rec(FileFrn, DirB, UsnReason.RenameNewName | UsnReason.Close, "a.txt", usn: 3),
        };

        IReadOnlyList<FileOperation> ops = new OperationReconstructor(Resolve).Reconstruct(records);
        Assert.Single(ops);
        Assert.Equal(OperationKind.Move, ops[0].Kind);
    }
}

public class OperationReverserTests
{
    private sealed class FakeFs : IFileSystemActions
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string From, string To)> Moves { get; } = [];

        public bool Exists(string path) => Files.Contains(path);
        public bool DirectoryExists(string path) => Dirs.Contains(path);
        public void Move(string from, string to)
        {
            if (Dirs.Remove(from)) Dirs.Add(to);
            else { Files.Remove(from); Files.Add(to); }
            Moves.Add((from, to));
        }
    }

    private static FileOperation Move(string from, string to, bool dir) => new()
    {
        Kind = OperationKind.Move,
        FileReferenceNumber = 1,
        TimestampUtc = DateTime.UtcNow,
        Name = Path.GetFileName(to),
        OldPath = from,
        NewPath = to,
        IsDirectory = dir,
    };

    [Fact]
    public void Reverses_a_folder_move_back_to_its_original_location()
    {
        var fs = new FakeFs();
        fs.Dirs.Add(@"C:\Work\Archive\ProjectX"); // where it was mistakenly moved
        FileOperation op = Move(@"C:\Work\ProjectX", @"C:\Work\Archive\ProjectX", dir: true);

        ReverseResult r = OperationReverser.Reverse(op, fs);

        Assert.True(r.Success);
        Assert.True(fs.DirectoryExists(@"C:\Work\ProjectX"));
        Assert.False(fs.DirectoryExists(@"C:\Work\Archive\ProjectX"));
    }

    [Fact]
    public void Refuses_to_reverse_when_the_original_location_is_now_occupied()
    {
        var fs = new FakeFs();
        fs.Files.Add(@"C:\Work\report.docx");            // the moved file, at its new spot
        fs.Files.Add(@"C:\Old\report.docx");             // DIFFERENT file now at the original spot
        FileOperation op = Move(@"C:\Old\report.docx", @"C:\Work\report.docx", dir: false);

        ReverseResult r = OperationReverser.Reverse(op, fs);

        Assert.False(r.Success);
        Assert.Empty(fs.Moves); // nothing overwritten
    }

    [Fact]
    public void Refuses_when_the_item_is_no_longer_at_its_moved_location()
    {
        var fs = new FakeFs(); // neither path exists
        FileOperation op = Move(@"C:\a\x.txt", @"C:\b\x.txt", dir: false);

        Assert.False(OperationReverser.Reverse(op, fs).Success);
    }

    [Fact]
    public void Delete_is_not_reversible_by_moving()
    {
        var fs = new FakeFs();
        var del = new FileOperation
        {
            Kind = OperationKind.Delete, FileReferenceNumber = 1, TimestampUtc = DateTime.UtcNow,
            Name = "x.txt", OldPath = @"C:\a\x.txt",
        };

        Assert.False(OperationReverser.Reverse(del, fs).Success);
    }
}
