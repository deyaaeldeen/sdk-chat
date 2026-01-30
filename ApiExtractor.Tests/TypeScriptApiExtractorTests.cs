// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.TypeScript;
using Xunit;

namespace ApiExtractor.Tests;

public class TypeScriptApiExtractorTests
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "TypeScript");

    private static bool IsNodeInstalled()
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

    [SkippableFact]
    public async Task Extract_ReturnsApiIndex_WithPackageName()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [SkippableFact]
    public async Task Extract_FindsModules()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        Assert.NotEmpty(api.Modules);
    }

    [SkippableFact]
    public async Task Extract_FindsClasses()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
    }

    [SkippableFact]
    public async Task Extract_FindsConstructors()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Constructors);
        Assert.NotEmpty(sampleClient.Constructors);
    }

    [SkippableFact]
    public async Task Extract_FindsMethods()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "getResource");
    }

    [SkippableFact]
    public async Task Extract_FindsInterfaces()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var interfaces = api.Modules.SelectMany(m => m.Interfaces ?? []).ToList();
        var resource = interfaces.FirstOrDefault(i => i.Name == "Resource");
        Assert.NotNull(resource);
    }

    [SkippableFact]
    public async Task Extract_FindsEnums()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var enums = api.Modules.SelectMany(m => m.Enums ?? []).ToList();
        var resultStatus = enums.FirstOrDefault(e => e.Name == "ResultStatus");
        Assert.NotNull(resultStatus);
        Assert.NotNull(resultStatus.Values);
        Assert.NotEmpty(resultStatus.Values);
    }

    [SkippableFact]
    public async Task Extract_FindsTypeAliases()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var types = api.Modules.SelectMany(m => m.Types ?? []).ToList();
        Assert.NotEmpty(types);
    }

    [SkippableFact]
    public async Task Extract_FindsFunctions()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var functions = api.Modules.SelectMany(m => m.Functions ?? []).ToList();
        Assert.NotEmpty(functions);
        Assert.Contains(functions, f => f.Name == "createDefaultClient" || f.Name == "batchGetResources");
    }

    [SkippableFact]
    public async Task Extract_FindsAsyncMethods()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Async == true);
    }

    [SkippableFact]
    public async Task Extract_FindsStaticMethods()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Static == true);
    }

    [SkippableFact]
    public async Task Extract_FindsProperties()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var interfaces = api.Modules.SelectMany(m => m.Interfaces ?? []).ToList();
        var resource = interfaces.FirstOrDefault(i => i.Name == "Resource");
        Assert.NotNull(resource);
        Assert.NotNull(resource.Properties);
        Assert.Contains(resource.Properties, p => p.Name == "id");
    }

    [SkippableFact]
    public async Task Extract_ExcludesPrivateMethods()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var allMethods = api.Modules
            .SelectMany(m => m.Classes ?? [])
            .SelectMany(c => c.Methods ?? [])
            .ToList();
        // Private methods (with #) should be excluded
        Assert.DoesNotContain(allMethods, m => m.Name.StartsWith("#"));
    }

    [SkippableFact]
    public async Task Extract_ProducesSmallerOutputThanSource()
    {
        Skip.IfNot(IsNodeInstalled(), "Node.js not installed");
        var api = await new TypeScriptApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(TestFixturesPath, "*.ts", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        Assert.True(json.Length < sourceSize * 0.8,
            $"JSON ({json.Length}) should be <80% of source ({sourceSize})");
    }
}
