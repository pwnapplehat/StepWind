#if DEBUG
using System.IO;
using System.Text.Json;
using System.Windows;

namespace StepWind.App;

/// <summary>
/// DEBUG-only end-to-end runner (never compiled into Release): launched with
/// --e2e=&lt;report-file&gt;, it drives the REAL web UI in the REAL window against the REAL
/// service — DOM clicks through the actual dialogs, bridge calls over the actual pipe —
/// and writes one JSON line per step. The harness (bb-e2e) launches the exe and reads the
/// report. Scratch data lives under a dedicated folder and its history is purged at the end.
/// </summary>
public partial class MainWindow
{
    private const string E2EScratch = @"e:\Work\WindowsCleaner\_swweb_e2e";
    private const string E2EUndoDir = @"e:\Work\WindowsCleaner\_swweb_undo";

    private async Task RunE2EIfRequestedAsync(string[] args)
    {
        string? report = args.FirstOrDefault(a => a.StartsWith("--e2e=", StringComparison.OrdinalIgnoreCase))?[6..];
        if (string.IsNullOrWhiteSpace(report))
        {
            return;
        }

        var lines = new List<string>();
        void Log(string step, bool ok, string detail = "") =>
            lines.Add(JsonSerializer.Serialize(new { step, ok, detail }));

        async Task<string> Js(string expr) =>
            await Web.CoreWebView2.ExecuteScriptAsync(expr);

        async Task<bool> WaitJs(string boolExpr, int timeoutMs = 15000)
        {
            for (int waited = 0; waited < timeoutMs; waited += 250)
            {
                if (await Js(boolExpr) == "true")
                {
                    return true;
                }
                await Task.Delay(250);
            }
            return false;
        }

        try
        {
            // ── 1. Boot: service reachable, timeline rendered ──
            Log("boot-status", await WaitJs("lastStatus !== null"));
            Log("timeline-rendered", await WaitJs("document.querySelectorAll('.tl-row').length > 0 || document.querySelector('#view-timeline .empty') !== null"),
                await Js("tlOps.length"));

            // ── 2. Add a scratch protected folder through the real bridge patch path ──
            Directory.CreateDirectory(E2EScratch);
            await Js($$"""
                (async () => {
                  const s = await call('settings');
                  const set = (s.WatchedFolders || []).filter(f => f.toLowerCase() !== {{JsonSerializer.Serialize(E2EScratch.ToLowerInvariant())}});
                  set.push({{JsonSerializer.Serialize(E2EScratch)}});
                  await call('patch', { patch: { WatchedFolders: set } });
                  window.__e2eAdd = true;
                })()
                """);
            Log("folder-added", await WaitJs("window.__e2eAdd === true"));

            // ── 3. A file change is captured, browses, has history, and diffs ──
            string file = Path.Combine(E2EScratch, "e2e-doc.txt");
            await File.WriteAllTextAsync(file, "version one\nline two\n");
            await Task.Delay(4500); // watcher settle + capture
            await File.WriteAllTextAsync(file, "version one CHANGED\nline two\nline three\n");
            await Task.Delay(4500);

            await Js("""
                (async () => {
                  const h = await call('history', { relativePath: '_swweb_e2e/e2e-doc.txt' });
                  window.__e2eHist = h.length;
                  if (h.length >= 2) {
                    const d = await call('diff', { oldSel: h[1].VersionId, newSel: h[0].VersionId });
                    window.__e2eDiff = d.Diff.includes('CHANGED');
                  }
                })()
                """);
            Log("history-captured", await WaitJs("window.__e2eHist >= 2", 20000), await Js("window.__e2eHist"));
            Log("diff-works", await Js("window.__e2eDiff") == "true");

            // ── 4. Files view renders the scratch folder + history pane through the DOM ──
            await Js("navigate('files')");
            await Task.Delay(800);
            await Js("openHistoryByPath('_swweb_e2e/e2e-doc.txt')");
            Log("files-history-pane", await WaitJs("document.querySelectorAll('.v-row').length >= 2"));

            // ── 5. Remove the folder through the REAL dialog (Delete history choice) ──
            await Js($"removeFolder({JsonSerializer.Serialize(E2EScratch)})");
            bool dialogShown = await WaitJs("document.querySelector('.dlg') !== null");
            Log("remove-dialog-shown", dialogShown);
            if (dialogShown)
            {
                await Js("document.querySelector('.dlg [data-r=\"secondary\"]').click()"); // Delete history
                bool noticeShown = await WaitJs("document.querySelector('.dlg') && document.querySelector('.dlg .dlg-title').textContent.includes('History deleted')", 20000);
                Log("remove-deleted-history", noticeShown);
                await Js("document.querySelector('.dlg [data-r=\"primary\"]')?.click()");
            }
            await Js("""
                (async () => {
                  const s = await call('settings');
                  window.__e2eGone = !(s.WatchedFolders || []).some(f => f.toLowerCase().includes('_swweb_e2e'));
                  const b = await call('browse', { path: '', query: 'e2e-doc' });
                  window.__e2ePurged = b.length === 0;
                })()
                """);
            Log("folder-removed", await WaitJs("window.__e2eGone === true"));
            Log("history-purged", await WaitJs("window.__e2ePurged === true"));

            // ── 6. Settings toggle round-trip (auto-update off → verify → on) ──
            await Js("""
                (async () => {
                  const before = (await call('settings')).AutoUpdateEnabled;
                  await call('patch', { patch: { AutoUpdateEnabled: !before } });
                  const mid = (await call('settings')).AutoUpdateEnabled;
                  await call('patch', { patch: { AutoUpdateEnabled: before } });
                  const after = (await call('settings')).AutoUpdateEnabled;
                  window.__e2eToggle = (mid === !before) && (after === before);
                })()
                """);
            Log("settings-toggle-roundtrip", await WaitJs("window.__e2eToggle === true"));

            // ── 7. Flight-recorder undo through the timeline's own Undo button ──
            Directory.CreateDirectory(E2EUndoDir);
            string movedName = "e2e-undo-" + DateTime.Now.ToString("HHmmss");
            string src = Path.Combine(E2EUndoDir, movedName);
            Directory.CreateDirectory(src);
            await File.WriteAllTextAsync(Path.Combine(src, "precious.txt"), "do not lose");
            await Task.Delay(1200);
            string wrongPlace = Path.Combine(E2EUndoDir, "wrong-place");
            Directory.CreateDirectory(wrongPlace);
            Directory.Move(src, Path.Combine(wrongPlace, movedName));
            await Task.Delay(5000); // recorder poll + attribution

            await Js("navigate('timeline')");
            await Task.Delay(500);
            // The move is on the Desktop (outside protected folders). If the timeline scope is
            // "Protected only" it's correctly filtered out — force "All drives" so the whole-
            // machine flight recorder's entry (the thing under test) is actually visible.
            await Js("tlProtectedOnly = false; renderTimeline();");
            await Js("loadTimeline(false)");
            bool undoBtn = await WaitJs(
                $"[...document.querySelectorAll('.tl-row')].some(r => r.textContent.includes('{movedName}') && r.querySelector('.tl-undo'))", 15000);
            Log("undo-button-visible", undoBtn);
            if (undoBtn)
            {
                await Js($"[...document.querySelectorAll('.tl-row')].find(r => r.textContent.includes('{movedName}') && r.querySelector('.tl-undo')).querySelector('.tl-undo').click()");
                for (int i = 0; i < 40 && !Directory.Exists(src); i++)
                {
                    await Task.Delay(250);
                }
                Log("undo-moved-back", Directory.Exists(src),
                    File.Exists(Path.Combine(src, "precious.txt")) ? "content intact" : "CONTENT MISSING");
            }

            // ── 7b. Exclusions: a path excluded inside a protected folder is NOT versioned,
            //         and versioning resumes once the exclusion is removed. Unique filenames
            //         per run so a prior run's store can never mask a real regression. ──
            string tag = DateTime.Now.ToString("HHmmss");
            string exclName = $"excl-{tag}.txt", keepName = $"keep-{tag}.txt", resumeName = $"resume-{tag}.txt";
            Directory.CreateDirectory(E2EScratch);
            string exclDir = Path.Combine(E2EScratch, "excluded-build");
            Directory.CreateDirectory(exclDir);
            await Js($$"""
                (async () => {
                  try {
                    const s = await call('settings');
                    const set = (s.WatchedFolders || []).filter(f => f.toLowerCase() !== {{JsonSerializer.Serialize(E2EScratch.ToLowerInvariant())}});
                    set.push({{JsonSerializer.Serialize(E2EScratch)}});
                    await call('patch', { patch: { WatchedFolders: set, ExcludedPrefixes: [{{JsonSerializer.Serialize(exclDir)}}] } });
                    const after = await call('settings');
                    window.__xStored = (after.ExcludedPrefixes || []).some(p => p.toLowerCase() === {{JsonSerializer.Serialize(exclDir.ToLowerInvariant())}});
                  } catch (e) { window.__xStored = 'ERR:' + e.message; }
                })()
                """);
            await WaitJs("window.__xStored !== undefined");
            Log("exclusion-stored", await Js("window.__xStored") == "true", (await Js("String(window.__xStored)")).Trim('"'));

            await Task.Delay(2500); // let the watch rebuild + settle before writing
            await File.WriteAllTextAsync(Path.Combine(exclDir, exclName), "should NOT be versioned");
            await File.WriteAllTextAsync(Path.Combine(E2EScratch, keepName), "should be versioned");
            await Task.Delay(6000); // capture window

            await Js($$"""
                (async () => {
                  const ex = await call('browse', { path: '', query: '{{exclName}}' });
                  const inc = await call('browse', { path: '', query: '{{keepName}}' });
                  window.__xExcluded = ex.length === 0;   // excluded file has NO history
                  window.__xIncluded = inc.length > 0;    // sibling outside the exclusion does
                })()
                """);
            await WaitJs("window.__xExcluded !== undefined", 12000);
            Log("excluded-file-not-versioned", await Js("window.__xExcluded") == "true");
            Log("sibling-still-versioned", await Js("window.__xIncluded") == "true");

            // Remove the exclusion → a fresh file in the same dir IS versioned.
            await Js("call('patch', { patch: { ExcludedPrefixes: [] } }).then(() => window.__x3 = true)");
            await WaitJs("window.__x3 === true");
            await Task.Delay(2500);
            await File.WriteAllTextAsync(Path.Combine(exclDir, resumeName), "now it SHOULD be versioned");
            await Task.Delay(6000);
            await Js($"call('browse', {{ path: '', query: '{resumeName}' }}).then(r => window.__xResumed = r.length > 0)");
            await WaitJs("window.__xResumed !== undefined", 12000);
            Log("versioning-resumes-after-unexclude", await Js("window.__xResumed") == "true");

            // Clean up this test's protected folder + history.
            await Js("""
                (async () => {
                  const s = await call('settings');
                  await call('patch', { patch: { WatchedFolders: (s.WatchedFolders||[]).filter(f => !f.toLowerCase().includes('_swweb_e2e')), ExcludedPrefixes: [] } });
                  await call('purge', { selector: '_swweb_e2e' });
                  window.__xClean = true;
                })()
                """);
            await WaitJs("window.__xClean === true", 12000);

            // ── 8. Agents view renders real detections (no connect/disconnect — real machine) ──
            await Js("navigate('agents')");
            Log("agents-render", await WaitJs("document.querySelectorAll('#view-agents .g-card').length >= 10"));

            // ── 9. Palette opens and finds commands ──
            await Js("openPalette()");
            Log("palette", await WaitJs("!document.querySelector('#palette').classList.contains('hidden') && document.querySelectorAll('.pal-item').length >= 5"));
            await Js("closePalette()");
        }
        catch (Exception ex)
        {
            Log("exception", false, ex.Message);
        }
        finally
        {
            try { Directory.Delete(E2EUndoDir, true); } catch { }
            try { Directory.Delete(E2EScratch, true); } catch { }
            await File.WriteAllLinesAsync(report!, lines);
            RunOnUi(() => { Tray.Dispose(); Application.Current.Shutdown(); });
        }
    }
}
#endif
