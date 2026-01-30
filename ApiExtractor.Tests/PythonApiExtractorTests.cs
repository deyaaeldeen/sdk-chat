// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Python;
using Xunit;

namespace ApiExtractor.Tests;

public class PythonApiExtractorTests
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Python");
    private readonly PythonApiExtractor _extractor = new();

    private static bool IsPythonInstalled()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "python3",
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
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [SkippableFact]
    public async Task Extract_FindsModules()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        Assert.NotEmpty(api.Modules);
    }

    [SkippableFact]
    public async Task Extract_FindsClasses()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
    }

    [SkippableFact]
    public async Task Extract_FindsMethods()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "get_resource");
    }

    [SkippableFact]
    public async Task Extract_FindsAsyncMethods()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.IsAsync == true);
    }

    [SkippableFact]
    public async Task Extract_FindsClassMethods()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.IsClassMethod == true);
    }

    [SkippableFact]
    public async Task Extract_FindsStaticMethods()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.IsStaticMethod == true);
    }

    [SkippableFact]
    public async Task Extract_FindsModuleFunctions()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var functions = api.Modules.SelectMany(m => m.Functions ?? []).ToList();
        Assert.NotEmpty(functions);
        Assert.Contains(functions, f => f.Name == "create_default_client");
    }

    [SkippableFact]
    public async Task Extract_CapturesDocstrings()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.False(string.IsNullOrEmpty(sampleClient.Doc));
    }

    [SkippableFact]
    public async Task Extract_CapturesMethodSignatures()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var classes = api.Modules.SelectMany(m => m.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        var method = sampleClient.Methods.FirstOrDefault(m => m.Name == "get_resource");
        Assert.NotNull(method);
        Assert.False(string.IsNullOrEmpty(method.Signature));
    }

    [SkippableFact]
    public async Task Extract_ExcludesPrivateMethods()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var allMethods = api.Modules
            .SelectMany(m => m.Classes ?? [])
            .SelectMany(c => c.Methods ?? [])
            .ToList();
        // Private methods (start with _) should be excluded, but __init__ and __dunder__ are allowed
        var privateMethods = allMethods.Where(m =>
            m.Name.StartsWith("_") && !m.Name.StartsWith("__"));
        Assert.Empty(privateMethods);
    }

    [SkippableFact]
    public async Task Extract_IncludesDunderMethods()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var allMethods = api.Modules
            .SelectMany(m => m.Classes ?? [])
            .SelectMany(c => c.Methods ?? [])
            .ToList();
        Assert.Contains(allMethods, m => m.Name == "__init__");
    }

    [SkippableFact]
    public async Task Format_ProducesReadableOutput()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var formatted = PythonFormatter.Format(api);
        Assert.Contains("class SampleClient", formatted);
        Assert.Contains("def get_resource", formatted);
    }

    [SkippableFact]
    public async Task Extract_ProducesSmallerOutputThanSource()
    {
        Skip.IfNot(IsPythonInstalled(), "Python3 not installed");
        var api = await _extractor.ExtractAsync(TestFixturesPath);
        var json = api.ToJson();
        var sourceSize = Directory.GetFiles(TestFixturesPath, "*.py", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        Assert.True(json.Length < sourceSize * 0.5,
            $"JSON ({json.Length}) should be <50% of source ({sourceSize})");
    }
}
