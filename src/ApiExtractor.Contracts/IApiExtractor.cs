// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace ApiExtractor.Contracts;

/// <summary>
/// Common interface for all API index types.
/// Enables polymorphic handling of different language extractors.
/// </summary>
public interface IApiIndex : IDiagnosticsSource
{
    /// <summary>Gets the package name.</summary>
    string Package { get; }

    /// <summary>Formats the API index as JSON.</summary>
    string ToJson(bool pretty = false);

    /// <summary>Formats the API index as human-readable language-native stubs.</summary>
    string ToStubs();

    /// <summary>
    /// Canonical cross-language package identifier, when available.
    /// </summary>
    string? CrossLanguagePackageId => null;

    /// <summary>
    /// Structured diagnostics emitted during extraction and post-processing.
    /// </summary>
    IReadOnlyList<ApiDiagnostic> Diagnostics => [];

    /// <summary>
    /// Strips language-specific generic/type-parameter suffixes from a type name to produce
    /// a canonical form suitable for cross-extractor comparison.
    /// C# uses <![CDATA[<T>]]>, Python uses [T], Java/Go/TypeScript have no suffix in names.
    /// </summary>
    static string NormalizeTypeName(string name) => name.Split('<', '[')[0];
}

/// <summary>
/// Non-generic base interface for language-agnostic API extractor operations.
/// </summary>
public interface IApiExtractor
{
    /// <summary>
    /// Gets the language this extractor supports (e.g., "csharp", "python", "java", "go", "typescript").
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Checks if the required runtime is available (e.g., Python interpreter, JBang, Node.js, Go).
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Gets a message describing why the extractor is unavailable.
    /// </summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Extracts the public API surface and returns a common result.
    /// </summary>
    Task<ExtractorResult> ExtractAsyncCore(string rootPath, CrossLanguageMap? crossLanguageMap = null, CancellationToken ct = default);
}

/// <summary>
/// Defines the contract for language-specific API extractors.
/// All extractors produce a JSON-serializable API index and can format as language-native stubs.
/// </summary>
/// <typeparam name="TIndex">The API index type specific to this language.</typeparam>
public interface IApiExtractor<TIndex> : IApiExtractor where TIndex : class, IApiIndex
{
    /// <summary>
    /// Extracts the public API surface from the specified directory.
    /// </summary>
    Task<ExtractorResult<TIndex>> ExtractAsync(string rootPath, CrossLanguageMap? crossLanguageMap = null, CancellationToken ct = default);

    /// <summary>
    /// Formats the API index as JSON.
    /// </summary>
    string ToJson(TIndex index, bool pretty = false);

    /// <summary>
    /// Formats the API index as human-readable language-native stubs.
    /// </summary>
    string ToStubs(TIndex index);

    /// <summary>
    /// Default implementation for non-generic extraction.
    /// </summary>
    async Task<ExtractorResult> IApiExtractor.ExtractAsyncCore(string rootPath, CrossLanguageMap? crossLanguageMap, CancellationToken ct)
    {
        var result = await ExtractAsync(rootPath, crossLanguageMap, ct).ConfigureAwait(false);
        return result.ToBase();
    }
}

/// <summary>
/// Discriminated union result of an extraction operation (non-generic base).
/// </summary>
public abstract record ExtractorResult
{
    private ExtractorResult() { }

    /// <summary>True if extraction succeeded.</summary>
    public abstract bool IsSuccess { get; }

    /// <summary>Structured diagnostics encountered during extraction.</summary>
    public IReadOnlyList<ApiDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets the API index or throws if failed.</summary>
    public abstract IApiIndex GetValueOrThrow();

    /// <summary>Successful extraction result.</summary>
    public sealed record Success(IApiIndex Value) : ExtractorResult
    {
        public override bool IsSuccess => true;
        public override IApiIndex GetValueOrThrow() => Value;
    }

    /// <summary>Failed extraction result.</summary>
    public sealed record Failure(string Error) : ExtractorResult
    {
        public override bool IsSuccess => false;
        public override IApiIndex GetValueOrThrow() => throw new InvalidOperationException(Error);
    }
}

/// <summary>
/// Discriminated union result of an extraction operation.
/// </summary>
/// <typeparam name="T">The API index type.</typeparam>
public abstract record ExtractorResult<T> where T : class, IApiIndex
{
    private ExtractorResult() { }

    /// <summary>True if extraction succeeded.</summary>
    public abstract bool IsSuccess { get; }

    /// <summary>Structured diagnostics encountered during extraction.</summary>
    public IReadOnlyList<ApiDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets the value or throws if failed.</summary>
    public abstract T GetValueOrThrow();

    /// <summary>Converts to non-generic base result.</summary>
    public abstract ExtractorResult ToBase();

    /// <summary>Successful extraction result.</summary>
    public sealed record Success(T Value) : ExtractorResult<T>
    {
        public override bool IsSuccess => true;
        public override T GetValueOrThrow() => Value;
        public override ExtractorResult ToBase() => new ExtractorResult.Success(Value) { Diagnostics = Diagnostics };
    }

    /// <summary>Failed extraction result.</summary>
    public sealed record Failure(string Error) : ExtractorResult<T>
    {
        public override bool IsSuccess => false;
        public override T GetValueOrThrow() => throw new InvalidOperationException(Error);
        public override ExtractorResult ToBase() => new ExtractorResult.Failure(Error) { Diagnostics = Diagnostics };
    }

    /// <summary>Creates a successful result.</summary>
    public static ExtractorResult<T> CreateSuccess(T value, IReadOnlyList<ApiDiagnostic>? diagnostics = null)
        => new Success(value) { Diagnostics = diagnostics ?? [] };

    /// <summary>Creates a failure result.</summary>
    public static ExtractorResult<T> CreateFailure(string error)
        => new Failure(error);
}
