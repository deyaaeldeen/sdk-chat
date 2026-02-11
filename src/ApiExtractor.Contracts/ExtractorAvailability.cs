// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace ApiExtractor.Contracts;

/// <summary>
/// Execution mode for an extractor.
/// </summary>
public enum ExtractorMode
{
    /// <summary>Extractor not available in any mode.</summary>
    Unavailable,

    /// <summary>Using precompiled native binary (AOT container).</summary>
    NativeBinary,

    /// <summary>Using runtime interpreter/compiler (JIT environment).</summary>
    RuntimeInterpreter,

    /// <summary>Using Docker container with precompiled extractors.</summary>
    Docker
}

/// <summary>
/// Result of extractor availability check.
/// </summary>
public sealed record ExtractorAvailabilityResult(
    bool IsAvailable,
    ExtractorMode Mode,
    string? ExecutablePath,
    string? UnavailableReason,
    string? Warning,
    string? DockerImageName = null
)
{
    /// <summary>Creates a result for unavailable extractor.</summary>
    public static ExtractorAvailabilityResult Unavailable(string reason) =>
        new(false, ExtractorMode.Unavailable, null, reason, null);

    /// <summary>Creates a result for native binary mode.</summary>
    public static ExtractorAvailabilityResult NativeBinary(string path, string? warning = null) =>
        new(true, ExtractorMode.NativeBinary, path, null, warning);

    /// <summary>Creates a result for runtime interpreter mode.</summary>
    public static ExtractorAvailabilityResult RuntimeInterpreter(string path, string? warning = null) =>
        new(true, ExtractorMode.RuntimeInterpreter, path, null, warning);

    /// <summary>Creates a result for Docker container mode.</summary>
    public static ExtractorAvailabilityResult Docker(string imageName) =>
        new(true, ExtractorMode.Docker, null, null, null, imageName);
}

/// <summary>
/// Provides robust availability detection for API extractors across AOT and JIT environments.
/// Caches results and validates executables actually work, not just that they exist.
/// </summary>
public static class ExtractorAvailability
{
    private static readonly ConcurrentDictionary<string, ExtractorAvailabilityResult> Cache = new();

    /// <summary>
    /// Timeout for executable validation in milliseconds.
    /// </summary>
    private const int ValidationTimeoutMs = 5000;

