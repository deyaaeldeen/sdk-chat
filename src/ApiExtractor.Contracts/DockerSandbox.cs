// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace ApiExtractor.Contracts;

/// <summary>
/// Executes API extractors inside per-language Docker containers as a fallback
/// when neither the native binary nor the runtime interpreter is available on the host.
///
/// Each language has its own minimal image:
///   api-extractor-go:latest        (~5MB, scratch)
///   api-extractor-python:latest    (~80MB, debian-slim)
///   api-extractor-java:latest      (~90MB, debian-slim)
///   api-extractor-typescript:latest (~90MB, debian-slim)
///
/// All images use /extractor as the entrypoint.
/// </summary>
public static class DockerSandbox
{
    /// <summary>
    /// Environment variable prefix to override Docker image for a specific language.
    /// E.g., SDK_CHAT_DOCKER_IMAGE_PYTHON=my-registry/python-extractor:v1
    /// </summary>
    public const string ImageEnvVarPrefix = "SDK_CHAT_DOCKER_IMAGE_";

    /// <summary>
    /// Default Docker image name pattern: api-extractor-{language}:latest
    /// </summary>
    public static string GetImageName(string language)
    {
        var envVar = $"{ImageEnvVarPrefix}{language.ToUpperInvariant()}";
        return Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } env
            ? env
            : $"api-extractor-{language.ToLowerInvariant()}:latest";
    }

    /// <summary>
    /// Execute an extractor inside its per-language Docker container.
    /// Mounts the host directory at the same path inside the container for path transparency.
    /// </summary>
    public static Task<ProcessResult> ExecuteAsync(
        string imageName,
        string hostPath,
        string[] arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => ExecuteAsync(imageName, [hostPath], arguments, timeout, cancellationToken);

    /// <summary>
    /// Execute an extractor inside its per-language Docker container with multiple volume mounts.
    /// Each host path is mounted at the same location inside the container for path transparency.
    /// All per-language images use /extractor as the entrypoint.
    /// </summary>
    public static Task<ProcessResult> ExecuteAsync(
        string imageName,
        string[] hostPaths,
        string[] arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // docker run --rm -v <path1>:<path1>:ro [-v ...] <image> <args...>
        // Entrypoint is baked into the image as /extractor
        List<string> dockerArgs = ["run", "--rm"];

        // Deduplicate and add volume mounts
        foreach (var path in hostPaths.Distinct(StringComparer.Ordinal))
        {
            dockerArgs.AddRange(["-v", $"{path}:{path}:ro"]);
        }

        dockerArgs.Add(imageName);
        dockerArgs.AddRange(arguments);

        // Use a longer timeout for Docker — includes container startup overhead
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(ExtractorTimeout.Value.TotalSeconds + 30);

        return ProcessSandbox.ExecuteAsync(
            "docker",
            dockerArgs,
            timeout: effectiveTimeout,
            cancellationToken: cancellationToken
        );
    }
}
