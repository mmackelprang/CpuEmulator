using System.Runtime.CompilerServices;

// The test project drives the internal block compiler directly (discovery + compile-count pins)
// and reads JittedCpu's internal CompileCount seam.
[assembly: InternalsVisibleTo("CpuEmulator.Tests")]
