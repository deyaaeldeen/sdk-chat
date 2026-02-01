// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Models;

namespace Microsoft.SdkChat.Services;

/// <summary>
/// Unified AI service supporting Copilot and OpenAI-compatible endpoints via BYOK.
/// </summary>
public class AiService : IAiService
{
    private readonly ILogger<AiService> _logger;
    private readonly AiProviderSettings _settings;
    private readonly AiDebugLogger _debugLogger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private readonly TimeSpan _requestTimeout;
    
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, string> SchemaCache = new();
    
    private int _disposed;
    private CopilotClient? _copilotClient;
    
    public AiService(ILogger<AiService> logger, AiProviderSettings? settings = null, AiDebugLogger? debugLogger = null)
    {
        _logger = logger;
        _settings = settings ?? AiProviderSettings.FromEnvironment();
        _debugLogger = debugLogger ?? new AiDebugLogger(
            NullLogger<AiDebugLogger>.Instance, 
            _settings);
        
        var timeoutStr = Environment.GetEnvironmentVariable("SDK_CLI_TIMEOUT");
        _requestTimeout = int.TryParse(timeoutStr, out var sec) ? TimeSpan.FromSeconds(sec) : TimeSpan.FromMinutes(5);
        
        _logger.LogDebug("AiService initialized with {Provider} provider, timeout {Timeout}s",
            _settings.UseOpenAi ? "OpenAI" : "Copilot",
            _requestTimeout.TotalSeconds);
    }
    
    public bool IsUsingOpenAi => _settings.UseOpenAi;
    public string GetEffectiveModel(string? modelOverride = null) => _settings.GetModel(modelOverride);
    
    public event EventHandler<AiPromptReadyEventArgs>? PromptReady;
    public event EventHandler<AiStreamCompleteEventArgs>? StreamComplete;
    
    public async IAsyncEnumerable<T> StreamItemsAsync<T>(
        string systemPrompt,
        IAsyncEnumerable<string> userPromptStream,
        string? model = null,
        ContextInfo? contextInfo = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var effectiveModel = _settings.GetModel(model);
        using var activity = Telemetry.SdkChatTelemetry.StartPrompt(sessionId, effectiveModel);
        
        // Materialize the streamed prompt (APIs require full prompt upfront)
        var promptBuilder = new StringBuilder();
        await foreach (var chunk in userPromptStream.WithCancellation(cancellationToken))
        {
            promptBuilder.Append(chunk);
        }
        var userPrompt = promptBuilder.ToString();
        
        // Append type schema to system prompt for structured output
        // Contract: NDJSON (one JSON object per line), no markdown, no array.
        var schema = GenerateJsonSchema<T>();
        var enhancedSystemPrompt =
            $"{systemPrompt}\n\n" +
            "Output MUST be NDJSON (newline-delimited JSON):\n" +
            "- Return exactly one JSON object per line\n" +
            "- Do NOT wrap output in a JSON array\n" +
            "- Do NOT use markdown or code fences\n" +
            "- Do NOT output any extra text\n\n" +
            $"Each JSON object MUST match this schema:\n{schema}";
        
        var provider = _settings.UseOpenAi ? "OpenAI" : "Copilot";

        // Fire prompt ready event (used by UX to print model/size and start spinners)
        var promptChars = enhancedSystemPrompt.Length + userPrompt.Length;
        var estimatedTokens = promptChars / SampleConstants.CharsPerToken;
        PromptReady?.Invoke(this, new AiPromptReadyEventArgs(promptChars, estimatedTokens));
        
        // Start debug session
        var debugSession = _debugLogger.StartSession(
            provider,
            effectiveModel,
            _settings.Endpoint,
            enhancedSystemPrompt,
            userPrompt,
            contextInfo);
        
        var responseBuilder = new StringBuilder();
        var itemCount = 0;
        var startTime = DateTime.UtcNow;
        
        IAsyncEnumerable<string> stream = StreamCopilotAsync(enhancedSystemPrompt, userPrompt, effectiveModel, cancellationToken);

        await using var enumerator = NdjsonStreamParser
            .ParseAsync<T>(TapStream(stream, responseBuilder, cancellationToken), JsonOptions, ignoreNonJsonLinesBeforeFirstObject: true, cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            T item;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                item = enumerator.Current;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI stream failed after {Duration}ms with {ResponseChars} chars received",
                    (DateTime.UtcNow - startTime).TotalMilliseconds, responseBuilder.Length);
                await _debugLogger.CompleteSessionAsync(debugSession, responseBuilder.ToString(), streaming: true, error: ex);
                throw;
            }

