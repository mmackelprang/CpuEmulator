/* superzazu/z80 benchmark glue for the CpuEmulator comparative suite (the Z80 cross-language
 * C anchor — mirrors fake6502_runner.c for the 6502).
 *
 * Targets superzazu/z80 (the single-file cycle-accurate C Z80, MIT, ZEXALL/ZEXDOC-proven by its
 * author). The host provides read_byte/write_byte over a flat 64 KiB image; the core exposes
 * z80_init(&z), z80_step(&z) (one instruction), and the cycle counter z->cyc (T-states). The
 * struct exposes pc/sp/c/e/d directly, which the BDOS service below reads.
 *
 * Loads a 64 KiB image, sets the start PC + SP, runs a warmup slice (excluded), then a measured
 * pass to a fixed emulated T-state window, and prints two machine-readable lines the C#
 * Z80CAdapter parses (the SubprocessRunner CYCLES / WALL_SECONDS contract):
 *     CYCLES <n>
 *     WALL_SECONDS <f>
 *
 * z80.h + z80.c are fetched by bench/third-party/fetch-subjects (NOT vendored — the fake6502.c
 * discipline). They are #included here so the read/write callbacks resolve.
 *
 * Build (done by the C# adapter via the detected cc):
 *     cc -O2 -I<cache>/z80c -o z80c_runner z80c_runner.c
 *
 * Usage: z80c_runner <image.bin> <startPc> <mode> <trapPc> <measureCycles>
 *   mode = "cap"   run for measureCycles emulated T-states (Z80-W2 kernel)
 *   mode = "bdos"  Z80-W1 CP/M: service the BDOS CALL (PC==5: fn-2/fn-9 + host RET), stop on the
 *                  warm-boot sentinel (PC==0) or the T-state window — runs the real ZEXDOC prefix.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <time.h>

#ifdef _WIN32
#include <windows.h>
static double now_seconds(void) {
    LARGE_INTEGER f, c;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&c);
    return (double)c.QuadPart / (double)f.QuadPart;
}
#else
static double now_seconds(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec + t.tv_nsec / 1e9;
}
#endif

#include "z80.h"

static uint8_t mem[0x10000];

/* Host memory + port callbacks. Named host_* to avoid colliding with z80.c's internal rb/wb helpers. */
static uint8_t host_read(void *userdata, uint16_t addr) { (void)userdata; return mem[addr]; }
static void host_write(void *userdata, uint16_t addr, uint8_t val) { (void)userdata; mem[addr] = val; }
static uint8_t host_port_in(z80 *z, uint8_t port) { (void)z; (void)port; return 0xFF; }
static void host_port_out(z80 *z, uint8_t port, uint8_t val) { (void)z; (void)port; (void)val; }

#include "z80.c"

#define WARM_BOOT 0x0000
#define BDOS_ENTRY 0x0005

/* Host-side BDOS service: fn-2 (console out, char in E) + fn-9 ($-string at DE), then RET. Console
 * output is DISCARDED (throughput run; the correctness transcript is the ZEX test's job). A port of
 * the proven CpmBdosHost.ServiceBdos convention. */
static void service_bdos(z80 *z) {
    uint8_t fn = z->c;
    if (fn == 9) {
        uint16_t addr = (uint16_t)((z->d << 8) | z->e);
        for (int guard = 0; guard < 0x10000; guard++) {
            uint8_t b = mem[addr];
            if (b == '$') break;
            addr = (uint16_t)(addr + 1);
        }
    }
    /* fn-2 and any other fn: nothing to emit (discarded). Host RET: pop the return address. */
    uint16_t sp = z->sp;
    uint8_t lo = mem[sp];
    uint8_t hi = mem[(uint16_t)(sp + 1)];
    z->sp = (uint16_t)(sp + 2);
    z->pc = (uint16_t)((hi << 8) | lo);
}

static uint64_t run_window(z80 *z, const char *mode, uint64_t cap) {
    uint64_t start = z->cyc;
    int bdos = strcmp(mode, "bdos") == 0;
    for (;;) {
        if (z->cyc - start >= cap) break;
        if (bdos) {
            if (z->pc == WARM_BOOT) break;                  /* early-stop (rare for a capped prefix) */
            if (z->pc == BDOS_ENTRY) { service_bdos(z); continue; }
        }
        z80_step(z);
    }
    return z->cyc - start;
}

static void setup(z80 *z, uint16_t start_pc) {
    z80_init(z);
    z->read_byte = host_read;
    z->write_byte = host_write;
    z->port_in = host_port_in;
    z->port_out = host_port_out;
    z->pc = start_pc;
    z->sp = 0xFFFE;
}

int main(int argc, char **argv) {
    if (argc != 6) {
        fprintf(stderr, "usage: %s <image.bin> <startPc> <mode> <trapPc> <measureCycles>\n", argv[0]);
        return 1;
    }
    const char *image_path = argv[1];
    uint16_t start_pc = (uint16_t)strtoul(argv[2], NULL, 10);
    const char *mode = argv[3];
    /* argv[4] = trapPc — unused for the Z80 (it terminates on the cap/window, not a PC trap). */
    uint64_t measure_cycles = strtoull(argv[5], NULL, 10);

    FILE *f = fopen(image_path, "rb");
    if (!f) { perror("fopen"); return 1; }
    size_t n = fread(mem, 1, sizeof(mem), f);
    fclose(f);
    if (n != sizeof(mem)) { fprintf(stderr, "image is %zu bytes, expected 65536\n", n); return 1; }

    uint8_t image_copy[0x10000];
    memcpy(image_copy, mem, sizeof(mem));

    z80 z;

    /* Warmup (excluded). */
    uint64_t warm = measure_cycles < 200000 ? measure_cycles : 200000;
    setup(&z, start_pc);
    run_window(&z, mode, warm);

    /* Reload (warmup may have self-modified) for the measured pass. */
    memcpy(mem, image_copy, sizeof(mem));

    setup(&z, start_pc);
    double t0 = now_seconds();
    uint64_t cycles = run_window(&z, mode, measure_cycles);
    double wall = now_seconds() - t0;

    printf("CYCLES %llu\n", (unsigned long long)cycles);
    printf("WALL_SECONDS %.6f\n", wall);
    return 0;
}
