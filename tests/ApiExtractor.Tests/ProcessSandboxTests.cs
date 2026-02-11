// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using ApiExtractor.Contracts;
using Xunit;

namespace ApiExtractor.Tests;

/// <summary>
/// Tests for <see cref="ProcessSandbox"/> cancellation and timeout behavior.
/// </summary>
public class ProcessSandboxTests
{
    [Fact]
    public async Task ExecuteAsync_UserCancellation_PropagatesOperationCanceledException()
    {
        // Arrange: cancel immediately
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert: user cancellation must propagate as OperationCanceledException,
        // not be swallowed into a ProcessResult.Failed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await ProcessSandbox.ExecuteAsync(
                "sleep", ["10"],
                workingDirectory: Path.GetTempPath(),
                cancellationToken: cts.Token);
        });
    }

    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsTimedOutResult()
    {
        var result = await ProcessSandbox.ExecuteAsync(
            "sleep", ["60"],
            workingDirectory: Path.GetTempPath(),
            timeout: TimeSpan.FromMilliseconds(200));

        Assert.True(result.TimedOut, "Process should have timed out");
        Assert.NotEqual(0, result.ExitCode);
    }
}
