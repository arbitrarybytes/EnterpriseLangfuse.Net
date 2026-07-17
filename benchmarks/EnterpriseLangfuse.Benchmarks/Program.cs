using BenchmarkDotNet.Running;

// Runs every benchmark by default; pass --filter to narrow, e.g.
//   dotnet run -c Release -- --filter *Telemetry*
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Entry point marker for <see cref="BenchmarkSwitcher"/>.</summary>
public partial class Program;
