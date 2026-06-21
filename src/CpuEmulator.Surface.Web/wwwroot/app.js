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

  // Inbound text from the host: a one-shot "ST <assetState>" board/asset string drives the banner +
  // status line (the design copy.md strings). Text frames arrive as strings; binary FB/AU frames arrive
  // as ArrayBuffer — so a string is NEVER fed to DataView below.
  function handleStatusText(s) {
    if (!s.startsWith("ST ")) return;
    const stateName = s.slice(3);
    const banner = document.getElementById("asset-banner");
    banner.hidden = true;
    if (stateName === "softcard-cpm") {
      status.textContent = "connected · Apple ][+ SoftCard · CP/M";
    } else if (stateName === "apple-fallback-font") {
      status.textContent = "connected · Apple ][+ · fallback font";
    } else if (stateName.startsWith("apple")) {
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
})();
