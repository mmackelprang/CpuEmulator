/* fake6502 benchmark glue for the CpuEmulator comparative suite.
 *
 * Targets the omarandlorraine/fake6502 fork (the context-struct API): the host
 * provides fake6502_mem_read/fake6502_mem_write over a memory image; the core
 * exposes fake6502_reset(ctx), fake6502_step(ctx) (one instruction), and the
 * cycle counter ctx->emu.clockticks. We define NMOS6502 + DECIMALMODE so the
 * core matches our NMOS-with-BCD emulator (the fair comparison).
 *
 * Loads a 64 KiB image, sets the start PC + S + P on the context, runs a warmup
 * slice (excluded), then a measured pass to a success-trap park (W1) or a fixed
 * emulated-cycle window (W2), and prints two machine-readable lines the C#
 * Fake6502Adapter parses:
 *     CYCLES <n>
 *     WALL_SECONDS <f>
 *
 * fake6502.c + fake6502.h are fetched by bench/third-party/fetch-subjects (NOT
 * vendored). They are #included here so the read/write callbacks resolve.
 *
 * Build (done by the C# adapter via the detected cc):
 *     cc -O2 -DNMOS6502 -DDECIMALMODE -I<cache>/fake6502 -o fake6502_runner fake6502_runner.c
 *
 * Usage: fake6502_runner <image.bin> <startPc> <mode> <trapPc> <measureCycles>
 *   mode = "trap"  run to PC park, verify it equals trapPc
 *   mode = "cap"   run for measureCycles emulated cycles
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <time.h>

#include "fake6502.h"

static uint8_t mem[0x10000];

uint8_t fake6502_mem_read(fake6502_context *c, uint16_t address) { (void)c; return mem[address]; }
void fake6502_mem_write(fake6502_context *c, uint16_t address, uint8_t val) { (void)c; mem[address] = val; }

#include "fake6502.c"

static long run_window(const char *mode, uint16_t start_pc, uint16_t trap_pc, long cap) {
    fake6502_context ctx;
    memset(&ctx, 0, sizeof(ctx));
    fake6502_reset(&ctx);
    ctx.cpu.pc = start_pc;
    ctx.cpu.s = 0xFD;
    ctx.cpu.flags = 0x34;
    long start_ticks = ctx.emu.clockticks;
    for (;;) {
        if (ctx.emu.clockticks - start_ticks >= cap) break;
        uint16_t before = ctx.cpu.pc;
        fake6502_step(&ctx);
        if (strcmp(mode, "trap") == 0 && ctx.cpu.pc == before) {
            if (ctx.cpu.pc != trap_pc) {
                fprintf(stderr, "parked at %04x, not trap %04x\n", ctx.cpu.pc, trap_pc);
                exit(2);
            }
            break;
        }
    }
    return ctx.emu.clockticks - start_ticks;
}

int main(int argc, char **argv) {
    if (argc != 6) {
        fprintf(stderr, "usage: %s <image.bin> <startPc> <mode> <trapPc> <measureCycles>\n", argv[0]);
        return 1;
    }
    const char *image_path = argv[1];
    uint16_t start_pc = (uint16_t)strtoul(argv[2], NULL, 10);
    const char *mode = argv[3];
    uint16_t trap_pc = (uint16_t)strtoul(argv[4], NULL, 10);
    long measure_cycles = strtol(argv[5], NULL, 10);

    FILE *f = fopen(image_path, "rb");
    if (!f) { perror("fopen"); return 1; }
    size_t n = fread(mem, 1, sizeof(mem), f);
    fclose(f);
    if (n != sizeof(mem)) { fprintf(stderr, "image is %zu bytes, expected 65536\n", n); return 1; }

    uint8_t image_copy[0x10000];
    memcpy(image_copy, mem, sizeof(mem));

    /* Warmup (excluded). */
    long warm = measure_cycles < 200000 ? measure_cycles : 200000;
    run_window("cap", start_pc, trap_pc, warm);

    /* Reload (warmup may have self-modified via SMC) for the measured pass. */
    memcpy(mem, image_copy, sizeof(mem));

    struct timespec t0, t1;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    long cycles = run_window(mode, start_pc, trap_pc, measure_cycles);
    clock_gettime(CLOCK_MONOTONIC, &t1);
    double wall = (t1.tv_sec - t0.tv_sec) + (t1.tv_nsec - t0.tv_nsec) / 1e9;

    printf("CYCLES %ld\n", cycles);
    printf("WALL_SECONDS %.6f\n", wall);
    return 0;
}
