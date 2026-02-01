// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.SdkChat.Mcp;
using Xunit;

namespace Microsoft.SdkChat.Tests.Mcp;

/// <summary>
/// Tests for McpServer transport validation.
/// </summary>
public class McpServerTests
{
    [Fact]
    public async Task RunAsync_WithUnsupportedTransport_ThrowsNotSupportedException()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            McpServer.RunAsync("sse", 8080, "info", useOpenAi: false));
        
        Assert.Contains("sse", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stdio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SSE")]
    [InlineData("Sse")]
    [InlineData("http")]
    [InlineData("websocket")]
    [InlineData("grpc")]
    [InlineData("")]
    public async Task RunAsync_WithVariousUnsupportedTransports_ThrowsNotSupportedException(string transport)
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            McpServer.RunAsync(transport, 8080, "info", useOpenAi: false));
        
        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
