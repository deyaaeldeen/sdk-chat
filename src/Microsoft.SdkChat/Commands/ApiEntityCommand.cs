// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.CommandLine;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Services;

namespace Microsoft.SdkChat.Commands;

/// <summary>
/// API entity commands: extract and coverage.
/// </summary>
public class ApiEntityCommand : Command
{
    public ApiEntityCommand() : base("api", "Extract public API surface and analyze sample coverage")
    {
        Add(new ExtractCommand());
        Add(new CoverageCommand());
    }

    /// <summary>Extract public API surface.</summary>
    private class ExtractCommand : Command
    {
        public ExtractCommand() : base("extract", "Extract all public types, methods, and properties from SDK source")
        {
            var pathArg = new Argument<string>("path") { Description = "Path to SDK root directory" };
            var language = new Option<string?>("--language") { Description = "Override language detection" };
            var json = new Option<bool>("--json") { Description = "Output structured JSON (default: human-readable code stubs)" };
            var output = new Option<string?>("--output", "-o") { Description = "Write to file instead of stdout" };

            Add(pathArg);
            Add(language);
            Add(json);
            Add(output);

            this.SetAction(async (ctx, ct) =>
            {
                var service = new PackageInfoService();
                var result = await service.ExtractPublicApiAsync(
                    ctx.GetValue(pathArg)!,
                    ctx.GetValue(language),
                    ctx.GetValue(json),
                    ct);

                if (!result.Success)
                {
                    ConsoleUx.Error(result.ErrorMessage ?? "API extraction failed");
                    Environment.ExitCode = 1;
                    return;
                }

                var outputPath = ctx.GetValue(output);
                if (!string.IsNullOrEmpty(outputPath))
                {
                    await File.WriteAllTextAsync(outputPath, result.ApiSurface, ct);
                    ConsoleUx.Success($"Wrote API surface to {outputPath}");
                }
                else
                {
                    Console.WriteLine(result.ApiSurface);
                }

                if (result.Warnings?.Length > 0)
                {
                    foreach (var warning in result.Warnings)
                    {
                        ConsoleUx.Info($"Warning: {warning}");
                    }
                }

                Environment.ExitCode = 0;
            });
        }
    }

    /// <summary>Analyze API coverage in samples.</summary>
    private class CoverageCommand : Command
    {
        public CoverageCommand() : base("coverage", "Find which API operations are missing from samples (coverage gaps)")
        {
            var pathArg = new Argument<string>("path") { Description = "Path to SDK root directory" };
            var samples = new Option<string?>("--samples") { Description = "Custom samples folder path (default: auto-detected)" };
            var language = new Option<string?>("--language") { Description = "Override language detection" };
            var json = new Option<bool>("--json") { Description = "Output as JSON for CI/automation" };
            var uncoveredOnly = new Option<bool>("--uncovered-only") { Description = "Only show operations that need samples" };
            var monorepo = new Option<bool>("--monorepo") { Description = "Analyze all SDK packages in a monorepo (batch coverage)" };
            var report = new Option<string?>("--report") { Description = "Write a Markdown report to file (monorepo only)" };
            var quiet = new Option<bool>("--quiet") { Description = "Suppress progress output" };
            var skipEmpty = new Option<bool>("--skip-empty") { Description = "Omit packages with 0 operations from report (monorepo only)" };

            Add(pathArg);
            Add(samples);
            Add(language);
            Add(json);
            Add(uncoveredOnly);
            Add(monorepo);
            Add(report);
            Add(quiet);
            Add(skipEmpty);

            this.SetAction(async (ctx, ct) =>
            {
                var service = new PackageInfoService();
                var isMonorepo = ctx.GetValue(monorepo);
                var reportPath = ctx.GetValue(report);

                if (isMonorepo)
                {
                    var progress = ctx.GetValue(quiet)
                        ? null
                        : new Progress<string>(message => Console.Error.WriteLine(message));

                    var batch = await service.AnalyzeCoverageMonorepoAsync(
                        ctx.GetValue(pathArg)!,
                        ctx.GetValue(samples),
                        ctx.GetValue(language),
                        progress,
                        ct);

                    if (!batch.Success)
                    {
                        ConsoleUx.Error(batch.ErrorMessage ?? "Coverage analysis failed");
                        Environment.ExitCode = 1;
                        return;
                    }

                    if (ctx.GetValue(json))
                    {
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(batch, PackageInfoJsonContext.Default.CoverageBatchResult));
                    }
                    else
                    {
                        var markdown = RenderCoverageMarkdown(batch, ctx.GetValue(uncoveredOnly), ctx.GetValue(skipEmpty));
                        if (!string.IsNullOrWhiteSpace(reportPath))
                        {
                            var directory = Path.GetDirectoryName(reportPath);
                            if (!string.IsNullOrWhiteSpace(directory))
                            {
                                Directory.CreateDirectory(directory);
                            }
                            await File.WriteAllTextAsync(reportPath, markdown, ct);
                            ConsoleUx.Success($"Wrote coverage report to {reportPath}");
                        }
                        else
                        {
                            Console.WriteLine(markdown);
                        }
                    }

                    Environment.ExitCode = 0;
                    return;
                }

                var result = await service.AnalyzeCoverageAsync(
                    ctx.GetValue(pathArg)!,
                    ctx.GetValue(samples),
                    ctx.GetValue(language),
                    ct);

                if (!result.Success)
                {
                    ConsoleUx.Error(result.ErrorMessage ?? "Coverage analysis failed");
                    Environment.ExitCode = 1;
                    return;
                }

                if (ctx.GetValue(json))
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, PackageInfoJsonContext.Default.CoverageAnalysisResult));
                }
                else
                {
                    Console.WriteLine();
                    ConsoleUx.Header($"API Coverage: {result.CoveragePercent}%");
                    ConsoleUx.Info($"Source: {result.SourceFolder}");
                    ConsoleUx.Info($"Samples: {result.SamplesFolder}");
                    ConsoleUx.Info($"Total operations: {result.TotalOperations}");
                    ConsoleUx.Success($"Covered: {result.CoveredCount}");

                    if (result.UncoveredCount > 0)
                    {
                        ConsoleUx.Error($"Uncovered: {result.UncoveredCount}");
                        Console.WriteLine();
                        ConsoleUx.Header("Uncovered Operations:");

                        foreach (var op in result.UncoveredOperations ?? [])
                        {
                            Console.WriteLine($"  {ConsoleUx.Dim(op.ClientType)}.{ConsoleUx.Yellow(op.Operation)}");
                            Console.WriteLine($"    {ConsoleUx.Dim(op.Signature)}");
                        }
                    }

                    if (!ctx.GetValue(uncoveredOnly) && result.CoveredOperations?.Length > 0)
                    {
                        Console.WriteLine();
                        ConsoleUx.Header("Covered Operations:");
                        foreach (var op in result.CoveredOperations)
                        {
                            Console.WriteLine($"  {ConsoleUx.Dim(op.ClientType)}.{ConsoleUx.Green(op.Operation)} → {op.File}:{op.Line}");
                        }
                    }

                    Console.WriteLine();
                }

