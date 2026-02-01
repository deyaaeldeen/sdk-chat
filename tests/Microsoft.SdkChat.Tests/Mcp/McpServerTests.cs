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
    [Theory]
    [InlineData("http")]
    [InlineData("websocket")]
    [InlineData("grpc")]
    [InlineData("")]
    [InlineData("invalid")]
    public async Task RunAsync_WithUnsupportedTransport_ThrowsNotSupportedException(string transport)
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            McpServer.RunAsync(transport, 8080, "info", useOpenAi: false));
        
        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
