// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using ApiExtractor.DotNet;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for the CSharpUsageAnalyzer.
/// </summary>
public class UsageAnalyzerTests
{
    private readonly CSharpUsageAnalyzer _analyzer = new();

    [Fact]
    public async Task AnalyzeAsync_EmptyDirectory_ReturnsEmptyIndex()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"usage_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var apiIndex = CreateTestApiIndex();

            // Act
            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // Assert
            Assert.Equal(0, result.FileCount);
            Assert.Empty(result.CoveredOperations);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsDirectMethodCall()
    {
        // Arrange
        var (tempDir, _) = await SetupTestFilesAsync(
            ("sample.cs", """
                using TestSdk;

                var client = new ChatClient();
                var result = await client.GetCompletionAsync("Hello");
                Console.WriteLine(result);
                """));

        try
        {
            var apiIndex = CreateTestApiIndex();

            // Act
            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // Assert
            Assert.Equal(1, result.FileCount);
            Assert.Single(result.CoveredOperations);
            Assert.Equal("ChatClient", result.CoveredOperations[0].ClientType);
            Assert.Equal("GetCompletionAsync", result.CoveredOperations[0].Operation);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsMultipleMethods()
    {
        // Arrange
        var (tempDir, _) = await SetupTestFilesAsync(
            ("sample.cs", """
                var chatClient = new ChatClient();
                var completion = await chatClient.GetCompletionAsync("Hi");
                var stream = chatClient.GetStreamingCompletionAsync("Hi");

                var embedClient = new EmbeddingClient();
                var embeddings = await embedClient.GetEmbeddingsAsync(["text"]);
                """));

        try
        {
            var apiIndex = CreateTestApiIndex();

            // Act
            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // Assert
            Assert.Equal(3, result.CoveredOperations.Count);

            var chatOps = result.CoveredOperations.Where(o => o.ClientType == "ChatClient").ToList();
            Assert.Equal(2, chatOps.Count);
            Assert.Contains(chatOps, o => o.Operation == "GetCompletionAsync");
            Assert.Contains(chatOps, o => o.Operation == "GetStreamingCompletionAsync");

            var embedOps = result.CoveredOperations.Where(o => o.ClientType == "EmbeddingClient").ToList();
            Assert.Single(embedOps);
            Assert.Equal("GetEmbeddingsAsync", embedOps[0].Operation);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_IdentifiesUncoveredOperations()
    {
        // Arrange
        var (tempDir, _) = await SetupTestFilesAsync(
            ("sample.cs", """
                var client = new ChatClient();
                await client.GetCompletionAsync("test");
                """));

        try
        {
            var apiIndex = CreateTestApiIndex();

            // Act
            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // Assert - GetStreamingCompletionAsync should be uncovered
            Assert.Contains(result.UncoveredOperations,
                o => o.ClientType == "ChatClient" && o.Operation == "GetStreamingCompletionAsync");

            // EmbeddingClient.GetEmbeddingsAsync should also be uncovered
            Assert.Contains(result.UncoveredOperations,
                o => o.ClientType == "EmbeddingClient" && o.Operation == "GetEmbeddingsAsync");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsSubclientMethodCall()
    {
        // Arrange
        var (tempDir, _) = await SetupTestFilesAsync(
            ("sample.cs", """
                var client = new EmptyClient();
                await client.Widgets.ListWidgetsAsync();
                """));

        try
        {
            var apiIndex = CreateTestApiIndex();

            // Act
            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // Assert
            Assert.Contains(result.CoveredOperations,
                o => o.ClientType == "WidgetClient" && o.Operation == "ListWidgetsAsync");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_DeduplicatesOperations()
    {
        // Arrange - same method called multiple times
        var (tempDir, _) = await SetupTestFilesAsync(
            ("sample1.cs", """
                var client = new ChatClient();
                await client.GetCompletionAsync("test1");
                """),
            ("sample2.cs", """
                var client = new ChatClient();
                await client.GetCompletionAsync("test2");
                await client.GetCompletionAsync("test3");
                """));

        try
        {
            var apiIndex = CreateTestApiIndex();

            // Act
            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // Assert - should only appear once despite 3 calls
            var chatOps = result.CoveredOperations.Where(o =>
                o.ClientType == "ChatClient" && o.Operation == "GetCompletionAsync").ToList();
            Assert.Single(chatOps);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_IgnoresBinObjFolders()
    {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), $"usage_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "obj"));

        // Put a file in obj that should be ignored
        await File.WriteAllTextAsync(Path.Combine(tempDir, "obj", "sample.cs"), """
            var client = new ChatClient();
            await client.GetCompletionAsync("test");
            """);

        try
        {
            var apiIndex = CreateTestApiIndex();

            // Act
            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // Assert - file in obj should be ignored
            Assert.Equal(0, result.FileCount);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Format_ProducesReadableOutput()
    {
        // Arrange
        var usage = new Contracts.UsageIndex
        {
            FileCount = 5,
            CoveredOperations =
            [
                new() { ClientType = "ChatClient", Operation = "GetCompletionAsync", File = "sample1.cs", Line = 10 },
                new() { ClientType = "ChatClient", Operation = "GetStreamingCompletionAsync", File = "sample2.cs", Line = 15 }
            ],
            UncoveredOperations =
            [
                new() { ClientType = "EmbeddingClient", Operation = "GetEmbeddingsAsync", Signature = "GetEmbeddingsAsync(...)" }
            ]
        };

        // Act
        var output = _analyzer.Format(usage);

        // Assert
        Assert.Contains("Analyzed 5 files", output);
        Assert.Contains("COVERED OPERATIONS", output);
        Assert.Contains("ChatClient.GetCompletionAsync", output);
        Assert.Contains("UNCOVERED OPERATIONS", output);
        Assert.Contains("EmbeddingClient.GetEmbeddingsAsync", output);
    }

    [Fact]
    public async Task AnalyzeAsync_NoApiClients_ReturnsEmptyIndex()
    {
        // Arrange
        var (tempDir, _) = await SetupTestFilesAsync(
            ("sample.cs", """
                Console.WriteLine("Hello World");
                """));

        try
        {
            // Empty API index with no clients
            var apiIndex = new ApiIndex { Package = "TestSdk" };

            // Act
            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // Assert
            Assert.Empty(result.CoveredOperations);
            Assert.Empty(result.UncoveredOperations);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    #region Test Helpers

    private static ApiIndex CreateTestApiIndex() => new()
    {
        Package = "TestSdk",
        Namespaces =
        [
            new NamespaceInfo
            {
                Name = "TestSdk",
                Types =
                [
                    new TypeInfo
                    {
                        Name = "ChatClient",
                        Kind = "class",
                        Members =
                        [
                            new MemberInfo { Name = "GetCompletionAsync", Kind = "method", Signature = "Task<string> GetCompletionAsync(string prompt)" },
                            new MemberInfo { Name = "GetStreamingCompletionAsync", Kind = "method", Signature = "IAsyncEnumerable<string> GetStreamingCompletionAsync(string prompt)" },
                            new MemberInfo { Name = "Widgets", Kind = "property", Signature = "WidgetClient Widgets { get; }" }
                        ]
                    },
                    new TypeInfo
                    {
                        Name = "EmptyClient",
                        Kind = "class",
                        Members =
                        [
                            new MemberInfo { Name = "Widgets", Kind = "property", Signature = "WidgetClient Widgets { get; }" }
                        ]
                    },
                    new TypeInfo
                    {
                        Name = "WidgetClient",
                        Kind = "class",
                        Members =
                        [
                            new MemberInfo { Name = "ListWidgetsAsync", Kind = "method", Signature = "Task<IReadOnlyList<string>> ListWidgetsAsync()" }
                        ]
                    },
                    new TypeInfo
                    {
                        Name = "EmbeddingClient",
                        Kind = "class",
                        Members =
                        [
                            new MemberInfo { Name = "GetEmbeddingsAsync", Kind = "method", Signature = "Task<float[][]> GetEmbeddingsAsync(string[] inputs)" }
                        ]
                    }
                ]
            }
        ]
    };

    private static async Task<(string TempDir, string[] Files)> SetupTestFilesAsync(params (string Name, string Content)[] files)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"usage_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var paths = new List<string>();
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(tempDir, name);
            await File.WriteAllTextAsync(path, content);
            paths.Add(path);
        }

        return (tempDir, paths.ToArray());
    }

    #endregion

    #region Regression: Shared UsageFormatter

    [Fact]
    public void UsageFormatter_MatchesAnalyzerFormat()
    {
        // The shared UsageFormatter.Format must produce identical output
        // to what the per-analyzer Format used to produce.
        var usage = new Contracts.UsageIndex
        {
            FileCount = 3,
            CoveredOperations =
            [
                new() { ClientType = "ChatClient", Operation = "SendAsync", File = "s1.cs", Line = 5 }
            ],
            UncoveredOperations =
            [
                new() { ClientType = "ChatClient", Operation = "ListAsync", Signature = "() -> Task" }
            ]
        };

        var fromAnalyzer = _analyzer.Format(usage);
        var fromShared = UsageFormatter.Format(usage);

        Assert.Equal(fromShared, fromAnalyzer);
    }

    [Fact]
    public void UsageFormatter_EmptyIndex_ProducesMinimalOutput()
    {
        var usage = new Contracts.UsageIndex
        {
            FileCount = 0,
            CoveredOperations = [],
            UncoveredOperations = []
        };

        var result = UsageFormatter.Format(usage);

        Assert.Contains("Analyzed 0 files", result);
        Assert.DoesNotContain("COVERED OPERATIONS", result);
        Assert.DoesNotContain("UNCOVERED OPERATIONS", result);
    }

    [Fact]
    public void UsageFormatter_SortsOperationsCorrectly()
    {
        var usage = new Contracts.UsageIndex
        {
            FileCount = 2,
            CoveredOperations =
            [
                new() { ClientType = "Zebra", Operation = "B", File = "f.cs", Line = 1 },
                new() { ClientType = "Alpha", Operation = "Z", File = "f.cs", Line = 2 },
                new() { ClientType = "Alpha", Operation = "A", File = "f.cs", Line = 3 }
            ],
            UncoveredOperations = []
        };

        var result = UsageFormatter.Format(usage);

        // Alpha.A should come before Alpha.Z, and both before Zebra.B
        var alphaA = result.IndexOf("Alpha.A", StringComparison.Ordinal);
        var alphaZ = result.IndexOf("Alpha.Z", StringComparison.Ordinal);
        var zebraB = result.IndexOf("Zebra.B", StringComparison.Ordinal);

        Assert.True(alphaA < alphaZ, "Alpha.A should appear before Alpha.Z");
        Assert.True(alphaZ < zebraB, "Alpha.Z should appear before Zebra.B");
    }

    #endregion

    #region Regression: Case-Insensitive Client Type Resolution (Fix #2)

    [Fact]
    public async Task CSharpUsageAnalyzer_CaseInsensitiveClientTypeResolution()
    {
        // This tests that stylistic casing differences in code don't affect detection
        var (tempDir, _) = await SetupTestFilesAsync(
            ("sample.cs", """
                var chatClient = new ChatClient();
                chatClient.GetCompletionAsync("hello");
                """));

        try
        {
            var apiIndex = new ApiIndex
            {
                Package = "TestSdk",
                Namespaces =
                [
                    new NamespaceInfo
                    {
                        Name = "TestSdk",
                        Types =
                        [
                            new TypeInfo
                            {
                                Name = "ChatClient",
                                Kind = "class",
                                Members =
                                [
                                    new MemberInfo { Name = "GetCompletionAsync", Kind = "method", Signature = "Task<string> GetCompletionAsync(string prompt)" }
                                ]
                            }
                        ]
                    }
                ]
            };

            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // Should detect the method call regardless of variable casing
            Assert.NotEmpty(result.CoveredOperations);
            // The ClientType should be the canonical name from API index
            Assert.Equal("ChatClient", result.CoveredOperations[0].ClientType);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region Regression: Uncovered Operations Use Real Signatures

    [Fact]
    public async Task AnalyzeAsync_UncoveredOperations_UseRealSignaturesFromApiIndex()
    {
        // Arrange: API index with a method signature, but no sample code covers it
        var tempDir = Path.Combine(Path.GetTempPath(), $"usage_sig_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write an empty sample file — no calls to GetResourceAsync
            await File.WriteAllTextAsync(Path.Combine(tempDir, "sample.cs"),
                """
                using System;
                class Sample { void Main() { } }
                """);

            var apiIndex = new ApiIndex
            {
                Package = "TestPackage",
                Namespaces =
                [
                    new NamespaceInfo
                    {
                        Name = "TestPackage",
                        Types =
                        [
                            new TypeInfo
                            {
                                Name = "SampleClient",
                                Kind = "class",
                                EntryPoint = true,
                                Members =
                                [
                                    new MemberInfo { Name = "GetResourceAsync", Kind = "method", Signature = "Task<Resource> GetResourceAsync(string id, CancellationToken ct)" }
                                ]
                            }
                        ]
                    }
                ]
            };

            var result = await _analyzer.AnalyzeAsync(tempDir, apiIndex);

            // The uncovered operation should use the real signature from API index
            var uncovered = result.UncoveredOperations.FirstOrDefault(u => u.Operation == "GetResourceAsync");
            Assert.NotNull(uncovered);
            Assert.Contains("GetResourceAsync", uncovered.Signature);
            Assert.DoesNotContain("(...)", uncovered.Signature);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    #endregion
}
