// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ApiExtractor.Contracts;

namespace ApiExtractor.Java;

/// <summary>
/// Analyzes Java code to extract which API operations are being used.
/// Uses JavaParser (via JBang) for accurate AST-based parsing.
/// </summary>
public class JavaUsageAnalyzer : IUsageAnalyzer<ApiIndex>
{
    /// <inheritdoc />
    public string Language => "java";

    /// <inheritdoc />
    public async Task<UsageIndex> AnalyzeAsync(string codePath, ApiIndex apiIndex, CancellationToken ct = default)
    {
        var normalizedPath = Path.GetFullPath(codePath);
        
        if (!Directory.Exists(normalizedPath))
            return new UsageIndex { FileCount = 0 };

        // Check if JBang is available
        if (!IsJBangAvailable())
            return new UsageIndex { FileCount = 0 };

        // Get client classes from API - if none, no point analyzing
        if (!apiIndex.GetClientClasses().Any())
            return new UsageIndex { FileCount = 0 };

        // Write API index to temp file for the script
        var tempApiFile = Path.GetTempFileName();
        try
        {
            var apiJson = JsonSerializer.Serialize(apiIndex);
            await File.WriteAllTextAsync(tempApiFile, apiJson, ct);

            // Get script path
            var scriptDir = GetScriptDir();
            var scriptPath = Path.Combine(scriptDir, "ExtractApi.java");

            // Call JBang script in --usage mode
            var psi = new ProcessStartInfo
            {
                FileName = "jbang",
                Arguments = $"\"{scriptPath}\" --usage \"{tempApiFile}\" \"{normalizedPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = scriptDir
            };

            using var process = Process.Start(psi);
            if (process == null)
                return new UsageIndex { FileCount = 0 };

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return new UsageIndex { FileCount = 0 };

            // Parse the JSON output
            var result = JsonSerializer.Deserialize<UsageResult>(output, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });

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
            try { File.Delete(tempApiFile); } catch { }
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

    private static bool IsJBangAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "jbang",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string GetScriptDir()
    {
        return Path.GetDirectoryName(typeof(JavaUsageAnalyzer).Assembly.Location) ?? ".";
    }

    // Internal DTOs for JSON parsing
    private record UsageResult(
        int FileCount,
        List<CoveredOp>? Covered,
        List<UncoveredOp>? Uncovered,
        List<string>? Patterns
    );

    private record CoveredOp(string? Client, string? Method, string? File, int Line);
    private record UncoveredOp(string? Client, string? Method, string? Sig);
}
