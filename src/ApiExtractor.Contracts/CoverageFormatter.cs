// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace ApiExtractor.Contracts;

/// <summary>
/// Shared scaffolding for coverage-aware formatting.
/// All five language formatters emit identical covered/uncovered summaries,
/// differing only in the comment prefix (<c>#</c> for Python, <c>//</c> for the rest).
/// This helper eliminates that duplication.
/// </summary>
public static class CoverageFormatter
{
    /// <summary>
    /// Appends the "ALREADY COVERED" summary section and builds the uncovered-by-client dictionary.
    /// Returns <c>null</c> if all operations are covered (and appends the "all covered" message).
    /// Deprecated operations are excluded from uncovered counts and listed separately.
    /// </summary>
    /// <param name="sb">The <see cref="StringBuilder"/> to append to.</param>
    /// <param name="coverage">The usage coverage index.</param>
    /// <param name="commentPrefix">Language comment prefix, e.g. <c>//</c> or <c>#</c>.</param>
    /// <returns>
    /// A dictionary mapping client type names to their uncovered operation names,
    /// or <c>null</c> if there are no uncovered operations.
    /// </returns>
    public static Dictionary<string, HashSet<string>>? AppendCoverageSummary(
        StringBuilder sb,
        UsageIndex coverage,
        string commentPrefix = "//")
    {
        // Section 1: Compact summary of what's already covered
        var coveredByClient = coverage.CoveredOperations
            .GroupBy(op => op.ClientType)
            .ToDictionary(g => g.Key, g => g.Select(op => op.Operation).Distinct().ToList());

        if (coveredByClient.Count > 0)
        {
            var totalCovered = coverage.CoveredOperations.Count;
            sb.AppendLine($"{commentPrefix} ALREADY COVERED ({totalCovered} calls across {coverage.FileCount} files) - DO NOT DUPLICATE:");
            foreach (var (client, ops) in coveredByClient.OrderBy(kv => kv.Key))
            {
                sb.AppendLine($"{commentPrefix}   {client}: {string.Join(", ", ops.Take(10))}{(ops.Count > 10 ? $" (+{ops.Count - 10} more)" : "")}");
            }
            sb.AppendLine();
        }

        // Separate deprecated operations from non-deprecated uncovered operations
        var deprecatedOps = coverage.UncoveredOperations.Where(op => op.IsDeprecated == true).ToList();
        var nonDeprecatedUncovered = coverage.UncoveredOperations.Where(op => op.IsDeprecated != true).ToList();

        // Section 2: Deprecated APIs (informational, should NOT be covered)
        if (deprecatedOps.Count > 0)
        {
            var deprecatedByClient = deprecatedOps
                .GroupBy(op => op.ClientType)
                .ToDictionary(g => g.Key, g => g.Select(op => op.Operation).ToList());

            sb.AppendLine($"{commentPrefix} DEPRECATED API ({deprecatedOps.Count} operations) - Do NOT generate samples for these:");
            foreach (var (client, ops) in deprecatedByClient.OrderBy(kv => kv.Key))
            {
                sb.AppendLine($"{commentPrefix}   {client}: {string.Join(", ", ops)}");
            }
            sb.AppendLine();
        }

        // Section 3: Build uncovered-by-client dictionary (excluding deprecated)
        var uncoveredByClient = nonDeprecatedUncovered
            .GroupBy(op => op.ClientType)
            .ToDictionary(g => g.Key, g => g.Select(op => op.Operation).ToHashSet());

        if (uncoveredByClient.Count == 0)
        {
            sb.AppendLine($"{commentPrefix} All operations are covered in existing samples.");
            return null;
        }

        sb.AppendLine($"{commentPrefix} UNCOVERED API ({nonDeprecatedUncovered.Count} operations) - Generate samples for these:");
        sb.AppendLine();

        return uncoveredByClient;
    }
}