            itemCount++;
            yield return item;
        }
        
        // Fire stream complete event with response usage
        var responseChars = responseBuilder.Length;
        var estimatedResponseTokens = responseChars / SampleConstants.CharsPerToken;
        var duration = DateTime.UtcNow - startTime;
        
        StreamComplete?.Invoke(this, new AiStreamCompleteEventArgs(responseChars, estimatedResponseTokens, duration));
        
        await _debugLogger.CompleteSessionAsync(debugSession, responseBuilder.ToString(), streaming: true);
    }

    private static async IAsyncEnumerable<string> TapStream(
        IAsyncEnumerable<string> stream,
        StringBuilder responseBuilder,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in stream.WithCancellation(cancellationToken))
        {
            responseBuilder.Append(chunk);
            yield return chunk;
        }
    }
    
    private static string GenerateJsonSchema<T>()
    {
        return SchemaCache.GetOrAdd(typeof(T), static type =>
        {
            return GenerateObjectSchema(type, 0, new HashSet<Type>());
        });
    }
    
    private static string GenerateObjectSchema(Type type, int indentLevel, HashSet<Type> visitedTypes)
    {
        const int MaxRecursionDepth = 10;
        if (indentLevel > MaxRecursionDepth)
            throw new NotSupportedException($"Schema generation exceeded max depth of {MaxRecursionDepth} for type '{type.FullName}'.");
        
        // Prevent infinite recursion on circular references
        if (visitedTypes.Contains(type))
            return "\"object (circular reference)\"";
        
        var indent = new string(' ', indentLevel * 2);
        var propIndent = new string(' ', (indentLevel + 1) * 2);
        var properties = type.GetProperties()
            .Where(p => p.CanRead)
            .Select(p => $"{propIndent}\"{ToCamelCase(p.Name)}\": {GetJsonType(p.PropertyType, indentLevel + 1, visitedTypes)}")
            .ToList();
        
        return "{\n" + string.Join(",\n", properties) + $"\n{indent}}}";
    }
    
    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
    
    private static string GetJsonType(Type type, int indentLevel, HashSet<Type> visitedTypes)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        
        // Primitive types
        if (underlying == typeof(string)) return "\"string\"";
        if (underlying == typeof(int) || underlying == typeof(long) || underlying == typeof(short) || underlying == typeof(byte))
            return "\"integer\"";
        if (underlying == typeof(double) || underlying == typeof(float) || underlying == typeof(decimal)) 
            return "\"number\"";
        if (underlying == typeof(bool)) return "\"boolean\"";
        if (underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) || underlying == typeof(DateOnly))
            return "\"string (ISO 8601 date-time)\"";
        if (underlying == typeof(Guid)) return "\"string (UUID)\"";
        if (underlying == typeof(Uri)) return "\"string (URI)\"";
        
        // Enum types - list valid values
        if (underlying.IsEnum)
        {
            var values = Enum.GetNames(underlying);
            return $"\"string (enum: {string.Join(", ", values)})\"";
        }
        
        // Array and List<T>
        if (underlying.IsArray)
        {
            var elementType = underlying.GetElementType()!;
            return $"[{GetJsonType(elementType, indentLevel, visitedTypes)}]";
        }
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = underlying.GetGenericArguments()[0];
            return $"[{GetJsonType(elementType, indentLevel, visitedTypes)}]";
        }
        
        // Dictionary<string, T>
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var keyType = underlying.GetGenericArguments()[0];
            var valueType = underlying.GetGenericArguments()[1];
            if (keyType != typeof(string))
                throw new NotSupportedException($"Dictionary key type '{keyType.Name}' is not supported. Only string keys are allowed.");
            return $"{{\"<key>\": {GetJsonType(valueType, indentLevel, visitedTypes)}}}";
        }
        
        // Nested object types (records, classes with public properties)
        if (underlying.IsClass && underlying != typeof(object))
        {
            var trackedTypes = new HashSet<Type>(visitedTypes) { underlying };
            return GenerateObjectSchema(underlying, indentLevel, trackedTypes);
        }
        
        // Unknown type - fail explicitly rather than silently returning wrong schema
        throw new NotSupportedException(
            $"Type '{underlying.FullName}' is not supported for JSON schema generation. " +
            "Supported types: primitives, string, bool, DateTime, Guid, Uri, enums, arrays, List<T>, Dictionary<string,T>, and nested objects.");
    }
    #region Copilot Implementation
    
    private async Task<CopilotClient> GetCopilotClientAsync(CancellationToken cancellationToken)
    {
        if (_copilotClient is not null) return _copilotClient;
        
        await _clientLock.WaitAsync(cancellationToken);
        try
        {
            if (_copilotClient is not null) return _copilotClient;
            
            _logger.LogDebug("Initializing GitHub Copilot client...");
            
            var cliPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH") ?? "copilot";
            _logger.LogDebug("Using Copilot CLI at: {CliPath}", cliPath);
            
            // Check for GitHub token in environment (GH_TOKEN or GITHUB_TOKEN)
            var githubToken = Environment.GetEnvironmentVariable("GH_TOKEN") 
                           ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            
            _copilotClient = new CopilotClient(new CopilotClientOptions
            {
                CliPath = cliPath,
                UseStdio = true,
                AutoStart = true,
                LogLevel = "debug",
                GithubToken = githubToken  // Pass token for auth if available
            });
            
            await _copilotClient.StartAsync();
            _logger.LogDebug("GitHub Copilot client started successfully");
            
            return _copilotClient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start GitHub Copilot client");
            throw new InvalidOperationException(
                $"Failed to start GitHub Copilot client: {ex.Message}. " +
                "Ensure the Copilot CLI is installed and authenticated, " +
                "or use --use-openai flag with OPENAI_API_KEY set.", ex);
        }
        finally
        {
            _clientLock.Release();
        }
    }
    
    private async IAsyncEnumerable<string> StreamCopilotAsync(
        string systemPrompt,
        string userPrompt,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var client = await GetCopilotClientAsync(cancellationToken);

        ProviderConfig? providerConfig = null;
        if (_settings.UseOpenAi)
        {
            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                throw new InvalidOperationException(
                    "OPENAI_API_KEY environment variable is required when using OpenAI mode. " +
                    "Set SDK_CLI_USE_OPENAI=true and OPENAI_API_KEY=your-key");
            }

            var baseUrl = !string.IsNullOrWhiteSpace(_settings.Endpoint)
                ? _settings.Endpoint
                : "https://api.openai.com/v1";

            providerConfig = new ProviderConfig
            {
                Type = "openai",
                BaseUrl = baseUrl,
                ApiKey = _settings.ApiKey
            };
        }
        
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = model,
            Streaming = true,
            Provider = providerConfig,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = systemPrompt
            },
            AvailableTools = new List<string>()
        });
        
        var chunks = System.Threading.Channels.Channel.CreateUnbounded<string>();
        
        using var subscription = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    chunks.Writer.TryWrite(delta.Data.DeltaContent ?? "");
                    break;
                case SessionIdleEvent:
                    chunks.Writer.Complete();
                    break;
                case SessionErrorEvent err:
                    chunks.Writer.Complete(new InvalidOperationException($"Session error: {err.Data.Message}"));
                    break;
            }
        });
        
        await session.SendAsync(new MessageOptions { Prompt = userPrompt });
        
        await foreach (var chunk in chunks.Reader.ReadAllAsync(cancellationToken))
        {
            yield return chunk;
        }
    }
    
    #endregion
    
    public async ValueTask DisposeAsync()
    {
        // Thread-safe disposal - only first caller proceeds
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        
        // Ensure no active initialization is in progress
        await _clientLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_copilotClient != null)
            {
                await _copilotClient.StopAsync().ConfigureAwait(false);
                await _copilotClient.DisposeAsync().ConfigureAwait(false);
                _copilotClient = null;
            }
        }
        finally
        {
            _clientLock.Release();
            _clientLock.Dispose();
        }
    }
}
