// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Buffers;

namespace Sdk.Tools.Chat.Helpers;

/// <summary>
/// Utility for sanitizing file names and paths for cross-platform safety.
/// Uses Span-based operations for allocation-efficient string manipulation.
/// </summary>
public static class PathSanitizer
{
    // Pre-computed lookup table for invalid filename characters
    private static readonly SearchValues<char> InvalidFileNameChars = 
        SearchValues.Create(Path.GetInvalidFileNameChars());
    
    // Additional characters to sanitize for cross-platform compatibility
    private static readonly SearchValues<char> AdditionalInvalidChars = 
        SearchValues.Create([':', ' ']);
    
    /// <summary>
    /// Sanitizes a file name by replacing invalid characters with underscores.
    /// Uses allocation-efficient Span operations.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "Sample";
        
        // Fast path: if no invalid characters, return original
        if (!ContainsInvalidChar(name.AsSpan()))
            return name;
        
        // Slow path: replace invalid characters
        return string.Create(name.Length, name, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                span[i] = IsInvalidFileNameChar(c) ? '_' : c;
            }
        });
    }
    
    /// <summary>
    /// Sanitizes a relative file path, ensuring each path segment is safe.
    /// </summary>
    public static string SanitizeFilePath(string? path, string expectedExtension)
    {
        if (string.IsNullOrEmpty(path)) return "Sample" + expectedExtension;
        
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        if (parts.Length == 0) return "Sample" + expectedExtension;
        
        var sanitizedParts = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            sanitizedParts[i] = SanitizeFileName(parts[i]);
        }
        
        var result = string.Join(Path.DirectorySeparatorChar, sanitizedParts);
        
        // Ensure correct extension
        if (!result.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            var lastPart = sanitizedParts[^1];
            var dotIndex = lastPart.LastIndexOf('.');
            if (dotIndex > 0)
            {
                sanitizedParts[^1] = lastPart[..dotIndex] + expectedExtension;
                result = string.Join(Path.DirectorySeparatorChar, sanitizedParts);
            }
            else
            {
                result += expectedExtension;
            }
        }
        
        return result;
    }
    
    /// <summary>
    /// Checks if a span contains any invalid filename characters.
    /// </summary>
    private static bool ContainsInvalidChar(ReadOnlySpan<char> span)
    {
        return span.ContainsAny(InvalidFileNameChars) || span.ContainsAny(AdditionalInvalidChars);
    }
    
    /// <summary>
    /// Checks if a character is invalid for filenames.
    /// </summary>
    private static bool IsInvalidFileNameChar(char c)
    {
        return InvalidFileNameChars.Contains(c) || c == ':' || c == ' ';
    }
}
