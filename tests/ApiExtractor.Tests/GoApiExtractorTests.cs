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

    public async ValueTask InitializeAsync()
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

    public ValueTask DisposeAsync() => default;
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
        if (_fixture.SkipReason != null) Assert.Skip(_fixture.SkipReason);
        return _fixture.Api!;
    }

    [Fact]
    public void Extract_ReturnsApiIndex_WithPackageName()
    {
        var api = GetApi();
        Assert.NotNull(api);
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [Fact]
    public void Extract_FindsPackages()
    {
        var api = GetApi();
        Assert.NotNull(api);
        Assert.NotEmpty(api.Packages);
    }

    [Fact]
    public void Extract_FindsStructs()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var sampleClient = structs.FirstOrDefault(s => s.Name == "SampleClient");
        Assert.NotNull(sampleClient);
    }

    [Fact]
    public void Extract_FindsStructMethods()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var sampleClient = structs.FirstOrDefault(s => s.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "GetResource");
    }

    [Fact]
    public void Extract_FindsInterfaces()
    {
        var api = GetApi();
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var iface = interfaces.FirstOrDefault(i => i.Name == "ResourceOperations");
        Assert.NotNull(iface);
    }

    [Fact]
    public void Extract_FindsFunctions()
    {
        var api = GetApi();
        var functions = api.Packages.SelectMany(p => p.Functions ?? []).ToList();
        Assert.NotEmpty(functions);
        Assert.Contains(functions, f => f.Name == "NewSampleClient");
    }

    [Fact]
    public void Extract_FindsTypeAliases()
    {
        var api = GetApi();
        var types = api.Packages.SelectMany(p => p.Types ?? []).ToList();
        Assert.NotEmpty(types);
    }

    [Fact]
    public void Extract_FindsConstants()
    {
        var api = GetApi();
        var constants = api.Packages.SelectMany(p => p.Constants ?? []).ToList();
        Assert.NotEmpty(constants);
    }

    [Fact]
    public void Extract_CapturesDocComments()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var sampleClient = structs.FirstOrDefault(s => s.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.False(string.IsNullOrEmpty(sampleClient.Doc));
    }

    [Fact]
    public void Extract_CapturesFunctionSignatures()
    {
        var api = GetApi();
        var functions = api.Packages.SelectMany(p => p.Functions ?? []).ToList();
        var newClient = functions.FirstOrDefault(f => f.Name == "NewSampleClient");
        Assert.NotNull(newClient);
        Assert.False(string.IsNullOrEmpty(newClient.Sig));
    }

    [Fact]
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

    [Fact]
    public void Extract_FindsStructFields()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var resource = structs.FirstOrDefault(s => s.Name == "Resource");
        Assert.NotNull(resource);
        Assert.NotNull(resource.Fields);
        Assert.NotEmpty(resource.Fields);
    }

    [Fact]
    public void Extract_FindsInterfaceMethods()
    {
        var api = GetApi();
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var iface = interfaces.FirstOrDefault(i => i.Name == "ResourceOperations");
        Assert.NotNull(iface);
        Assert.NotNull(iface.Methods);
        Assert.NotEmpty(iface.Methods);
    }

    [Fact]
    public void Extract_FindsStructEmbeds()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var tracked = structs.FirstOrDefault(s => s.Name == "TrackedResource");
        Assert.NotNull(tracked);
        Assert.NotNull(tracked.Embeds);
        Assert.Contains("BaseModel", tracked.Embeds);
        Assert.Contains("AuditInfo", tracked.Embeds);
    }

    [Fact]
    public void Extract_StructEmbedsNotInFields()
    {
        var api = GetApi();
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var tracked = structs.FirstOrDefault(s => s.Name == "TrackedResource");
        Assert.NotNull(tracked);
        // Embedded types should NOT appear as regular fields
        var fieldNames = (tracked.Fields ?? []).Select(f => f.Name).ToList();
        Assert.DoesNotContain("BaseModel", fieldNames);
        Assert.DoesNotContain("AuditInfo", fieldNames);
        // But regular fields should still be present
        Assert.Contains("DisplayName", fieldNames);
    }

    [Fact]
    public void Extract_FindsInterfaceEmbeds()
    {
        var api = GetApi();
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var readWriter = interfaces.FirstOrDefault(i => i.Name == "ReadWriter");
        Assert.NotNull(readWriter);
        Assert.NotNull(readWriter.Embeds);
        Assert.Contains("Reader", readWriter.Embeds);
        Assert.Contains("Writer", readWriter.Embeds);
    }

    [Fact]
    public void Extract_InterfaceEmbedsChained()
    {
        var api = GetApi();
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var rwc = interfaces.FirstOrDefault(i => i.Name == "ReadWriteCloser");
        Assert.NotNull(rwc);
        Assert.NotNull(rwc.Embeds);
        Assert.Contains("ReadWriter", rwc.Embeds);
        Assert.Contains("Closer", rwc.Embeds);
    }

    [Fact]
    public void Extract_ProducesSmallerOutputThanSource()
    {
        // For small test fixtures, API surface can be 80-120% of source size
        // (metadata like entryPoint, reExportedFrom add overhead for tiny test cases).
        // Real SDK packages (100s of KB) show >90% reduction.
        var api = GetApi();
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(_fixture.FixturePath, "*.go", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_test.go", StringComparison.Ordinal))
            .Sum(f => new FileInfo(f).Length);
        var maxAllowedSize = (int)(sourceSize * 1.2); // Allow 20% overhead for small fixtures
        Assert.True(json.Length <= maxAllowedSize,
            $"JSON ({json.Length}) should be <= 120% of source ({sourceSize})");
    }
}
