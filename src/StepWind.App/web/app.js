/* ═══════════════════════════════════════════════════════════════════════════
   StepWind web UI — production application layer.

   Deliberately dependency-free vanilla JS (no framework, no build step, no
   node_modules): the whole UI is one auditable file served from disk. The
   surface is five views over a small JSON bridge; a framework would add a
   supply chain and a build pipeline without adding capability here.

   Bridge contract (host = MainWindow/Bridge.cs):
     web  → { id, method, params }
     host → { id, ok, data | error }   plus pushed events { type: ... }.
   ═══════════════════════════════════════════════════════════════════════════ */
"use strict";

/* ═══════════════ Bridge ═══════════════ */

const pending = new Map();
let nextMsgId = 1;

function call(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = nextMsgId++;
    pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, method, params });
  });
}

window.chrome.webview.addEventListener("message", (e) => {
  const msg = e.data;
  if (msg && msg.type === "winstate") {
    document.body.classList.toggle("maximized", !!msg.maximized);
    return;
  }
  const p = pending.get(msg.id);
  if (!p) return;
  pending.delete(msg.id);
  msg.ok ? p.resolve(msg.data) : p.reject(new Error(msg.error || "bridge error"));
});

/* ═══════════════ Helpers ═══════════════ */

const $ = (sel, root = document) => root.querySelector(sel);
const $$ = (sel, root = document) => [...root.querySelectorAll(sel)];

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function fmtSize(b) {
  if (b == null) return "";
  if (b < 1024) return b + " B";
  if (b < 1048576) return (b / 1024).toFixed(1) + " KB";
  if (b < 1073741824) return (b / 1048576).toFixed(1) + " MB";
  return (b / 1073741824).toFixed(2) + " GB";
}

function fmtClock(iso) {
  return new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
}

function dayLabel(iso) {
  const d = new Date(iso);
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const that = new Date(d); that.setHours(0, 0, 0, 0);
  const diff = Math.round((today - that) / 86400000);
  if (diff === 0) return "Today";
  if (diff === 1) return "Yesterday";
  return d.toLocaleDateString([], today.getFullYear() === d.getFullYear()
    ? { month: "long", day: "numeric" } : { month: "long", day: "numeric", year: "numeric" });
}

function fmtWhen(iso) {
  const d = new Date(iso);
  return d.toLocaleDateString([], { month: "short", day: "numeric" }) + ", " +
    d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
}

function shortPath(p) {
  if (!p) return "?";
  const parts = p.split("\\");
  return parts.length <= 3 ? p : "…\\" + parts.slice(-2).join("\\");
}

function stagger(el, i) { el.style.animationDelay = Math.min(i * 16, 360) + "ms"; }

/* ═══════════════ Toasts ═══════════════ */

function toast(kind, title, body, ms = 4200) {
  const el = document.createElement("div");
  el.className = "toast " + kind;
  el.innerHTML = `<div class="t-title">${esc(title)}</div><div class="t-body">${esc(body)}</div>`;
  $("#toasts").appendChild(el);
  setTimeout(() => { el.classList.add("out"); setTimeout(() => el.remove(), 300); }, ms);
}

/* ═══════════════ Dialogs (promise-based) ═══════════════ */

function showDialog({ title, body, primary, secondary, cancel = "Cancel", danger = false, hideCancel = false }) {
  return new Promise((resolve) => {
    const host = $("#dialog-host");
    const overlay = document.createElement("div");
    overlay.className = "dlg-overlay";
    overlay.innerHTML = `
      <div class="dlg" role="dialog" aria-modal="true">
        <div class="dlg-title">${esc(title)}</div>
        <div class="dlg-body">${esc(body)}</div>
        <div class="dlg-actions">
          ${hideCancel ? "" : `<button class="btn" data-r="cancel">${esc(cancel)}</button>`}
          ${secondary ? `<button class="btn" data-r="secondary">${esc(secondary)}</button>` : ""}
          <button class="btn ${danger ? "danger" : "primary"}" data-r="primary">${esc(primary)}</button>
        </div>
      </div>`;
    const done = (r) => { overlay.remove(); resolve(r); };
    overlay.onclick = (e) => { if (e.target === overlay && !hideCancel) done("cancel"); };
    $$("[data-r]", overlay).forEach((b) => (b.onclick = () => done(b.dataset.r)));
    host.appendChild(overlay);
    const primaryBtn = $('[data-r="primary"]', overlay);
    primaryBtn.focus();
    overlay.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !hideCancel) done("cancel");
      if (e.key === "Enter") done("primary");
    });
  });
}

const dlg = {
  notice: (title, body, ok = "OK") =>
    showDialog({ title, body, primary: ok, hideCancel: true }),
  confirm: async (title, body, primary, danger = false) =>
    (await showDialog({ title, body, primary, danger })) === "primary",
  three: (title, body, primary, secondary) =>
    showDialog({ title, body, primary, secondary }),
};

/* ═══════════════ Window controls ═══════════════ */

$("#win-min").onclick = () => call("win", { action: "minimize" });
$("#win-max").onclick = () => call("win", { action: "maximize" });
$("#win-close").onclick = () => call("win", { action: "close" });

/* ═══════════════ Router ═══════════════ */

const VIEW_TITLES = {
  timeline: "Timeline", files: "File versions", folders: "Protected folders",
  agents: "AI agents", settings: "Settings",
};
let currentView = "timeline";

function movePill(target) {
  const pill = $("#nav-pill");
  const nav = $("#nav");
  pill.style.transform = `translateY(${target.getBoundingClientRect().top - nav.getBoundingClientRect().top}px)`;
  pill.style.opacity = "1";
}

function navigate(view) {
  if (!VIEW_TITLES[view]) return;
  currentView = view;
  $$(".nav-item").forEach((b) => b.classList.toggle("active", b.dataset.view === view));
  $$(".view").forEach((v) => v.classList.toggle("active", v.id === "view-" + view));
  $("#crumbs").innerHTML =
    `<span class="crumb-app">StepWind</span><span class="crumb-sep">/</span>` +
    `<span class="crumb-here">${esc(VIEW_TITLES[view])}</span>`;
  movePill($(`.nav-item[data-view="${view}"]`));
  VIEW_LOADERS[view]?.();
}

