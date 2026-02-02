// Agent Client Protocol - .NET SDK
// JSON source generator for compile-time serialization

using System.Text.Json.Serialization;
using AgentClientProtocol.Sdk.JsonRpc;
using AgentClientProtocol.Sdk.Schema;

namespace AgentClientProtocol.Sdk;

/// <summary>
/// Source-generated JSON serializer context for ACP protocol types.
/// Provides AOT-compatible, reflection-free JSON serialization with improved performance.
/// 
/// Usage:
///   var json = JsonSerializer.Serialize(request, AcpJsonContext.Default.JsonRpcRequest);
///   var response = JsonSerializer.Deserialize(json, AcpJsonContext.Default.InitializeResponse);
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    UseStringEnumConverter = true)]

// JSON-RPC message types
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(JsonRpcError))]

// Session types
[JsonSerializable(typeof(InitializeRequest))]
[JsonSerializable(typeof(InitializeResponse))]
[JsonSerializable(typeof(AuthenticateRequest))]
[JsonSerializable(typeof(AuthenticateResponse))]
[JsonSerializable(typeof(NewSessionRequest))]
[JsonSerializable(typeof(NewSessionResponse))]
[JsonSerializable(typeof(LoadSessionRequest))]
[JsonSerializable(typeof(LoadSessionResponse))]
[JsonSerializable(typeof(PromptRequest))]
[JsonSerializable(typeof(PromptResponse))]
[JsonSerializable(typeof(CancelNotification))]
[JsonSerializable(typeof(SessionNotification))]

// Capability types
[JsonSerializable(typeof(AgentCapabilities))]
[JsonSerializable(typeof(ClientCapabilities))]
[JsonSerializable(typeof(FileSystemCapability))]
[JsonSerializable(typeof(McpCapabilities))]
[JsonSerializable(typeof(PromptCapabilities))]
[JsonSerializable(typeof(SessionCapabilities))]
[JsonSerializable(typeof(Implementation))]

// Content types
[JsonSerializable(typeof(ContentBlock))]
[JsonSerializable(typeof(TextContent))]
[JsonSerializable(typeof(ImageContent))]
[JsonSerializable(typeof(AudioContent))]
[JsonSerializable(typeof(EmbeddedResource))]
[JsonSerializable(typeof(ResourceContents))]
[JsonSerializable(typeof(TextResourceContents))]
[JsonSerializable(typeof(BlobResourceContents))]
[JsonSerializable(typeof(Annotations))]

// Session update types
[JsonSerializable(typeof(SessionUpdate))]
[JsonSerializable(typeof(AgentMessageChunk))]
[JsonSerializable(typeof(ToolCallUpdate))]
[JsonSerializable(typeof(ToolCallStatusUpdate))]
[JsonSerializable(typeof(PlanUpdate))]
[JsonSerializable(typeof(PlanEntry))]
[JsonSerializable(typeof(CurrentModeUpdate))]
[JsonSerializable(typeof(Diff))]

// Permission types
[JsonSerializable(typeof(RequestPermissionRequest))]
[JsonSerializable(typeof(RequestPermissionResponse))]
[JsonSerializable(typeof(PermissionOption))]
[JsonSerializable(typeof(PermissionOutcome))]
[JsonSerializable(typeof(SelectedPermissionOutcome))]
[JsonSerializable(typeof(DismissedPermissionOutcome))]
[JsonSerializable(typeof(RequestInputRequest))]
[JsonSerializable(typeof(RequestInputResponse))]

// File system types
[JsonSerializable(typeof(ReadTextFileRequest))]
[JsonSerializable(typeof(ReadTextFileResponse))]
[JsonSerializable(typeof(WriteTextFileRequest))]
[JsonSerializable(typeof(WriteTextFileResponse))]

// Terminal types
[JsonSerializable(typeof(CreateTerminalRequest))]
[JsonSerializable(typeof(CreateTerminalResponse))]
[JsonSerializable(typeof(TerminalOutputRequest))]
[JsonSerializable(typeof(TerminalOutputResponse))]
[JsonSerializable(typeof(ReleaseTerminalRequest))]
[JsonSerializable(typeof(ReleaseTerminalResponse))]
[JsonSerializable(typeof(WaitForTerminalExitRequest))]
[JsonSerializable(typeof(WaitForTerminalExitResponse))]
[JsonSerializable(typeof(KillTerminalCommandRequest))]
[JsonSerializable(typeof(KillTerminalCommandResponse))]
[JsonSerializable(typeof(TerminalExitStatus))]
[JsonSerializable(typeof(Terminal))]

// MCP server configuration types
[JsonSerializable(typeof(McpServer))]
[JsonSerializable(typeof(McpServerStdio))]
[JsonSerializable(typeof(McpServerHttp))]
[JsonSerializable(typeof(McpServerSse))]
[JsonSerializable(typeof(EnvVariable))]
[JsonSerializable(typeof(HttpHeader))]

// Session mode types
[JsonSerializable(typeof(SessionModeState))]
[JsonSerializable(typeof(SessionMode))]
[JsonSerializable(typeof(AuthMethod))]

// Arrays for common collection types
[JsonSerializable(typeof(ContentBlock[]))]
[JsonSerializable(typeof(PlanEntry[]))]
[JsonSerializable(typeof(PermissionOption[]))]
[JsonSerializable(typeof(McpServer[]))]
[JsonSerializable(typeof(EnvVariable[]))]
[JsonSerializable(typeof(HttpHeader[]))]
[JsonSerializable(typeof(AuthMethod[]))]
[JsonSerializable(typeof(SessionMode[]))]
[JsonSerializable(typeof(string[]))]

public partial class AcpJsonContext : JsonSerializerContext
{
    private static System.Text.Json.JsonSerializerOptions? _flexibleOptions;
    private static readonly object _lock = new();

    /// <summary>
    /// Hardened JsonDocumentOptions for parsing untrusted JSON input.
    /// - MaxDepth=64: Prevents stack overflow DoS from deeply nested payloads
    /// - CommentHandling=Disallow: Strict JSON spec compliance
    /// - AllowTrailingCommas=false: Strict parsing, reject malformed input
    /// </summary>
    public static System.Text.Json.JsonDocumentOptions SecureDocumentOptions { get; } = new()
    {
        MaxDepth = 64,
        CommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    /// <summary>
    /// JSON options that prefer source-generated serialization for known ACP types
    /// but fall back to reflection for unknown/dynamic types (e.g., anonymous types).
    /// Use this for AOT-friendly serialization while maintaining flexibility.
    /// </summary>
    public static System.Text.Json.JsonSerializerOptions FlexibleOptions
    {
        get
        {
            if (_flexibleOptions is not null) return _flexibleOptions;

            lock (_lock)
            {
                if (_flexibleOptions is not null) return _flexibleOptions;

                // Intentionally allow reflection fallback for dynamic types (anonymous types, user-defined types)
                // This is the only place where we accept the AOT/trim tradeoff for flexibility
#pragma warning disable IL2026, IL3050 // Reflection fallback intentional for dynamic types
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    WriteIndented = false,
                    // Chain: source-generated first, then fall back to default reflection resolver
                    TypeInfoResolver = System.Text.Json.Serialization.Metadata.JsonTypeInfoResolver.Combine(Default, new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver())
                };
#pragma warning restore IL2026, IL3050
                _flexibleOptions = options;
            }

            return _flexibleOptions;
        }
    }
}
