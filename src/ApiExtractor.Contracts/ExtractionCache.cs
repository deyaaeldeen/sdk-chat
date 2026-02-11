// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace ApiExtractor.Contracts;

/// <summary>
/// Provides fingerprint-based caching for API extraction.
/// Avoids re-extraction when source files haven't changed, enabling incremental workflows.
/// </summary>
/// <typeparam name="TIndex">The API index type (e.g., ApiIndex for each language).</typeparam>
public sealed class ExtractionCache<TIndex> where TIndex : class, IApiIndex
{
    private readonly Func<string, CancellationToken, Task<TIndex?>> _extractFunc;
    private readonly string[] _extensions;

    private string? _cachedFingerprint;
    private TIndex? _cachedValue;
    private string? _cachedPath;

    /// <summary>
    /// Creates an extraction cache with the given extraction function and file extensions.
    /// </summary>
    /// <param name="extractFunc">The function that performs API extraction given a root path.</param>
    /// <param name="fileExtensions">File extensions to monitor for changes (e.g., [".py"], [".cs"]).</param>
    public ExtractionCache(
        Func<string, CancellationToken, Task<TIndex?>> extractFunc,
        string[] fileExtensions)
    {
        _extractFunc = extractFunc;
        _extensions = fileExtensions;
    }

    /// <summary>
    /// Returns a cached result if the directory fingerprint hasn't changed,
    /// otherwise performs a full extraction and caches the result.
    /// Only non-null results are cached.
    /// </summary>
    public async Task<TIndex?> ExtractAsync(string rootPath, CancellationToken ct = default)
    {
        var normalized = Path.GetFullPath(rootPath);
        var fingerprint = DirectoryFingerprint.Compute(normalized, _extensions);

        if (_cachedValue != null && _cachedFingerprint == fingerprint && _cachedPath == normalized)
            return _cachedValue;

        var value = await _extractFunc(normalized, ct).ConfigureAwait(false);

        if (value != null)
        {
            _cachedValue = value;
            _cachedFingerprint = fingerprint;
            _cachedPath = normalized;
        }

        return value;
    }

    /// <summary>
    /// Checks whether a valid cached result exists for the given path
    /// and the directory fingerprint still matches.
    /// </summary>
    public bool IsCached(string rootPath)
    {
        if (_cachedValue == null || _cachedPath == null)
            return false;

        var normalized = Path.GetFullPath(rootPath);
        if (_cachedPath != normalized)
            return false;

        var fingerprint = DirectoryFingerprint.Compute(normalized, _extensions);
        return _cachedFingerprint == fingerprint;
    }

    /// <summary>
    /// Clears the cached result, forcing re-extraction on the next call.
    /// </summary>
    public void Invalidate()
    {
        _cachedValue = null;
        _cachedFingerprint = null;
        _cachedPath = null;
    }
}
