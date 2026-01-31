// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace TestPackage;

/// <summary>
/// A sample client for testing API extraction.
/// Demonstrates public API patterns.
/// </summary>
public class SampleClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private bool _disposed;

    /// <summary>Gets the endpoint URI.</summary>
    public Uri Endpoint { get; }

    /// <summary>Gets the API version.</summary>
    public string ApiVersion { get; init; } = "2024-01-01";

    /// <summary>
    /// Creates a new instance of <see cref="SampleClient"/>.
    /// </summary>
    /// <param name="endpoint">The service endpoint.</param>
    /// <param name="options">Optional client options.</param>
    public SampleClient(Uri endpoint, SampleClientOptions? options = null)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Gets a resource by ID.
    /// </summary>
    /// <param name="id">The resource ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resource.</returns>
    public async Task<Resource> GetResourceAsync(string id, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1, cancellationToken);
        return new Resource { Id = id, Name = "Test" };
    }

    /// <summary>
    /// Lists all resources.
    /// </summary>
    /// <param name="filter">Optional filter expression.</param>
    /// <returns>An enumerable of resources.</returns>
    public IAsyncEnumerable<Resource> ListResourcesAsync(string? filter = null)
    {
        return AsyncEnumerable.Empty<Resource>();
    }

    /// <summary>
    /// Creates a new resource.
    /// </summary>
    public Task<Resource> CreateResourceAsync(ResourceCreateOptions options, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Resource { Id = Guid.NewGuid().ToString(), Name = options.Name });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Options for configuring <see cref="SampleClient"/>.
/// </summary>
public class SampleClientOptions
{
    /// <summary>Gets or sets the retry count.</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Gets or sets the timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The service version.</summary>
    public ServiceVersion Version { get; set; } = ServiceVersion.V2024_01_01;

    /// <summary>Service versions.</summary>
    public enum ServiceVersion
    {
        /// <summary>Version 2023-01-01.</summary>
        V2023_01_01,
        /// <summary>Version 2024-01-01.</summary>
        V2024_01_01
    }
}

/// <summary>
/// Represents a resource.
/// </summary>
public class Resource
{
    /// <summary>Gets or sets the resource ID.</summary>
    public required string Id { get; set; }

    /// <summary>Gets or sets the resource name.</summary>
    public required string Name { get; set; }

    /// <summary>Gets or sets optional tags.</summary>
    public IDictionary<string, string>? Tags { get; set; }

    /// <summary>Gets the created timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Options for creating a resource.
/// </summary>
public class ResourceCreateOptions
{
    /// <summary>The resource name.</summary>
    public required string Name { get; set; }

    /// <summary>Optional tags.</summary>
    public IDictionary<string, string>? Tags { get; set; }
}

/// <summary>
/// An interface for resource operations.
/// </summary>
public interface IResourceOperations
{
    /// <summary>Gets a resource.</summary>
    Task<Resource> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Deletes a resource.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Updates a resource.</summary>
    Task<Resource> UpdateAsync(string id, Resource resource, CancellationToken ct = default);
}

/// <summary>
/// Result status enumeration.
/// </summary>
public enum ResultStatus
{
    /// <summary>Operation succeeded.</summary>
    Success,
    /// <summary>Operation failed.</summary>
    Failed,
    /// <summary>Operation is pending.</summary>
    Pending
}

/// <summary>
/// A generic result wrapper.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
public readonly struct Result<T>
{
    /// <summary>Gets the value.</summary>
    public T Value { get; init; }

    /// <summary>Gets the status.</summary>
    public ResultStatus Status { get; init; }

    /// <summary>Gets the error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Creates a success result.</summary>
    public static Result<T> Ok(T value) => new() { Value = value, Status = ResultStatus.Success };

    /// <summary>Creates a failure result.</summary>
    public static Result<T> Fail(string error) => new() { Status = ResultStatus.Failed, Error = error };
}
