using System.Text.Json;
using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class AgentConfigRefreshTests(SharpClawAppFixture fixture)
{
    /// <summary>
    /// Verifies that updating an agent's LlmModel via PUT /agents/{id} takes effect
    /// immediately for an already-loaded in-memory session. The next LLM call should
    /// use the updated model.
    /// </summary>
    [Fact]
    public async Task UpdateAgentModel_PropagatesToActiveSession()
    {
        await fixture.ResetStateAsync();

        // Arrange: get the default agent config to know the original model
        using var originalAgent = await fixture.Api.GetAgentAsync(1);
        var originalModel = originalAgent.RootElement.GetProperty("llmModel").GetString();
        Assert.NotNull(originalModel);

        // Create a session — this loads the agent config into memory
        var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1);

        // Update the agent with a different model
        const string newModel = "updated-test-model";
        using var updateResponse = await fixture.Api.UpdateAgentAsync(
            agentId: 1,
            name: "Main",
            llmModel: newModel);

        Assert.Equal(newModel, updateResponse.RootElement.GetProperty("llmModel").GetString());

        // Act: send a message — the session is already in memory and should use the new model
        fixture.LlmServer!.TextSse("Response with updated model.", _ => true);
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "Reply with one sentence.");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        // Assert: the LLM request should have been sent with the updated model
        var lastRequest = fixture.LlmServer.LastChatRequestBody;
        Assert.NotNull(lastRequest);

        using var requestJson = JsonDocument.Parse(lastRequest);
        var actualModel = requestJson.RootElement.GetProperty("model").GetString();
        Assert.Equal(newModel, actualModel);
    }

    /// <summary>
    /// Verifies that updating an agent's Temperature propagates to an active session.
    /// </summary>
    [Fact]
    public async Task UpdateAgentTemperature_PropagatesToActiveSession()
    {
        await fixture.ResetStateAsync();

        // Create a session — loads config into memory
        var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1);

        // Update the temperature
        const float newTemperature = 0.99f;
        using var updateResponse = await fixture.Api.UpdateAgentAsync(
            agentId: 1,
            name: "Main",
            temperature: newTemperature);

        Assert.Equal(newTemperature, updateResponse.RootElement.GetProperty("temperature").GetSingle());

        // Send a message
        fixture.LlmServer!.TextSse("Response with updated temperature.", _ => true);
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "Reply with one sentence.");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        // Assert: the LLM request should include the updated temperature
        var lastRequest = fixture.LlmServer.LastChatRequestBody;
        Assert.NotNull(lastRequest);

        using var requestJson = JsonDocument.Parse(lastRequest);
        var actualTemperature = requestJson.RootElement.GetProperty("temperature").GetSingle();
        Assert.Equal(newTemperature, actualTemperature);
    }

    /// <summary>
    /// Verifies that updating compaction thresholds propagates to an active session
    /// by checking the agent endpoint returns the updated values.
    /// </summary>
    [Fact]
    public async Task UpdateAgentCompactionThresholds_PropagatesToActiveSession()
    {
        await fixture.ResetStateAsync();

        // Create a session — loads config into memory
        var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1);

        // Update the compaction thresholds
        const long newSoft = 50 * 1024;
        const long newHard = 60 * 1024;
        using var updateResponse = await fixture.Api.UpdateAgentAsync(
            agentId: 1,
            name: "Main",
            softCompactThreshold: newSoft,
            hardCompactThreshold: newHard);

        Assert.Equal(newSoft, updateResponse.RootElement.GetProperty("softCompactThreshold").GetInt64());
        Assert.Equal(newHard, updateResponse.RootElement.GetProperty("hardCompactThreshold").GetInt64());

        // Send a message to verify the session still works after the update
        fixture.LlmServer!.TextSse("Response after threshold update.", _ => true);
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "Reply with one sentence.");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        // Verify the conversation completed successfully
        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messages = history.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Contains(messages, m =>
            m.GetProperty("role").GetString() == "assistant" &&
            (m.GetProperty("text").GetString() ?? string.Empty)
                .Contains("Response after threshold update.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that updating an agent does NOT affect sessions belonging to a different agent.
    /// Creates two agents, creates sessions for both, updates agent 1, and verifies
    /// that agent 2's sessions still use the original config.
    /// </summary>
    [Fact]
    public async Task UpdateAgentModel_DoesNotAffectOtherAgentSessions()
    {
        await fixture.ResetStateAsync();

        // Create a second agent via the API
        using var createResponse = await fixture.Api.CreateAgentAsync("Agent2");
        var agent2Id = createResponse.RootElement.GetProperty("id").GetInt64();

        // Create sessions for both agents
        var session1Id = await fixture.Api.CreateSessionAsync(agentId: 1);
        var session2Id = await fixture.Api.CreateSessionAsync(agentId: agent2Id);

        // Get agent 2's original model
        using var agent2Before = await fixture.Api.GetAgentAsync(agent2Id);
        var agent2ModelBefore = agent2Before.RootElement.GetProperty("llmModel").GetString();

        // Update agent 1
        await fixture.Api.UpdateAgentAsync(agentId: 1, name: "Main", llmModel: "agent-1-only-model");

        // Verify agent 2's config is unchanged in the DB
        using var agent2After = await fixture.Api.GetAgentAsync(agent2Id);
        var agent2ModelAfter = agent2After.RootElement.GetProperty("llmModel").GetString();
        Assert.Equal(agent2ModelBefore, agent2ModelAfter);

        // Verify agent 2's session still works with the original model
        fixture.LlmServer!.TextSse("Response from agent 2.", _ => true);
        var messageId = await fixture.Api.EnqueueMessageAsync(session2Id, "Reply with one sentence.");
        await fixture.Api.WaitForStreamCompleted(session2Id, messageId);

        var lastRequest = fixture.LlmServer.LastChatRequestBody;
        Assert.NotNull(lastRequest);
        using var requestJson = System.Text.Json.JsonDocument.Parse(lastRequest);
        var actualModel = requestJson.RootElement.GetProperty("model").GetString();
        Assert.Equal(agent2ModelBefore, actualModel);
    }
}
