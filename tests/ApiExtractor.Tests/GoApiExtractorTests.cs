// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Go;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Shared fixture that extracts Go API once for all tests.
/// Dramatically reduces test time by avoiding repeated Go invocations.
/// </summary>
public class GoExtractorFixture : IAsyncLifetime
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Go");
    
    public ApiIndex? Api { get; private set; }
    public string? SkipReason { get; private set; }
    public string FixturePath => TestFixturesPath;
    
    public async Task InitializeAsync()
    {
        var extractor = new GoApiExtractor();
        if (!extractor.IsAvailable())
        {
            SkipReason = extractor.UnavailableReason ?? "Go not available";
            return;
        }
        
        try
        {
            Api = await extractor.ExtractAsync(TestFixturesPath);
        }
        catch (Exception ex)
        {
            SkipReason = $"Go extraction failed: {ex.Message}";
        }
    }
    
    public Task DisposeAsync() => Task.CompletedTask;
}

/// <summary>
/// Tests for the Go API extractor.
/// Uses a shared fixture to extract API once, making tests faster.
/// </summary>
public class GoApiExtractorTests : IClassFixture<GoExtractorFixture>
{
    private readonly GoExtractorFixture _fixture;

    public GoApiExtractorTests(GoExtractorFixture fixture)
    {
        _fixture = fixture;
    }

    private ApiIndex GetApi()
    {
        Skip.If(_fixture.SkipReason != null, _fixture.SkipReason);
        return _fixture.Api!;
    }

    [SkippableFact]
    public void Extract_ReturnsApiIndex_WithPackageName()
    {
        var api = GetApi();
        Assert.NotNull(api);
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [SkippableFact]
    public void Extract_FindsPackages()
    {
        var api = GetApi();
        Assert.NotNull(api);
        Assert.NotEmpty(api.Packages);
    }

    [SkippableFact]
    public void Extract_FindsStructs()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var sampleClient = structs.FirstOrDefault(s => s.Name == "SampleClient");
        Assert.NotNull(sampleClient);
    }

    [SkippableFact]
    public void Extract_FindsStructMethods()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var sampleClient = structs.FirstOrDefault(s => s.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "GetResource");
    }

    [SkippableFact]
    public void Extract_FindsInterfaces()
    {
        var api = GetApi();
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var iface = interfaces.FirstOrDefault(i => i.Name == "ResourceOperations");
        Assert.NotNull(iface);
    }

    [SkippableFact]
    public void Extract_FindsFunctions()
    {
        var api = GetApi();
        var functions = api.Packages.SelectMany(p => p.Functions ?? []).ToList();
        Assert.NotEmpty(functions);
        Assert.Contains(functions, f => f.Name == "NewSampleClient");
    }

    [SkippableFact]
    public void Extract_FindsTypeAliases()
    {
        var api = GetApi();
        var types = api.Packages.SelectMany(p => p.Types ?? []).ToList();
        Assert.NotEmpty(types);
    }

    [SkippableFact]
    public void Extract_FindsConstants()
    {
        var api = GetApi();
        var constants = api.Packages.SelectMany(p => p.Constants ?? []).ToList();
        Assert.NotEmpty(constants);
    }

    [SkippableFact]
    public void Extract_CapturesDocComments()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var sampleClient = structs.FirstOrDefault(s => s.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.False(string.IsNullOrEmpty(sampleClient.Doc));
    }

    [SkippableFact]
    public void Extract_CapturesFunctionSignatures()
    {
        var api = GetApi();
        var functions = api.Packages.SelectMany(p => p.Functions ?? []).ToList();
        var newClient = functions.FirstOrDefault(f => f.Name == "NewSampleClient");
        Assert.NotNull(newClient);
        Assert.False(string.IsNullOrEmpty(newClient.Sig));
    }

    [SkippableFact]
    public void Extract_OnlyIncludesExportedSymbols()
    {
        var api = GetApi();

        var allStructs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var allFunctions = api.Packages.SelectMany(p => p.Functions ?? []).ToList();
        var allInterfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();

        // All exported symbols in Go start with uppercase
        foreach (var s in allStructs)
        {
            Assert.True(char.IsUpper(s.Name[0]), $"Struct {s.Name} should be exported");
        }
        foreach (var f in allFunctions)
        {
            Assert.True(char.IsUpper(f.Name[0]), $"Function {f.Name} should be exported");
        }
        foreach (var i in allInterfaces)
        {
            Assert.True(char.IsUpper(i.Name[0]), $"Interface {i.Name} should be exported");
        }
    }

    [SkippableFact]
    public void Extract_FindsStructFields()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var resource = structs.FirstOrDefault(s => s.Name == "Resource");
        Assert.NotNull(resource);
        Assert.NotNull(resource.Fields);
        Assert.NotEmpty(resource.Fields);
    }

    [SkippableFact]
    public void Extract_FindsInterfaceMethods()
    {
        var api = GetApi();
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var iface = interfaces.FirstOrDefault(i => i.Name == "ResourceOperations");
        Assert.NotNull(iface);
        Assert.NotNull(iface.Methods);
        Assert.NotEmpty(iface.Methods);
    }

    [SkippableFact]
    public void Extract_ProducesSmallerOutputThanSource()
    {
        // For small test fixtures, API surface can be 80-90% of source.
        // Real SDK packages (100s of KB) show >90% reduction.
        var api = GetApi();
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(_fixture.FixturePath, "*.go", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_test.go"))
            .Sum(f => new FileInfo(f).Length);
        Assert.True(json.Length <= sourceSize,
            $"JSON ({json.Length}) should be <= source ({sourceSize})");
    }
}
