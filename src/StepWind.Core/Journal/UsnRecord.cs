namespace StepWind.Core.Journal;

/// <summary>USN change-journal reason flags (subset StepWind acts on).</summary>
[Flags]
public enum UsnReason : uint
{
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    BasicInfoChange = 0x00008000,
    Close = 0x80000000,
}

/// <summary>
/// One parsed NTFS USN journal record. Works for both V2 and V3 physical layouts (the reader
/// normalizes them); the 128-bit V3 ids are represented by their low 64 bits, which is what
/// NTFS uses and what OpenFileById accepts.
/// </summary>
public readonly record struct UsnRecord(
    ulong FileReferenceNumber,
    ulong ParentFileReferenceNumber,
    long Usn,
    UsnReason Reason,
    DateTime TimestampUtc,
    string FileName,
    bool IsDirectory)
{
    public bool Has(UsnReason flag) => (Reason & flag) != 0;
}