    /// <summary>
    /// Checks if an extractor is available, preferring precompiled native binary over runtime.
    /// Results are cached for the lifetime of the process.
    /// </summary>
    /// <param name="language">Extractor language identifier (e.g., "go", "python").</param>
    /// <param name="nativeBinaryName">Name of the precompiled binary (e.g., "go_extractor").</param>
    /// <param name="runtimeToolName">Name of the runtime tool (e.g., "go", "python3").</param>
    /// <param name="runtimeCandidates">Candidate paths for the runtime tool.</param>
    /// <param name="nativeValidationArgs">Args to validate native binary (default: "--help").</param>
    /// <param name="runtimeValidationArgs">Args to validate runtime tool (default: "--version").</param>
    /// <param name="forceRecheck">If true, bypasses cache and rechecks availability.</param>
    /// <returns>Availability result with mode, path, and any warnings.</returns>
    public static ExtractorAvailabilityResult Check(
        string language,
        string nativeBinaryName,
        string runtimeToolName,
        string[] runtimeCandidates,
        string nativeValidationArgs = "--help",
        string runtimeValidationArgs = "--version",
        bool forceRecheck = false)
    {
        var cacheKey = $"{language}:{nativeBinaryName}:{runtimeToolName}";

        if (!forceRecheck && Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var result = CheckInternal(
            language,
            nativeBinaryName,
            runtimeToolName,
            runtimeCandidates,
            nativeValidationArgs,
            runtimeValidationArgs);

        Cache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Clears the availability cache. Useful for testing.
    /// </summary>
    public static void ClearCache()
    {
        Cache.Clear();
        DockerImageCache.Clear();
        _dockerCliAvailable = null;
    }

    /// <summary>Cached Docker CLI availability (null = not yet checked).</summary>
    private static bool? _dockerCliAvailable;
    private static readonly Lock DockerCliLock = new();

    /// <summary>Cached Docker image availability per image name.</summary>
    private static readonly ConcurrentDictionary<string, bool> DockerImageCache = new();

    /// <summary>
    /// Checks if the Docker CLI is available on the host.
    /// </summary>
    private static bool IsDockerCliAvailable()
    {
        if (_dockerCliAvailable.HasValue)
            return _dockerCliAvailable.Value;

        lock (DockerCliLock)
        {
            if (_dockerCliAvailable.HasValue)
                return _dockerCliAvailable.Value;

            _dockerCliAvailable = ValidateExecutable("docker", "--version").Success;
            return _dockerCliAvailable.Value;
        }
    }

    /// <summary>
    /// Checks if a Docker image is available locally.
    /// </summary>
    private static bool IsDockerImageAvailable(string imageName) =>
        DockerImageCache.GetOrAdd(imageName, name =>
            ValidateExecutable("docker", $"image inspect {name}").Success);

    private static ExtractorAvailabilityResult CheckInternal(
        string language,
        string nativeBinaryName,
        string runtimeToolName,
        string[] runtimeCandidates,
        string nativeValidationArgs,
        string runtimeValidationArgs)
    {
        // 1. Check for precompiled native binary first (AOT mode)
        var nativePath = GetNativeBinaryPath(nativeBinaryName);
        if (nativePath is not null)
        {
            var validation = ValidateExecutable(nativePath, nativeValidationArgs);
            if (validation.Success)
            {
                return ExtractorAvailabilityResult.NativeBinary(nativePath);
            }

            // Native binary exists but failed validation - log warning but continue to runtime
            // This handles cases like wrong architecture, missing libraries, etc.
        }

        // 2. Fall back to runtime interpreter/compiler (JIT mode)
        var runtimeResult = ToolPathResolver.ResolveWithDetails(
            runtimeToolName,
            runtimeCandidates,
            runtimeValidationArgs);

        if (runtimeResult.IsAvailable && runtimeResult.Path is not null)
        {
            return ExtractorAvailabilityResult.RuntimeInterpreter(
                runtimeResult.Path,
                runtimeResult.WarningOrError);
        }

        // 3. Fall back to per-language Docker container
        var dockerImage = DockerSandbox.GetImageName(language);
        if (IsDockerCliAvailable() && IsDockerImageAvailable(dockerImage))
        {
            return ExtractorAvailabilityResult.Docker(dockerImage);
        }

        // 4. Neither available - provide helpful error message
        var nativeHint = nativePath is not null
            ? $" Native binary found at {nativePath} but failed to execute."
            : "";

        var dockerHint = IsDockerCliAvailable()
            ? $" Docker image '{dockerImage}' not found — build it with: docker build -f extractors/{language.ToLowerInvariant()}/Dockerfile -t {dockerImage} ."
            : " Docker not available for container fallback.";

        return ExtractorAvailabilityResult.Unavailable(
            $"{language} extractor not available.{nativeHint} " +
            $"Runtime tool '{runtimeToolName}' not found. " +
            GetInstallHint(language) +
            dockerHint);
    }

    /// <summary>
    /// Gets the path to a precompiled native binary if it exists.
    /// Checks AppContext.BaseDirectory (where the main executable lives).
    /// </summary>
    private static string? GetNativeBinaryPath(string binaryName)
    {
        var assemblyDir = AppContext.BaseDirectory;
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        var binaryPath = Path.Combine(assemblyDir, binaryName + extension);

        return File.Exists(binaryPath) ? binaryPath : null;
    }

    /// <summary>
    /// Validates that an executable runs successfully with given args.
    /// More robust than just checking file existence.
    /// </summary>
    private static (bool Success, string? Error) ValidateExecutable(string path, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // SECURITY: Use ArgumentList for proper escaping - prevents injection
            foreach (var arg in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "Failed to start process");
            }

            // Drain stdout/stderr BEFORE WaitForExit to prevent pipe-buffer deadlocks:
            // if the child fills the OS pipe buffer (~4-64KB), WaitForExit hangs indefinitely.
            process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            var completed = process.WaitForExit(ValidationTimeoutMs);
            if (!completed)
            {
                try { process.Kill(); }
                catch (Exception ex)
                {
                    Trace.TraceWarning("Failed to kill timed-out validation process '{0}': {1}", path, ex.Message);
                }
                return (false, "Validation timed out");
            }

            // Exit code 0 or 1 are acceptable for --help (some tools return 1 for help)
            // Exit code 0 is required for --version
            // Use exact token match to avoid false positives from substrings like "--helper"
            var splitArgs = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var isHelpArgs = Array.Exists(splitArgs, a => a == "--help");
            var acceptableExitCodes = isHelpArgs ? new[] { 0, 1 } : new[] { 0 };
            if (acceptableExitCodes.Contains(process.ExitCode))
            {
                return (true, null);
            }

            return (false, $"Exit code {process.ExitCode}: {stderr}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Gets installation hint for a language's runtime tool.
    /// Provides exact shell commands where possible so the user can copy-paste to fix.
    /// </summary>
    private static string GetInstallHint(string language) => language.ToLowerInvariant() switch
    {
        "go" => "Install Go: https://go.dev/dl/ — or run: curl -fsSL https://go.dev/dl/go1.23.0.linux-amd64.tar.gz | sudo tar -C /usr/local -xzf -",
        "python" => "Install Python 3.9+: https://python.org — or run: sudo apt install python3 (Debian/Ubuntu) / brew install python3 (macOS)",
        "java" => "Install JBang: https://jbang.dev — or run: curl -Ls https://sh.jbang.dev | bash -s - app setup",
        "typescript" or "javascript" => "Install Node.js 20+: https://nodejs.org — or run: curl -fsSL https://deb.nodesource.com/setup_20.x | sudo bash -",
        "dotnet" or "csharp" => "Install .NET SDK: https://dot.net — or run: curl -fsSL https://dot.net/v1/dotnet-install.sh | bash",
        _ => $"Install the {language} runtime"
    };
}
