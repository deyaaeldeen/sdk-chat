// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

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
}
