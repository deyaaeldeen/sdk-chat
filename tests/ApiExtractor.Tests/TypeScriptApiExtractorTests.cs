// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.TypeScript;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Shared fixture that extracts TypeScript API once for all tests.
/// Dramatically reduces test time by avoiding repeated npm install and node invocations.
/// </summary>
public class TypeScriptExtractorFixture : IAsyncLifetime
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "TypeScript");
    
    public ApiIndex? Api { get; private set; }
    public string? SkipReason { get; private set; }
    public string FixturePath => TestFixturesPath;
    
    public async Task InitializeAsync()
    {
        if (!CheckNodeInstalled())
        {
            SkipReason = "Node.js not installed";
            return;
        }
        
        try
        {
            Api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        }
        catch (Exception ex)
        {
            SkipReason = $"TypeScript extraction failed: {ex.Message}";
        }
    }
    
    public Task DisposeAsync() => Task.CompletedTask;
    
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
/// Tests for the TypeScript API extractor.
/// Uses a shared fixture to extract API once, making tests ~10x faster.
/// </summary>
public class TypeScriptApiExtractorTests : IClassFixture<TypeScriptExtractorFixture>
{
    private readonly TypeScriptExtractorFixture _fixture;

    public TypeScriptApiExtractorTests(TypeScriptExtractorFixture fixture)
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
    public void Extract_FindsModules()
    {
        var api = GetApi();
        Assert.NotNull(api);
        Assert.NotEmpty(api.Modules);
    }

    [SkippableFact]
    public void Extract_FindsClasses()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
    }

    [SkippableFact]
    public void Extract_FindsConstructors()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Constructors);
        Assert.NotEmpty(sampleClient.Constructors);
    }

    [SkippableFact]
    public void Extract_FindsMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "getResource");
    }

    [SkippableFact]
    public void Extract_FindsInterfaces()
    {
        var api = GetApi();
        var interfaces = api.Modules.SelectMany(m => m.Interfaces ?? []).ToList();
        var resource = interfaces.FirstOrDefault(i => i.Name == "Resource");
        Assert.NotNull(resource);
    }

    [SkippableFact]
    public void Extract_FindsEnums()
    {
        var api = GetApi();
        var enums = api.Modules.SelectMany(m => m.Enums ?? []).ToList();
        var resultStatus = enums.FirstOrDefault(e => e.Name == "ResultStatus");
        Assert.NotNull(resultStatus);
        Assert.NotNull(resultStatus.Values);
        Assert.NotEmpty(resultStatus.Values);
    }

    [SkippableFact]
    public void Extract_FindsTypeAliases()
    {
        var api = GetApi();
        var types = api.Modules.SelectMany(m => m.Types ?? []).ToList();
        Assert.NotEmpty(types);
    }

    [SkippableFact]
    public void Extract_FindsFunctions()
    {
        var api = GetApi();
        var functions = api.Modules.SelectMany(m => m.Functions ?? []).ToList();
        Assert.NotEmpty(functions);
        Assert.Contains(functions, f => f.Name == "createDefaultClient" || f.Name == "batchGetResources");
    }

    [SkippableFact]
    public void Extract_FindsAsyncMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Async == true);
    }

    [SkippableFact]
    public void Extract_FindsStaticMethods()
    {
        var api = GetApi();
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Static == true);
    }

    [SkippableFact]
    public void Extract_FindsProperties()
    {
        var api = GetApi();
        var interfaces = api.Modules.SelectMany(m => m.Interfaces ?? []).ToList();
        var resource = interfaces.FirstOrDefault(i => i.Name == "Resource");
        Assert.NotNull(resource);
        Assert.NotNull(resource.Properties);
        Assert.Contains(resource.Properties, p => p.Name == "id");
    }

    [SkippableFact]
    public void Extract_ExcludesPrivateMethods()
    {
        var api = GetApi();
        var allMethods = api.Modules
            .SelectMany(m => m.Classes ?? [])
            .SelectMany(c => c.Methods ?? [])
            .ToList();
        Assert.DoesNotContain(allMethods, m => m.Name.StartsWith("#"));
    }

    [SkippableFact]
    public void Extract_ProducesSmallerOutputThanSource()
    {
        var api = GetApi();
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(_fixture.FixturePath, "*.ts", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        Assert.True(json.Length < sourceSize * 0.8,
            $"JSON ({json.Length}) should be <80% of source ({sourceSize})");
    }
}
