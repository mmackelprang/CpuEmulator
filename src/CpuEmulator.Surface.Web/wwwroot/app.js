"use strict";
(function () {
  const canvas = document.getElementById("screen");
  const ctx = canvas.getContext("2d");
  const status = document.getElementById("status");

  const wsUrl = (location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/ws";
  const ws = new WebSocket(wsUrl);
  ws.binaryType = "arraybuffer";

  ws.onopen = () => {
    // Leave the "connecting…" line until the first ST frame sets the canonical copy.md §3 status (the bare
    // "connected" was a non-canonical transient). M4: arm the "no frame yet" diagnostic — a real FB frame
    // (near-instant on localhost) or the first canonical ST status clears it; if neither lands within ~3 s
    // the surface reads as alive-but-waiting rather than silently blank.
    firstFrameTimer = setTimeout(() => {
      if (!firstFrameSeen) status.textContent = "connected · waiting for first frame…";
    }, 3000);
  };
  ws.onclose = () => { clearFirstFrameTimer(); status.textContent = "disconnected — reload to reconnect"; };
  ws.onerror = () => { clearFirstFrameTimer(); status.textContent = "connection error — is the server running?"; };

  // M4: the first-frame diagnostic timer (started on open, cleared by the first FB frame or a disconnect).
  let firstFrameTimer = null;
  let firstFrameSeen = false;
  function clearFirstFrameTimer() {
    if (firstFrameTimer) { clearTimeout(firstFrameTimer); firstFrameTimer = null; }
  }

  // H3: the keyboard-hint copy (copy.md §4) lives in two states the asset drives. The booted form is the
  // server-rendered #hint markup; the demo form omits the RESET/BASIC chords (there is no Apple to reset).
  const HINT_BOOTED = "Uppercase only. <kbd>Ctrl+B</kbd> = BASIC. <kbd>Ctrl+Backspace</kbd> = RESET.";
  const HINT_DEMO = "Fetch the ROMs to boot a real Apple ][+: <kbd>tools/get-apple2-roms.sh</kbd>";
  function setHint(booted) {
    const hint = document.getElementById("hint");
    if (hint) hint.innerHTML = booted ? HINT_BOOTED : HINT_DEMO;
  }

  // H4: while the demo fallback is showing there is no Apple disk drive, so the panels stay disabled (no
  // reachable-then-rejected picker). renderControlStrip honours this flag instead of the upload-state path.
  let demoMode = false;

  // --- Web Audio (the beeper / AU frames) ---
  const AUDIO_RATE = 44100;            // fixed contract rate (matches IAudioSink.SampleRate)
  let audioCtx = null;
  let nextStartTime = 0;               // running schedule cursor (seconds, in the AudioContext clock)

  function ensureAudio() {
    if (audioCtx) return;
    audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: AUDIO_RATE });
    nextStartTime = audioCtx.currentTime;
  }

  // A user gesture is required to start audio (browser autoplay policy).
  document.getElementById("enable-sound").addEventListener("click", () => {
    ensureAudio();
    if (audioCtx.state === "suspended") audioCtx.resume();
    document.getElementById("enable-sound").textContent = "sound on";
  });

  function handleAudioFrame(data) {
    if (!audioCtx || audioCtx.state !== "running") return; // sound not enabled yet
    const channels = data.getUint8(3);
    const sampleCount = data.getUint32(4, true);           // total shorts
    const perChannel = sampleCount / channels;
    const pcm = new Int16Array(data.buffer, 8, sampleCount);

    const buffer = audioCtx.createBuffer(channels, perChannel, AUDIO_RATE);
    for (let ch = 0; ch < channels; ch++) {
      const out = buffer.getChannelData(ch);
      for (let i = 0; i < perChannel; i++)
        out[i] = pcm[i * channels + ch] / 32768.0;         // S16 → float [-1,1]
    }

    const src = audioCtx.createBufferSource();
    src.buffer = buffer;
    src.connect(audioCtx.destination);
    // Schedule back-to-back; if we've fallen behind, snap to now to avoid a growing gap.
    const now = audioCtx.currentTime;
    if (nextStartTime < now) nextStartTime = now;
    src.start(nextStartTime);
    nextStartTime += buffer.duration;
  }

  // Inbound text from the host: the "ST " status frame. Two shapes: a STRUCTURED JSON body
  // (the Apple surfaces, design D14 — board/asset/mode/per-drive motor+label, pushed on change) or the
  // LEGACY bare asset string (Spectrum/demo one-shot). Both start with "ST ". Read-only: the client never
  // fabricates these — every field is real machine state the host pushed. Text frames arrive as strings;
  // binary FB/AU frames arrive as ArrayBuffer — so a string is NEVER fed to DataView below.
  function handleStatusText(s) {
    if (!s.startsWith("ST ")) return;
    const body = s.slice(3);
    const banner = document.getElementById("asset-banner");
    banner.hidden = true;

    if (body.startsWith("{")) {
      let st;
      try { st = JSON.parse(body); } catch { return; }
      // An upload-result ack (PR-S, design D12): resolve the panel's UPLOADING state to INSERTED or error.
      if (st.upload) {
        const u = st.upload;
        window.uploadState[u.drive] = u.ok ? "idle" : "error";
        window.uploadLastError[u.drive] = u.ok ? "" : (u.message || "That image looks corrupt");
        if (window.onUploadResult) window.onUploadResult(u.drive, u.ok, u.message || "");
        return;
      }
      window.machineStatus = st;                 // row T binds drive panels to this
      const modeLabel = document.getElementById("mode-label");
      if (modeLabel) modeLabel.textContent = st.mode || "";
      renderControlStrip();                       // repaint lights/labels/eject from the real snapshot
      // M3: applyAssetBanner owns the canonical copy.md §3 status line (board · machine-descriptor). The
      // per-drive light already carries motor state accessibly, so the status line drops the old drive tail.
      applyAssetBanner(st.asset, banner);
      return;
    }

    // Legacy bare-asset one-shot (Spectrum/demo).
    applyAssetBanner(body, banner);
  }

  // The asset → banner/status mapping (shared by both ST shapes). Owns the canonical copy.md §3 status
  // line, the §4 hint state, and (demo only) the §5 banner + §6.5 disabled-drive note.
  function applyAssetBanner(stateName, banner) {
    // M4: a real board-level ST status proves the session is live, so it retires the "waiting for first
    // frame…" diagnostic and prevents the 3 s timer from later clobbering this canonical line. (Without
    // this, a slow first FB frame on an already-status'd session would leave the diagnostic stuck.)
    firstFrameSeen = true;
    clearFirstFrameTimer();
    if (stateName === "softcard-cpm-videx") {
      status.textContent = "connected · Apple ][+ SoftCard · CP/M · Videx 80-col";
      leaveDemoMode();
    } else if (stateName === "softcard-cpm") {
      status.textContent = "connected · Apple ][+ SoftCard · CP/M";
      leaveDemoMode();
    } else if (stateName === "apple-fallback-font") {
      status.textContent = "connected · Apple ][+ · fallback font";
      leaveDemoMode();
    } else if (stateName && stateName.startsWith("apple")) {
      status.textContent = "connected · Apple ][+ · documented 6502";
      leaveDemoMode();
    } else if (stateName === "spectrum") {
      status.textContent = "connected · ZX Spectrum";
      leaveDemoMode();
    } else if (stateName === "demo") {
      status.textContent = "connected · demo fallback · no Apple ROM";
      // H2: the three-line §5 banner, built via innerHTML so the <kbd> element survives (textContent can't
      // carry markup). Two spaces of separation after "Fetch them once:" per the mockup.
      banner.hidden = false;
      banner.innerHTML =
        "Apple ][+ ROMs not found — showing the demo pattern.<br>" +
        "Fetch them once:&nbsp;&nbsp;<kbd>tools/get-apple2-roms.sh</kbd><br>" +
        "then reload this page.";
      enterDemoMode();
    }
  }

  // H3/H4: enter the demo-fallback presentation — the chord-less hint, the disabled drive panels, and the
  // §6.5 note split across the two panels (layout.md §3). renderDrivePanel keys off demoMode to keep the
  // panels disabled through later repaints (no reachable-then-rejected picker).
  function enterDemoMode() {
    demoMode = true;
    setHint(false);
    setDemoDriveNote();
    renderControlStrip();
  }

  // Leaving demo mode (a reconnect or asset change): restore the booted hint, clear the override, and let
  // the normal renderDrivePanel path re-enable the panels per the real upload/catalog state.
  function leaveDemoMode() {
    if (demoMode) { demoMode = false; populateLibrary(1); populateLibrary(2); }
    setHint(true);
    renderControlStrip();
  }

  // H4 / copy.md §6.5: the disabled-drive note, split across the panels per layout.md §3.
  function setDemoDriveNote() {
    const l1 = document.getElementById("drive-1-label");
    const l2 = document.getElementById("drive-2-label");
    if (l1) l1.textContent = "Insert a disk after";
    if (l2) l2.textContent = "fetching the Apple ROMs.";
  }

  // Decode a binary FB frame: 'F','B', version, reserved, u16 width LE, u16 height LE, then RGBA u32 LE.
  ws.onmessage = (ev) => {
    if (typeof ev.data === "string") { handleStatusText(ev.data); return; }
    const data = new DataView(ev.data);
    const m0 = data.getUint8(0), m1 = data.getUint8(1);
    if (m0 === 0x41 && m1 === 0x55) { handleAudioFrame(data); return; } // 'A','U'
    if (m0 !== 0x46 || m1 !== 0x42) return;                             // not 'F','B'
    // M4: the first real frame cancels the "waiting" diagnostic; from here the ST-driven status owns the line.
    if (!firstFrameSeen) { firstFrameSeen = true; clearFirstFrameTimer(); }
    const width = data.getUint16(4, true);
    const height = data.getUint16(6, true);
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
    const image = ctx.createImageData(width, height);
    const src = new Uint8Array(ev.data, 8);
    // Wire pixels are RGBA8888 stored little-endian as 0xAABBGGRR bytes -> [R,G,B,A] in memory.
    // Our encoder writes uint32 0xFFrrggbb little-endian = bytes [bb, gg, rr, FF]. Re-pack to RGBA.
    for (let i = 0, p = 0; i < width * height; i++, p += 4) {
      const b = src[p], g = src[p + 1], r = src[p + 2], a = src[p + 3];
      image.data[p] = r;
      image.data[p + 1] = g;
      image.data[p + 2] = b;
      image.data[p + 3] = a;
    }
    ctx.putImageData(image, 0, 0);
  };

  function sendKey(action, ev) {
    if (ws.readyState !== WebSocket.OPEN) return;
    // A single printable character (length-1 key) is the typed char; otherwise empty.
    const ch = ev.key && ev.key.length === 1 ? ev.key : "";
    // D5: forward the Ctrl modifier so the Apple keyboard chip can fold Ctrl+letter into a control code
    // (Ctrl+B = enter BASIC, Ctrl+C = break). The server reads the `ctrl` field; absent on older shapes.
    ws.send(JSON.stringify({ action: action, code: ev.code, char: ch, ctrl: ev.ctrlKey }));
    // Keep the browser from scrolling on Space/Arrows, and from stealing Ctrl+B / Ctrl+C, while focused.
    if (["Space", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(ev.code))
      ev.preventDefault();
    if (ev.ctrlKey && (ev.code === "KeyB" || ev.code === "KeyC"))
      ev.preventDefault();
  }

  window.addEventListener("keydown", (ev) => sendKey("down", ev));
  window.addEventListener("keyup", (ev) => sendKey("up", ev));

  // RESET is Ctrl+Backspace (the browser cannot send the hardware Ctrl+Reset); keep the browser from
  // navigating back on Ctrl+Backspace while the surface is focused.
  window.addEventListener("keydown", (ev) => {
    if (ev.ctrlKey && ev.code === "Backspace") ev.preventDefault();
  });

  // --- Disk library (PR-R, design D11) ---
  // Fetch the cached-disk catalog (GET /disks) once on load; row T's drive panels render window.diskCatalog.
  // Read-only data — the client never fabricates entries; the server lists the real cache.
  window.diskCatalog = [];
  function loadCatalog() {
    fetch("/disks")
      .then((r) => (r.ok ? r.json() : []))
      .then((list) => { window.diskCatalog = Array.isArray(list) ? list : []; })
      .catch(() => { window.diskCatalog = []; });
  }
  loadCatalog();

  // Insert a library disk into drive N (text WS, design D11). The bytes are already server-side; the wire
  // carries only the catalog id. Row T's [ Library ▾] onchange calls this.
  window.insertFromLibrary = function (drive, id) {
    if (ws.readyState !== WebSocket.OPEN || !id) return;
    ws.send(JSON.stringify({ action: "disk-insert", drive: drive, id: id }));
  };

  // Eject drive N (text WS, design D13). Row T's [ Eject ] calls this.
  window.ejectDrive = function (drive) {
    if (ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ action: "disk-eject", drive: drive }));
    window.uploadedLabel[drive] = "";   // M5: drop the optimistic name so the snapshot repaints to empty
  };

  // --- Disk upload (PR-S, design D12 — the surface's first inbound binary path) ---
  // Per-drive UPLOADING state for row T's panel: "idle" | "uploading" | "error", + the last error message.
  window.uploadState = { 1: "idle", 2: "idle" };
  window.uploadLastError = { 1: "", 2: "" };
  // M5: the filename captured at upload time (for the UPLOADING label and the optimistic INSERTED label),
  // and the optimistic label override that wins over the synthetic "upload (dsk)" host label until eject.
  window.uploadFilename = { 1: "", 2: "" };
  window.uploadedLabel = { 1: "", 2: "" };

  // The S ack hook: handleStatusText calls this when an upload result arrives. Repaint the panel; on a
  // server-side error, show the calm inline message (it auto-clears on the next ST snapshot or after ~6 s).
  window.onUploadResult = function (drive, ok, message) {
    // M5 (lower-effort interim, copy.md §6.2): on success, promote the captured filename to the optimistic
    // label override so the panel shows the real name rather than the synthetic "upload (dsk)" host label.
    if (ok) window.uploadedLabel[drive] = window.uploadFilename[drive] || "";
    renderControlStrip();
    if (!ok) showDriveError(drive, message || "That image looks corrupt");
  };

  // A per-drive inline error (copy.md §7). Auto-clears after ~6 s; the next successful action also clears it.
  const driveErrorTimers = { 1: null, 2: null };
  function showDriveError(drive, msg) {
    const el = document.getElementById("drive-" + drive + "-error");
    if (!el) return;
    el.textContent = msg;
    if (driveErrorTimers[drive]) clearTimeout(driveErrorTimers[drive]);
    driveErrorTimers[drive] = setTimeout(() => { el.textContent = ""; }, 6000);
  }

  // The 2 MB client cap + the extension allow-list (design §4.4). .dsk/.po load end-to-end; .woz is
  // validated client-side but the server returns the not-yet-supported reject (no WozFluxImage yet).
  const UPLOAD_MAX_BYTES = 2 * 1024 * 1024;
  const FORMAT_BYTE = { woz: 0, dsk: 1, po: 2 };

  // Validate a File, then send it as a binary DK frame on the open socket. Row T's [ Insert… ] picker
  // onchange calls this with the chosen File. Returns the client-side error string, or "" if the upload
  // was sent (the server's ack resolves INSERTED / a server-side error).
  window.uploadDisk = function (drive, file) {
    const name = (file && file.name) || "";
    const dotIdx = name.lastIndexOf(".");
    // A no-dot name has no extension -> "" (which matches no format below); a real ext is lower-cased.
    const ext = dotIdx === -1 ? "" : name.slice(dotIdx).toLowerCase();   // ".dsk" / ".po" / ".woz"
    const format = { ".woz": "woz", ".dsk": "dsk", ".po": "po" }[ext];
    if (!format) {
      window.uploadLastError[drive] = "Unsupported file — use .woz, .dsk, or .po";
      window.uploadState[drive] = "error";
      return window.uploadLastError[drive];
    }
    if (file.size === 0) {
      window.uploadLastError[drive] = "That file is empty";
      window.uploadState[drive] = "error";
      return window.uploadLastError[drive];
    }
    if (file.size > UPLOAD_MAX_BYTES) {
      window.uploadLastError[drive] = "File too large — Disk II images are under ~250 KB";
      window.uploadState[drive] = "error";
      return window.uploadLastError[drive];
    }
    if (ws.readyState !== WebSocket.OPEN) return "disconnected";

    window.uploadState[drive] = "uploading";
    window.uploadLastError[drive] = "";
    window.uploadFilename[drive] = name;   // M5/M6: the real name for the UPLOADING + optimistic INSERTED label
    window.uploadedLabel[drive] = "";      // a fresh upload supersedes any prior optimistic override
    const reader = new FileReader();
    reader.onload = function () {
      const body = new Uint8Array(reader.result);
      const frame = new Uint8Array(5 + body.length);
      frame[0] = 0x44; frame[1] = 0x4B;        // 'D','K'
      frame[2] = 0x01;                          // version
      frame[3] = drive;                         // 1 | 2
      frame[4] = FORMAT_BYTE[format];           // 0=woz 1=dsk 2=po
      frame.set(body, 5);
      ws.send(frame);                           // binary send (ws.binaryType is "arraybuffer")
    };
    reader.readAsArrayBuffer(file);
    return "";
  };

  // --- Control strip (PR-T, design T-E/T-G/T-H) ---
  // Repaint both drive panels from the REAL host-pushed snapshot (window.machineStatus.drives[i] =
  // {motor,label}) + the per-drive upload state. Nothing here is fabricated: an absent snapshot leaves
  // the boot defaults (○ / empty). Called on each ST frame and after an upload result.
  const GLYPH = { idle: "○", active: "●", uploading: "◐" };

  function renderControlStrip() {
    for (let drive = 1; drive <= 2; drive++) renderDrivePanel(drive);
  }

  // M5: the synthetic host label DispatchUpload writes for an uploaded image — "upload (dsk|po|woz)".
  const SYNTHETIC_UPLOAD_LABEL = /^upload \((woz|dsk|po)\)$/i;

  function renderDrivePanel(drive) {
    const selEl = document.getElementById("drive-" + drive + "-library");
    const insEl = document.getElementById("drive-" + drive + "-insert");
    const labelEl = document.getElementById("drive-" + drive + "-label");
    const ejectEl = document.getElementById("drive-" + drive + "-eject");

    // H4: in demo fallback there is NO Apple disk drive — force the panels disabled and keep the §6.5 note
    // (set by setDemoDriveNote) instead of repainting the label. This removes the reachable-then-rejected path.
    if (demoMode) {
      if (selEl) selEl.disabled = true;
      if (insEl) insEl.disabled = true;
      if (ejectEl) ejectEl.hidden = true;
      return;
    }

    const st = window.machineStatus;
    const d = st && st.drives && st.drives[drive - 1];   // {motor, label} or undefined
    const uploading = window.uploadState[drive] === "uploading";
    let label = d ? d.label : "—";
    const hasDisk = !!d && label && label !== "—" && label !== "empty";

    // M5 (lower-effort interim, copy.md §6.2): the host snapshot only knows the synthetic "upload (dsk)"
    // form — prefer the real filename captured at upload time. Purely cosmetic; motor/inserted state still
    // comes from the snapshot. The proper fix (carry the filename in the DK/ST frame) is a follow-on.
    if (hasDisk && SYNTHETIC_UPLOAD_LABEL.test(label) && window.uploadedLabel[drive])
      label = window.uploadedLabel[drive];

    const lightEl = document.getElementById("drive-" + drive + "-light");

    // The light: amber only when the REAL motor is on; the spinner during upload; else the idle outline.
    let glyph, cls, aria;
    if (uploading) { glyph = GLYPH.uploading; cls = "drive-light uploading"; aria = "drive " + drive + " uploading"; }
    else if (d && d.motor) { glyph = GLYPH.active; cls = "drive-light active"; aria = "drive " + drive + " active"; }
    else if (hasDisk) { glyph = GLYPH.idle; cls = "drive-light"; aria = "drive " + drive + " idle"; }
    else { glyph = GLYPH.idle; cls = "drive-light"; aria = "drive " + drive + " empty"; }
    if (lightEl) { lightEl.textContent = glyph; lightEl.className = cls; lightEl.setAttribute("aria-label", aria); }

    // The label: the uploading text (M6: with the filename when known), the image name, or "empty".
    if (labelEl) {
      if (uploading) {
        const name = window.uploadFilename[drive];
        labelEl.textContent = name ? "Uploading " + name + "…" : "Uploading…";
      } else if (hasDisk) labelEl.textContent = label;
      else labelEl.textContent = "empty";
    }

    // Eject is shown only when a disk is inserted and not uploading.
    if (ejectEl) ejectEl.hidden = !(hasDisk && !uploading);

    // The library select + the Insert… button are disabled during an upload (controls locked, interactions §4.1).
    if (selEl) selEl.disabled = uploading || selEl.dataset.empty === "1";
    if (insEl) insEl.disabled = uploading;
  }

  // Populate a drive's [ Library ▾] from window.diskCatalog (read-only — the server lists the real cache).
  // The placeholder option is first; an empty catalog disables the select with the named-script hint;
  // .woz items (supported:false) render disabled-with-note (no WozFluxImage yet — backlog row W).
  function populateLibrary(drive) {
    const sel = document.getElementById("drive-" + drive + "-library");
    if (!sel) return;
    const cat = window.diskCatalog || [];
    sel.innerHTML = "";
    if (cat.length === 0) {
      const opt = document.createElement("option");
      opt.textContent = "No cached disks — see tools/get-*";
      opt.value = "";
      sel.appendChild(opt);
      sel.disabled = true;
      sel.dataset.empty = "1";
      return;
    }
    sel.dataset.empty = "0";
    const placeholder = document.createElement("option");
    placeholder.textContent = "Insert from library…";
    placeholder.value = "";
    placeholder.disabled = true;
    placeholder.selected = true;
    sel.appendChild(placeholder);
    cat.forEach((e) => {
      const opt = document.createElement("option");
      const fmt = e.format ? " (." + String(e.format).toLowerCase() + ")" : "";
      if (e.supported === false) {
        opt.textContent = e.name + fmt + " — not yet supported";
        opt.disabled = true;
      } else {
        opt.textContent = e.name + fmt;
      }
      opt.value = e.id;
      sel.appendChild(opt);
    });
  }

  // Wire each panel's controls ONCE (the renderer only repaints; the listeners are attached here).
  function wireDrivePanels() {
    for (let drive = 1; drive <= 2; drive++) {
      const sel = document.getElementById("drive-" + drive + "-library");
      const ins = document.getElementById("drive-" + drive + "-insert");
      const file = document.getElementById("drive-" + drive + "-file");
      const eject = document.getElementById("drive-" + drive + "-eject");

      // Library select: an explicit choice inserts that catalog id into this drive (text WS); reset to
      // the placeholder so the same item can be re-selected.
      if (sel) sel.addEventListener("change", function () {
        const id = sel.value;
        if (id) { window.insertFromLibrary(drive, id); sel.selectedIndex = 0; }
      });

      // Insert…: open the OS file picker (a real button .click()s the hidden input — no keyboard trap).
      if (ins && file) ins.addEventListener("click", function () { file.value = ""; file.click(); });
      if (file) file.addEventListener("change", function () {
        const f = file.files && file.files[0];
        if (!f) return;
        const err = window.uploadDisk(drive, f);   // "" on send; a client-side error string otherwise
        renderDrivePanel(drive);
        if (err) showDriveError(drive, err);
      });

      // Eject: remove this drive's image (text WS); the next ST snapshot repaints to empty.
      if (eject) eject.addEventListener("click", function () { window.ejectDrive(drive); });
    }
  }

  // Initial render + wiring. The catalog arrives async (loadCatalog's fetch); re-populate when it lands by
  // polling window.diskCatalog once it differs from the initial empty array (cheap, one-shot).
  wireDrivePanels();
  populateLibrary(1); populateLibrary(2);
  renderControlStrip();
  // loadCatalog() resolves asynchronously; re-populate the selects once the catalog is in.
  (function awaitCatalog() {
    let tries = 0;
    const t = setInterval(function () {
      if ((window.diskCatalog && window.diskCatalog.length) || ++tries > 40) {
        clearInterval(t);
        populateLibrary(1); populateLibrary(2); renderControlStrip();
      }
    }, 100);
  })();
})();
