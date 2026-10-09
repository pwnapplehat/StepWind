using System.Text.Json;
using StepWind.Core.Engine;
using StepWind.Core.Ipc;
using StepWind.Core.Storage;
using Xunit;

namespace StepWind.Core.Tests;

/// <summary>
/// Live disk checks for the HillsCloud follow-up: a real file is saved, an excluded
/// folder is not, an online-only file is counted instead of silently ignored, and a
/// drive can be switched off the timeline.
/// </summary>
public class HillsCloudLiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stepwind-live", Guid.NewGuid().ToString("N"));
    private readonly string _docs;
    private readonly string _excluded;
    private readonly string _missing;
    private readonly StepWindHost _host;

    public HillsCloudLiveTests()
    {
        _docs = Path.Combine(_root, "Docs");
        _excluded = Path.Combine(_docs, "skip");
        _missing = Path.Combine(_root, "Gone");
        Directory.CreateDirectory(_excluded);
        File.WriteAllText(Path.Combine(_docs, "keep.txt"), "saved on purpose");
        File.WriteAllText(Path.Combine(_excluded, "secret.txt"), "must not be versioned");
        string cloud = Path.Combine(_docs, "online-only.txt");
        File.WriteAllText(cloud, "placeholder");
        File.SetAttributes(cloud, FileAttributes.Offline);

        var settings = new StepWindSettings
        {
            StoreRoot = Path.Combine(_root, "store"),
            WatchedFolders = [_docs, _missing],
            ExcludedPrefixes = [_excluded],
            FlightRecorderEnabled = false,
        };
        _host = new StepWindHost(settings, new GzipBlobCodec());
    }

    [Fact]
    public async Task Live_folder_saves_a_real_file_and_skips_the_exclusion_and_the_cloud_file()
    {
        Assert.True((File.GetAttributes(Path.Combine(_docs, "online-only.txt")) & FileAttributes.Offline) != 0);

        await WaitForVersions("Docs/keep.txt", 1);
        Assert.Equal(0, await VersionCount("Docs/skip/secret.txt"));
        Assert.Equal(0, await VersionCount("Docs/online-only.txt"));

        JsonElement status = await WaitForScan();
        Assert.True(status.GetProperty("TotalVersions").GetInt32() >= 1);
        Assert.True(status.GetProperty("StoreBytes").GetInt64() > 0);
        Assert.True(status.GetProperty("ScanSkippedCloud").GetInt32() >= 1);
        Assert.Contains(_missing, status.GetProperty("UnreachableFolders").EnumerateArray().Select(e => e.GetString()));

        File.WriteAllText(Path.Combine(_docs, "keep.txt"), "edited after the baseline");
        await WaitForVersions("Docs/keep.txt", 2);
    }

    [Fact]
    public void Turning_a_real_drive_off_is_stored_and_reported_on_the_status()
    {
        string? drive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed)?.Name;
        Assert.NotNull(drive);
        string id = drive.TrimEnd('\\');

        IpcResponse patched = _host.Handle(new IpcRequest
        {
            Command = IpcCommand.SetSettings,
            Arg1 = JsonSerializer.Serialize(new { IgnoredVolumes = new[] { id } }),
        });
        Assert.True(patched.Ok, patched.Error);

        using JsonDocument settings = JsonDocument.Parse(_host.Handle(new IpcRequest { Command = IpcCommand.GetSettings }).Json!);
        Assert.Contains(settings.RootElement.GetProperty("IgnoredVolumes").EnumerateArray().Select(e => e.GetString()), v => string.Equals(v, id, StringComparison.OrdinalIgnoreCase));

        using JsonDocument status = JsonDocument.Parse(_host.Handle(new IpcRequest { Command = IpcCommand.GetStatus }).Json!);
        JsonElement volume = status.RootElement.GetProperty("Volumes").EnumerateArray()
            .Single(v => string.Equals(v.GetProperty("Name").GetString(), id, StringComparison.OrdinalIgnoreCase));
        Assert.True(volume.GetProperty("Ignored").GetBoolean());
        Assert.False(volume.GetProperty("Monitored").GetBoolean());
        Assert.Contains("off", volume.GetProperty("Note").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int> VersionCount(string rel)
    {
        IpcResponse resp = _host.Handle(new IpcRequest { Command = IpcCommand.GetHistory, Arg1 = rel });
        return resp.Ok && resp.Json is not null ? JsonSerializer.Deserialize<VersionEntry[]>(resp.Json)!.Length : 0;
    }

    private async Task WaitForVersions(string rel, int atLeast)
    {
        for (int i = 0; i < 80; i++)
        {
            if (await VersionCount(rel) >= atLeast)
            {
                return;
            }

            await Task.Delay(250);
        }

        Assert.Fail($"timed out waiting for {atLeast} version(s) of {rel}; have {await VersionCount(rel)}");
    }

    private async Task<JsonElement> WaitForScan()
    {
        JsonElement last = default;
        for (int i = 0; i < 80; i++)
        {
            using JsonDocument doc = JsonDocument.Parse(_host.Handle(new IpcRequest { Command = IpcCommand.GetStatus }).Json!);
            last = doc.RootElement.Clone();
            if (!last.GetProperty("ScanRunning").GetBoolean()
                && last.GetProperty("TotalVersions").GetInt32() >= 1
                && last.GetProperty("ScanSkippedCloud").GetInt32() >= 1)
            {
                return last;
            }

            await Task.Delay(250);
        }

        Assert.Fail("timed out waiting for the live scan to finish: " + last);
        return last;
    }

    public void Dispose() => _host.Dispose();
}
