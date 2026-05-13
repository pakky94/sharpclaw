using Microsoft.Extensions.Configuration;
using NSubstitute;
using SharpClaw.API.Agents;
using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.UnitTests;

public class SessionStoreTests
{
    private readonly AgentsRepository _agentsRepository;
    private readonly ChatRepository _chatRepository;
    private readonly WorkspaceRepository _workspaceRepository;
    private readonly SessionStore _store;

    public SessionStoreTests()
    {
        // Use a real configuration so the repository constructors don't throw
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:sharpclaw"] = "Host=localhost;Database=sharpclaw_test;Username=test;Password=test"
            })
            .Build();

        _agentsRepository = Substitute.For<AgentsRepository>(config);
        _chatRepository = Substitute.For<ChatRepository>(config);
        _workspaceRepository = Substitute.For<WorkspaceRepository>(config);
        _store = new SessionStore(_chatRepository, _workspaceRepository, _agentsRepository);
    }

    [Fact]
    public async Task RefreshAgentConfigForSessions_UpdatesMatchingSessions()
    {
        // Arrange
        const long agentId = 1;

        var session1 = CreateSessionState(Guid.NewGuid(), agentId, "old-model-1", 0.5f, 1000, 2000);
        var session2 = CreateSessionState(Guid.NewGuid(), agentId, "old-model-2", 0.7f, 3000, 4000);
        var session3 = CreateSessionState(Guid.NewGuid(), 2, "other-agent", 0.3f, 5000, 6000);

        await _store.Add(session1);
        await _store.Add(session2);
        await _store.Add(session3);

        var updatedConfig = new AgentConfig(
            agentId, "test-agent", "new-model", 0.9f, 8000, 12000,
            DateTime.UtcNow, DateTime.UtcNow);

        _agentsRepository.GetAgent(agentId).Returns(updatedConfig);

        // Act
        await _store.RefreshAgentConfigForSessions(agentId);

        // Assert — sessions for agent 1 should be updated
        Assert.Equal("new-model", session1.Context.LlmModel);
        Assert.Equal(0.9f, session1.Context.Temperature);
        Assert.Equal(8000, session1.Context.SoftCompactThreshold);
        Assert.Equal(12000, session1.Context.HardCompactThreshold);

        Assert.Equal("new-model", session2.Context.LlmModel);
        Assert.Equal(0.9f, session2.Context.Temperature);
        Assert.Equal(8000, session2.Context.SoftCompactThreshold);
        Assert.Equal(12000, session2.Context.HardCompactThreshold);

        // Assert — session for agent 2 should NOT be updated
        Assert.Equal("other-agent", session3.Context.LlmModel);
        Assert.Equal(0.3f, session3.Context.Temperature);
        Assert.Equal(5000, session3.Context.SoftCompactThreshold);
        Assert.Equal(6000, session3.Context.HardCompactThreshold);
    }

    [Fact]
    public async Task RefreshAgentConfigForSessions_NoSessions_DoesNotThrow()
    {
        // Arrange
        _agentsRepository.GetAgent(1).Returns((AgentConfig?)null);

        // Act & Assert
        var exception = await Record.ExceptionAsync(() => _store.RefreshAgentConfigForSessions(1));
        Assert.Null(exception);
    }

    [Fact]
    public async Task RefreshAgentConfigForSessions_AgentNotFound_DoesNotUpdate()
    {
        // Arrange
        const long agentId = 1;

        var session = CreateSessionState(Guid.NewGuid(), agentId, "old-model", 0.5f, 1000, 2000);
        await _store.Add(session);

        _agentsRepository.GetAgent(agentId).Returns((AgentConfig?)null);

        // Act
        await _store.RefreshAgentConfigForSessions(agentId);

        // Assert — unchanged because agent wasn't found
        Assert.Equal("old-model", session.Context.LlmModel);
        Assert.Equal(0.5f, session.Context.Temperature);
    }

    [Fact]
    public async Task RefreshAgentConfigForSessions_MultipleAgents_OnlyUpdatesTarget()
    {
        // Arrange
        var sessionAgent1 = CreateSessionState(Guid.NewGuid(), 1, "model-a", 0.1f, 100, 200);
        var sessionAgent2a = CreateSessionState(Guid.NewGuid(), 2, "model-b", 0.2f, 300, 400);
        var sessionAgent2b = CreateSessionState(Guid.NewGuid(), 2, "model-b2", 0.3f, 500, 600);

        await _store.Add(sessionAgent1);
        await _store.Add(sessionAgent2a);
        await _store.Add(sessionAgent2b);

        var updatedConfig = new AgentConfig(
            2, "agent-2", "new-model-b", 0.99f, 999, 1999,
            DateTime.UtcNow, DateTime.UtcNow);

        _agentsRepository.GetAgent(2).Returns(updatedConfig);

        // Act
        await _store.RefreshAgentConfigForSessions(2);

        // Assert — agent 1 unchanged
        Assert.Equal("model-a", sessionAgent1.Context.LlmModel);
        Assert.Equal(0.1f, sessionAgent1.Context.Temperature);

        // Assert — agent 2 sessions updated
        Assert.Equal("new-model-b", sessionAgent2a.Context.LlmModel);
        Assert.Equal(0.99f, sessionAgent2a.Context.Temperature);
        Assert.Equal(999, sessionAgent2a.Context.SoftCompactThreshold);
        Assert.Equal(1999, sessionAgent2a.Context.HardCompactThreshold);

        Assert.Equal("new-model-b", sessionAgent2b.Context.LlmModel);
        Assert.Equal(0.99f, sessionAgent2b.Context.Temperature);
        Assert.Equal(999, sessionAgent2b.Context.SoftCompactThreshold);
        Assert.Equal(1999, sessionAgent2b.Context.HardCompactThreshold);
    }

    private static AgentSessionState CreateSessionState(
        Guid sessionId, long agentId, string model, float temperature,
        long softThreshold, long hardThreshold)
    {
        var context = new AgentExecutionContext
        {
            SessionId = sessionId,
            AgentId = agentId,
            LlmModel = model,
            Temperature = temperature,
            SoftCompactThreshold = softThreshold,
            HardCompactThreshold = hardThreshold,
            Messages = [],
            ActiveWorkspaceNames = [],
        };

        return new AgentSessionState(sessionId, context);
    }
}
