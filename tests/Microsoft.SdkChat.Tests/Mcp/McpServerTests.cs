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
    [InlineData("sse")]
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
    [InlineData("http")]
    [InlineData("HTTP")]
    [InlineData("Http")]
    public async Task RunAsync_WithHttpTransport_DoesNotExitImmediately(string transport)
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Use an ephemeral port to avoid conflicts
        var port = GetAvailablePort();

        // Act - Start the HTTP server in a background task with cancellation token
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
        Assert.False(serverTask.IsCompleted, $"HTTP server with transport '{transport}' should not complete immediately");

        // Ensure cleanup - cancel and wait for server to shut down
        await cts.CancelAsync();
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // If server still hasn't stopped, force it by ignoring
            // The CTS is already cancelled, so the server should stop soon
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
    public async Task RunAsync_WithHttpTransport_StartsHttpServerOnSpecifiedPort()
    {
        // Arrange
        var port = GetAvailablePort();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        // Act - Start the HTTP server with cancellation token
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await McpServer.RunAsync("http", port, "error", useOpenAi: false, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: server shutdown via cancellation token
            }
        });

        // Wait for server to initialize with retry logic
        // Streamable HTTP transport uses POST on /mcp — verify server is listening by connecting
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        HttpResponseMessage? response = null;
        var attempts = 0;
        var maxAttempts = 10;
        var serverReady = false;

        while (attempts < maxAttempts && !cts.Token.IsCancellationRequested)
        {
            attempts++;
            try
            {
                await Task.Delay(200 * attempts, cts.Token); // Exponential backoff
                // Use POST since Streamable HTTP transport expects POST requests
                response = await httpClient.PostAsync($"http://localhost:{port}/mcp",
                    new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), cts.Token);
                // Any HTTP response (even 4xx) means the server is running and listening
                serverReady = true;
                break;
            }
            catch (HttpRequestException)
            {
                // Server not ready yet (connection refused), retry
                if (attempts == maxAttempts)
                {
                    throw;
                }
            }
            catch (TaskCanceledException) when (!cts.Token.IsCancellationRequested)
            {
                // HTTP timeout, server might be slow - retry
                if (attempts == maxAttempts)
                {
                    throw;
                }
            }
        }

        // Assert - Verify the server accepted a connection (any HTTP response proves it's running)
        Assert.True(serverReady, "HTTP server should be accepting connections");
        Assert.NotNull(response);

        // Ensure cleanup - cancel and wait for server to shut down
        await cts.CancelAsync();
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Server didn't shut down cleanly within timeout
        }
    }

    [Fact]
    public async Task RunAsync_WithDifferentPorts_EachHttpServerUsesCorrectPort()
    {
        // Test that multiple HTTP servers can be configured with different ports
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
                await McpServer.RunAsync("http", port1, "error", useOpenAi: false, cancellationToken: cts1.Token);
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
                await McpServer.RunAsync("http", port2, "error", useOpenAi: false, cancellationToken: cts2.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: server shutdown via cancellation token
            }
        });

        // Wait for both servers to initialize with retry logic
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        // Helper function to wait for server - uses POST since Streamable HTTP transport expects POST
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
                    var response = await httpClient.PostAsync($"http://localhost:{port}/mcp",
                        new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), cancellationToken);
                    // Any HTTP response (even 4xx) means the server is running
                    return response;
                }
                catch (HttpRequestException)
                {
                    // Server not ready yet (connection refused), retry
                    if (attempts == maxAttempts)
                    {
                        throw;
                    }
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // HTTP timeout - retry
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
        Assert.NotNull(response2);

        // Ensure cleanup - cancel both and wait for servers to shut down
        await cts1.CancelAsync();
        await cts2.CancelAsync();
        try
        {
            await Task.WhenAll(
                server1Task.WaitAsync(TimeSpan.FromSeconds(5)),
                server2Task.WaitAsync(TimeSpan.FromSeconds(5))
            );
        }
        catch (TimeoutException)
        {
            // Servers didn't shut down cleanly within timeout
        }
    }

    [Theory]
    [InlineData("http")]
    public async Task RunAsync_TransportNames_AreCaseInsensitive(string transport)
    {
        // Test that transport names are case-insensitive for HTTP transport
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

        // Ensure cleanup - cancel explicitly
        await cts.CancelAsync();
        try
        {
            await taskLower.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Server didn't shut down cleanly within timeout
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

        // Ensure cleanup - cancel explicitly
        await cts2.CancelAsync();
        try
        {
            await taskUpper.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Server didn't shut down cleanly within timeout
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
