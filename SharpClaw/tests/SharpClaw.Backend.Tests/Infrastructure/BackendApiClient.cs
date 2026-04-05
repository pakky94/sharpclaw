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

    public async Task<Guid> CreateSessionAsync(long? agentId = null, CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsJsonAsync("/sessions", new { agentId }, cancellationToken);
        using var payload = await ReadDocumentAsync(response, cancellationToken);
        return payload.RootElement.GetProperty("sessionId").GetGuid();
    }

    public async Task<Guid> EnqueueMessageAsync(Guid sessionId, string message, CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsJsonAsync($"/sessions/{sessionId}/messages", new { message }, cancellationToken);
        using var payload = await ReadDocumentAsync(response, cancellationToken);
        return payload.RootElement.GetProperty("runId").GetGuid();
    }

    public async Task<JsonDocument> GetRunAsync(Guid sessionId, Guid runId, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync($"/sessions/{sessionId}/runs/{runId}", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> WaitForRunTerminalStateAsync(
        Guid sessionId,
        Guid runId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var start = DateTimeOffset.UtcNow;
        while (true)
        {
            var run = await GetRunAsync(sessionId, runId, cancellationToken);
            var status = run.RootElement.GetProperty("status").GetString();

            if (status is "completed" or "failed")
                return run;

            if (DateTimeOffset.UtcNow - start > timeout)
            {
                run.Dispose();
                throw new TimeoutException($"Run {runId} in session {sessionId} did not reach a terminal status.");
            }

            run.Dispose();
            await Task.Delay(100, cancellationToken);
        }
    }

    public async Task<JsonDocument> GetHistoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync($"/sessions/{sessionId}/history", cancellationToken);
        return await ReadDocumentAsync(response, cancellationToken);
    }

    public async Task<JsonDocument> GetPendingApprovalsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync($"/sessions/{sessionId}/approvals/pending", cancellationToken);
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

    public async Task<IReadOnlyList<string>> WaitForStreamEventTypesAsync(
        Guid sessionId,
        Guid runId,
        IReadOnlyCollection<string> requiredTypes,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{sessionId}/runs/{runId}/stream");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var reader = new StreamReader(stream);
        var seen = new List<string>();

        while (!timeoutCts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(timeoutCts.Token);
            if (line is null)
                break;

            if (!line.StartsWith("event: ", StringComparison.Ordinal))
                continue;

            var eventType = line["event: ".Length..].Trim();
            seen.Add(eventType);

            if (requiredTypes.All(type => seen.Contains(type, StringComparer.Ordinal)))
                return seen;
        }

        throw new TimeoutException(
            $"Did not receive required stream events in time. Required: {string.Join(", ", requiredTypes)}. Seen: {string.Join(", ", seen)}");
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
