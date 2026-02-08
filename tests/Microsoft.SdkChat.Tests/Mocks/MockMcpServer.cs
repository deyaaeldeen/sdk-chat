// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.SdkChat.Mcp;
using Microsoft.SdkChat.Models;
using ModelContextProtocol.Protocol;

namespace Microsoft.SdkChat.Tests.Mocks;

/// <summary>
/// Factory for creating fake MCP samplers for testing.
/// </summary>
public static class MockMcpServerFactory
{
    /// <summary>
    /// Creates a fake MCP sampler that returns the specified samples when SampleAsync is called.
    /// </summary>
    public static IMcpSampler CreateWithSamples(params GeneratedSample[] samples)
    {
        var samplesList = samples.ToList();
        var samplesJson = JsonSerializer.Serialize(samplesList, AiStreamingJsonContext.CaseInsensitive.ListGeneratedSample);
        
        var result = new CreateMessageResult
        {
            Content = [new TextContentBlock { Text = samplesJson }],
            Role = Role.Assistant,
            Model = "mock-model"
        };

        return new FakeMcpSampler((request, ct) => ValueTask.FromResult(result));
    }

    /// <summary>
    /// Creates a fake MCP sampler that throws the specified exception.
    /// </summary>
    public static IMcpSampler CreateThatThrows(Exception exception)
    {
        return new FakeMcpSampler((request, ct) => ValueTask.FromException<CreateMessageResult>(exception));
    }
}

/// <summary>
/// Fake MCP sampler for testing.
/// </summary>
public class FakeMcpSampler : IMcpSampler
{
    private readonly Func<CreateMessageRequestParams, CancellationToken, ValueTask<CreateMessageResult>> _sampleFunc;

    public FakeMcpSampler(Func<CreateMessageRequestParams, CancellationToken, ValueTask<CreateMessageResult>> sampleFunc)
    {
        _sampleFunc = sampleFunc;
    }

    public ValueTask<CreateMessageResult> SampleAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
    {
        return _sampleFunc(request, cancellationToken);
    }
}
