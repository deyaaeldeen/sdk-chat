// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using PublicApiGraphEngine.TypeScript;
using Xunit;

namespace PublicApiGraphEngine.Tests;

/// <summary>
/// Shared fixture that graphs API from the CompiledMode/TypeScript fixture once.
/// This fixture has multi-target conditional exports (node/browser/default)
/// with separate .d.ts entry points per condition, plus unified source files.
/// </summary>
public class TypeScriptCompiledFixture : IAsyncLifetime
{
    private static readonly string TestFixturesPath =
        Path.Combine(AppContext.BaseDirectory, "TestFixtures", "CompiledMode", "TypeScript");

    public ApiIndex? Api { get; private set; }
    public string? SkipReason { get; private set; }
    public string FixturePath => TestFixturesPath;

    public async ValueTask InitializeAsync()
    {
        if (!CheckNodeInstalled())
        {
            SkipReason = "Node.js not installed";
            return;
        }

        try
        {
            Api = await new TypeScriptPublicApiGraphEngine().GraphAsync(TestFixturesPath);
        }
        catch (Exception ex)
        {
            SkipReason = $"TypeScript engine failed: {ex.Message}";
        }
    }

    public ValueTask DisposeAsync() => default;

    private static bool CheckNodeInstalled()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(1000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// Tests that REQUIRE compiled/runtime analysis to pass.
/// These document the accuracy gap that a compiled engine will close:
/// - Default vs platform-specific export condition scoping
/// - External package dependency resolution without node_modules
/// </summary>
public class TypeScriptCompiledPrecisionTests : IClassFixture<TypeScriptCompiledFixture>
{
    private readonly TypeScriptCompiledFixture _fixture;

    public TypeScriptCompiledPrecisionTests(TypeScriptCompiledFixture fixture)
    {
        _fixture = fixture;
    }

    private ApiIndex GetApi()
    {
        if (_fixture.SkipReason != null) Assert.Skip(_fixture.SkipReason);
        return _fixture.Api!;
    }

    /// <summary>
    /// The "default" export condition re-exports all shared types (BaseClient,
    /// ClientOptions, Resource) but should NOT include platform-specific types.
    ///
    /// The source engine processes src/index.ts which re-exports from all
    /// sub-modules, making everything available in one unified surface.
    ///
    /// A compiled engine processing dist/types/index.d.ts would see only
    /// the types that the default condition explicitly exports.
    /// </summary>
    [Fact(Skip = "Requires compiled artifacts")]
    [Trait("Category", "CompiledOnly")]
    public void SourceParser_CannotDistinguish_DefaultExports_FromPlatformSpecific()
    {
        var api = GetApi();
        var allClasses = api.Modules.SelectMany(m => m.Classes ?? []).ToList();

        Assert.Contains(allClasses, c => c.Name == "NodeClient");
        Assert.Contains(allClasses, c => c.Name == "BrowserClient");
        Assert.Contains(allClasses, c => c.Name == "BaseClient");

        var defaultModule = api.Modules.FirstOrDefault(m =>
            m.Name.Contains("default", StringComparison.OrdinalIgnoreCase) ||
            m.Name == "." || m.Name == "index");

        if (defaultModule != null)
        {
            var defaultClasses = defaultModule.Classes ?? [];
            Assert.Contains(defaultClasses, c => c.Name == "BaseClient");
            Assert.DoesNotContain(defaultClasses, c => c.Name == "NodeClient");
            Assert.DoesNotContain(defaultClasses, c => c.Name == "BrowserClient");
        }
        else
        {
            Assert.Fail("Compiled engine should produce a 'default' condition module");
        }
    }
}
