using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Microsoft.SdkChat.Helpers;

/// <summary>
/// Parses NDJSON (newline-delimited JSON) from streaming chunks.
/// Handles multi-line JSON objects by tracking brace depth.
/// </summary>
public static class NdjsonStreamParser
{
    /// <summary>
    /// Maximum size of a single JSON object in characters (1MB).
    /// Prevents memory exhaustion from malformed AI responses or attacks.
    /// </summary>
    private const int MaxObjectSizeChars = 1024 * 1024;

    public static async IAsyncEnumerable<T> ParseAsync<T>(
        IAsyncEnumerable<string> chunks,
        JsonSerializerOptions jsonOptions,
        bool ignoreNonJsonLinesBeforeFirstObject = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var objectBuilder = new StringBuilder();
        var seenAnyItem = false;
        var braceDepth = 0;
        var inString = false;
        var escapeNext = false;

        await foreach (var chunk in chunks.WithCancellation(cancellationToken))
        {
            foreach (var ch in chunk)
            {
                if (ch == '\r')
                {
                    continue; // normalize CRLF to LF
                }

                // Track whether we're inside a JSON object
                if (braceDepth == 0 && objectBuilder.Length == 0)
                {
                    // Not currently building an object - skip non-JSON content
                    if (ch == '\n' || char.IsWhiteSpace(ch))
                    {
                        continue;
                    }
                    
                    // Skip code fence markers
                    if (ch == '`')
                    {
                        // Consume until end of line
                        continue;
                    }
                    
                    if (ch != '{')
                    {
                        // Skip non-JSON lines before first object
                        if (ignoreNonJsonLinesBeforeFirstObject && !seenAnyItem)
                        {
                            continue;
                        }
                        // After we've seen objects, non-JSON is an error
                        // But be lenient - just skip it
                        continue;
                    }
                }

                objectBuilder.Append(ch);

                // Guard against memory exhaustion from malformed responses
                if (objectBuilder.Length > MaxObjectSizeChars)
                {
                    throw new InvalidOperationException(
                        $"JSON object exceeded maximum size of {MaxObjectSizeChars / 1024}KB. " +
                        "This may indicate a malformed AI response or malicious input.");
                }

                // Track brace depth and string state for proper JSON boundary detection
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (ch == '\\' && inString)
                {
                    escapeNext = true;
                    continue;
                }

                if (ch == '"' && !escapeNext)
                {
                    inString = !inString;
                    continue;
                }

                if (!inString)
                {
                    if (ch == '{')
                    {
                        braceDepth++;
                    }
                    else if (ch == '}')
                    {
                        braceDepth--;
                        
                        if (braceDepth == 0)
                        {
                            // Complete JSON object
                            var jsonText = objectBuilder.ToString().Trim();
                            objectBuilder.Clear();
                            
                            if (TryParseObject<T>(jsonText, jsonOptions, out var item))
                            {
                                seenAnyItem = true;
                                yield return item;
                            }
                        }
                    }
                }
            }
        }

        // Handle any remaining content
        if (objectBuilder.Length > 0)
        {
            var remaining = objectBuilder.ToString().Trim();
            if (remaining.Length > 0 && remaining.StartsWith('{'))
            {
                if (TryParseObject<T>(remaining, jsonOptions, out var item))
                {
                    yield return item;
                }
            }
        }
    }

    private static bool TryParseObject<T>(string jsonText, JsonSerializerOptions jsonOptions, out T item)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            item = default!;
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<T>(jsonText, jsonOptions);
            if (parsed is not null)
            {
                item = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // Malformed JSON - skip it
        }

        item = default!;
        return false;
    }

    private static string Truncate(string value, int maxChars = 200)
    {
        if (value.Length <= maxChars)
        {
            return value;
        }

        return value[..maxChars] + "…";
    }
}
