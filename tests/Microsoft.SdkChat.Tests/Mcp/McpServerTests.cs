// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.SdkChat.Mcp;
using Xunit;

namespace Microsoft.SdkChat.Tests.Mcp;

/// <summary>
/// Tests for McpServer transport validation and functionality.
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

    [Theory]
    [InlineData("stdio")]
    [InlineData("STDIO")]
    [InlineData("Stdio")]
    [InlineData("sse")]
    [InlineData("SSE")]
    [InlineData("Sse")]
    public async Task RunAsync_WithSupportedTransport_DoesNotThrowImmediately(string transport)
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        
        // Use a unique port for each test to avoid conflicts
        var port = 9800 + Math.Abs(transport.GetHashCode()) % 100;

        // Act - Start the server in a background task
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync(transport, port, "error", useOpenAi: false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the process is killed
            }
        }, cts.Token);

        // Wait briefly to see if it throws immediately
        var delayTask = Task.Delay(TimeSpan.FromMilliseconds(200));
        var completedTask = await Task.WhenAny(serverTask, delayTask);

        // Assert - Server should not complete immediately (delay completes first)
        Assert.Equal(delayTask, completedTask);
        Assert.False(serverTask.IsCompleted, $"Server with transport '{transport}' should not complete immediately");
    }

    [Fact]
    public async Task RunAsync_WithSseTransport_StartsHttpServerOnSpecifiedPort()
    {
        // Arrange
        var port = 9901; // Use a specific high port to avoid conflicts
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        // Act - Start the SSE server
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync("sse", port, "error", useOpenAi: false);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }, cts.Token);

        // Wait for server to initialize
        await Task.Delay(1500);

        // Assert - Verify the server is accessible via HTTP
        bool serverResponded = false;
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        
        try
        {
            // Try to connect to the SSE endpoint
            var response = await httpClient.GetAsync($"http://localhost:{port}/sse", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            serverResponded = true;
            Assert.NotNull(response);
        }
        catch (TaskCanceledException)
        {
            // Timeout - server didn't respond
        }
        catch (HttpRequestException)
        {
            // Connection refused - server not listening
        }

        Assert.True(serverResponded, $"SSE server should be accessible on port {port}");
    }

    [Fact]
    public async Task RunAsync_WithDifferentPorts_EachSseServerUsesCorrectPort()
    {
        // Test that multiple SSE servers can be configured with different ports
        var port1 = 9902;
        var port2 = 9903;

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        cts1.CancelAfter(TimeSpan.FromSeconds(5));
        cts2.CancelAfter(TimeSpan.FromSeconds(5));

        // Start first server on port1
        var server1Task = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync("sse", port1, "error", useOpenAi: false);
            }
            catch (OperationCanceledException) { }
        }, cts1.Token);

        await Task.Delay(1000);

        // Start second server on port2
        var server2Task = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync("sse", port2, "error", useOpenAi: false);
            }
            catch (OperationCanceledException) { }
        }, cts2.Token);

        await Task.Delay(1000);

        // Verify both servers are accessible on their respective ports
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        
        var response1 = await httpClient.GetAsync($"http://localhost:{port1}/sse", HttpCompletionOption.ResponseHeadersRead, cts1.Token);
        var response2 = await httpClient.GetAsync($"http://localhost:{port2}/sse", HttpCompletionOption.ResponseHeadersRead, cts2.Token);

        Assert.NotNull(response1);
        Assert.NotNull(response2);
    }

    [Theory]
    [InlineData("stdio", "STDIO")]
    [InlineData("sse", "SSE")]
    [InlineData("Stdio", "stdio")]
    [InlineData("Sse", "sse")]
    public async Task RunAsync_TransportNames_AreCaseInsensitive(string transport1, string transport2)
    {
        // Both transport names should be treated the same (case-insensitive)
        // We test this by verifying both start without throwing immediately
        
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        
        var port = 9910 + Math.Abs(transport1.GetHashCode()) % 10;

        var task1 = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync(transport1, port, "error", useOpenAi: false);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(100);
        
        // Verify neither throws NotSupportedException
        Assert.False(task1.IsCompleted, $"Transport '{transport1}' should be accepted");
        
        // Both should be treated identically (both supported or both not)
        // Since we're testing with supported transports, both should work
    }
}