$$(".nav-item").forEach((b) => (b.onclick = () => navigate(b.dataset.view)));

/* ═══════════════ Status + live refresh (3s, fingerprinted) ═══════════════ */

let lastStatus = null;
let watchedFolders = [];

async function pollStatus() {
  try {
    const s = await call("status");
    lastStatus = s;
    $("#status-dot").className = "dot ok";
    $("#status-title").textContent = "Protection active";
    $("#status-sub").textContent = s.WatchedRoots === 0
      ? "No folders protected yet"
      : `${s.WatchedRoots} folder${s.WatchedRoots === 1 ? "" : "s"} · ` +
        `${(s.TotalVersions ?? 0).toLocaleString()} versions · ${fmtSize(s.StoreBytes)}`;
  } catch {
    lastStatus = null;
    $("#status-dot").className = "dot bad";
    $("#status-title").textContent = "Not protecting";
    $("#status-sub").textContent = "The StepWind service is not reachable.";
  }
}

/* Auto-refresh: pull fresh data for the visible view, but only touch the DOM when the
   payload actually changed (fingerprints) — a 3s tick must never steal scroll position. */
async function liveTick() {
  await pollStatus();
  try {
    if (currentView === "timeline") await loadTimeline(true);
    else if (currentView === "files") { await refreshBrowse(true); await refreshHistory(true); }
  } catch { /* transient pipe failures already reflected in status */ }
}

/* ═══════════════ Timeline view ═══════════════ */

let tlFilter = "All";
let tlProtectedOnly = false;
let tlOps = [];
let tlFingerprint = "";

function describeOp(op) {
  switch (op.Kind) {
    case "Create": return `Created <span class="name">${esc(op.Name)}</span>`;
    case "Modify": return `Changed <span class="name">${esc(op.Name)}</span>`;
    case "Delete": return `Deleted <span class="name">${esc(op.Name)}</span>`;
    case "Rename": {
      const to = op.NewPath ? op.NewPath.split("\\").pop() : "";
      return `Renamed <span class="name">${esc(op.Name)}</span> → <span class="name">${esc(to)}</span>`;
    }
    case "Move":
      return `Moved <span class="name">${esc(op.Name)}</span>` +
        `<span style="color:var(--text-3);font-size:11.5px"> ${esc(shortPath(op.OldPath))} → ${esc(shortPath(op.NewPath))}</span>`;
    default: return esc(op.Name);
  }
}

function opInProtectedFolder(op) {
  for (const root of watchedFolders) {
    const prefix = root.replace(/\\+$/, "") + "\\";
    if ((op.OldPath || "").toLowerCase().startsWith(prefix.toLowerCase()) ||
        (op.NewPath || "").toLowerCase().startsWith(prefix.toLowerCase())) return true;
  }
  return false;
}

function renderTimeline() {
  const host = $("#view-timeline");
  let ops = tlFilter === "All" ? tlOps : tlOps.filter((o) => o.Kind === tlFilter);
  if (tlProtectedOnly) ops = ops.filter(opInProtectedFolder);
  const scroller = $(".tl-scroll", host);
  const keepScroll = scroller ? scroller.scrollTop : 0;

  let html = `
    <div class="page-head">
      <div>
        <div class="page-title">Timeline</div>
        <div class="page-sub">Everything that just happened to your files, on every drive — moves and renames undo in one click.</div>
      </div>
      <div class="page-actions">
        <button class="btn" id="tl-refresh">
          <svg viewBox="0 0 24 24"><path d="M21 12a9 9 0 1 1-2.6-6.4M21 3v5h-5"/></svg>
          Refresh
        </button>
      </div>
    </div>
    <div style="display:flex;align-items:center;margin-bottom:10px">
      <div class="chips">
        ${["All", "Move", "Rename", "Delete", "Create", "Modify"].map((k) =>
          `<button class="chip ${k === tlFilter ? "active" : ""}" data-k="${k}">${k === "All" ? "All" : k + "s"}</button>`).join("")}
      </div>
      <div class="seg">
        <button class="chip ${!tlProtectedOnly ? "active" : ""}" data-scope="all">All drives</button>
        <button class="chip ${tlProtectedOnly ? "active" : ""}" data-scope="protected">Protected only</button>
      </div>
    </div>
    <div class="tl-scroll">`;

  if (!ops.length) {
    const why = lastStatus && lastStatus.FlightRecorder === false
      ? "The flight recorder is off. Turn it on in Settings to record file operations across your drives."
      : lastStatus
        ? "No file activity to show here yet. Move, rename, or delete a file and it'll show up — with one-click undo."
        : "Start the StepWind service to begin recording.";
    html += `<div class="empty"><div><div class="empty-title">Nothing here yet</div>${esc(why)}</div></div>`;
  } else {
    let lastDay = null, i = 0;
    for (const op of ops) {
      const day = dayLabel(op.TimestampUtc);
      if (day !== lastDay) {
        lastDay = day;
        const count = ops.filter((o) => dayLabel(o.TimestampUtc) === day).length;
        html += `<div class="tl-day"><span class="tl-day-label">${esc(day)}</span><span class="tl-day-line"></span><span class="tl-day-count">${count} operation${count === 1 ? "" : "s"}</span></div>`;
      }
      const path = op.NewPath || op.OldPath || "";
      const proc = op.ByProcess ? `by ${esc(op.ByProcess)}` : "";
      html += `
        <div class="tl-row k-${op.Kind}" data-i="${i}">
          <div class="tl-rail"></div>
          <div class="tl-time">${fmtClock(op.TimestampUtc)}</div>
          <div class="tl-main">
            <div class="tl-desc"><span class="tl-badge">${op.Kind}</span> ${describeOp(op)}</div>
            <div class="tl-meta"><span class="path" title="${esc(path)}">${esc(path)}</span>${proc ? `<span class="proc">${proc}</span>` : ""}</div>
          </div>
          ${op.Reversible ? `<button class="btn primary tl-undo" data-op="${esc(op.OperationId)}">Undo</button>` : "<span></span>"}
        </div>`;
      i++;
    }
  }
  html += "</div>";
  host.innerHTML = html;

  const newScroller = $(".tl-scroll", host);
  if (newScroller && keepScroll) newScroller.scrollTop = keepScroll;

  $$(".tl-row", host).forEach((r, idx) => stagger(r, idx));
  $$(".chip[data-k]", host).forEach((c) => (c.onclick = () => { tlFilter = c.dataset.k; renderTimeline(); }));
  $$(".chip[data-scope]", host).forEach((c) => (c.onclick = () => {
    const protectedOnly = c.dataset.scope === "protected";
    if (protectedOnly === tlProtectedOnly) return;
    tlProtectedOnly = protectedOnly;
    renderTimeline();
    call("patch", { patch: { TimelineProtectedOnly: protectedOnly } }).catch(() => { });
  }));
  $("#tl-refresh", host).onclick = () => loadTimeline(false);
  $$(".tl-undo", host).forEach((b) => (b.onclick = async () => {
    b.disabled = true;
    try {
      await call("undo", { operationId: b.dataset.op });
      toast("ok", "Undone", "Moved back where it was.");
      await loadTimeline(false);
    } catch (err) {
      toast("err", "Couldn't undo", err.message);
      b.disabled = false;
    }
  }));
}

