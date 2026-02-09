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
public static class MockMcpSamplerFactory
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
/// Fake MCP sampler for testing. Captures request params for assertion.
/// </summary>
public class FakeMcpSampler : IMcpSampler
{
    private readonly Func<CreateMessageRequestParams, CancellationToken, ValueTask<CreateMessageResult>> _sampleFunc;

    /// <summary>Last system prompt passed to SampleAsync.</summary>
    public string? LastSystemPrompt { get; private set; }

    /// <summary>Last user prompt text passed to SampleAsync.</summary>
    public string? LastUserPrompt { get; private set; }

    /// <summary>Number of times SampleAsync was called.</summary>
    public int CallCount { get; private set; }

    public FakeMcpSampler(Func<CreateMessageRequestParams, CancellationToken, ValueTask<CreateMessageResult>> sampleFunc)
    {
        _sampleFunc = sampleFunc;
    }

    public ValueTask<CreateMessageResult> SampleAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastSystemPrompt = request.SystemPrompt;
        LastUserPrompt = string.Concat(
            request.Messages
                .SelectMany(m => m.Content.OfType<TextContentBlock>())
                .Select(b => b.Text ?? string.Empty));
        return _sampleFunc(request, cancellationToken);
    }
}

/// <summary>
/// Mock MCP sampler that captures the request and returns configured samples.
/// Replaces MockAiService for SamplesMcpTools tests.
/// </summary>
public class MockMcpSampler : IMcpSampler
{
    private readonly List<object> _samplesToReturn = [];
    private Exception? _exceptionToThrow;

    /// <summary>Last system prompt passed to SampleAsync.</summary>
    public string? LastSystemPrompt { get; private set; }

    /// <summary>Last user prompt text passed to SampleAsync.</summary>
    public string? LastUserPrompt { get; private set; }

    /// <summary>Number of times SampleAsync was called.</summary>
    public int CallCount { get; private set; }

    /// <summary>
    /// Configure samples to return when SampleAsync is called.
    /// </summary>
    public void SetSamplesToReturn(params GeneratedSample[] samples)
    {
        _samplesToReturn.Clear();
        _samplesToReturn.AddRange(samples);
    }

    /// <summary>
    /// Configure an exception to throw.
    /// </summary>
    public void SetExceptionToThrow(Exception ex)
    {
        _exceptionToThrow = ex;
    }

    public ValueTask<CreateMessageResult> SampleAsync(CreateMessageRequestParams request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastSystemPrompt = request.SystemPrompt;
        LastUserPrompt = string.Concat(
            request.Messages
                .SelectMany(m => m.Content.OfType<TextContentBlock>())
                .Select(b => b.Text ?? string.Empty));

        if (_exceptionToThrow != null)
            return ValueTask.FromException<CreateMessageResult>(_exceptionToThrow);

        var samplesJson = JsonSerializer.Serialize(
            _samplesToReturn.Cast<GeneratedSample>().ToList(),
            AiStreamingJsonContext.CaseInsensitive.ListGeneratedSample);

        var result = new CreateMessageResult
        {
            Content = [new TextContentBlock { Text = samplesJson }],
            Role = Role.Assistant,
            Model = "mock-model"
        };

        return ValueTask.FromResult(result);
    }
}
