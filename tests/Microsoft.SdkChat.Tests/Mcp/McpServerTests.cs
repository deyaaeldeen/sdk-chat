// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Sockets;
using Microsoft.SdkChat.Mcp;
using Xunit;

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
    [InlineData("sse")]
    [InlineData("SSE")]
    [InlineData("Sse")]
    public async Task RunAsync_WithSseTransport_DoesNotExitImmediately(string transport)
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Use an ephemeral port to avoid conflicts
        var port = GetAvailablePort();

        // Act - Start the SSE server in a background task with cancellation token
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
        Assert.False(serverTask.IsCompleted, $"SSE server with transport '{transport}' should not complete immediately");

        // Ensure cleanup - wait for server to shut down
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Server didn't shut down cleanly, but test already passed
        }
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("STDIO")]
    [InlineData("Stdio")]
    public async Task RunAsync_WithStdioTransport_StartsWithoutThrowing(string transport)
    {
        // Note: stdio transport will exit immediately when stdin is unavailable (test environment)
        // This is expected behavior - we just verify it doesn't throw an exception

        // Arrange
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var port = GetAvailablePort(); // Port not used for stdio, but required by API

        // Act - Start the stdio server
        Exception? caughtException = null;
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
            catch (Exception ex)
            {
                caughtException = ex;
            }
        });

        // Wait for task to complete or timeout
        await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

        // Assert - Should not throw any exception (other than cancellation)
        Assert.Null(caughtException);
    }

    [Fact]
    public async Task RunAsync_WithSseTransport_StartsHttpServerOnSpecifiedPort()
    {
        // Arrange
        var port = GetAvailablePort();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        // Act - Start the SSE server with cancellation token
        var serverTask = Task.Run(async () =>
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

        // Wait for server to initialize with retry logic
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        HttpResponseMessage? response = null;
        var attempts = 0;
        var maxAttempts = 10;

        while (attempts < maxAttempts && !cts.Token.IsCancellationRequested)
        {
            attempts++;
            try
            {
                await Task.Delay(200 * attempts, cts.Token); // Exponential backoff
                response = await httpClient.GetAsync($"http://localhost:{port}/sse", HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    break; // Server is ready
                }
            }
            catch (HttpRequestException)
            {
                // Server not ready yet, retry
                if (attempts == maxAttempts)
                {
                    throw;
                }
            }
            catch (TaskCanceledException)
            {
                // Timeout or cancellation
                throw;
            }
        }

        // Assert - Verify the server is accessible via HTTP and responds with success
        Assert.NotNull(response);
        Assert.True(response.IsSuccessStatusCode, $"SSE endpoint should return success status code, but got {response.StatusCode}");

        // Ensure cleanup - wait for server to shut down
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            // Server didn't shut down cleanly, but test already passed
        }
    }

    [Fact]
    public async Task RunAsync_WithDifferentPorts_EachSseServerUsesCorrectPort()
    {
        // Test that multiple SSE servers can be configured with different ports
        var port1 = GetAvailablePort();
        var port2 = GetAvailablePort();

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        cts1.CancelAfter(TimeSpan.FromSeconds(10));
        cts2.CancelAfter(TimeSpan.FromSeconds(10));

        // Start first server on port1
        var server1Task = Task.Run(async () =>
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

        // Start second server on port2 immediately (no need to wait)
        var server2Task = Task.Run(async () =>
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

        // Wait for both servers to initialize with retry logic
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        // Helper function to wait for server
        async Task<HttpResponseMessage> WaitForServerAsync(int port, CancellationToken cancellationToken)
        {
            var attempts = 0;
            var maxAttempts = 10;

            while (attempts < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                attempts++;
                try
                {
                    await Task.Delay(200 * attempts, cancellationToken);
                    var response = await httpClient.GetAsync($"http://localhost:{port}/sse", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }
                }
                catch (HttpRequestException)
                {
                    // Server not ready yet, retry
                    if (attempts == maxAttempts)
                    {
                        throw;
                    }
                }
            }

            throw new TimeoutException($"Server on port {port} did not become ready in time");
        }

        var response1 = await WaitForServerAsync(port1, cts1.Token);
        var response2 = await WaitForServerAsync(port2, cts2.Token);

        Assert.NotNull(response1);
        Assert.True(response1.IsSuccessStatusCode, $"Server 1 should return success status code, but got {response1.StatusCode}");
        Assert.NotNull(response2);
        Assert.True(response2.IsSuccessStatusCode, $"Server 2 should return success status code, but got {response2.StatusCode}");

        // Ensure cleanup - wait for both servers to shut down
        try
        {
            await Task.WhenAll(
                server1Task.WaitAsync(TimeSpan.FromSeconds(2)),
                server2Task.WaitAsync(TimeSpan.FromSeconds(2))
            );
        }
        catch (TimeoutException)
        {
            // Servers didn't shut down cleanly, but test already passed
        }
    }

    [Theory]
    [InlineData("sse")]
    public async Task RunAsync_TransportNames_AreCaseInsensitive(string transport)
    {
        // Test that transport names are case-insensitive for SSE transport
        // (stdio transport is tested separately due to stdin dependency)

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

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

        await Task.Delay(200);

        // Assert - Verify lowercase transport name is accepted and server keeps running
        Assert.False(taskLower.IsCompleted, $"Transport '{transport.ToLower()}' should be accepted");

        // Ensure cleanup
        try
        {
            await taskLower.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            // Server didn't shut down cleanly, but test already passed
        }

        // Test uppercase with new cancellation token and port
        using var cts2 = new CancellationTokenSource();
        cts2.CancelAfter(TimeSpan.FromMilliseconds(500));
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

        await Task.Delay(200);

        // Assert - Verify uppercase transport name is accepted and server keeps running
        Assert.False(taskUpper.IsCompleted, $"Transport '{transport.ToUpper()}' should be accepted");

        // Ensure cleanup
        try
        {
            await taskUpper.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
            // Server didn't shut down cleanly, but test already passed
        }
    }

    [Theory]
    [InlineData("stdio")]
    [InlineData("STDIO")]
    [InlineData("Stdio")]
    public async Task RunAsync_StdioTransportNames_AreCaseInsensitive(string transport)
    {
        // Verify that stdio transport accepts case-insensitive names
        // Note: stdio may exit immediately when stdin is unavailable (expected in tests)

        var port = GetAvailablePort();
        Exception? caughtException = null;

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync(transport, port, "error", useOpenAi: false);
            }
            catch (NotSupportedException ex)
            {
                // This would indicate the transport name wasn't recognized
                caughtException = ex;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromMilliseconds(500)));

        // Assert - Should not throw NotSupportedException (transport name was accepted)
        Assert.Null(caughtException);
    }
}