async function loadTimeline(silent) {
  let json = "[]";
  try {
    const data = (await call("timeline", { limit: 250 })) || [];
    json = JSON.stringify(data);
    if (silent && json === tlFingerprint) return; // nothing changed — leave the DOM alone
    tlOps = data;
  } catch {
    if (silent) return;
    tlOps = [];
  }
  tlFingerprint = json;
  renderTimeline();
}

/* ═══════════════ Files view (browser + history + diff) ═══════════════ */

const filesState = {
  path: "", query: "", entries: [], browseFp: "",
  historyPath: null, history: [], historyFp: "", selectedVersion: null,
};

function filesCrumbsHtml() {
  const parts = filesState.path ? filesState.path.split("/") : [];
  let html = `<span class="fc ${parts.length ? "" : "here"}" data-p="">Home</span>`;
  let acc = "";
  parts.forEach((p, i) => {
    acc = acc ? acc + "/" + p : p;
    const last = i === parts.length - 1;
    html += `<span class="sep">›</span><span class="fc ${last ? "here" : ""}" data-p="${esc(acc)}">${esc(p)}</span>`;
  });
  return html;
}

/* Builds the static two-pane scaffold once per navigation; list/history render separately
   so the 3s refresh can update one side without clobbering the other (or the diff). */
