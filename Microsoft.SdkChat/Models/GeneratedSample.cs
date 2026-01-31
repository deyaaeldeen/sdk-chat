using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Microsoft.SdkChat.Models;

/// <summary>
/// Represents a generated code sample from AI.
/// </summary>
public sealed record GeneratedSample
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    
    [JsonPropertyName("description")]
    public required string Description { get; init; }
    
    [JsonPropertyName("code")]
    public required string Code { get; init; }
    
    /// <summary>
    /// Relative file path where the sample should be written (e.g., "Assistants/Example01_GetAssistant.cs").
    /// Directories will be created automatically.
    /// </summary>
    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; }
    
    /// <summary>
    /// Validates that the sample has valid content.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Name), nameof(Code))]
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Code);
}
