// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.SdkChat.Mcp;
using Xunit;
using System.Net.Sockets;

namespace Microsoft.SdkChat.Tests.Mcp;

/// <summary>
/// Tests for McpServer transport validation and functionality.
/// </summary>
public class McpServerTests
{
    /// <summary>
    /// Gets an available ephemeral port to avoid port conflicts in tests.
    /// </summary>
    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

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
        
        // Use an ephemeral port to avoid conflicts
        var port = GetAvailablePort();

        // Act - Start the server in a background task with cancellation token
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync(transport, port, "error", useOpenAi: false, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when the cancellation token is triggered
            }
        });

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
        var port = GetAvailablePort();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        // Act - Start the SSE server with cancellation token
        _ = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync("sse", port, "error", useOpenAi: false, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: server shutdown via cancellation token
            }
        });

        // Wait for server to initialize
        await Task.Delay(1500);

        // Assert - Verify the server is accessible via HTTP and responds with success
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        
        var response = await httpClient.GetAsync($"http://localhost:{port}/sse", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        
        Assert.NotNull(response);
        Assert.True(response.IsSuccessStatusCode, $"SSE endpoint should return success status code, but got {response.StatusCode}");
    }

    [Fact]
    public async Task RunAsync_WithDifferentPorts_EachSseServerUsesCorrectPort()
    {
        // Test that multiple SSE servers can be configured with different ports
        var port1 = GetAvailablePort();
        var port2 = GetAvailablePort();

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        cts1.CancelAfter(TimeSpan.FromSeconds(5));
        cts2.CancelAfter(TimeSpan.FromSeconds(5));

        // Start first server on port1
        _ = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync("sse", port1, "error", useOpenAi: false, cancellationToken: cts1.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: server shutdown via cancellation token
            }
        });

        await Task.Delay(1000);

        // Start second server on port2
        _ = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync("sse", port2, "error", useOpenAi: false, cancellationToken: cts2.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: server shutdown via cancellation token
            }
        });

        await Task.Delay(1000);

        // Verify both servers are accessible on their respective ports with success status codes
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        
        var response1 = await httpClient.GetAsync($"http://localhost:{port1}/sse", HttpCompletionOption.ResponseHeadersRead, cts1.Token);
        var response2 = await httpClient.GetAsync($"http://localhost:{port2}/sse", HttpCompletionOption.ResponseHeadersRead, cts2.Token);

        Assert.NotNull(response1);
        Assert.True(response1.IsSuccessStatusCode, $"Server 1 should return success status code, but got {response1.StatusCode}");
        Assert.NotNull(response2);
        Assert.True(response2.IsSuccessStatusCode, $"Server 2 should return success status code, but got {response2.StatusCode}");
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("sse")]
    public async Task RunAsync_TransportNames_AreCaseInsensitive(string transport)
    {
        // Test that transport names are case-insensitive by verifying both lower and upper case work
        
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        
        var port = GetAvailablePort();

        // Test lowercase
        var taskLower = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync(transport.ToLower(), port, "error", useOpenAi: false, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: server shutdown via cancellation token
            }
        });

        await Task.Delay(100);
        
        // Assert - Verify lowercase transport name is accepted
        Assert.False(taskLower.IsCompleted, $"Transport '{transport.ToLower()}' should be accepted");
        
        // Cancel and wait
        cts.Cancel();
        await Task.WhenAny(taskLower, Task.Delay(1000));
        
        // Test uppercase with new cancellation token and port
        using var cts2 = new CancellationTokenSource();
        cts2.CancelAfter(TimeSpan.FromMilliseconds(300));
        var port2 = GetAvailablePort();
        
        var taskUpper = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync(transport.ToUpper(), port2, "error", useOpenAi: false, cancellationToken: cts2.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: server shutdown via cancellation token
            }
        });

        await Task.Delay(100);
        
        // Assert - Verify uppercase transport name is accepted
        Assert.False(taskUpper.IsCompleted, $"Transport '{transport.ToUpper()}' should be accepted");
    }
}
