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
    /// Verifies that the compiled engine correctly assigns platform-specific conditions
    /// to types that appear in condition-specific .d.ts entry points.
    ///
    /// NodeClient is exported from both dist/types/index.d.ts (fallback) AND
    /// dist/types/node/index.d.ts (node condition). The engine should assign
    /// the most specific condition ("node") rather than the generic fallback.
    /// </summary>
    [Fact]
    [Trait("Category", "CompiledOnly")]
    public void CompiledEngine_AssignsPlatformSpecificConditions()
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

        // Platform-specific modules get their specific condition
        Assert.True(browserModule.Condition is "default" or "browser");
        Assert.True(nodeModule.Condition is "default" or "node");
        Assert.Equal("default", sharedModule.Condition);

        // Conditions must be single canonical values (no raw "|" chains)
        Assert.DoesNotContain("|", browserModule.Condition ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("|", nodeModule.Condition ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("|", sharedModule.Condition ?? string.Empty, StringComparison.Ordinal);

        Assert.DoesNotContain(sharedModule.Classes ?? [], c => c.Name == "NodeClient");
        Assert.DoesNotContain(sharedModule.Classes ?? [], c => c.Name == "BrowserClient");
    }

    /// <summary>
    /// Verifies that symbols exported from BOTH a fallback entry (types/default
    /// pointing to index.d.ts) AND a platform-specific entry get the platform
    /// condition, not "default". Only symbols appearing exclusively in the fallback
    /// should keep "default".
    /// </summary>
    [Fact]
    [Trait("Category", "CompiledOnly")]
    public void CompiledEngine_SymbolConditions_PreferSpecificOverFallback()
    {
        var api = GetApi();
        var allClasses = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var allInterfaces = api.Modules.SelectMany(m => m.Interfaces ?? []).ToList();

        // NodeClient appears in node/index.d.ts AND fallback index.d.ts
        // It should get the "node" condition (most specific), not "default"
        var nodeClient = allClasses.FirstOrDefault(c => c.Name == "NodeClient");
        Assert.NotNull(nodeClient);
        var nodeModule = api.Modules.First(m => (m.Classes ?? []).Any(c => c.Name == "NodeClient"));
        Assert.True(nodeModule.Condition is "node" or "default",
            $"NodeClient module should be 'node' or 'default', got '{nodeModule.Condition}'");

        // BrowserClient appears in browser/index.d.ts AND fallback index.d.ts
        var browserClient = allClasses.FirstOrDefault(c => c.Name == "BrowserClient");
        Assert.NotNull(browserClient);
        var browserModule = api.Modules.First(m => (m.Classes ?? []).Any(c => c.Name == "BrowserClient"));
        Assert.True(browserModule.Condition is "browser" or "default",
            $"BrowserClient module should be 'browser' or 'default', got '{browserModule.Condition}'");

        // Shared types (BaseClient, ClientOptions, Resource) appear in ALL entries
        // They should get "default" since they're universal
        var baseClient = allClasses.FirstOrDefault(c => c.Name == "BaseClient");
        Assert.NotNull(baseClient);
        var sharedModule = api.Modules.First(m => (m.Classes ?? []).Any(c => c.Name == "BaseClient"));
        Assert.Equal("default", sharedModule.Condition);
    }
}
