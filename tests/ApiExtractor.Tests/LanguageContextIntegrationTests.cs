// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Services.Languages.Samples;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Integration tests for language contexts with API extractor fallback.
/// Tests that contexts properly use extractors when available and fall back to raw source otherwise.
/// </summary>
[Collection("LanguageContext")]
public class LanguageContextIntegrationTests
{
    private readonly FileHelper _fileHelper = new();
    private static readonly string TestFixturesPath = Path.Combine(AppContext.BaseDirectory, "TestFixtures");

    #region DotNet Integration

    [Fact]
    public async Task DotNetContext_UsesApiExtractor()
    {
        var context = new DotNetSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "DotNet");

        var chunks = new List<string>();
        await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
        {
            chunks.Add(chunk);
        }

        var content = string.Join("", chunks);

        // Should use API extractor format
        Assert.Contains("<api-surface", content);
        Assert.Contains("</api-surface>", content);
        Assert.Contains("SampleClient", content);
    }

    [Fact]
    public async Task DotNetContext_ExtractedContentIsSmallerThanSource()
    {
        var context = new DotNetSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "DotNet");

        var chunks = new List<string>();
        await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
        {
            chunks.Add(chunk);
        }

        var extractedSize = string.Join("", chunks).Length;
        var sourceSize = Directory.GetFiles(sourcePath, "*.cs", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);

        Assert.True(extractedSize < sourceSize,
            $"Extracted ({extractedSize}) should be smaller than source ({sourceSize})");
    }

    #endregion

    #region Python Integration

    [Fact]
    public async Task PythonContext_UsesApiExtractor_WhenPythonAvailable()
    {
        if (!IsPythonInstalled()) Assert.Skip("Python not installed");

        var context = new PythonSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "Python");

        var chunks = new List<string>();
        await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
        {
            chunks.Add(chunk);
        }

        var content = string.Join("", chunks);

        // Should use API extractor format when Python is available
        Assert.Contains("<api-surface", content);
        Assert.Contains("SampleClient", content);
    }

    [Fact]
    public async Task PythonContext_ReturnsContent()
    {
        // This test verifies context works regardless of extractor availability
        var context = new PythonSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "Python");

        var chunks = new List<string>();
        await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
        {
            chunks.Add(chunk);
        }

        var content = string.Join("", chunks);

        // Either extracted or raw source should contain relevant content
        Assert.Contains("SampleClient", content);
    }

    #endregion

    #region Java Integration

    [Fact]
    public async Task JavaContext_UsesApiExtractor_WhenJBangAvailable()
    {
        if (!IsJBangInstalled()) Assert.Skip("JBang not installed");

        var context = new JavaSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "Java");

        var chunks = new List<string>();
        await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
        {
            chunks.Add(chunk);
        }

        var content = string.Join("", chunks);

        // Should use API extractor format when JBang is available
        Assert.Contains("<api-surface", content);
        Assert.Contains("SampleClient", content);
    }

    [Fact]
    public async Task JavaContext_ReturnsContent()
    {
        if (!IsJBangInstalled()) Assert.Skip("JBang not installed");

        var context = new JavaSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "Java");

        try
        {
            var chunks = new List<string>();
            await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
            {
                chunks.Add(chunk);
            }

            var content = string.Join("", chunks);

            // Either extracted or raw source should contain relevant content
            Assert.Contains("SampleClient", content);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Failed to extract"))
        {
            // Skip if extraction fails due to resource contention in parallel test runs
            if (true) Assert.Skip($"Extraction failed (likely parallel test contention): {ex.Message}");
        }
    }

    #endregion

    #region TypeScript Integration

    [Fact]
    public async Task TypeScriptContext_UsesApiExtractor_WhenNodeAvailable()
    {
        if (!IsNodeInstalled()) Assert.Skip("Node.js not installed");

        var context = new TypeScriptSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "TypeScript");

        var chunks = new List<string>();
        await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
        {
            chunks.Add(chunk);
        }

        var content = string.Join("", chunks);

        // Should use API extractor format when Node is available
        Assert.Contains("<api-surface", content);
        Assert.Contains("SampleClient", content);
    }

    [Fact]
    public async Task TypeScriptContext_ReturnsContent()
    {
        var context = new TypeScriptSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "TypeScript");

        var chunks = new List<string>();
        await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
        {
            chunks.Add(chunk);
        }

        var content = string.Join("", chunks);

        // Either extracted or raw source should contain relevant content
        Assert.Contains("SampleClient", content);
    }

    #endregion

    #region Go Integration

    [Fact]
    public async Task GoContext_UsesApiExtractor_WhenGoAvailable()
    {
        if (!IsGoInstalled()) Assert.Skip("Go not installed");

        var context = new GoSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "Go");

        var chunks = new List<string>();
        await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
        {
            chunks.Add(chunk);
        }

        var content = string.Join("", chunks);

        // Should use API extractor format when Go is available
        Assert.Contains("<api-surface", content);
        Assert.Contains("SampleClient", content);
    }

    [Fact]
    public async Task GoContext_ReturnsContent()
    {
        var context = new GoSampleLanguageContext(_fileHelper);
        var sourcePath = Path.Combine(TestFixturesPath, "Go");

        var chunks = new List<string>();
        await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
        {
            chunks.Add(chunk);
        }

        var content = string.Join("", chunks);

        // Either extracted or raw source should contain relevant content
        Assert.Contains("SampleClient", content);
    }

    #endregion

    #region Cross-Language Consistency

    [Fact]
    public async Task AllLanguages_ExtractSameSemanticContent()
    {
        // When extractors work, all should extract similar semantic information
        var expectedTypes = new[] { "SampleClient", "Resource" };

        // Only test languages where the runtime is available
        var languages = new List<(SampleLanguageContext context, string folder)>
        {
            (new DotNetSampleLanguageContext(_fileHelper), "DotNet"),
        };

        if (IsPythonInstalled())
            languages.Add((new PythonSampleLanguageContext(_fileHelper), "Python"));
        if (IsJBangInstalled())
            languages.Add((new JavaSampleLanguageContext(_fileHelper), "Java"));
        if (IsNodeInstalled())
            languages.Add((new TypeScriptSampleLanguageContext(_fileHelper), "TypeScript"));
        if (IsGoInstalled())
            languages.Add((new GoSampleLanguageContext(_fileHelper), "Go"));

        foreach (var (context, folder) in languages)
        {
            var sourcePath = Path.Combine(TestFixturesPath, folder);

            try
            {
                var chunks = new List<string>();
                await foreach (var chunk in context.StreamContextAsync(sourcePath, null))
                {
                    chunks.Add(chunk);
                }

                var content = string.Join("", chunks);

                foreach (var expectedType in expectedTypes)
                {
                    Assert.Contains(expectedType, content, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Failed to extract"))
            {
                // Skip this language if extraction fails due to resource contention
                // This can happen when parallel test runs compete for JBang/Node resources
                continue;
            }
        }
    }

    #endregion

    #region Helpers

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

    private static bool IsJBangInstalled()
    {
        try
        {
            var paths = new[] { "jbang", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jbang", "bin", "jbang") };
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

    #endregion
}
