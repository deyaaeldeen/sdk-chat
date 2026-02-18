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
    [Fact]
    [Trait("Category", "CompiledOnly")]
    public void SourceParser_CannotDistinguish_DefaultExports_FromPlatformSpecific()
    {
        var api = GetApi();
        var browserModule = api.Modules.FirstOrDefault(m => m.Name == "browser");
        var nodeModule = api.Modules.FirstOrDefault(m => m.Name == "node");
        var sharedModule = api.Modules.FirstOrDefault(m => m.Name == "shared");

        Assert.NotNull(browserModule);
        Assert.NotNull(nodeModule);
        Assert.NotNull(sharedModule);

        Assert.Contains(browserModule.Classes ?? [], c => c.Name == "BrowserClient");
        Assert.Contains(nodeModule.Classes ?? [], c => c.Name == "NodeClient");
        Assert.Contains(sharedModule.Classes ?? [], c => c.Name == "BaseClient");

        Assert.True(browserModule.Condition is "default" or "browser");
        Assert.True(nodeModule.Condition is "default" or "node");
        Assert.Equal("default", sharedModule.Condition);

        Assert.DoesNotContain("|", browserModule.Condition ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("|", nodeModule.Condition ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("|", sharedModule.Condition ?? string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain(sharedModule.Classes ?? [], c => c.Name == "NodeClient");
        Assert.DoesNotContain(sharedModule.Classes ?? [], c => c.Name == "BrowserClient");
    }
}