function renderFilesScaffold() {
  const host = $("#view-files");
  host.innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">File versions</div>
        <div class="page-sub">Browse protected folders, open any file's history, and see exactly what changed between versions.</div>
      </div>
      <div class="page-actions">
        <button class="btn" id="f-openfile">
          <svg viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Z"/></svg>
          Open a file…
        </button>
      </div>
    </div>
    <div class="files-grid">
      <div class="card files-pane">
        <div class="input-wrap" style="margin-bottom:8px">
          <svg viewBox="0 0 24 24"><circle cx="11" cy="11" r="7"/><path d="m21 21-4.3-4.3"/></svg>
          <input class="input" id="f-search" placeholder="Search this folder and everything under it…"/>
        </div>
        <div class="files-crumbs" id="f-crumbs"></div>
        <div class="files-list" id="f-list"></div>
      </div>
      <div class="card files-pane" id="hist-pane"></div>
    </div>`;

  $("#f-openfile", host).onclick = async () => {
    const path = await call("pickFile");
    if (path) { await openHistoryByPath(path); }
  };
  const search = $("#f-search", host);
  search.value = filesState.query;
  let debounce = 0;
  search.oninput = () => {
    clearTimeout(debounce);
    debounce = setTimeout(() => {
      filesState.query = search.value.trim();
      filesState.browseFp = "";
      refreshBrowse(false);
    }, 220);
  };
  renderHistoryPane();
}

function renderBrowseList() {
  const list = $("#f-list");
  const crumbs = $("#f-crumbs");
  if (!list) return;
  crumbs.innerHTML = filesCrumbsHtml();
  $$(".fc", crumbs).forEach((c) => {
    if (!c.classList.contains("here")) {
      c.onclick = () => { filesState.path = c.dataset.p; filesState.query = ""; $("#f-search").value = ""; filesState.browseFp = ""; refreshBrowse(false); };
    }
  });

  const st = filesState;
  if (!st.entries.length) {
    list.innerHTML = `<div class="empty"><div><div class="empty-title">${st.query ? "No matches" : "Nothing saved yet"}</div>${st.query ? "Try a different search." : "Versions appear here as protected files change."}</div></div>`;
    return;
  }
  list.innerHTML = st.entries.map((e0, i) => `
    <div class="f-row ${e0.RelativePath === st.historyPath ? "selected" : ""}" data-i="${i}">
      <div class="f-ico ${e0.IsFolder ? "folder" : "file"}">
        ${e0.IsFolder
          ? '<svg viewBox="0 0 24 24"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7Z"/></svg>'
          : '<svg viewBox="0 0 24 24"><path d="M14 3v5h5M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8l-5-5Z"/></svg>'}
      </div>
      <div style="min-width:0">
        <div class="f-name">${esc(e0.Name)}</div>
        <div class="f-sub">${e0.IsFolder
          ? `${e0.FileCount} file${e0.FileCount === 1 ? "" : "s"} · ${e0.VersionCount} version${e0.VersionCount === 1 ? "" : "s"}`
          : `${e0.VersionCount} version${e0.VersionCount === 1 ? "" : "s"} · ${fmtWhen(e0.LastCapturedUtc)}`}</div>
      </div>
      ${e0.IsFolder ? '<div class="f-chev"><svg viewBox="0 0 24 24"><path d="m9 6 6 6-6 6"/></svg></div>' : ""}
    </div>`).join("");
  $$(".f-row", list).forEach((r, i) => {
    stagger(r, i);
    r.onclick = () => {
      const entry = filesState.entries[+r.dataset.i];
      if (entry.IsFolder) {
        filesState.path = entry.RelativePath; filesState.query = "";
        $("#f-search").value = ""; filesState.browseFp = "";
        refreshBrowse(false);
      } else {
        $$(".f-row", list).forEach((x) => x.classList.remove("selected"));
        r.classList.add("selected");
        openHistoryByPath(entry.RelativePath);
      }
    };
  });
}

async function refreshBrowse(silent) {
  try {
    const data = (await call("browse", { path: filesState.path, query: filesState.query || null })) || [];
    const fp = filesState.path + "\u0001" + filesState.query + "\u0001" + JSON.stringify(data);
    if (silent && fp === filesState.browseFp) return;
    filesState.browseFp = fp;
    filesState.entries = data;
  } catch {
    if (silent) return;
    filesState.entries = [];
  }
  renderBrowseList();
}

function renderHistoryPane() {
  const pane = $("#hist-pane");
  if (!pane) return;
  const st = filesState;

  if (!st.historyPath) {
    pane.innerHTML = `<div class="empty"><div>
      <div class="empty-title">Pick a file</div>
      Every saved version appears here — click a version to see exactly what changed, restore any of them, even after an overwrite or delete.
    </div></div>`;
    return;
  }

  pane.innerHTML = `
    <div class="hist-head" style="display:flex;align-items:flex-start">
      <div style="min-width:0">
        <div class="hist-title">Version history</div>
        <div class="hist-path">${esc(st.historyPath)}</div>
      </div>
      <div class="hist-toolbar">
        <button class="btn danger-ghost" id="h-delete" title="Delete every saved version of this file (the file on disk is not touched)">
          <svg viewBox="0 0 24 24"><path d="M3 6h18M8 6V4a1 1 0 0 1 1-1h6a1 1 0 0 1 1 1v2m3 0-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/></svg>
        </button>
      </div>
    </div>
    <div class="hist-list" id="h-list">
      ${st.history.length ? st.history.map((v, i) => `
        <div class="v-row ${st.selectedVersion === v.VersionId ? "selected" : ""}" data-i="${i}">
          <span class="v-when">${fmtWhen(v.CapturedUtc)}</span>
          <span class="v-size">${fmtSize(v.Size)}</span>
          <span class="v-reason">${esc(v.Reason)}</span>
          <span class="v-actions"><button class="btn v-restore" data-v="${esc(v.VersionId)}">Restore</button></span>
        </div>`).join("")
      : `<div class="empty" style="padding:24px"><div>No saved versions for this file yet.</div></div>`}
    </div>
    <div class="diff-box" id="diff-box">
      <div class="diff-note">Select a version above to see what changed between it and the file on disk now.</div>
    </div>`;

  $("#h-delete", pane).onclick = deleteFileHistory;
  $$(".v-row", pane).forEach((r, i) => {
    stagger(r, i);
    r.onclick = (ev) => {
      if (ev.target.closest(".v-restore")) return;
      const v = st.history[+r.dataset.i];
      st.selectedVersion = v.VersionId;
      $$(".v-row", pane).forEach((x) => x.classList.remove("selected"));
      r.classList.add("selected");
      showDiff(v);
    };
  });
  $$(".v-restore", pane).forEach((b) => (b.onclick = () => restoreVersion(b)));
}

async function openHistoryByPath(relativeOrAbsolutePath) {
  filesState.historyPath = relativeOrAbsolutePath;
  filesState.selectedVersion = null;
  filesState.historyFp = "";
  await refreshHistory(false);
}

async function refreshHistory(silent) {
  const st = filesState;
  if (!st.historyPath) return;
  try {
    const data = (await call("history", { relativePath: st.historyPath })) || [];
    const fp = st.historyPath + "\u0001" + JSON.stringify(data);
    if (silent && fp === st.historyFp) return; // don't clobber selection/diff on the 3s tick
    st.historyFp = fp;
    st.history = data;
  } catch (err) {
    if (silent) return;
    st.history = [];
    toast("err", "Couldn't load history", err.message);
  }
  renderHistoryPane();
}

async function restoreVersion(btn) {
  btn.disabled = true;
  try {
    const res = await call("restore", { versionId: btn.dataset.v });
    const p = res?.RestoredPath || "";
    const r = await dlg.three("Restored — nothing overwritten",
      `The version was recovered to a NEW file next to the original:\n\n${p}`,
      "Show in Explorer", "Close");
    if (r === "primary") call("openPath", { path: p });
  } catch (err) {
    dlg.notice("Couldn't restore", err.message);
  }
  btn.disabled = false;
}

async function deleteFileHistory() {
  const target = filesState.historyPath;
  if (!target) return;
  const ok = await dlg.confirm("Delete this file's history?",
    `${target}\n\nEvery saved version of this file will be permanently deleted. The file itself on disk is not touched.`,
    "Delete history", true);
  if (!ok) return;
  try {
    const res = await call("purge", { selector: target });
    filesState.historyPath = null; filesState.history = []; filesState.selectedVersion = null;
    filesState.browseFp = "";
    renderHistoryPane();
    await refreshBrowse(false);
    dlg.notice("History deleted", purgeSummary(res));
  } catch (err) {
    dlg.notice("Couldn't delete history", err.message);
  }
}

function purgeSummary(res) {
  const v = res?.RemovedVersions ?? 0, b = res?.SweptBlobs ?? 0;
  return `Deleted ${v.toLocaleString()} version${v === 1 ? "" : "s"} and freed ${b.toLocaleString()} stored chunk${b === 1 ? "" : "s"}.`;
}

function renderDiffText(diffText) {
  return diffText.split("\n").map((l) => {
    let cls = "";
    if (l.startsWith("+++") || l.startsWith("---")) cls = "head";
    else if (l.startsWith("@@")) cls = "hunk";
    else if (l.startsWith("+")) cls = "add";
    else if (l.startsWith("-")) cls = "del";
    return `<div class="dl ${cls}">${esc(l) || " "}</div>`;
  }).join("");
}

async function showDiff(version) {
  const box = $("#diff-box");
  box.innerHTML = `<div class="skeleton" style="height:120px;margin:12px"></div>`;
  try {
    const d = await call("diff", { oldSel: version.VersionId, newSel: "current:" + version.RelativePath });
    box.innerHTML = d.Binary
      ? `<div class="diff-note">Binary content — no text diff. (${fmtSize(d.OldSize)} → ${fmtSize(d.NewSize)})</div>`
      : renderDiffText(d.Diff || "(no differences)");
  } catch (err) {
    // The live file may be deleted — show the version's own content instead.
    try {
      const c = await call("read", { selector: version.VersionId });
      box.innerHTML =
        `<div class="diff-note">The live file isn't on disk right now — showing this saved version's content.</div>` +
        (c.Content != null
          ? c.Content.split("\n").map((l) => `<div class="dl">${esc(l) || " "}</div>`).join("")
          : `<div class="diff-note">${c.IsBinary ? "Binary content." : "Too large to preview."}</div>`);
    } catch (err2) {
      box.innerHTML = `<div class="diff-note">${esc(err2.message)}</div>`;
    }
  }
}

function loadFiles() {
  renderFilesScaffold();
  refreshBrowse(false);
}

/* ═══════════════ Folders view ═══════════════ */

async function loadFolders() {
  const host = $("#view-folders");
  let settings = null;
  try { settings = await call("settings"); } catch { }
  const folders = settings?.WatchedFolders || [];
  watchedFolders = folders;

  host.innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">Protected folders</div>
        <div class="page-sub">Files in these folders get continuous version history — every save is kept, deduplicated and compressed, so you can go back in time.</div>
      </div>
      <div class="page-actions"><button class="btn primary" id="fo-add">
        <svg viewBox="0 0 24 24"><path d="M12 5v14M5 12h14"/></svg> Add folder
      </button></div>
    </div>
    <div class="card banner">
      <svg viewBox="0 0 24 24"><path d="M12 22s8-3.4 8-10V5.5L12 2 4 5.5V12c0 6.6 8 10 8 10Z"/><path d="m9 12 2 2 4-4"/></svg>
      <div>A version is captured a couple of seconds after a file settles. Identical re-saves are skipped, build junk and caches are excluded automatically, and restores never overwrite your current file — the recovered version lands next to it. Removing a folder only stops new captures: already-saved versions stay restorable until retention ages them out.</div>
    </div>
    <div class="card-grid">
      ${folders.map((f, i) => `
        <div class="card g-card" data-i="${i}">
          <div class="g-head">
            <div class="g-ico green"><svg viewBox="0 0 24 24"><path d="M12 22s8-3.4 8-10V5.5L12 2 4 5.5V12c0 6.6 8 10 8 10Z"/><path d="m9 12 2 2 4-4"/></svg></div>
            <div style="min-width:0">
              <div class="g-title" style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis" title="${esc(f)}">${esc(f.split("\\").pop())}</div>
              <div class="g-sub ok">Watching, including subfolders</div>
            </div>
          </div>
          <div class="g-foot">
            <div class="g-path">${esc(f)}</div>
            <button class="btn g-open" data-p="${esc(f)}">Open</button>
            <button class="btn danger-ghost g-remove" data-p="${esc(f)}" title="Stop protecting this folder">
              <svg viewBox="0 0 24 24"><path d="M18 6 6 18M6 6l12 12"/></svg>
            </button>
          </div>
        </div>`).join("")}
      ${!folders.length ? `<div class="empty" style="grid-column:1/-1"><div><div class="empty-title">No folders protected</div>Add a folder to start building version history for the files inside it.</div></div>` : ""}
    </div>`;

  $$(".g-card", host).forEach((c, i) => stagger(c, i));
  $$(".g-open", host).forEach((b) => (b.onclick = () => call("openPath", { path: b.dataset.p })));
  $("#fo-add", host).onclick = addFolder;
  $$(".g-remove", host).forEach((b) => (b.onclick = () => removeFolder(b.dataset.p)));
}

