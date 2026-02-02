// Agent Client Protocol - .NET SDK
// Agent's view of the ACP connection

using System.Text.Json;
using AgentClientProtocol.Sdk.JsonRpc;
using AgentClientProtocol.Sdk.Schema;
using AgentClientProtocol.Sdk.Stream;
using Microsoft.Extensions.Logging;

namespace AgentClientProtocol.Sdk;

/// <summary>
/// Agent-side connection to a client.
/// 
/// Provides the agent's view of an ACP connection, allowing agents to
/// communicate with clients (IDEs). Implements methods for requesting permissions,
/// accessing the file system, and sending session updates.
/// 
/// Uses compile-time generated dispatch tables for O(1) method lookup.
/// </summary>
public class AgentSideConnection : IAsyncDisposable
{
    private readonly Connection _connection;

    // O(1) method dispatch using source-generated dispatch tables
    private readonly Dictionary<string, Func<IAgent, JsonElement?, Task<object?>>> _requestHandlers;
    private readonly Dictionary<string, Func<IAgent, JsonElement?, Task>> _notificationHandlers;

    public AgentSideConnection(IAgent agent, IAcpStream stream, ILogger? logger = null)
    {
        _connection = new Connection(stream, logger);

        // Use compile-time generated dispatch tables
        _requestHandlers = IAgentDispatch.CreateRequestHandlers();
        _notificationHandlers = IAgentDispatch.CreateNotificationHandlers();

        _connection.OnRequest(async (method, @params) =>
        {
            // Params is dynamic object - type unknown at compile time
#pragma warning disable IL2026, IL3050 // Dynamic object serialization unavoidable
            var json = @params is JsonElement el ? el : JsonSerializer.SerializeToElement(@params, AcpJsonContext.FlexibleOptions);
#pragma warning restore IL2026, IL3050

            if (_requestHandlers.TryGetValue(method, out var handler))
                return await handler(agent, json);

            throw RequestError.MethodNotFound(method);
        });

        _connection.OnNotification(async (method, @params) =>
        {
            // Params is dynamic object - type unknown at compile time
#pragma warning disable IL2026, IL3050 // Dynamic object serialization unavoidable
            var json = @params is JsonElement el ? el : JsonSerializer.SerializeToElement(@params, AcpJsonContext.FlexibleOptions);
#pragma warning restore IL2026, IL3050

            if (_notificationHandlers.TryGetValue(method, out var handler))
                await handler(agent, json);
        });
    }

    /// <summary>
    /// Start processing messages.
    /// </summary>
    public Task RunAsync(CancellationToken ct = default) => _connection.RunAsync(ct);

    /// <summary>
    /// Send session update to client.
    /// </summary>
    public Task SessionUpdateAsync(SessionNotification notification, CancellationToken ct = default) =>
        _connection.SendNotificationAsync(ClientMethods.SessionUpdate, notification, ct);

    /// <summary>
    /// Send text chunk to client.
    /// </summary>
    public Task SendTextAsync(string sessionId, string text, CancellationToken ct = default) =>
        SessionUpdateAsync(new SessionNotification
        {
            SessionId = sessionId,
            Update = new AgentMessageChunk { Content = new TextContent { Text = text } }
        }, ct);

    /// <summary>
    /// Send plan update to client.
    /// </summary>
    public Task SendPlanAsync(string sessionId, PlanEntry[] entries, CancellationToken ct = default) =>
        SessionUpdateAsync(new SessionNotification
        {
            SessionId = sessionId,
            Update = new PlanUpdate { Entries = entries }
        }, ct);

    /// <summary>
    /// Send tool call update to client.
    /// </summary>
    public Task SendToolCallAsync(string sessionId, string id, string name, string status,
        object? arguments = null, ContentBlock[]? content = null, CancellationToken ct = default)
    {
        // Arguments is dynamic object - type unknown at compile time
#pragma warning disable IL2026, IL3050 // Dynamic object serialization unavoidable
        var serializedArgs = arguments != null ? JsonSerializer.SerializeToElement(arguments, AcpJsonContext.FlexibleOptions) : (JsonElement?)null;
#pragma warning restore IL2026, IL3050

        return SessionUpdateAsync(new SessionNotification
        {
            SessionId = sessionId,
            Update = new ToolCallUpdate
            {
                Id = id,
                Name = name,
                Status = status,
                Arguments = serializedArgs,
                Content = content
            }
        }, ct);
    }

    /// <summary>
    /// Request permission from user.
    /// </summary>
    public Task<RequestPermissionResponse> RequestPermissionAsync(
        RequestPermissionRequest request, CancellationToken ct = default) =>
        _connection.SendRequestAsync<RequestPermissionResponse>(ClientMethods.SessionRequestPermission, request, ct)!;

    /// <summary>
    /// Request text input from user.
    /// </summary>
    public Task<RequestInputResponse> RequestInputAsync(
        string sessionId,
        string requestId,
        string prompt,
        string? defaultValue = null,
        CancellationToken ct = default) =>
        _connection.SendRequestAsync<RequestInputResponse>(ClientMethods.SessionRequestInput, new RequestInputRequest
        {
            SessionId = sessionId,
            RequestId = requestId,
            Prompt = prompt,
            DefaultValue = defaultValue
        }, ct)!;

    /// <summary>
    /// Read a text file from client.
    /// </summary>
    public Task<ReadTextFileResponse> ReadTextFileAsync(ReadTextFileRequest request, CancellationToken ct = default) =>
        _connection.SendRequestAsync<ReadTextFileResponse>(ClientMethods.FsReadTextFile, request, ct)!;

    /// <summary>
    /// Write a text file to client.
    /// </summary>
    public Task<WriteTextFileResponse> WriteTextFileAsync(WriteTextFileRequest request, CancellationToken ct = default) =>
        _connection.SendRequestAsync<WriteTextFileResponse>(ClientMethods.FsWriteTextFile, request, ct)!;

    /// <summary>
    /// Create a terminal on client.
    /// </summary>
    public Task<CreateTerminalResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct = default) =>
        _connection.SendRequestAsync<CreateTerminalResponse>(ClientMethods.TerminalCreate, request, ct)!;

    /// <summary>
    /// Get terminal output from client.
    /// </summary>
    public Task<TerminalOutputResponse> GetTerminalOutputAsync(TerminalOutputRequest request, CancellationToken ct = default) =>
        _connection.SendRequestAsync<TerminalOutputResponse>(ClientMethods.TerminalOutput, request, ct)!;

    /// <summary>
    /// Release terminal on client.
    /// </summary>
    public Task<ReleaseTerminalResponse> ReleaseTerminalAsync(ReleaseTerminalRequest request, CancellationToken ct = default) =>
        _connection.SendRequestAsync<ReleaseTerminalResponse>(ClientMethods.TerminalRelease, request, ct)!;

    /// <summary>
    /// Wait for terminal to exit.
    /// </summary>
    public Task<WaitForTerminalExitResponse> WaitForTerminalExitAsync(WaitForTerminalExitRequest request, CancellationToken ct = default) =>
        _connection.SendRequestAsync<WaitForTerminalExitResponse>(ClientMethods.TerminalWaitForExit, request, ct)!;

    /// <summary>
    /// Kill terminal command.
    /// </summary>
    public Task<KillTerminalCommandResponse> KillTerminalAsync(KillTerminalCommandRequest request, CancellationToken ct = default) =>
        _connection.SendRequestAsync<KillTerminalCommandResponse>(ClientMethods.TerminalKill, request, ct)!;

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
