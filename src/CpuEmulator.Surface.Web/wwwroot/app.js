"use strict";
(function () {
  const canvas = document.getElementById("screen");
  const ctx = canvas.getContext("2d");
  const status = document.getElementById("status");

  const wsUrl = (location.protocol === "https:" ? "wss://" : "ws://") + location.host + "/ws";
  const ws = new WebSocket(wsUrl);
  ws.binaryType = "arraybuffer";

  ws.onopen = () => { status.textContent = "connected"; };
  ws.onclose = () => { status.textContent = "disconnected"; };
  ws.onerror = () => { status.textContent = "error"; };

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
      applyAssetBanner(st.asset, banner);
      // The status line: board · mode · the active drive summary (read-only reflection).
      const active = (st.drives || []).find(d => d.motor);
      const driveText = active ? " · drive ●" : "";
      status.textContent = "connected · " + st.board + " · " + st.mode + driveText;
      return;
    }

    // Legacy bare-asset one-shot (Spectrum/demo).
    applyAssetBanner(body, banner);
  }

  // The asset → banner/status mapping (shared by both ST shapes). Preserves the shipped demo banner copy.
  function applyAssetBanner(stateName, banner) {
    if (stateName === "softcard-cpm-videx") {
      status.textContent = "connected · Apple ][+ SoftCard · CP/M · Videx 80-col";
    } else if (stateName === "softcard-cpm") {
      status.textContent = "connected · Apple ][+ SoftCard · CP/M";
    } else if (stateName === "apple-fallback-font") {
      status.textContent = "connected · Apple ][+ · fallback font";
    } else if (stateName && stateName.startsWith("apple")) {
      status.textContent = "connected · Apple ][+ · documented 6502";
    } else if (stateName === "spectrum") {
      status.textContent = "connected · ZX Spectrum";
    } else if (stateName === "demo") {
      status.textContent = "connected · demo fallback · no Apple ROM";
      banner.hidden = false;
      banner.textContent = "Apple ][+ ROMs not found — showing the demo pattern. " +
                           "Fetch them once: tools/get-apple2-roms.sh (or .ps1) — then reload this page.";
    }
  }

  // Decode a binary FB frame: 'F','B', version, reserved, u16 width LE, u16 height LE, then RGBA u32 LE.
  ws.onmessage = (ev) => {
    if (typeof ev.data === "string") { handleStatusText(ev.data); return; }
    const data = new DataView(ev.data);
    const m0 = data.getUint8(0), m1 = data.getUint8(1);
    if (m0 === 0x41 && m1 === 0x55) { handleAudioFrame(data); return; } // 'A','U'
    if (m0 !== 0x46 || m1 !== 0x42) return;                             // not 'F','B'
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
    ws.send(JSON.stringify({ action: action, code: ev.code, char: ch }));
    // Keep the browser from scrolling on Space/Arrows while focused.
    if (["Space", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(ev.code))
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
  };

  // --- Disk upload (PR-S, design D12 — the surface's first inbound binary path) ---
  // Per-drive UPLOADING state for row T's panel: "idle" | "uploading" | "error", + the last error message.
  window.uploadState = { 1: "idle", 2: "idle" };
  window.uploadLastError = { 1: "", 2: "" };

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
})();