async function addFolder() {
  const folder = await call("pickFolder", { title: "Choose a folder to protect" });
  if (!folder) return;
  const settings = await call("settings");
  const set = settings?.WatchedFolders || [];
  if (set.some((f) => f.toLowerCase() === folder.toLowerCase())) {
    toast("ok", "Already protected", folder);
    return;
  }
  try {
    await call("patch", { patch: { WatchedFolders: [...set, folder] } });
    toast("ok", "Now protecting", folder);
    await loadFolders();
    await pollStatus();
  } catch (err) {
    dlg.notice("Couldn't add folder", err.message);
  }
}

async function removeFolder(folder) {
  // Removal is an explicit decision about the DATA, not just the watching:
  // Keep = versions stay restorable; Delete = gone now, space freed.
  const choice = await dlg.three("Stop protecting this folder?",
    `${folder}\n\nNew changes will no longer be captured. What should happen to the versions already saved for this folder?`,
    "Keep history", "Delete history");
  if (choice === "cancel") return;

  try {
    const settings = await call("settings");
    const set = (settings?.WatchedFolders || []).filter((f) => f.toLowerCase() !== folder.toLowerCase());
    await call("patch", { patch: { WatchedFolders: set } });

    if (choice === "secondary") {
      // Store paths are "<folder-basename>/..." — the basename selects this folder's history.
      const selector = folder.replace(/[\\/]+$/, "").split("\\").pop();
      const res = await call("purge", { selector });
      dlg.notice("History deleted", purgeSummary(res));
    } else {
      toast("ok", "Stopped protecting", "Already-saved versions stay restorable from File versions.");
    }
    await loadFolders();
    await pollStatus();
  } catch (err) {
    dlg.notice("Couldn't remove folder", err.message);
  }
}

/* ═══════════════ AI agents view ═══════════════ */

