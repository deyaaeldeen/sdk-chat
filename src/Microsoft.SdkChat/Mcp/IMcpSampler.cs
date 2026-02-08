// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ModelContextProtocol.Protocol;

namespace Microsoft.SdkChat.Mcp;

/// <summary>
/// Interface for MCP sampling operations. Allows for testing.
/// </summary>
public interface IMcpSampler
{
    /// <summary>
    /// Requests the LLM to sample/generate a response via MCP protocol.
    /// </summary>
    ValueTask<CreateMessageResult> SampleAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Wrapper for ModelContextProtocol.Server.McpServer that implements IMcpSampler.
/// </summary>
public class McpServerSampler(ModelContextProtocol.Server.McpServer mcpServer) : IMcpSampler
{
    private readonly ModelContextProtocol.Server.McpServer _mcpServer = mcpServer;

    public ValueTask<CreateMessageResult> SampleAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
    {
        return _mcpServer.SampleAsync(request, cancellationToken);
    }
}
