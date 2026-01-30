namespace Sdk.Tools.Chat.Models;

/// <summary>
/// Represents a source file loaded for context.
/// </summary>
/// <param name="FilePath">Absolute or relative path to the source file.</param>
/// <param name="Content">The file content (may be truncated).</param>
/// <param name="Priority">Loading priority (lower = higher priority, loaded first).</param>
public record SourceInput(
    string FilePath,
    string Content,
    int Priority = 10
);