async function loadAgents() {
  const host = $("#view-agents");
  let agents = [];
  let mcp = null;
  try { agents = (await call("agents")) || []; mcp = await call("mcpInfo"); } catch { }
  const found = agents.filter((a) => a.detected).length;
  const connected = agents.filter((a) => a.connected).length;
  const order = (a) => a.detected ? (a.connected ? (a.needsRepair ? 0 : 1) : 0) : 2;
  agents.sort((a, b) => order(a) - order(b) || a.name.localeCompare(b.name));

  host.innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">AI agents</div>
        <div class="page-sub">Give AI coding tools a time machine: they can checkpoint a file before a risky edit, diff exactly what they changed, and restore — never delete.</div>
      </div>
      <div class="page-actions"><button class="btn" id="ag-rescan">
        <svg viewBox="0 0 24 24"><path d="M21 12a9 9 0 1 1-2.6-6.4M21 3v5h-5"/></svg> Re-scan
      </button></div>
    </div>
    <div class="card banner">
      <svg viewBox="0 0 24 24"><path d="M12 22s8-3.4 8-10V5.5L12 2 4 5.5V12c0 6.6 8 10 8 10Z"/><path d="m9 12 2 2 4-4"/></svg>
      <div>${found === 0
        ? "No supported AI tools were found on this PC. Install one (Cursor, Claude, VS Code…) or use the manual setup below."
        : `${found} AI tool${found === 1 ? "" : "s"} found on this PC · ${connected} connected.`}
        Connecting merges StepWind's MCP server into that tool's config — a backup is taken first and every change is reversible. The agent gets read + restore powers only: it can never delete history or change settings.</div>
    </div>
    <div class="scroll-y" style="flex:1;min-height:0">
      <div class="card-grid" style="overflow:visible;scrollbar-gutter:auto">
        ${agents.map((a, i) => `
          <div class="card g-card ${a.detected ? "" : "dim"}" data-i="${i}">
            <div class="g-head">
              <div class="g-ico ${a.connected ? "green" : "indigo"}">
                <svg viewBox="0 0 24 24"><rect x="4" y="8" width="16" height="12" rx="3"/><path d="M12 8V4M8 4h8M9 14h.01M15 14h.01M9.5 17h5"/></svg>
              </div>
              <div style="min-width:0">
                <div class="g-title">${esc(a.name)}</div>
                <div class="g-sub ${a.connected && !a.needsRepair ? "ok" : ""}" ${a.needsRepair ? 'style="color:var(--warn)"' : ""}>${a.connected
                  ? (a.needsRepair ? "Connected, but pointing at an old StepWind location" : "Connected")
                  : a.detected ? "Ready to connect" : "Not found on this PC"}</div>
              </div>
            </div>
            <div class="g-foot">
              <div class="g-path" title="${esc(a.configPath)}">${esc(a.configPath)}</div>
              ${a.detected && !a.connected ? `<button class="btn primary ag-act" data-id="${esc(a.id)}" data-act="connect">Connect</button>` : ""}
              ${a.connected && a.needsRepair ? `<button class="btn primary ag-act" data-id="${esc(a.id)}" data-act="connect">Repair</button>` : ""}
              ${a.connected ? `<button class="btn ag-act" data-id="${esc(a.id)}" data-act="disconnect">Disconnect</button>` : ""}
            </div>
          </div>`).join("")}
      </div>

      <div class="page-sub" style="margin:14px 2px 8px;font-weight:600;color:var(--text-3);text-transform:uppercase;font-size:10px;letter-spacing:1.4px">Manual setup — any other MCP client</div>
      <div class="card set-card" style="max-width:820px;margin-bottom:8px">
        <div class="set-sub" style="margin-bottom:10px">Using an AI tool that isn't listed above? Paste this into its MCP configuration — it's the standard mcpServers shape nearly every client understands.</div>
        <div style="position:relative">
          <pre class="mono" style="background:var(--inset);border:1px solid var(--line);border-radius:8px;padding:12px;font-size:11.5px;line-height:1.6;overflow:auto;user-select:text">${esc(mcp?.snippet || "")}</pre>
          <button class="btn" id="ag-copy" style="position:absolute;top:8px;right:8px;padding:5px 12px">Copy</button>
        </div>
        <div class="set-sub" style="margin-top:10px">Before StepWind edits any tool's config it saves a copy — newest first in the backups folder.</div>
        <div class="set-actions" style="justify-content:flex-start;margin-top:8px">
          <button class="btn" id="ag-backups">Open backups folder</button>
        </div>
      </div>
    </div>`;

  $$(".g-card", host).forEach((c, i) => stagger(c, i));
  $("#ag-rescan", host).onclick = loadAgents;
  $("#ag-backups", host).onclick = () => call("openBackups");
  $("#ag-copy", host).onclick = async () => {
    await call("copyText", { text: mcp?.snippet || "" });
    toast("ok", "Copied", "Paste it into the AI tool's MCP settings.");
  };
  $$(".ag-act", host).forEach((b) => (b.onclick = async () => {
    if (b.dataset.act === "disconnect") {
      const ok = await dlg.confirm(`Disconnect ${b.dataset.id}?`,
        "StepWind's entry will be removed from this tool's MCP config. The tool itself and the rest of its config are untouched, and you can reconnect any time.",
        "Disconnect");
      if (!ok) return;
    }
    b.disabled = true;
    try {
      const res = await call(b.dataset.act === "connect" ? "agentConnect" : "agentDisconnect", { id: b.dataset.id });
      res.ok ? toast("ok", b.dataset.act === "connect" ? "Connected" : "Disconnected", res.message, 6000)
             : dlg.notice("Not connected", res.message);
    } catch (err) {
      dlg.notice("Problem", err.message);
    }
    await loadAgents();
  }));
}

/* ═══════════════ Settings view ═══════════════ */

async function loadSettings() {
  const host = $("#view-settings");
  let s = null;
  try { s = await call("settings"); } catch { }
  watchedFolders = s?.WatchedFolders || watchedFolders;
  tlProtectedOnly = !!s?.TimelineProtectedOnly;

  const sw = (id, on) => `<div class="switch ${on ? "on" : ""}" id="${id}" role="switch" aria-checked="${!!on}" tabindex="0"></div>`;
  host.innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">Settings</div>
        <div class="page-sub">StepWind is set-and-forget by design — there isn't much to configure.</div>
      </div>
    </div>
    <div class="set-scroll">
     <div class="set-cols">
      <div class="set-section">
        <div class="set-label">Updates</div>
        <div class="card set-card"><div class="set-row">
          <div><div class="set-title">Automatic silent updates</div>
          <div class="set-sub">The background service checks GitHub daily, verifies each update's SHA-256, and installs it with zero prompts.</div></div>
          ${sw("sw-update", s?.AutoUpdateEnabled)}
        </div></div>
      </div>

      <div class="set-section">
        <div class="set-label">Protection</div>
        <div class="card set-card">
          <div class="set-row">
            <div><div class="set-title">Flight recorder</div>
            <div class="set-sub">Records file operations on all NTFS drives for the timeline and one-click undo. Takes effect immediately — no restart.</div></div>
            ${sw("sw-fr", s?.FlightRecorderEnabled)}
          </div>
          <div class="set-row">
            <div><div class="set-title">Encryption at rest</div>
            <div class="set-sub">AES-256-GCM with a key sealed by Windows (machine scope) — a stolen or offline drive can't read your history. Toggling re-encodes existing versions in the background; everything stays restorable throughout. File contents are encrypted; the index of names and dates is not.</div>
            ${lastStatus?.ReEncoding ? '<div class="badge-live">Re-encoding your history in the background…</div>' : ""}</div>
            ${sw("sw-enc", s?.EncryptionEnabled)}
          </div>
          <div class="set-row">
            <div><div class="set-title">History storage</div>
            <div class="set-sub">Deduplicated and compressed — old versions are garbage-collected automatically per the retention rules below.</div></div>
            <div class="set-value">${lastStatus ? `${(lastStatus.TotalVersions ?? 0).toLocaleString()} versions · ${fmtSize(lastStatus.StoreBytes)}` : "—"}</div>
          </div>
        </div>
      </div>

      <div class="set-section">
        <div class="set-label">Retention</div>
        <div class="card set-card">
          <div class="set-sub">How long versions are kept. Everything is kept for the first window, then thinned to hourly and daily as versions age.</div>
          <div class="ret-grid">
            ${[["ret-kah", "Keep all (hours)", s?.RetentionKeepAllHours],
               ["ret-hd", "Hourly (days)", s?.RetentionHourlyDays],
               ["ret-dd", "Daily (days)", s?.RetentionDailyDays],
               ["ret-mad", "Max age (days)", s?.RetentionMaxAgeDays],
               ["ret-mv", "Max per file", s?.RetentionMaxVersionsPerFile]]
              .map(([id, label, val]) => `
                <div class="ret-cell"><label for="${id}">${label}</label>
                <input id="${id}" type="number" min="0" value="${val ?? ""}"/></div>`).join("")}
          </div>
          <div class="set-actions">
            <button class="btn" id="ret-run" title="Apply the retention rules immediately instead of waiting for the daily pass">Run cleanup now</button>
            <button class="btn primary" id="ret-apply" style="min-width:90px">Apply</button>
          </div>
        </div>
      </div>

      <div class="set-section">
        <div class="set-label">Data management</div>
        <div class="card set-card">
          <div class="set-row">
            <div><div class="set-title">Clean up unprotected history</div>
            <div class="set-sub">Deletes saved versions belonging to folders you no longer protect.</div></div>
            <button class="btn" id="dm-unprot" style="margin-left:auto;flex-shrink:0">Clean up</button>
          </div>
          <div class="set-row">
            <div><div class="set-title" style="color:var(--danger)">Delete all history</div>
            <div class="set-sub">Permanently deletes every saved version of every file and frees the disk space. Your actual files are not touched.</div></div>
            <button class="btn danger" id="dm-all" style="margin-left:auto;flex-shrink:0">Delete all…</button>
          </div>
        </div>
      </div>

      <div class="set-section">
        <div class="set-label">Shortcuts</div>
        <div class="card set-card"><div class="set-row">
          <div><div class="set-title">Panic hotkey</div>
          <div class="set-sub">Opens StepWind from anywhere the moment something goes wrong.</div></div>
          <div class="set-value" style="color:var(--text)">Ctrl + Shift + Z</div>
        </div></div>
      </div>

      <div class="set-section">
        <div class="set-label">About</div>
        <div class="card set-card" style="margin-bottom:8px"><div class="set-row">
          <div><div class="set-title">StepWind <span id="ab-ver" style="color:var(--text-3);font-weight:400"></span></div>
          <div class="set-sub">Free · open source · 100% local · no cloud · no account · no telemetry</div></div>
          <div style="margin-left:auto;display:flex;gap:8px;flex-shrink:0">
            <button class="btn" id="ab-site">stepwind.app</button>
            <button class="btn" id="ab-repo">GitHub</button>
          </div>
        </div></div>
      </div>
     </div>
    </div>`;

  call("appInfo").then((info) => { const el = $("#ab-ver"); if (el) el.textContent = "v" + info.version; });

  // Toggles: optimistic flip, push the patch, then reload from the service — on failure the
  // reload snaps the switch back to the truth (e.g. flight recorder in an unprivileged run).
  wireSwitch("sw-update", (on) => patchAndReload({ AutoUpdateEnabled: on }, false));
  wireSwitch("sw-fr", (on) => patchAndReload({ FlightRecorderEnabled: on }, true));
  wireSwitch("sw-enc", (on) => patchAndReload({ EncryptionEnabled: on }, true));

  $("#ret-apply", host).onclick = async () => {
    const patch = {
      RetentionKeepAllHours: +$("#ret-kah").value || 0,
      RetentionHourlyDays: +$("#ret-hd").value || 0,
      RetentionDailyDays: +$("#ret-dd").value || 0,
      RetentionMaxAgeDays: +$("#ret-mad").value || 0,
      RetentionMaxVersionsPerFile: +$("#ret-mv").value || 0,
    };
    try {
      await call("patch", { patch });
      await loadSettings(); // reflect service-side clamping immediately
      dlg.notice("Retention updated", "New retention rules saved. They apply on the next cleanup pass — or run one now.");
    } catch (err) {
      dlg.notice("Couldn't update retention", err.message);
    }
  };

  $("#ret-run", host).onclick = async () => {
    try {
      const r = await call("runRetention");
      await pollStatus();
      dlg.notice("Cleanup",
        `Cleanup done — kept ${(r?.VersionsKept ?? 0).toLocaleString()} of ${(r?.VersionsBefore ?? 0).toLocaleString()} versions, freed ${(r?.BlobsSwept ?? 0).toLocaleString()} chunk${(r?.BlobsSwept ?? 0) === 1 ? "" : "s"}.`);
    } catch (err) {
      dlg.notice("Couldn't run cleanup", err.message);
    }
  };

  $("#dm-unprot", host).onclick = async () => {
    const ok = await dlg.confirm("Clean up unprotected history?",
      "Saved versions belonging to folders you no longer protect will be permanently deleted. Files on disk are not touched.",
      "Clean up", true);
    if (!ok) return;
    try {
      const res = await call("purge", { selector: "unprotected" });
      await pollStatus();
      dlg.notice("Clean-up done", purgeSummary(res));
    } catch (err) { dlg.notice("Couldn't clean up", err.message); }
  };

  $("#dm-all", host).onclick = async () => {
    const ok = await dlg.confirm("Delete ALL history?",
      "Every saved version of every file will be permanently deleted and the disk space freed. This cannot be undone. Your actual files on disk are not touched.",
      "Delete everything", true);
    if (!ok) return;
    try {
      const res = await call("purge", { selector: "*" });
      await pollStatus();
      dlg.notice("History deleted", purgeSummary(res));
    } catch (err) { dlg.notice("Couldn't delete history", err.message); }
  };

  $("#ab-site", host).onclick = () => call("openUrl", { url: "https://stepwind.app" });
  $("#ab-repo", host).onclick = () => call("openUrl", { url: "https://github.com/pwnapplehat/StepWind" });
}