                Environment.ExitCode = 0;
            });
        }

        private static string RenderCoverageMarkdown(CoverageBatchResult batch, bool uncoveredOnly, bool skipEmpty)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("# SDK Coverage Report");
            sb.AppendLine();
            sb.AppendLine($"Generated: {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}");
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine($"- Root: {batch.RootPath}");
            sb.AppendLine($"- Packages: {batch.TotalPackages}");
            sb.AppendLine($"- Analyzed: {batch.AnalyzedPackages}");
            sb.AppendLine($"- Skipped (no samples): {batch.SkippedPackages}");
            sb.AppendLine($"- Failed: {batch.FailedPackages}");
            sb.AppendLine($"- Total operations: {batch.TotalOperations}");
            sb.AppendLine($"- Covered: {batch.CoveredCount}");
            sb.AppendLine($"- Uncovered: {batch.UncoveredCount}");
            sb.AppendLine($"- Coverage: {batch.CoveragePercent:F2}%");
            sb.AppendLine();

            foreach (var item in batch.Packages ?? [])
            {
                // Skip empty packages if requested
                if (skipEmpty && item.Success && item.Result?.TotalOperations == 0)
                {
                    continue;
                }

                sb.AppendLine($"## {item.RelativePath}");
                sb.AppendLine();

                if (item.SkippedNoSamples)
                {
                    sb.AppendLine("Skipped (no samples detected).");
                    sb.AppendLine();
                    continue;
                }

                if (!item.Success || item.Result == null)
                {
                    sb.AppendLine("Coverage analysis failed.");
                    if (!string.IsNullOrWhiteSpace(item.ErrorCode) || !string.IsNullOrWhiteSpace(item.ErrorMessage))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"{item.ErrorCode}: {item.ErrorMessage}");
                    }
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"Coverage: {item.Result.CoveredCount}/{item.Result.TotalOperations} ({item.Result.CoveragePercent:F2}%)");
                sb.AppendLine();
                if (!uncoveredOnly)
                {
                    sb.AppendLine("### Covered APIs");
                    sb.AppendLine();
                    if (item.Result.CoveredOperations?.Length > 0)
                    {
                        foreach (var op in item.Result.CoveredOperations)
                        {
                            sb.AppendLine($"- {op.ClientType}.{op.Operation} ({op.File}:{op.Line})");
                        }
                    }
                    sb.AppendLine();
                }
                sb.AppendLine("### Uncovered APIs");
                sb.AppendLine();
                if (item.Result.UncoveredOperations?.Length > 0)
                {
                    foreach (var op in item.Result.UncoveredOperations)
                    {
                        sb.AppendLine($"- {op.ClientType}.{op.Operation}: {op.Signature}");
                    }
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
