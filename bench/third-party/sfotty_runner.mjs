// sfotty (@sfotty-pie/sfotty) benchmark glue for the CpuEmulator comparative suite.
//
// sfotty is a cycle-exact MOS 6502 emulator for Node.js. Its run() executes ONE
// CPU CYCLE (the last micro-op of each instruction is an internal decode() that
// sets up the next instruction); we count total cycles ourselves and detect the
// JMP-self success-trap park at instruction boundaries.
//
// Reads a 64 KiB image, runs a warmup slice (excluded), then a measured pass to
// either the success trap (W1) or a fixed emulated-cycle window (W2), and prints
// two machine-readable lines the C# JsEmulatorAdapter parses:
//     CYCLES <n>
//     WALL_SECONDS <f>
//
// Usage: node sfotty_runner.mjs <image.bin> <startPc> <mode> <trapPc> <measureCycles>
//   mode = "trap"  run to PC park, verify it equals trapPc
//   mode = "cap"   run for measureCycles emulated cycles
import { readFileSync } from "node:fs";
import { performance } from "node:perf_hooks";

const require = (await import("node:module")).createRequire(import.meta.url);
const { Sfotty } = require("@sfotty-pie/sfotty");

const [imagePath, startPcArg, mode, trapPcArg, measureArg] = process.argv.slice(2);
const startPc = Number(startPcArg);
const trapPc = Number(trapPcArg);
const measureCycles = Number(measureArg);

const image = readFileSync(imagePath);
if (image.length !== 0x10000) throw new Error(`image is ${image.length} bytes, expected 65536`);

function makeCpu(img) {
  const ram = Uint8Array.from(img);
  const memory = {
    read: (a) => ram[a & 0xffff],
    write: (a, v) => { ram[a & 0xffff] = v & 0xff; },
  };
  const cpu = new Sfotty(memory);
  cpu.PC = startPc;
  cpu.S = 0xfd;
  cpu.setP(0x34);
  return cpu;
}

// Run a measured window. Returns the emulated cycle count. We track instruction
// boundaries (PC at the start of each instruction) to detect the JMP-self park:
// when an instruction completes and PC is unchanged from its start, it parked.
function runWindow(cpu, modeArg, cap) {
  let cycles = 0;
  let instrStartPc = cpu.PC;
  let cyclesThisInstr = 0;
  while (cycles < cap) {
    cpu.run();
    cycles++;
    cyclesThisInstr++;
    // An instruction completed when sfotty resets cycleCounter to 0 for the next
    // fetch. We approximate the boundary by watching PC advance past the opcode:
    // after a full instruction the PC differs from instrStartPc unless it parked.
    if (cpu.cycleCounter === 0 && cyclesThisInstr > 0) {
      if (modeArg === "trap" && cpu.PC === instrStartPc) {
        if (cpu.PC !== trapPc) throw new Error(`parked at ${cpu.PC.toString(16)}, not trap ${trapPc.toString(16)}`);
        break;
      }
      if (cpu.crashed) throw new Error(`cpu crashed at ${cpu.PC.toString(16)}`);
      instrStartPc = cpu.PC;
      cyclesThisInstr = 0;
    }
  }
  return cycles;
}

// Warmup (excluded): a short slice so V8 JITs the hot path before timing.
runWindow(makeCpu(image), "cap", Math.min(measureCycles, 200000));

// Measured pass.
const cpu = makeCpu(image);
const t0 = performance.now();
const cycles = runWindow(cpu, mode, measureCycles);
const wall = (performance.now() - t0) / 1000;

console.log(`CYCLES ${cycles}`);
console.log(`WALL_SECONDS ${wall.toFixed(6)}`);
