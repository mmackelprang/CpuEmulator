namespace CpuEmulator.Tests.Importer;

/// <summary>
/// xUnit collection that serializes tests which redirect Console.Out/Error.
/// Tests that call Program.Main in-proc share the process-wide Console streams;
/// running them in parallel causes output from one test to bleed into another's
/// StringWriter capture. Placing all such tests in this collection forces them
/// to execute sequentially, not in parallel.
/// </summary>
[CollectionDefinition("ConsoleIsolation")]
public sealed class ConsoleIsolationCollection { }