function wireSwitch(id, onToggle) {
  const el = $("#" + id);
  if (!el) return;
  const toggle = () => {
    const next = !el.classList.contains("on");
    el.classList.toggle("on", next);
    el.setAttribute("aria-checked", String(next));
    onToggle(next);
  };
  el.onclick = toggle;
  el.onkeydown = (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); toggle(); } };
}

async function patchAndReload(patch, reload) {
  try {
    await call("patch", { patch });
  } catch (err) {
    dlg.notice("Couldn't change setting", err.message);
  }
  if (reload) {
    await pollStatus();
    await loadSettings(); // snaps switches back to the service's truth on failure
  }
}

/* ═══════════════ Command palette ═══════════════ */

const PALETTE_COMMANDS = [
  { label: "Go to Timeline", hint: "view", run: () => navigate("timeline") },
  { label: "Go to File versions", hint: "view", run: () => navigate("files") },
  { label: "Go to Protected folders", hint: "view", run: () => navigate("folders") },
  { label: "Go to AI agents", hint: "view", run: () => navigate("agents") },
  { label: "Go to Settings", hint: "view", run: () => navigate("settings") },
  { label: "Add a folder to protect", hint: "action", run: () => { navigate("folders"); setTimeout(addFolder, 250); } },
  { label: "Open a file's version history", hint: "action", run: async () => { navigate("files"); const p = await call("pickFile"); if (p) await openHistoryByPath(p); } },
  { label: "Re-scan AI tools", hint: "action", run: () => navigate("agents") },
  { label: "Open MCP config backups folder", hint: "action", run: () => call("openBackups") },
];
let palItems = [];
let palIndex = 0;

