using System.Runtime.CompilerServices;
using DiffEngine;

namespace EnterpriseLangfuse.Generators.Tests;

public static class ModuleInit
{
    [ModuleInitializer]
    public static void Init()
    {
        VerifySourceGenerators.Initialize();

        // A snapshot mismatch on CI must fail the run, not try to open a diff tool on a headless agent.
        DiffRunner.Disabled = true;
    }
}
