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

  // Decode a binary FB frame: 'F','B', version, reserved, u16 width LE, u16 height LE, then RGBA u32 LE.
  ws.onmessage = (ev) => {
    const data = new DataView(ev.data);
    if (data.getUint8(0) !== 0x46 || data.getUint8(1) !== 0x42) return; // not "FB"
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
})();
