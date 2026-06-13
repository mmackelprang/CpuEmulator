#!/usr/bin/env python3
"""py65 benchmark glue for the CpuEmulator comparative suite (W1/W2).

Reads a 64 KiB workload image from a file, runs py65's MPU6502 to either a
success-trap PC (PC parks at a JMP-self) or a fixed instruction-bounded cycle
cap, and prints two machine-readable lines the C# Py65Adapter parses:

    CYCLES <n>
    WALL_SECONDS <f>

A warmup pass runs first (excluded from the measured window) so the comparison
matches the BenchmarkDotNet warmup the methodology requires. py65 is a
pure-Python emulator — by orders of magnitude the slowest subject, which is an
honest, interesting cross-language data point, not a defect. To keep the run
bounded in wall-clock, the measured window is capped at MEASURE_CYCLES emulated
cycles (a portable slice; the adapter reports cycles/sec, which is rate, not a
fixed total, so a shorter slice is still a fair rate measurement).

Usage:
    python py65_runner.py <image.bin> <start_pc> <mode> <arg> <measure_cycles>
      mode = "trap"  arg = success_trap_pc   (run to PC park, verify it is the trap)
      mode = "cap"   arg = (ignored)          (run to measure_cycles)
"""
import sys
import time

from py65.devices.mpu6502 import MPU


def load(mpu, image):
    # py65's memory is a flat list of 65536 ints; copy the image in.
    mpu.memory = list(image)


def run_window(mpu, mode, trap_pc, measure_cycles):
    """Run until `measure_cycles` emulated cycles elapse, or (trap mode) the PC
    parks. Returns the emulated cycle count over the window."""
    start_cycles = mpu.processorCycles
    while mpu.processorCycles - start_cycles < measure_cycles:
        before = mpu.pc
        mpu.step()
        if mode == "trap" and mpu.pc == before:
            if mpu.pc != trap_pc:
                raise RuntimeError(f"parked at {mpu.pc:#06x}, not the trap {trap_pc:#06x}")
            break
    return mpu.processorCycles - start_cycles


def main():
    image_path = sys.argv[1]
    start_pc = int(sys.argv[2])
    mode = sys.argv[3]
    trap_pc = int(sys.argv[4])
    measure_cycles = int(sys.argv[5])

    with open(image_path, "rb") as f:
        image = f.read()
    if len(image) != 0x10000:
        raise RuntimeError(f"image is {len(image)} bytes, expected 65536")

    # ── Warmup (excluded from the measured window): a short slice so the Python
    #    interpreter + py65's dispatch caches are hot before we time it.
    warm = MPU()
    load(warm, image)
    warm.pc = start_pc
    warm.sp = 0xFD
    warm.p = 0x34
    run_window(warm, "cap", trap_pc, min(measure_cycles, 200_000))

    # ── Measured pass.
    mpu = MPU()
    load(mpu, image)
    mpu.pc = start_pc
    mpu.sp = 0xFD
    mpu.p = 0x34

    t0 = time.perf_counter()
    cycles = run_window(mpu, mode, trap_pc, measure_cycles)
    wall = time.perf_counter() - t0

    print(f"CYCLES {cycles}")
    print(f"WALL_SECONDS {wall:.6f}")


if __name__ == "__main__":
    main()
