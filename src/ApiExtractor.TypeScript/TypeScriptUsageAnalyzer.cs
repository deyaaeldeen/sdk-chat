// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.TypeScript;

/// <summary>
/// Analyzes TypeScript/JavaScript code to extract which API operations are being used.
/// Uses ts-morph for accurate AST-based parsing via extract_api.js --usage mode.
/// </summary>
public class TypeScriptUsageAnalyzer : IUsageAnalyzer<ApiIndex>
{
    /// <inheritdoc />
    public string Language => "typescript";

    /// <inheritdoc />
    public async Task<UsageIndex> AnalyzeAsync(string codePath, ApiIndex apiIndex, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(codePath);

        if (!Directory.Exists(normalizedPath))
            return new UsageIndex { FileCount = 0 };

        // Check if Node.js is available
        if (!IsNodeAvailable())
            return new UsageIndex { FileCount = 0 };

        // Get client classes from API - if none, no point analyzing
        if (!apiIndex.GetClientClasses().Any())
            return new UsageIndex { FileCount = 0 };

        // Write API index to temp file for the script
        var tempApiFile = Path.GetTempFileName();
        try
        {
            var apiJson = JsonSerializer.Serialize(apiIndex, SourceGenerationContext.Default.ApiIndex);
            await File.WriteAllTextAsync(tempApiFile, apiJson, ct);

            // Get script path
            var scriptDir = GetScriptDir();
            var scriptPath = Path.Combine(scriptDir, "dist", "extract_api.js");
            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(scriptDir, "extract_api.mjs");
            }

            // Call Node script in --usage mode
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = scriptDir
            };
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("--usage");
            psi.ArgumentList.Add(tempApiFile);
            psi.ArgumentList.Add(normalizedPath);

            using var process = Process.Start(psi);
            if (process == null)
                return new UsageIndex { FileCount = 0 };

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return new UsageIndex { FileCount = 0 };

            // Parse the JSON output
            var result = DeserializeResult(output);

            if (result == null)
                return new UsageIndex { FileCount = 0 };

            return new UsageIndex
            {
                FileCount = result.FileCount,
                CoveredOperations = result.Covered?.Select(c => new OperationUsage
                {
                    ClientType = c.Client ?? "",
                    Operation = c.Method ?? "",
                    File = c.File ?? "",
                    Line = c.Line
                }).ToList() ?? [],
                UncoveredOperations = result.Uncovered?.Select(u => new UncoveredOperation
                {
                    ClientType = u.Client ?? "",
                    Operation = u.Method ?? "",
                    Signature = u.Sig ?? $"{u.Method}(...)"
                }).ToList() ?? []
            };
        }
        finally
        {
            // Best-effort cleanup: temp file deletion failure is non-critical
            // (OS will clean up temp files, and we don't want to mask the real result)
            try { File.Delete(tempApiFile); } catch { /* Intentionally ignored - temp file cleanup */ }
        }
    }

    /// <inheritdoc />
    public string Format(UsageIndex index)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Analyzed {index.FileCount} files.");
        sb.AppendLine();

        if (index.CoveredOperations.Count > 0)
        {
            sb.AppendLine("COVERED OPERATIONS (already have examples):");
            foreach (var op in index.CoveredOperations.OrderBy(o => o.ClientType).ThenBy(o => o.Operation))
            {
                sb.AppendLine($"  - {op.ClientType}.{op.Operation} ({op.File}:{op.Line})");
            }
            sb.AppendLine();
        }

        if (index.UncoveredOperations.Count > 0)
        {
            sb.AppendLine("UNCOVERED OPERATIONS (need examples):");
            foreach (var op in index.UncoveredOperations.OrderBy(o => o.ClientType).ThenBy(o => o.Operation))
            {
                sb.AppendLine($"  - {op.ClientType}.{op.Operation}: {op.Signature}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static bool IsNodeAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string GetScriptDir()
    {
        return AppContext.BaseDirectory;
    }

    // Internal DTOs for JSON parsing - suppressions are safe as these are internal
    // utilities for parsing known JSON from our own scripts
#pragma warning disable IL2026, IL3050 // Suppressed: internal DTOs with known schema
    private static UsageResult? DeserializeResult(string json) =>
        JsonSerializer.Deserialize<UsageResult>(json, JsonOptionsCache.CaseInsensitive);
#pragma warning restore IL2026, IL3050

    private record UsageResult(
        int FileCount,
        List<CoveredOp>? Covered,
        List<UncoveredOp>? Uncovered,
        List<string>? Patterns
    );

    private record CoveredOp(string? Client, string? Method, string? File, int Line);
    private record UncoveredOp(string? Client, string? Method, string? Sig);
}
