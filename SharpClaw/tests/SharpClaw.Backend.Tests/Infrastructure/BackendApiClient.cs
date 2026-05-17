using System.Net.Http.Json;
using System.Text.Json;

namespace SharpClaw.Backend.Tests.Infrastructure;

public sealed class BackendApiClient(HttpClient client)
{
    public async Task ResetConversationStateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await WaitForSchemaAsync(connection, cancellationToken);

        const string sql =
            """
            truncate table
                conversation_history,
                messages,
                summaries,
                workspace_approval_events,
                lcm_files,
                session_active_workspaces,
                channel_sessions,
                channels,
                scheduled_jobs,
                secrets,
                sessions
            restart identity cascade;
            """;

        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WaitForSchemaAsync(Npgsql.NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var checkCommand = new Npgsql.NpgsqlCommand(
                "select to_regclass('public.conversation_history') is not null;",
                connection);

            var result = await checkCommand.ExecuteScalarAsync(cancellationToken);
            if (result is true)
                return;

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("Database schema was not initialized in time.");
    }

    public async Task<JsonDocument> GetAgentsAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync("/agents", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> GetAgentAsync(long agentId, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync($"/agents/{agentId}", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> CreateAgentAsync(
        string name,
        string? llmModel = null,
        float? temperature = null,
        long? softCompactThreshold = null,
        long? hardCompactThreshold = null,
        CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsJsonAsync("/agents", new
        {
            name,
            llmModel,
            temperature,
            softCompactThreshold,
            hardCompactThreshold,
        }, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> UpdateAgentAsync(
        long agentId,
        string name,
        string? llmModel = null,
        float? temperature = null,
        long? softCompactThreshold = null,
        long? hardCompactThreshold = null,
        CancellationToken cancellationToken = default)
    {
        var response = await client.PutAsJsonAsync($"/agents/{agentId}", new
        {
            name,
            llmModel,
            temperature,
            softCompactThreshold,
            hardCompactThreshold,
        }, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<Guid> CreateSessionAsync(long? agentId = null, string? name = null, string? tag = null, CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsJsonAsync("/sessions", new { agentId, name, tag }, cancellationToken);
        using var payload = await ReadDocumentAsync(response, cancellationToken);
        return payload.RootElement.GetProperty("sessionId").GetGuid();
    }

    public async Task RenameSessionAsync(Guid sessionId, string name, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/sessions/{sessionId}")
        {
            Content = JsonContent.Create(new { name }),
        };
        var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task SetSessionTagAsync(Guid sessionId, string? tag, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/sessions/{sessionId}")
        {
            Content = JsonContent.Create(new { tag }),
        };
        var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<long> EnqueueMessageAsync(Guid sessionId, string message, CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsJsonAsync($"/sessions/{sessionId}/messages", new { message }, cancellationToken);
        using var payload = await ReadDocumentAsync(response, cancellationToken);
        return payload.RootElement.GetProperty("latestSequenceId").GetInt64();
    }

    public async Task<JsonDocument> GetRunAsync(Guid sessionId, Guid runId, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync($"/sessions/{sessionId}/runs/{runId}", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> GetHistoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync($"/sessions/{sessionId}/history", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> GetSessionsAsync(long agentId = 1, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync($"/agents/{agentId}/sessions", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> GetPendingApprovalsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync($"/sessions/{sessionId}/approvals/pending", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> DebugToolCallAsync(
        string toolName,
        object? arguments = null,
        long? agentId = null,
        string? callId = null,
        string? workspaceName = null,
        bool unrestrictedWorkspace = true,
        CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsJsonAsync("/debugging/tool-call", new
        {
            toolName,
            arguments = arguments ?? new { },
            agentId,
            callId,
            workspaceName,
            unrestrictedWorkspace,
        }, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<string> WaitForPendingApprovalTokenAsync(
        Guid sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        while (true)
        {
            using var pending = await GetPendingApprovalsAsync(sessionId, cancellationToken);
            var approvals = pending.RootElement.GetProperty("approvals").EnumerateArray().ToArray();
            if (approvals.Length > 0)
                return approvals[0].GetProperty("approvalToken").GetString()
                       ?? throw new InvalidOperationException("Pending approval token was null.");

            if (DateTimeOffset.UtcNow - start > timeout)
                throw new TimeoutException($"No pending approvals appeared for session {sessionId}.");

            await Task.Delay(100, cancellationToken);
        }
    }

    public async Task ApproveAsync(Guid sessionId, string token, CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsync($"/sessions/{sessionId}/approvals/{token}/approve", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task RejectAsync(Guid sessionId, string token, CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsync($"/sessions/{sessionId}/approvals/{token}/reject", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JsonDocument> ResumeIfPossibleAsync(Guid sessionId, bool includeDescendants = true, CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsync($"/sessions/{sessionId}/resume-if-possible?includeDescendants={includeDescendants.ToString().ToLowerInvariant()}", content: null, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> StopSessionAsync(Guid sessionId, bool includeDescendants = true, CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsync($"/sessions/{sessionId}/stop?includeDescendants={includeDescendants.ToString().ToLowerInvariant()}", content: null, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> ListScheduledJobsAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync("/jobs", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> CreateScheduledJobAsync(
        string name,
        string cronExpression,
        string prompt,
        long agentId,
        string? timezone = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsJsonAsync("/jobs", new
        {
            name,
            cronExpression,
            prompt,
            agentId,
            timezone,
            enabled,
        }, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> UpdateScheduledJobAsync(
        long id,
        string? name = null,
        string? cronExpression = null,
        string? prompt = null,
        long? agentId = null,
        string? timezone = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/jobs/{id}")
        {
            Content = JsonContent.Create(new
            {
                name,
                cronExpression,
                prompt,
                agentId,
                timezone,
                enabled,
            }),
        };
        var response = await client.SendAsync(request, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> DeleteScheduledJobAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await client.DeleteAsync($"/jobs/{id}", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> ListChannelsAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync("/channels", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> CreateChannelAsync(
        string name,
        string type,
        long agentId,
        string? routingMode = null,
        string? config = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsJsonAsync("/channels", new
        {
            name,
            type,
            agentId,
            routingMode,
            config,
            enabled,
        }, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> UpdateChannelAsync(
        long id,
        string? name = null,
        string? type = null,
        long? agentId = null,
        string? routingMode = null,
        string? config = null,
        bool? enabled = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/channels/{id}")
        {
            Content = JsonContent.Create(new
            {
                name,
                type,
                agentId,
                routingMode,
                config,
                enabled,
            }),
        };
        var response = await client.SendAsync(request, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> DeleteChannelAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await client.DeleteAsync($"/channels/{id}", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> ListSecretsAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync("/secrets", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> CreateSecretAsync(
        string name,
        string value,
        string scope = "global",
        long? ownerId = null,
        bool allowBridge = false,
        CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsJsonAsync("/secrets", new
        {
            name,
            value,
            scope,
            ownerId,
            allowBridge,
        }, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> UpdateSecretAsync(
        long id,
        string? value = null,
        string? scope = null,
        long? ownerId = null,
        bool? allowBridge = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/secrets/{id}")
        {
            Content = JsonContent.Create(new
            {
                value,
                scope,
                ownerId,
                allowBridge,
            }),
        };
        var response = await client.SendAsync(request, cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> DeleteSecretAsync(long id, CancellationToken cancellationToken = default)
    {
        var response = await client.DeleteAsync($"/secrets/{id}", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<StreamEvent>> WaitForStreamCompleted(
        Guid sessionId,
        long messageId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => WaitForStreamEventTypes(sessionId, messageId, ["completed"],
            timeout ?? TimeSpan.FromSeconds(30),
            cancellationToken);

    public async Task<IReadOnlyList<StreamEvent>> WaitForStreamEventTypes(
        Guid sessionId,
        long messageId,
        IReadOnlyCollection<string> requiredTypes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{sessionId}/messages/{messageId}/stream");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var reader = new StreamReader(stream);
        var seen = new List<StreamEvent>();

        while (!timeoutCts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (line is null)
                break;

            if (!line.StartsWith("event: ", StringComparison.Ordinal))
                continue;

            var eventType = line["event: ".Length..].Trim();
            var payload = await reader.ReadLineAsync(timeoutCts.Token);
            seen.Add(new StreamEvent
            {
                Type = eventType,
                Payload = payload ?? string.Empty,
            });

            if (eventType == "completed" || requiredTypes.All(r => seen.Any(s => s.Type == r)))
                return seen;
        }

        throw new TimeoutException("Did not receive 'completed' stream events in time.");
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}

public class StreamEvent
{
    public required string Type { get; set; }
    public required string Payload { get; set; }
}
