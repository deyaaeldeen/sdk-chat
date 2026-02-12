// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using Xunit;

namespace ApiExtractor.Tests;

public class ExtractionCacheTests : IDisposable
{
    private readonly string _tempDir;

    public ExtractionCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sdk-chat-cache-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>Minimal IApiIndex implementation for testing.</summary>
    private sealed record StubApiIndex(string Package) : IApiIndex
    {
        public string ToJson(bool pretty = false) => $"{{\"package\":\"{Package}\"}}";
        public string ToStubs() => $"package {Package}";
    }

    [Fact]
    public async Task ExtractAsync_FirstCall_InvokesExtractor()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");
        var callCount = 0;

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) =>
            {
                callCount++;
                return Task.FromResult<StubApiIndex?>(new StubApiIndex("test-pkg"));
            },
            [".py"]);

        var result = await cache.ExtractAsync(_tempDir);

        Assert.NotNull(result);
        Assert.Equal("test-pkg", result!.Package);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExtractAsync_SecondCall_SameState_ReturnsCached()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");
        var callCount = 0;

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) =>
            {
                callCount++;
                return Task.FromResult<StubApiIndex?>(new StubApiIndex("test-pkg"));
            },
            [".py"]);

        var result1 = await cache.ExtractAsync(_tempDir);
        var result2 = await cache.ExtractAsync(_tempDir);

        Assert.Same(result1, result2);
        Assert.Equal(1, callCount); // Only extracted once
    }

    [Fact]
    public async Task ExtractAsync_FileChanged_ReExtracts()
    {
        var filePath = Path.Combine(_tempDir, "file.py");
        File.WriteAllText(filePath, "x = 1");
        var callCount = 0;

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) =>
            {
                callCount++;
                return Task.FromResult<StubApiIndex?>(new StubApiIndex($"pkg-v{callCount}"));
            },
            [".py"]);

        var result1 = await cache.ExtractAsync(_tempDir);
        Assert.Equal("pkg-v1", result1!.Package);

        // Modify file to change fingerprint
        Thread.Sleep(50); // Ensure mtime changes
        File.WriteAllText(filePath, "x = 2; y = 3");

        var result2 = await cache.ExtractAsync(_tempDir);
        Assert.Equal("pkg-v2", result2!.Package);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExtractAsync_FileAdded_ReExtracts()
    {
        File.WriteAllText(Path.Combine(_tempDir, "a.py"), "x = 1");
        var callCount = 0;

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) =>
            {
                callCount++;
                return Task.FromResult<StubApiIndex?>(new StubApiIndex($"pkg-v{callCount}"));
            },
            [".py"]);

        await cache.ExtractAsync(_tempDir);

        // Add a new file
        File.WriteAllText(Path.Combine(_tempDir, "b.py"), "y = 2");

        await cache.ExtractAsync(_tempDir);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExtractAsync_NullResult_NotCached()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");
        var callCount = 0;

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) =>
            {
                callCount++;
                return Task.FromResult<StubApiIndex?>(null);
            },
            [".py"]);

        var result1 = await cache.ExtractAsync(_tempDir);
        var result2 = await cache.ExtractAsync(_tempDir);

        Assert.Null(result1);
        Assert.Null(result2);
        Assert.Equal(2, callCount); // Called twice since null is not cached
    }

    [Fact]
    public async Task Invalidate_ForceReExtraction()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");
        var callCount = 0;

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) =>
            {
                callCount++;
                return Task.FromResult<StubApiIndex?>(new StubApiIndex($"pkg-v{callCount}"));
            },
            [".py"]);

        await cache.ExtractAsync(_tempDir);
        Assert.Equal(1, callCount);

        cache.Invalidate();

        await cache.ExtractAsync(_tempDir);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task IsCached_ReturnsTrueWhenCacheValid()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) => Task.FromResult<StubApiIndex?>(new StubApiIndex("pkg")),
            [".py"]);

        Assert.False(cache.IsCached(_tempDir));

        await cache.ExtractAsync(_tempDir);

        Assert.True(cache.IsCached(_tempDir));
    }

    [Fact]
    public async Task IsCached_ReturnsFalseAfterFileChange()
    {
        var filePath = Path.Combine(_tempDir, "file.py");
        File.WriteAllText(filePath, "x = 1");

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) => Task.FromResult<StubApiIndex?>(new StubApiIndex("pkg")),
            [".py"]);

        await cache.ExtractAsync(_tempDir);
        Assert.True(cache.IsCached(_tempDir));

        Thread.Sleep(50);
        File.WriteAllText(filePath, "x = 2");

        Assert.False(cache.IsCached(_tempDir));
    }

    [Fact]
    public async Task IsCached_ReturnsFalseAfterInvalidation()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) => Task.FromResult<StubApiIndex?>(new StubApiIndex("pkg")),
            [".py"]);

        await cache.ExtractAsync(_tempDir);
        cache.Invalidate();

        Assert.False(cache.IsCached(_tempDir));
    }

    [Fact]
    public async Task IsCached_ReturnsFalseForDifferentPath()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) => Task.FromResult<StubApiIndex?>(new StubApiIndex("pkg")),
            [".py"]);

        await cache.ExtractAsync(_tempDir);

        Assert.False(cache.IsCached("/some/other/path"));
    }

    [Fact]
    public async Task ExtractAsync_ExceptionPropagates()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");

        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) => throw new InvalidOperationException("extractor failed"),
            [".py"]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.ExtractAsync(_tempDir));
    }

    [Fact]
    public async Task ExtractAsync_DifferentPaths_NotConfused()
    {
        var dir2 = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(dir2);
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");
        File.WriteAllText(Path.Combine(dir2, "file.py"), "y = 2");

        var callCount = 0;
        var cache = new ExtractionCache<StubApiIndex>(
            (path, ct) =>
            {
                callCount++;
                return Task.FromResult<StubApiIndex?>(new StubApiIndex(path));
            },
            [".py"]);

        var r1 = await cache.ExtractAsync(_tempDir);
        var r2 = await cache.ExtractAsync(dir2);

        Assert.Equal(2, callCount);
        Assert.NotEqual(r1!.Package, r2!.Package);
    }

    [Fact]
    public async Task ExtractAsync_ConcurrentAccess_IsThreadSafe()
    {
        File.WriteAllText(Path.Combine(_tempDir, "file.py"), "x = 1");

        var callCount = 0;
        var cache = new ExtractionCache<StubApiIndex>(
            async (path, ct) =>
            {
                Interlocked.Increment(ref callCount);
                await Task.Delay(50, ct); // Simulate work to increase contention window
                return new StubApiIndex(path);
            },
            [".py"]);

        // Fire multiple concurrent extractions against the same path
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => cache.ExtractAsync(_tempDir)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // All results should be non-null and consistent
        Assert.All(results, r => Assert.NotNull(r));
        Assert.All(results, r => Assert.Equal(_tempDir, r!.Package));

        // Verify extraction ran at least once
        Assert.True(callCount >= 1, $"Expected at least 1 extraction call, got {callCount}");

        // After concurrent burst, subsequent calls should hit cache (fingerprint unchanged)
        callCount = 0;
        var cached = await cache.ExtractAsync(_tempDir);
        Assert.NotNull(cached);
        Assert.Equal(0, callCount);
    }
}
