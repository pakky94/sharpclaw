using SharpClaw.Backend.Tests.Helpers;
using SharpClaw.Backend.Tests.Infrastructure;
using System.Text.Json;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class ToolInvocationTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task ListFilesToolCall_IsInvoked_AndPersistedInHistory()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallSse("ws_list_files",
            """{"path":".","recursive":false}""",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "TEST_TOOL_LIST_FILES");

        fixture.LlmServer?.TextSse("Tool invocation finished.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_TOOL_LIST_FILES");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "ws_list_files");

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result");

        Assert.Contains(ResponseHelpers.GetMessageTexts(history),
            text => text.Contains("Tool invocation finished.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCommandToolCall_RequiresApproval_ThenCompletes()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.ToolCallSse("ws_run_command",
            """{"command":"echo test-from-tool"}""",
            c => c.Messages.Last().Content == "TEST_TOOL_RUN_COMMAND");

        fixture.LlmServer?.TextSse("Tool invocation finished.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId,  "TEST_TOOL_RUN_COMMAND");

        var token = await fixture.Api.WaitForPendingApprovalTokenAsync(sessionId, TimeSpan.FromSeconds(15));
        await fixture.Api.ApproveAsync(sessionId, token);

        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "ws_run_command");

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result");
    }

    [Fact]
    public async Task Stream_EmitsToolCall_BeforeApprovalRequired()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.ToolCallSse("ws_write_file",
            """{"path":"tmp/sse-approval-check.txt","content":"from-test","mode":"overwrite"}""",
            c => c.Messages.Last().Content == "TEST_TOOL_WRITE_FILE");

        fixture.LlmServer?.TextSse("Tool invocation finished.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var runId = await fixture.Api.EnqueueMessageAsync(sessionId,  "TEST_TOOL_WRITE_FILE");

        var events = await fixture.Api.WaitForStreamEventTypes(
            sessionId,
            runId,
            requiredTypes: ["tool_call", "approval_required"],
            timeout: TimeSpan.FromSeconds(20));

        var toolCallIndex = ResponseHelpers.IndexOf(events, "tool_call");
        var approvalIndex = ResponseHelpers.IndexOf(events, "approval_required");

        Assert.True(toolCallIndex >= 0, "tool_call event was not observed.");
        Assert.True(approvalIndex >= 0, "approval_required event was not observed.");
        Assert.True(toolCallIndex < approvalIndex, $"Expected tool_call before approval_required. Seen: {string.Join(", ", events)}");
        Assert.True(events.All(e => e.Type != "completed"));

        var token = await fixture.Api.WaitForPendingApprovalTokenAsync(sessionId, TimeSpan.FromSeconds(15));
        await fixture.Api.ApproveAsync(sessionId, token);

        events = await fixture.Api.WaitForStreamCompleted(sessionId, runId);
        Assert.Equal("completed", events[^1].Type);
    }

    [Fact]
    public async Task MultipleCallInvocations()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallsSse(["ws_list_files", "ws_list_files"],
            ["""{"path":".","recursive":false}""", """{"path":".","recursive":false}"""],
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "TEST_TOOL_LIST_FILES");

        fixture.LlmServer?.TextSse("Tool invocation finished.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_TOOL_LIST_FILES");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "ws_list_files");

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result");

        Assert.Contains(ResponseHelpers.GetMessageTexts(history),
            text => text.Contains("Tool invocation finished.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultipleCallInvocations_TaskTool()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallsSse(["task", "task"],
            [
                """
                {
                  "description": "task1",
                  "prompt": "task prompt 1"
                }
                """,
                """
                {
                  "description": "task2",
                  "prompt": "task prompt 2"
                }
                """,
            ],
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "TEST_TOOL_LIST_FILES");

        fixture.LlmServer?.TextSse("task result 1",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "task prompt 1");

        fixture.LlmServer?.TextSse("task result 2",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content ==  "task prompt 2");

        fixture.LlmServer?.TextSse("result after both tasks",
                c => c.Messages.Last().Role == "tool"
                     && (c.Messages[^2].Content?.Contains("task result 1") ?? false)
                     && (c.Messages.Last().Content?.Contains("task result 2") ?? false));

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_TOOL_LIST_FILES");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        // Assert.Contains(messageContents, content =>
        //     content.GetProperty("type").GetString() == "tool_call" &&
        //     content.GetProperty("toolName").GetString() == "ws_list_files");
        //
        // Assert.Contains(messageContents, content =>
        //     content.GetProperty("type").GetString() == "tool_result");

        Assert.Contains(ResponseHelpers.GetMessageTexts(history),
            text => text.Contains("result after both tasks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TaskTool_EmitsParentToolResultUpdate_WhenChildCompletes()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallSse("task",
            """
            {
              "description": "task1",
              "prompt": "task prompt 1"
            }
            """,
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "TEST_TASK_CHILD_COMPLETION");

        fixture.LlmServer?.TextSse("task result 1",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "task prompt 1");

        fixture.LlmServer?.TextSse("result after child task",
            c => c.Messages.Last().Role == "tool"
                 && (c.Messages.Last().Content?.Contains("task result 1", StringComparison.Ordinal) ?? false));

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_TASK_CHILD_COMPLETION");
        var events = await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        Assert.Contains(events, e => e.Type == "child_session_spawned");

        var taskResults = events
            .Where(e => e.Type == "tool_result")
            .Select(TryReadTaskResultPayload)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .ToArray();

        Assert.Contains(taskResults, r =>
            string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase)
            && r.Output is not null
            && r.Output.Contains("task result 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TaskTool_PersistsParentChildSessionLinks_InHistory()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallSse("task",
            """
            {
              "description": "task1",
              "prompt": "task prompt 1"
            }
            """,
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "TEST_TASK_HISTORY_LINKS");

        fixture.LlmServer?.TextSse("task result 1",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "task prompt 1");

        fixture.LlmServer?.TextSse("result after child task",
            c => c.Messages.Last().Role == "tool"
                 && (c.Messages.Last().Content?.Contains("task result 1", StringComparison.Ordinal) ?? false));

        var parentSessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(parentSessionId, "TEST_TASK_HISTORY_LINKS");
        await fixture.Api.WaitForStreamCompleted(parentSessionId, messageId);

        using var parentHistory = await fixture.Api.GetHistoryAsync(parentSessionId);
        var childLinks = parentHistory.RootElement.GetProperty("childSessions").EnumerateArray().ToArray();
        Assert.NotEmpty(childLinks);

        var childSessionId = childLinks[0].GetProperty("childSessionId").GetGuid();
        Assert.True(childLinks[0].GetProperty("completed").GetBoolean());

        using var childHistory = await fixture.Api.GetHistoryAsync(childSessionId);
        var childParent = childHistory.RootElement.GetProperty("parentSessionId").GetGuid();
        Assert.Equal(parentSessionId, childParent);

        using var sessions = await fixture.Api.GetSessionsAsync();
        var sessionRows = sessions.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
        var childRow = sessionRows.Single(x => x.GetProperty("sessionId").GetGuid() == childSessionId);
        var listedParent = childRow.GetProperty("parentSessionId").GetGuid();
        Assert.Equal(parentSessionId, listedParent);
    }

    private static (string Status, string? Output)? TryReadTaskResultPayload(StreamEvent ev)
    {
        if (string.IsNullOrWhiteSpace(ev.Payload) || !ev.Payload.StartsWith("data: ", StringComparison.Ordinal))
            return null;

        var json = ev.Payload["data: ".Length..];
        using var payloadDoc = JsonDocument.Parse(json);
        if (!payloadDoc.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
            return null;

        if (!data.TryGetProperty("result", out var resultElement)
            || resultElement.ValueKind != JsonValueKind.String)
            return null;

        var resultText = resultElement.GetString();
        if (string.IsNullOrWhiteSpace(resultText))
            return null;

        using var resultDoc = JsonDocument.Parse(resultText);
        if (resultDoc.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        if (!resultDoc.RootElement.TryGetProperty("type", out var typeElement)
            || typeElement.GetString() != "child_session")
            return null;

        var status = resultDoc.RootElement.TryGetProperty("status", out var statusElement)
            ? statusElement.GetString() ?? string.Empty
            : string.Empty;
        var output = resultDoc.RootElement.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.String
            ? outputElement.GetString()
            : null;

        return (status, output);
    }
}
