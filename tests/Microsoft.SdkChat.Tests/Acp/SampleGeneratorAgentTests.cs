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
using Microsoft.SdkChat.Models;
using Microsoft.SdkChat.Services;
using Microsoft.SdkChat.Tests.Mocks;
using Xunit;

namespace Microsoft.SdkChat.Tests.Acp;

/// <summary>
/// Comprehensive tests for the ACP Sample Generator agent.
/// </summary>
public class SampleGeneratorAgentTests : IDisposable
{
    private readonly string _testRoot;
    private readonly ServiceProvider _services;
    private readonly MockAiService _mockAiService;

    public SampleGeneratorAgentTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"AcpAgentTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));
        serviceCollection.AddSingleton(SdkChatOptions.FromEnvironment());
        serviceCollection.AddSingleton<AiDebugLogger>();
        serviceCollection.AddSingleton<FileHelper>();

        _mockAiService = new MockAiService();
        serviceCollection.AddSingleton<IAiService>(_mockAiService);

        _services = serviceCollection.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        SdkInfo.ClearCache(); // Clear SDK detection cache between tests
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
        // Arrange
        var agent = CreateAgent();
        var request = new InitializeRequest
        {
            ProtocolVersion = Protocol.Version,
            ClientCapabilities = new ClientCapabilities()
        };

