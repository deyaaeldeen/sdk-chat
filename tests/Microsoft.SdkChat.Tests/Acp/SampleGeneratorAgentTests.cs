// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Text.Json;
using AgentClientProtocol.Sdk;
using AgentClientProtocol.Sdk.JsonRpc;
using AgentClientProtocol.Sdk.Schema;
using AgentClientProtocol.Sdk.Stream;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SdkChat.Acp;
using Microsoft.SdkChat.Configuration;
using Microsoft.SdkChat.Helpers;
using Microsoft.SdkChat.Services;
using Xunit;

namespace Microsoft.SdkChat.Tests.Acp;

/// <summary>
/// Tests for the ACP Sample Generator agent.
///
/// The agent no longer calls AI directly - it:
/// 1. Analyzes SDK and builds AI prompts
/// 2. Returns prompts to client for AI generation
/// 3. Receives samples from client and writes to disk
/// </summary>
public class SampleGeneratorAgentTests : IDisposable
{
    private readonly string _testRoot;
    private readonly ServiceProvider _services;

    public SampleGeneratorAgentTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"AcpAgentTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));
        serviceCollection.AddSingleton(SdkChatOptions.FromEnvironment());
        serviceCollection.AddSingleton<FileHelper>();
        serviceCollection.AddSingleton<IPackageInfoService, PackageInfoService>();

