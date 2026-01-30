// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Go;
using Xunit;

namespace ApiExtractor.Tests;

public class GoApiExtractorTests
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Go");

    private static bool IsGoInstalled()
    {
        try
        {
            var paths = new[] { "go", "/usr/local/go/bin/go" };
            foreach (var goPath in paths)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = goPath,
                        Arguments = "version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(1000);
                    if (p?.ExitCode == 0) return true;
                }
                catch { }
            }
            return false;
        }
        catch { return false; }
    }

    [SkippableFact]
    public async Task Extract_ReturnsApiIndex_WithPackageName()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [SkippableFact]
    public async Task Extract_FindsPackages()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        Assert.NotEmpty(api.Packages);
    }

    [SkippableFact]
    public async Task Extract_FindsStructs()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var sampleClient = structs.FirstOrDefault(s => s.Name == "SampleClient");
        Assert.NotNull(sampleClient);
    }

    [SkippableFact]
    public async Task Extract_FindsStructMethods()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var sampleClient = structs.FirstOrDefault(s => s.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "GetResource");
    }

    [SkippableFact]
    public async Task Extract_FindsInterfaces()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var iface = interfaces.FirstOrDefault(i => i.Name == "ResourceOperations");
        Assert.NotNull(iface);
    }

    [SkippableFact]
    public async Task Extract_FindsFunctions()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var functions = api.Packages.SelectMany(p => p.Functions ?? []).ToList();
        Assert.NotEmpty(functions);
        Assert.Contains(functions, f => f.Name == "NewSampleClient");
    }

    [SkippableFact]
    public async Task Extract_FindsTypeAliases()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var types = api.Packages.SelectMany(p => p.Types ?? []).ToList();
        Assert.NotEmpty(types);
    }

    [SkippableFact]
    public async Task Extract_FindsConstants()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var constants = api.Packages.SelectMany(p => p.Constants ?? []).ToList();
        Assert.NotEmpty(constants);
    }

    [SkippableFact]
    public async Task Extract_CapturesDocComments()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var sampleClient = structs.FirstOrDefault(s => s.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.False(string.IsNullOrEmpty(sampleClient.Doc));
    }

    [SkippableFact]
    public async Task Extract_CapturesFunctionSignatures()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var functions = api.Packages.SelectMany(p => p.Functions ?? []).ToList();
        var newClient = functions.FirstOrDefault(f => f.Name == "NewSampleClient");
        Assert.NotNull(newClient);
        Assert.False(string.IsNullOrEmpty(newClient.Sig));
    }

    [SkippableFact]
    public async Task Extract_OnlyIncludesExportedSymbols()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);

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
    public async Task Extract_FindsStructFields()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var structs = api.Packages.SelectMany(p => p.Structs ?? []).ToList();
        var resource = structs.FirstOrDefault(s => s.Name == "Resource");
        Assert.NotNull(resource);
        Assert.NotNull(resource.Fields);
        Assert.NotEmpty(resource.Fields);
    }

    [SkippableFact]
    public async Task Extract_FindsInterfaceMethods()
    {
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var iface = interfaces.FirstOrDefault(i => i.Name == "ResourceOperations");
        Assert.NotNull(iface);
        Assert.NotNull(iface.Methods);
        Assert.NotEmpty(iface.Methods);
    }

    [SkippableFact]
    public async Task Extract_ProducesSmallerOutputThanSource()
    {
        // For small test fixtures, API surface can be 80-90% of source.
        // Real SDK packages (100s of KB) show >90% reduction.
        Skip.IfNot(IsGoInstalled(), "Go not installed");
        var api = await new GoApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(TestFixturesPath, "*.go", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_test.go"))
            .Sum(f => new FileInfo(f).Length);
        Assert.True(json.Length <= sourceSize,
            $"JSON ({json.Length}) should be <= source ({sourceSize})");
    }
}
