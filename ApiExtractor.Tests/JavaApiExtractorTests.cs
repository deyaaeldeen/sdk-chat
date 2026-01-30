// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using ApiExtractor.Java;
using Xunit;

namespace ApiExtractor.Tests;

public class JavaApiExtractorTests
{
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures", "Java");

    private static bool IsJBangInstalled()
    {
        try
        {
            var paths = new[]
            {
                "jbang",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jbang", "bin", "jbang")
            };
            foreach (var jbang in paths)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = jbang,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(5000);
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
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        Assert.False(string.IsNullOrEmpty(api.Package));
    }

    [SkippableFact]
    public async Task Extract_FindsPackages()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        Assert.NotEmpty(api.Packages);
    }

    [SkippableFact]
    public async Task Extract_FindsClasses()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
    }

    [SkippableFact]
    public async Task Extract_FindsConstructors()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Constructors);
        Assert.NotEmpty(sampleClient.Constructors);
    }

    [SkippableFact]
    public async Task Extract_FindsMethods()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        Assert.Contains(sampleClient.Methods, m => m.Name == "getResource");
    }

    [SkippableFact]
    public async Task Extract_FindsInterfaces()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var interfaces = api.Packages.SelectMany(p => p.Interfaces ?? []).ToList();
        var iface = interfaces.FirstOrDefault(i => i.Name == "ResourceOperations");
        Assert.NotNull(iface);
    }

    [SkippableFact]
    public async Task Extract_FindsEnums()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var enums = api.Packages.SelectMany(p => p.Enums ?? []).ToList();
        var serviceVersion = enums.FirstOrDefault(e => e.Name == "ServiceVersion");
        Assert.NotNull(serviceVersion);
        Assert.NotNull(serviceVersion.Values);
        Assert.NotEmpty(serviceVersion.Values);
    }

    [SkippableFact]
    public async Task Extract_CapturesJavadoc()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.False(string.IsNullOrEmpty(sampleClient.Doc));
    }

    [SkippableFact]
    public async Task Extract_CapturesMethodSignatures()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        var method = sampleClient.Methods.FirstOrDefault(m => m.Name == "getResource");
        Assert.NotNull(method);
        Assert.False(string.IsNullOrEmpty(method.Sig));
    }

    [SkippableFact]
    public async Task Extract_FindsAsyncMethods()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var classes = api.Packages.SelectMany(p => p.Classes ?? []).ToList();
        var sampleClient = classes.FirstOrDefault(c => c.Name == "SampleClient");
        Assert.NotNull(sampleClient);
        Assert.NotNull(sampleClient.Methods);
        // Async methods in Java often return CompletableFuture
        var asyncMethod = sampleClient.Methods.FirstOrDefault(m => 
            (m.Ret != null && m.Ret.Contains("CompletableFuture")) || m.Name.EndsWith("Async"));
        Assert.NotNull(asyncMethod);
    }

    [SkippableFact]
    public async Task Format_ProducesReadableOutput()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var formatted = JavaFormatter.Format(api);
        Assert.Contains("class SampleClient", formatted);
        Assert.Contains("getResource", formatted);
    }

    [SkippableFact]
    public async Task Extract_ProducesSmallerOutputThanSource()
    {
        Skip.IfNot(IsJBangInstalled(), "JBang not installed");
        var api = await new JavaApiExtractor().ExtractAsync(TestFixturesPath);
        Assert.NotNull(api);
        var json = JsonSerializer.Serialize(api);
        var sourceSize = Directory.GetFiles(TestFixturesPath, "*.java", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
        Assert.True(json.Length < sourceSize * 0.8,
            $"JSON ({json.Length}) should be <80% of source ({sourceSize})");
    }
}
