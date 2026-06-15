// M4.1: the 68000 register-state foundation. Unlike the Z80 partial, the M4.1 68000 exposes no internal
// seam to another assembly: it is NOT JIT-driven (M4.6) and the register-state tests drive it through
// public members only (GetRegister/SetRegister, A7, SR, SupervisorMode, Ccr). No InternalsVisibleTo is
// declared yet; the IL-JIT's AdvanceCycles seam (the Z80's InternalsVisibleTo to CpuEmulator.Jit) lands
// when the 68000 goes through the JIT (M4.6).
