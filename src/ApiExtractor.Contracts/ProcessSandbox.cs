// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ApiExtractor.Contracts;

/// <summary>
/// Centralized, hardened process execution for API extractors.
/// All external process invocations SHOULD go through this class.
/// 
/// Security features:
/// - Enforced timeouts (no runaway processes)
/// - Output capture with size limits
/// - Structured telemetry
/// </summary>
public static class ProcessSandbox
{
    /// <summary>Maximum output size per stream (10MB).</summary>
    public const int MaxOutputBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Execute a process with sandboxed settings.
    /// </summary>
    /// <param name="fileName">Executable path - must be validated before calling.</param>
    /// <param name="arguments">Arguments - will be passed via ArgumentList for safety.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <param name="timeout">Timeout override. Uses ExtractorTimeout.Value if not specified.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process result with exit code, stdout, and stderr.</returns>
    public static async Task<ProcessResult> ExecuteAsync(
        string fileName,
        IEnumerable<string>? arguments = null,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateFileName(fileName);

        var effectiveTimeout = timeout ?? ExtractorTimeout.Value;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);
        var effectiveCt = timeoutCts.Token;

        using var activity = ExtractorTelemetry.StartProcess(Path.GetFileName(fileName), "sandbox");

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        // Use ArgumentList for proper escaping - prevents injection
        if (arguments != null)
        {
            foreach (var arg in arguments)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        var startTime = Stopwatch.GetTimestamp();

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return ProcessResult.Failed(-1, "", $"Failed to start process: {fileName}");
            }

            // Close stdin immediately - most extractors don't need it
            process.StandardInput.Close();

            // Read streams in parallel to prevent deadlocks
            var outputTask = ReadStreamWithLimitAsync(process.StandardOutput, MaxOutputBytes, effectiveCt);
            var errorTask = ReadStreamWithLimitAsync(process.StandardError, MaxOutputBytes, effectiveCt);

            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(effectiveCt)).ConfigureAwait(false);

            var output = await outputTask;
            var error = await errorTask;
            var elapsed = Stopwatch.GetElapsedTime(startTime);

            activity?.SetTag("process.exit_code", process.ExitCode);
            activity?.SetTag("process.duration_ms", elapsed.TotalMilliseconds);

            return new ProcessResult(process.ExitCode, output, error, elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTime);
            activity?.SetTag("process.timed_out", true);

            return ProcessResult.Failed(-1, "", $"Process timed out after {effectiveTimeout.TotalSeconds}s", elapsed, timedOut: true);
        }
        catch (Exception ex)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTime);
            activity?.SetTag("error", true);
            activity?.SetTag("error.message", ex.Message);

            return ProcessResult.Failed(-1, "", ex.Message, elapsed);
        }
    }

    /// <summary>
    /// Validate file name to prevent obvious injection attacks.
    /// </summary>
    private static void ValidateFileName([NotNull] string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name cannot be null or empty.", nameof(fileName));
        }

        // Block obvious shell metacharacters in the executable name
        var invalidChars = new[] { ';', '|', '&', '$', '`', '\n', '\r' };
        if (fileName.IndexOfAny(invalidChars) >= 0)
        {
            throw new ArgumentException($"File name contains invalid characters: {fileName}", nameof(fileName));
        }
    }

    /// <summary>
    /// Read stream with size limit to prevent memory exhaustion.
    /// </summary>
    private static async Task<string> ReadStreamWithLimitAsync(
        StreamReader reader,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var result = new StringBuilder();
        var totalRead = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0) break;

            totalRead += bytesRead * sizeof(char);
            if (totalRead > maxBytes)
            {
                result.Append(buffer, 0, bytesRead);
                result.Append("\n[OUTPUT TRUNCATED - exceeded ");
                result.Append(maxBytes / 1024 / 1024);
                result.Append("MB limit]");
                break;
            }

            result.Append(buffer, 0, bytesRead);
        }

        return result.ToString();
    }
}

/// <summary>
/// Result of a sandboxed process execution.
/// </summary>
public sealed record ProcessResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; }
    public string StandardError { get; init; }
    public TimeSpan Duration { get; init; }
    public bool TimedOut { get; init; }
    public bool Success => ExitCode == 0 && !TimedOut;

    public ProcessResult(int exitCode, string standardOutput, string standardError, TimeSpan? duration = null)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
        Duration = duration ?? TimeSpan.Zero;
        TimedOut = false;
    }

    private ProcessResult(int exitCode, string output, string error, TimeSpan duration, bool timedOut)
    {
        ExitCode = exitCode;
        StandardOutput = output;
        StandardError = error;
        Duration = duration;
        TimedOut = timedOut;
    }

    public static ProcessResult Failed(int exitCode, string output, string error, TimeSpan? duration = null, bool timedOut = false)
        => new(exitCode, output, error, duration ?? TimeSpan.Zero, timedOut);
}