        // Act
        var response = await agent.InitializeAsync(request);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(Protocol.Version, response.ProtocolVersion);
        Assert.NotNull(response.AgentInfo);
        Assert.Equal("sdk-chat", response.AgentInfo.Name);
        Assert.Equal("1.0.0", response.AgentInfo.Version);
    }

    [Fact]
    public async Task InitializeAsync_ReturnsSessionCapabilities()
    {
        // Arrange
        var agent = CreateAgent();
        var request = new InitializeRequest
        {
            ProtocolVersion = Protocol.Version,
            ClientCapabilities = new ClientCapabilities()
        };

        // Act
        var response = await agent.InitializeAsync(request);

        // Assert
        Assert.NotNull(response.AgentCapabilities);
        Assert.NotNull(response.AgentCapabilities.SessionCapabilities);
    }

    [Fact]
    public async Task NewSessionAsync_CreatesUniqueSession()
    {
        // Arrange
        var agent = CreateAgent();
        var request = new NewSessionRequest { Cwd = _testRoot };

        // Act
        var response1 = await agent.NewSessionAsync(request);
        var response2 = await agent.NewSessionAsync(request);

        // Assert
        Assert.NotNull(response1.SessionId);
        Assert.NotNull(response2.SessionId);
        Assert.NotEqual(response1.SessionId, response2.SessionId);
        Assert.StartsWith("sess_", response1.SessionId);
        Assert.StartsWith("sess_", response2.SessionId);
    }

    [Fact]
    public async Task NewSessionAsync_StoresCwd()
    {
        // Arrange
        var agent = CreateAgent();
        CreateDotNetProject();
        _mockAiService.SetSamplesToReturn(CreateSample("Sample", "A sample", "// Code", "Sample.cs"));

        var request = new NewSessionRequest { Cwd = _testRoot };
        var sessionResponse = await agent.NewSessionAsync(request);

        // Act - Prompt uses stored cwd
        var promptRequest = new PromptRequest
        {
            SessionId = sessionResponse.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        };
        var response = await agent.PromptAsync(promptRequest);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);
        Assert.Equal(1, _mockAiService.CallCount);
    }

    [Fact]
    public async Task PromptAsync_WithUnknownSession_ThrowsException()
    {
        // Arrange
        var agent = CreateAgent();
        var request = new PromptRequest
        {
            SessionId = "unknown-session-id",
            Prompt = new ContentBlock[] { new TextContent { Text = "test" } }
        };

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.PromptAsync(request));
    }

    [Fact]
    public async Task CancelAsync_DoesNotThrow()
    {
        // Arrange
        var agent = CreateAgent();
        var notification = new CancelNotification { SessionId = "any-session" };

        // Act & Assert - should complete without exception
        await agent.CancelAsync(notification);
    }

    #endregion

    #region Sample Generation Tests

    [Fact]
    public async Task PromptAsync_WithDotNetProject_GeneratesSamples()
    {
        // Arrange
        var agent = CreateAgentWithConnection();
        CreateDotNetProject();
        _mockAiService.SetSamplesToReturn(
            CreateSample("Sample1", "First", "// 1", "Sample1.cs"),
            CreateSample("Sample2", "Second", "// 2", "Sample2.cs")
        );

        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot });

        // Act
        var response = await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        });

        // Assert
        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);

        var samplesDir = Path.Combine(_testRoot, "examples");
        Assert.True(Directory.Exists(samplesDir), $"Expected samples dir at {samplesDir}");
        Assert.True(File.Exists(Path.Combine(samplesDir, "Sample1.cs")));
        Assert.True(File.Exists(Path.Combine(samplesDir, "Sample2.cs")));
    }

    [Fact]
    public async Task PromptAsync_WithPythonProject_GeneratesSamples()
    {
        // Arrange
        var agent = CreateAgentWithConnection();
        CreatePythonProject();
        _mockAiService.SetSamplesToReturn(CreateSample("sample", "A sample", "# Python", "sample.py"));

        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot });

        // Act
        var response = await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        });

        // Assert
        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);
    }

    [Fact]
    public async Task PromptAsync_WithUnknownProject_ReturnsEndTurn()
    {
        // Arrange - empty directory, no project files
        var agent = CreateAgentWithConnection();
        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot });

        // Act
        var response = await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        });

        // Assert
        Assert.NotNull(response);
        Assert.Equal(StopReason.EndTurn, response.StopReason);
        Assert.Equal(0, _mockAiService.CallCount); // Should not call AI
    }

    [Fact]
    public async Task PromptAsync_CreatesSubfolders()
    {
        // Arrange
        var agent = CreateAgentWithConnection();
        CreateDotNetProject();
        _mockAiService.SetSamplesToReturn(CreateSample("Sample", "Sample", "// Code", "deep/nested/Sample.cs"));

        var session = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot });

        // Act
        await agent.PromptAsync(new PromptRequest
        {
            SessionId = session.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate" } }
        });

        // Assert
        var filePath = Path.Combine(_testRoot, "examples", "deep", "nested", "Sample.cs");
        Assert.True(File.Exists(filePath), $"Expected file at {filePath}");
    }

    #endregion

    #region Protocol Flow Tests

    [Fact]
    public async Task FullFlow_Initialize_NewSession_Prompt()
    {
        // Arrange
        var agent = CreateAgentWithConnection();
        CreateDotNetProject();
        _mockAiService.SetSamplesToReturn(CreateSample("Sample", "Sample", "// Code", "Sample.cs"));

        // Act - Full protocol flow
        var initResponse = await agent.InitializeAsync(new InitializeRequest
        {
            ProtocolVersion = Protocol.Version,
            ClientCapabilities = new ClientCapabilities()
        });

        var sessionResponse = await agent.NewSessionAsync(new NewSessionRequest { Cwd = _testRoot });

        var promptResponse = await agent.PromptAsync(new PromptRequest
        {
            SessionId = sessionResponse.SessionId,
            Prompt = new ContentBlock[] { new TextContent { Text = "Generate samples" } }
        });

        // Assert
        Assert.Equal(Protocol.Version, initResponse.ProtocolVersion);
        Assert.StartsWith("sess_", sessionResponse.SessionId);
        Assert.Equal(StopReason.EndTurn, promptResponse.StopReason);
        Assert.Equal(1, _mockAiService.CallCount);
    }

    [Fact]
    public async Task MultipleSessions_IndependentState()
    {
        // Arrange
        var agent = CreateAgent();

        // Create test directories for each session
        var testDir1 = Path.Combine(_testRoot, "session1");
        var testDir2 = Path.Combine(_testRoot, "session2");
        Directory.CreateDirectory(testDir1);
        Directory.CreateDirectory(testDir2);

        // Act
        var session1 = await agent.NewSessionAsync(new NewSessionRequest { Cwd = testDir1 });
        var session2 = await agent.NewSessionAsync(new NewSessionRequest { Cwd = testDir2 });

        // Assert
        Assert.NotEqual(session1.SessionId, session2.SessionId);
    }

    #endregion

    #region Wire Protocol Tests

    [Fact]
    public async Task JsonRpcFlow_InitializeRequest()
    {
        // Arrange
        var (inputPipe, outputPipe) = CreatePipePair();

        var agentStream = new NdJsonStream(
            new StreamReader(inputPipe.reader),
            new StreamWriter(outputPipe.writer) { AutoFlush = true });

        var agent = CreateAgent();
        var connection = new AgentSideConnection(agent, agentStream);

        // Start agent
        var agentTask = Task.Run(async () =>
        {
            try { await connection.RunAsync(); } catch { }
        });

        // Client sends initialize
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

        // Read response
        var clientReader = new StreamReader(outputPipe.reader);
        var responseLine = await clientReader.ReadLineAsync();

        // Assert
        Assert.NotNull(responseLine);
        var response = JsonSerializer.Deserialize<JsonRpcResponse>(responseLine);
        Assert.NotNull(response);
        Assert.Equal(1, ((JsonElement)response.Id!).GetInt32());
        Assert.NotNull(response.Result);

        // Cleanup
        clientWriter.Close();
        try { await agentTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch { }
    }

    #endregion

    #region Helper Methods

    private static GeneratedSample CreateSample(string name, string description, string code, string? filePath) =>
        new() { Name = name, Description = description, Code = code, FilePath = filePath };

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

    private void CreatePythonProject()
    {
        var srcDir = Path.Combine(_testRoot, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(_testRoot, "pyproject.toml"), "[project]");
        File.WriteAllText(Path.Combine(srcDir, "client.py"), "class Client: pass");
    }

    #endregion
}
