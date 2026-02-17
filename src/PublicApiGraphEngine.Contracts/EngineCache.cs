// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace PublicApiGraphEngine.Contracts;

/// <summary>
/// Provides fingerprint-based caching for API graphing.
/// Avoids re-processing when source files haven't changed, enabling incremental workflows.
/// </summary>
/// <typeparam name="TIndex">The API index type (e.g., ApiIndex for each language).</typeparam>
public sealed class EngineCache<TIndex> where TIndex : class, IApiIndex
{
    private readonly Func<string, CancellationToken, Task<TIndex?>> _extractFunc;
    private readonly string[] _extensions;
    private readonly Lock _lock = new();

    private string? _cachedFingerprint;
    private TIndex? _cachedValue;
    private string? _cachedPath;

    /// <summary>
    /// Creates an engine cache with the given engine function and file extensions.
    /// </summary>
    /// <param name="extractFunc">The function that performs API graphing given a root path.</param>
    /// <param name="fileExtensions">File extensions to monitor for changes (e.g., [".py"], [".cs"]).</param>
    public EngineCache(
        Func<string, CancellationToken, Task<TIndex?>> extractFunc,
        string[] fileExtensions)
    {
        _extractFunc = extractFunc;
        _extensions = fileExtensions;
    }

    /// <summary>
    /// Returns a cached result if the directory fingerprint hasn't changed,
    /// otherwise performs a full engine run and caches the result.
    /// Only non-null results are cached.
    /// Thread-safe: concurrent callers may both trigger engine, but cached
    /// state updates are atomic.
    /// </summary>
    public async Task<TIndex?> GraphAsync(string rootPath, CancellationToken ct = default)
    {
        var normalized = Path.GetFullPath(rootPath);
        var fingerprint = DirectoryFingerprint.Compute(normalized, _extensions);

        // Read cached state atomically
        lock (_lock)
        {
            if (_cachedValue is not null && _cachedFingerprint == fingerprint && _cachedPath == normalized)
                return _cachedValue;
        }

        // Engine runs outside the lock to avoid blocking concurrent readers
        var value = await _extractFunc(normalized, ct).ConfigureAwait(false);

        if (value is not null)
        {
            lock (_lock)
            {
                _cachedValue = value;
                _cachedFingerprint = fingerprint;
                _cachedPath = normalized;
            }
        }

        return value;
    }

}