        _services = serviceCollection.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        SdkInfo.ClearCache();
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch { }
        GC.SuppressFinalize(this);
    }

    #region IAgent Interface Tests

    [Fact]
    public async Task InitializeAsync_ReturnsProtocolInfo()
    {
        var agent = CreateAgent();
        var request = new InitializeRequest
        {
            ProtocolVersion = Protocol.Version,
            ClientCapabilities = new ClientCapabilities()
        };

        var response = await agent.InitializeAsync(request);

        Assert.NotNull(response);
        Assert.Equal(Protocol.Version, response.ProtocolVersion);
        Assert.NotNull(response.AgentInfo);
        Assert.Equal("sdk-chat", response.AgentInfo.Name);
        Assert.Equal("1.0.0", response.AgentInfo.Version);
    }

    [Fact]
    public async Task InitializeAsync_ReturnsSessionCapabilities()
    {
        var agent = CreateAgent();
        var request = new InitializeRequest
        {
            ProtocolVersion = Protocol.Version,
            ClientCapabilities = new ClientCapabilities()
        };

        var response = await agent.InitializeAsync(request);

        Assert.NotNull(response.AgentCapabilities);
        Assert.NotNull(response.AgentCapabilities.SessionCapabilities);
    }

    [Fact]
    public async Task NewSessionAsync_CreatesUniqueSession()
    {
        var agent = CreateAgent();
        var request = new NewSessionRequest { Cwd = _testRoot, McpServers = [] };

        var response1 = await agent.NewSessionAsync(request);
        var response2 = await agent.NewSessionAsync(request);

        Assert.NotNull(response1.SessionId);
        Assert.NotNull(response2.SessionId);
        Assert.NotEqual(response1.SessionId, response2.SessionId);
        Assert.StartsWith("sess_", response1.SessionId);
        Assert.StartsWith("sess_", response2.SessionId);
    }

    [Fact]
    public async Task PromptAsync_WithUnknownSession_ThrowsRequestError()
    {
        var agent = CreateAgent();
        var request = new PromptRequest
        {
            SessionId = "unknown-session-id",
            Prompt = new ContentBlock[] { new TextContent { Text = "test" } }
        };

        await Assert.ThrowsAsync<RequestError>(() => agent.PromptAsync(request));
    }

    [Fact]
    public async Task CancelAsync_DoesNotThrow()
    {
        var agent = CreateAgent();
        var notification = new CancelNotification { SessionId = "any-session" };

        await agent.CancelAsync(notification);
    }

    #endregion

    #region Phase-Based Flow Tests

    [Fact]
    public async Task PromptAsync_InitialPhase_AsksForSdkPath()
    {
        var agent = CreateAgentWithConnection();
        CreateDotNetProject();

        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot, McpServers = [] });

        var response = await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate samples" } }
        });

        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);
        // Agent should now be awaiting SDK path
    }

    [Fact]
    public async Task PromptAsync_UnknownLanguage_ReturnsEndTurn()
    {
        var agent = CreateAgentWithConnection();
        // Empty directory - no project files

        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot, McpServers = [] });

        // Phase 1: Initial - asks for SDK path
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        });

        // Phase 2: Provide SDK path (use default workspace)
        var response = await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } }
        });

        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);
    }

    [Fact]
    public async Task PromptAsync_OutputFolderPhase_AsksForSampleCount()
    {
        var agent = CreateAgentWithConnection();
        CreateDotNetProject();

        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot, McpServers = [] });

        // Phase 1: Initial - asks for SDK path
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        });

        // Phase 2: Provide SDK path (use default)
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } }
        });

        // Phase 3: Provide output folder (empty = use default)
        var response = await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } }
        });

        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);
        // Agent should now be awaiting sample count
    }

    [Fact]
    public async Task PromptAsync_ConfirmationPhase_ReturnsPromptData()
    {
        var agent = CreateAgentWithConnection();
        CreateDotNetProject();

        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot, McpServers = [] });

        // Phase 1: Initial - asks for SDK path
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        });

        // Phase 2: Provide SDK path (use default)
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } }
        });

        // Phase 3: Provide output folder
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } }
        });

        // Phase 4: Provide sample count
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "3" } }
        });

        // Phase 5: Confirm to get prompt
        var response = await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "generate" } }
        });

        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);
        Assert.NotNull(response.Meta);
        Assert.True(response.Meta.ContainsKey("promptData"));
    }

    [Fact]
    public async Task PromptAsync_SamplesPhase_WritesFiles()
    {
        var agent = CreateAgentWithConnection();
        CreateDotNetProject();

        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot, McpServers = [] });

        // Phase 1-5: Get to samples phase
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        });
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } } // default SDK path
        });
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } } // default output folder
        });
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } } // default sample count
        });
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "generate" } }
        });

        // Phase 5: Send samples JSON
        var samplesJson = """[{"name":"HelloWorld","description":"A hello world sample","code":"Console.WriteLine(\"Hello\");","filePath":"HelloWorld.cs"}]""";
        var response = await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = samplesJson } }
        });

        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);

        // Verify file was written
        var outputPath = Path.Combine(_testRoot, "examples", "HelloWorld.cs");
        Assert.True(File.Exists(outputPath), $"Expected file at {outputPath}");
        var content = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("Hello", content);
    }

    [Fact]
    public async Task PromptAsync_CancelDuringConfirmation_Exits()
    {
        var agent = CreateAgentWithConnection();
        CreateDotNetProject();

        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot, McpServers = [] });

        // Phase 1-4: Get to confirmation phase
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        });
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } } // default SDK path
        });
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } } // default output folder
        });
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "" } } // default sample count
        });

        // Cancel instead of confirm
        var response = await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "cancel" } }
        });

        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);
    }

    #endregion

    #region Wire Protocol Tests

    [Fact]
    public async Task JsonRpcFlow_InitializeRequest()
    {
        var (inputPipe, outputPipe) = CreatePipePair();

        var agentStream = new NdJsonStream(
            new StreamReader(inputPipe.reader),
            new StreamWriter(outputPipe.writer) { AutoFlush = true });

        var agent = CreateAgent();
        var connection = new AgentSideConnection(agent, agentStream);

        var agentTask = Task.Run(async () =>
        {
            try { await connection.RunAsync(); } catch { }
        });

        var clientWriter = new StreamWriter(inputPipe.writer) { AutoFlush = true };
        var initRequest = new JsonRpcRequest
        {
            Id = 1,
            Method = "initialize",
            Params = JsonSerializer.SerializeToElement(new InitializeRequest
            {
                ProtocolVersion = Protocol.Version,
                ClientCapabilities = new ClientCapabilities()
            })
        };
        await clientWriter.WriteLineAsync(JsonSerializer.Serialize(initRequest));

        var clientReader = new StreamReader(outputPipe.reader);
        var responseLine = await clientReader.ReadLineAsync();

        Assert.NotNull(responseLine);
        var response = JsonSerializer.Deserialize<JsonRpcResponse>(responseLine);
        Assert.NotNull(response);
        Assert.Equal(1, ((JsonElement)response.Id!).GetInt32());
        Assert.NotNull(response.Result);

        clientWriter.Close();
        try { await agentTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
    }

    #endregion

    #region Helper Methods

    private SampleGeneratorAgent CreateAgent()
    {
        var logger = _services.GetRequiredService<ILogger<SampleGeneratorAgent>>();
        return new SampleGeneratorAgent(_services, logger);
    }

    private SampleGeneratorAgent CreateAgentWithConnection()
    {
        var agent = CreateAgent();
        var mockStream = new NdJsonStream(new StringReader(""), new StringWriter());
        var connection = new AgentSideConnection(agent, mockStream);
        agent.SetConnection(connection);
        return agent;
    }

    private static ((Stream reader, Stream writer) input, (Stream reader, Stream writer) output) CreatePipePair()
    {
        var inputPipe = new AnonymousPipeServerStream(PipeDirection.Out);
        var inputClient = new AnonymousPipeClientStream(PipeDirection.In, inputPipe.ClientSafePipeHandle);

        var outputPipe = new AnonymousPipeServerStream(PipeDirection.In);
        var outputClient = new AnonymousPipeClientStream(PipeDirection.Out, outputPipe.ClientSafePipeHandle);

        return ((inputClient, inputPipe), (outputPipe, outputClient));
    }

    private void CreateDotNetProject()
    {
        var srcDir = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, "MyProject.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(srcDir, "Client.cs"), "public class Client { }");
    }

    #endregion
}
