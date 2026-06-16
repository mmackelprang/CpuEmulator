// DrGoldfire/Z80.js benchmark glue for the CpuEmulator comparative suite (the OPTIONAL Z80
// cross-language JS subject — mirrors sfotty_runner.mjs for the 6502).
//
// DrGoldfire/Z80.js (Molly Howell, MIT) is a straightforward instruction interpreter (NOT
// cycle-accurate by its own README — its cycle_counter uses the documented per-opcode T-state
// counts, a legitimate OWN cycle model; the report labels all Z80 numbers "indicative
// cross-language", which covers this). Its public API: new Z80({mem_read, mem_write, io_read,
// io_write}); run_instruction() returns the T-cycles of that instruction; getState()/setState()
// expose pc/sp/c/e/d for the BDOS service.
//
// The Z80.js source file is `function Z80(core){ ... return {…}; }` with NO module.exports, so we
// load it via vm.runInThisContext and capture the global it defines. It is fetched (NOT vendored)
// by bench/third-party/fetch-subjects into <cache>/z80js/Z80.js.
//
// Prints the two machine-readable lines the C# Z80JsAdapter parses (the SubprocessRunner contract):
//     CYCLES <n>
//     WALL_SECONDS <f>
//
// Usage: node z80js_runner.mjs <image.bin> <startPc> <mode> <trapPc> <measureCycles>
//   mode = "cap"   run for measureCycles emulated T-states (Z80-W2 kernel)
//   mode = "bdos"  Z80-W1 CP/M: service the BDOS CALL (PC==5: fn-2/fn-9 + RET), stop on warm-boot
//                  (PC==0) or the T-state window — runs the real ZEXDOC prefix.

import { readFileSync } from "node:fs";
import { runInThisContext } from "node:vm";

const [, , imagePath, startPcArg, mode, , measureArg] = process.argv;
if (!imagePath) {
  console.error("usage: node z80js_runner.mjs <image.bin> <startPc> <mode> <trapPc> <measureCycles>");
  process.exit(1);
}
const startPc = parseInt(startPcArg, 10);
const measureCycles = parseInt(measureArg, 10);

// Resolve the fetched Z80.js from the bench cache (CPUEMULATOR_BENCHCACHE or ~/.cache/cpuemulator/bench).
const cache = process.env.CPUEMULATOR_BENCHCACHE
  || `${process.env.HOME || process.env.USERPROFILE}/.cache/cpuemulator/bench`;
const z80Path = `${cache}/z80js/Z80.js`;

// Z80.js's constructor has a browser-era guard `if (this === window) throw ...` to catch a call
// without `new`. Under node there is no `window`, so the bare reference would throw a ReferenceError
// before reaching the (correct) `new` check. Define a unique sentinel `window` so the guard
// evaluates to false when the constructor IS called with `new` (then `this` is the new instance,
// never this sentinel). This does NOT make the file browser-dependent — it only satisfies the guard.
let Z80;
try {
  if (typeof globalThis.window === "undefined") globalThis.window = Symbol("z80js-runner-no-window");
  const src = readFileSync(z80Path, "utf8");
  // The file defines `function Z80(...)`; evaluating it in this context binds Z80 as a global.
  runInThisContext(src + "\n;globalThis.__Z80 = Z80;", { filename: z80Path });
  Z80 = globalThis.__Z80;
  if (typeof Z80 !== "function") throw new Error("Z80 constructor not found in source");
} catch (e) {
  console.error(`failed to load Z80.js (${z80Path}): ${e.message}`);
  process.exit(2);
}

try {
  const _probe = new Z80({ mem_read: () => 0, mem_write: () => {}, io_read: () => 0, io_write: () => {} });
  if (typeof _probe.run_instruction !== "function") throw new Error("Z80 instance missing run_instruction");
} catch (e) {
  console.error(`Z80.js construction probe failed (fetched version incompatible?): ${e.message}`);
  process.exit(3);
}

const WARM_BOOT = 0x0000;
const BDOS_ENTRY = 0x0005;

function runOnce(image, cap, bdos) {
  const mem = Uint8Array.from(image); // a private 64 KiB copy per pass
  const cpu = new Z80({
    mem_read: (addr) => mem[addr & 0xffff],
    mem_write: (addr, val) => { mem[addr & 0xffff] = val & 0xff; },
    io_read: () => 0xff,
    io_write: () => {},
  });
  cpu.reset();
  let st = cpu.getState();
  st.pc = startPc;
  st.sp = 0xfffe;
  cpu.setState(st);

  let cycles = 0;
  while (cycles < cap) {
    st = cpu.getState();
    if (bdos) {
      if (st.pc === WARM_BOOT) break;                 // early-stop (rare for a capped prefix)
      if (st.pc === BDOS_ENTRY) { serviceBdos(cpu, mem); continue; }
    }
    cycles += cpu.run_instruction();
  }
  return cycles;
}

// Host-side BDOS: fn-9 ($-string at DE) walked + discarded; any fn then host-RET (pop the return
// address). Console output discarded (throughput run). A port of the proven CpmBdosHost convention.
function serviceBdos(cpu, mem) {
  const st = cpu.getState();
  if (st.c === 9) {
    let addr = ((st.d << 8) | st.e) & 0xffff;
    for (let guard = 0; guard < 0x10000; guard++) {
      if (mem[addr] === 0x24 /* '$' */) break;
      addr = (addr + 1) & 0xffff;
    }
  }
  const sp = st.sp & 0xffff;
  const lo = mem[sp];
  const hi = mem[(sp + 1) & 0xffff];
  st.sp = (sp + 2) & 0xffff;
  st.pc = ((hi << 8) | lo) & 0xffff;
  cpu.setState(st);
}

const image = readFileSync(imagePath);
if (image.length !== 0x10000) {
  console.error(`image is ${image.length} bytes, expected 65536`);
  process.exit(1);
}
const bdos = mode === "bdos";

// Warmup (excluded).
runOnce(image, Math.min(measureCycles, 200000), bdos);

const t0 = process.hrtime.bigint();
const cycles = runOnce(image, measureCycles, bdos);
const t1 = process.hrtime.bigint();
const wall = Number(t1 - t0) / 1e9;

console.log(`CYCLES ${cycles}`);
console.log(`WALL_SECONDS ${wall.toFixed(6)}`);
