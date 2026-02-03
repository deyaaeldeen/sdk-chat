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

            Add(pathArg);
            Add(samples);
            Add(language);
            Add(json);
            Add(uncoveredOnly);

            this.SetAction(async (ctx, ct) =>
            {
                var service = new PackageInfoService();
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
    }
}