function openPalette() {
  $("#palette").classList.remove("hidden");
  const inp = $("#palette-input");
  inp.value = ""; inp.focus();
  palIndex = 0;
  renderPalette("");
}

function closePalette() { $("#palette").classList.add("hidden"); }

async function renderPalette(q) {
  const ql = q.toLowerCase();
  palItems = PALETTE_COMMANDS.filter((c) => c.label.toLowerCase().includes(ql));

  if (q.length >= 2) {
    try {
      const hits = (await call("browse", { path: "", query: q })) || [];
      for (const h of hits.filter((x) => !x.IsFolder).slice(0, 8)) {
        palItems.push({
          label: h.RelativePath, hint: `${h.VersionCount} version${h.VersionCount === 1 ? "" : "s"}`,
          run: () => { navigate("files"); setTimeout(() => openHistoryByPath(h.RelativePath), 300); },
        });
      }
    } catch { }
  }

  palIndex = Math.min(palIndex, Math.max(0, palItems.length - 1));
  $("#palette-list").innerHTML = palItems.map((c, i) => `
    <div class="pal-item ${i === palIndex ? "active" : ""}" data-i="${i}">
      <svg viewBox="0 0 24 24"><path d="m9 18 6-6-6-6"/></svg>
      <span>${esc(c.label)}</span><span class="hint">${esc(c.hint)}</span>
    </div>`).join("") ||
    `<div class="pal-item">No results</div>`;
  $$(".pal-item", $("#palette-list")).forEach((el) => {
    el.onclick = () => { const c = palItems[+el.dataset.i]; if (c) { closePalette(); c.run(); } };
  });
}

$("#palette-hint").onclick = openPalette;
$("#palette").onclick = (e) => { if (e.target.id === "palette") closePalette(); };
$("#palette-input").oninput = (e) => { palIndex = 0; renderPalette(e.target.value); };

document.addEventListener("keydown", (e) => {
  if (e.ctrlKey && e.key.toLowerCase() === "k") { e.preventDefault(); openPalette(); return; }
  if ($("#palette").classList.contains("hidden")) return;
  if (e.key === "Escape") closePalette();
  else if (e.key === "ArrowDown") { palIndex = Math.min(palIndex + 1, palItems.length - 1); renderPalette($("#palette-input").value); }
  else if (e.key === "ArrowUp") { palIndex = Math.max(palIndex - 1, 0); renderPalette($("#palette-input").value); }
  else if (e.key === "Enter") { const c = palItems[palIndex]; if (c) { closePalette(); c.run(); } }
});

/* ═══════════════ Boot ═══════════════ */

const VIEW_LOADERS = {
  timeline: () => loadTimeline(false),
  files: loadFiles,
  folders: loadFolders,
  agents: loadAgents,
  settings: loadSettings,
};

(async function boot() {
  await pollStatus();
  try {
    const s = await call("settings");
    watchedFolders = s?.WatchedFolders || [];
    tlProtectedOnly = !!s?.TimelineProtectedOnly;
  } catch { }
  setInterval(liveTick, 3000);
  navigate("timeline");
})();
