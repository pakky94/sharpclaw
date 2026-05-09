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

        // await Task.Delay(TimeSpan.FromHours(1));

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
    public async Task RunCommandToolCall_RejectedApproval_ThenCompletesWithErrorResult()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.ToolCallSse("ws_run_command",
            """{"command":"echo test-from-tool"}""",
            c => c.Messages.Last().Content == "TEST_TOOL_RUN_COMMAND_REJECT");

        fixture.LlmServer?.TextSse("Tool invocation rejected.",
            c => c.Messages.Last().Role == "tool"
                 && (c.Messages.Last().Content?.Contains("rejected", StringComparison.OrdinalIgnoreCase) ?? false));

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_TOOL_RUN_COMMAND_REJECT");

        var token = await fixture.Api.WaitForPendingApprovalTokenAsync(sessionId, TimeSpan.FromSeconds(15));
        await fixture.Api.RejectAsync(sessionId, token);

        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result" &&
            content.GetProperty("result").GetString()!.Contains("rejected", StringComparison.OrdinalIgnoreCase));
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

    [Fact]
    public async Task ApprovalFromNestedChild_BubblesToAncestors_AndCanBeResolvedFromRootSession()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallSse("task",
            """
            {
              "description": "l1",
              "prompt": "NESTED_L1"
            }
            """,
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "TEST_NESTED_APPROVAL_BUBBLE");

        fixture.LlmServer?.ToolCallSse("task",
            """
            {
              "description": "l2",
              "prompt": "NESTED_L2"
            }
            """,
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "NESTED_L1");

        fixture.LlmServer?.ToolCallSse("ws_run_command",
            """{"command":"echo nested-approval"}""",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "NESTED_L2");

        fixture.LlmServer?.TextSse("L2 done",
            c => c.Messages.Last().Role == "tool");

        fixture.LlmServer?.TextSse("L1 done",
            c => c.Messages.Last().Role == "tool"
                 && (c.Messages.Last().Content?.Contains("L2 done", StringComparison.Ordinal) ?? false));

        fixture.LlmServer?.TextSse("Parent done",
            c => c.Messages.Last().Role == "tool"
                 && (c.Messages.Last().Content?.Contains("L1 done", StringComparison.Ordinal) ?? false));

        var rootSessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(rootSessionId, "TEST_NESTED_APPROVAL_BUBBLE");

        var events = await fixture.Api.WaitForStreamEventTypes(
            rootSessionId,
            messageId,
            requiredTypes: ["approval_required"],
            timeout: TimeSpan.FromSeconds(20));
        Assert.Contains(events, e => e.Type == "approval_required");

        using var pendingDoc = await fixture.Api.GetPendingApprovalsAsync(rootSessionId);
        var pendingApprovals = pendingDoc.RootElement.GetProperty("approvals").EnumerateArray().ToArray();
        Assert.NotEmpty(pendingApprovals);

        var token = pendingApprovals[0].GetProperty("approvalToken").GetString()
                    ?? throw new InvalidOperationException("Expected approval token.");
        var sourceSessionId = pendingApprovals[0].GetProperty("sessionId").GetGuid();
        Assert.NotEqual(rootSessionId, sourceSessionId);

        await fixture.Api.ApproveAsync(rootSessionId, token);
        var completedEvents = await fixture.Api.WaitForStreamCompleted(rootSessionId, messageId);
        Assert.Equal("completed", completedEvents[^1].Type);
    }

    [Fact]
    public async Task ResumeIfPossible_BlocksWhenPendingApprovalsExist()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.ToolCallSse("ws_run_command",
            """{"command":"echo blocked-by-approval"}""",
            c => c.Messages.Last().Content == "TEST_RESUME_BLOCKED_BY_APPROVAL");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_RESUME_BLOCKED_BY_APPROVAL");

        var token = await fixture.Api.WaitForPendingApprovalTokenAsync(sessionId, TimeSpan.FromSeconds(15));
        Assert.False(string.IsNullOrWhiteSpace(token));

        using var resume = await fixture.Api.ResumeIfPossibleAsync(sessionId);
        Assert.Equal(0, resume.RootElement.GetProperty("resumed").GetInt32());
        Assert.True(resume.RootElement.GetProperty("blockedByApprovals").GetInt32() >= 1);

        var events = await fixture.Api.WaitForStreamEventTypes(
            sessionId,
            messageId,
            requiredTypes: ["approval_required"],
            timeout: TimeSpan.FromSeconds(20));
        Assert.Contains(events, e => e.Type == "approval_required");
    }

    [Fact]
    public async Task StopSession_StopsDescendantChildSessions()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallSse("task",
            """
            {
              "description": "child-stop",
              "prompt": "STOP_CHILD_PROMPT"
            }
            """,
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "TEST_STOP_TREE");

        fixture.LlmServer?.ToolCallSse("ws_run_command",
            """{"command":"echo child-needs-approval"}""",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "STOP_CHILD_PROMPT");

        var rootSessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(rootSessionId, "TEST_STOP_TREE");

        var events = await fixture.Api.WaitForStreamEventTypes(
            rootSessionId,
            messageId,
            requiredTypes: ["child_session_spawned", "approval_required"],
            timeout: TimeSpan.FromSeconds(20));
        Assert.Contains(events, e => e.Type == "child_session_spawned");
        Assert.Contains(events, e => e.Type == "approval_required");

        using var rootHistory = await fixture.Api.GetHistoryAsync(rootSessionId);
        var childSessionId = rootHistory.RootElement.GetProperty("childSessions").EnumerateArray().First().GetProperty("childSessionId").GetGuid();

        using var stopResult = await fixture.Api.StopSessionAsync(rootSessionId, includeDescendants: true);
        Assert.True(stopResult.RootElement.GetProperty("stopped").GetInt32() >= 2);

        using var rootStoppedHistory = await fixture.Api.GetHistoryAsync(rootSessionId);
        using var childHistory = await fixture.Api.GetHistoryAsync(childSessionId);
        Assert.Equal("failed", rootStoppedHistory.RootElement.GetProperty("runStatus").GetString());
        Assert.Equal("failed", childHistory.RootElement.GetProperty("runStatus").GetString());
    }

    [Fact]
    public async Task StopSession_ThenResumeIfPossible_ResumesSuccessfully()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.TextSse("First response.", _ => true);

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "Hello");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        await fixture.Api.StopSessionAsync(sessionId, includeDescendants: false);

        using var stoppedHistory = await fixture.Api.GetHistoryAsync(sessionId);
        Assert.Equal("failed", stoppedHistory.RootElement.GetProperty("runStatus").GetString());

        // Resume — should succeed
        using var resume = await fixture.Api.ResumeIfPossibleAsync(sessionId, includeDescendants: false);
        Assert.Equal(1, resume.RootElement.GetProperty("resumed").GetInt32());
    }

    [Fact]
    public async Task StopSession_WhileWaitingForApproval_ThenResumeIfPossible_ResumesSuccessfully()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer!.ToolCallSse("ws_run_command",
            """{"command":"echo stop-resume-test"}""",
            c => c.Messages.Last().Role == "user" && c.Messages.Last().Content == "TEST_STOP_RESUME_APPROVAL");

        fixture.LlmServer?.TextSse("After approval response.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_STOP_RESUME_APPROVAL");

        var token = await fixture.Api.WaitForPendingApprovalTokenAsync(sessionId, TimeSpan.FromSeconds(15));
        Assert.False(string.IsNullOrWhiteSpace(token));

        // Stop while waiting for approval
        await fixture.Api.StopSessionAsync(sessionId, includeDescendants: false);

        // Resume — should succeed even with pending approvals (user explicitly requested resume)
        using var resume = await fixture.Api.ResumeIfPossibleAsync(sessionId, includeDescendants: false);
        Assert.Equal(1, resume.RootElement.GetProperty("resumed").GetInt32());
    }

    [Fact]
    public async Task ListScheduledJobsTool_IsInvoked_AndReturnsJobs()
    {
        await fixture.ResetStateAsync();

        // Pre-create a job so the list returns something
        await fixture.Api.CreateScheduledJobAsync(
            name: "Existing Job", cronExpression: "0 8 * * *", prompt: "test", agentId: 1);

        fixture.LlmServer?.ToolCallSse("list_scheduled_jobs",
            "{}",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "TEST_LIST_SCHEDULED_JOBS");

        fixture.LlmServer?.TextSse("Here are your scheduled jobs.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_LIST_SCHEDULED_JOBS");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "list_scheduled_jobs");

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result");

        Assert.Contains(ResponseHelpers.GetMessageTexts(history),
            text => text.Contains("Here are your scheduled jobs.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateScheduledJobTool_IsInvoked_AndPersistsJob()
    {
        await fixture.ResetStateAsync();

        fixture.LlmServer?.ToolCallSse("create_scheduled_job",
            """{"name":"Agent Job","cron_expression":"30 9 * * 1-5","prompt":"Daily standup summary"}""",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "TEST_CREATE_SCHEDULED_JOB");

        fixture.LlmServer?.TextSse("Job created successfully.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_CREATE_SCHEDULED_JOB");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "create_scheduled_job");

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_result");

        // Verify the job was actually persisted
        using var jobs = await fixture.Api.ListScheduledJobsAsync();
        var jobList = jobs.RootElement.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.Contains(jobList, j =>
            j.GetProperty("name").GetString() == "Agent Job" &&
            j.GetProperty("cronExpression").GetString() == "30 9 * * 1-5");
    }

    [Fact]
    public async Task UpdateScheduledJobTool_IsInvoked_AndUpdatesJob()
    {
        await fixture.ResetStateAsync();

        // Pre-create a job to update
        using var created = await fixture.Api.CreateScheduledJobAsync(
            name: "Update Me", cronExpression: "0 8 * * *", prompt: "original", agentId: 1);
        var jobId = created.RootElement.GetProperty("id").GetInt64();

        fixture.LlmServer?.ToolCallSse("update_scheduled_job",
            $$"""{"id":{{jobId}},"name":"Updated Name","enabled":false}""",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "TEST_UPDATE_SCHEDULED_JOB");

        fixture.LlmServer?.TextSse("Job updated successfully.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_UPDATE_SCHEDULED_JOB");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "update_scheduled_job");

        // Verify the job was actually updated
        using var jobs = await fixture.Api.ListScheduledJobsAsync();
        var jobList = jobs.RootElement.GetProperty("jobs").EnumerateArray().ToArray();
        var updated = jobList.Single(j => j.GetProperty("id").GetInt64() == jobId);
        Assert.Equal("Updated Name", updated.GetProperty("name").GetString());
        Assert.False(updated.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task DeleteScheduledJobTool_IsInvoked_AndRemovesJob()
    {
        await fixture.ResetStateAsync();

        // Pre-create a job to delete
        using var created = await fixture.Api.CreateScheduledJobAsync(
            name: "Delete Me", cronExpression: "0 8 * * *", prompt: "test", agentId: 1);
        var jobId = created.RootElement.GetProperty("id").GetInt64();

        fixture.LlmServer?.ToolCallSse("delete_scheduled_job",
            $$"""{"id":{{jobId}}}""",
            c => c.Messages.Last().Role == "user"
                 && c.Messages.Last().Content == "TEST_DELETE_SCHEDULED_JOB");

        fixture.LlmServer?.TextSse("Job deleted successfully.",
            c => c.Messages.Last().Role == "tool");

        var sessionId = await fixture.Api.CreateSessionAsync();
        var messageId = await fixture.Api.EnqueueMessageAsync(sessionId, "TEST_DELETE_SCHEDULED_JOB");
        await fixture.Api.WaitForStreamCompleted(sessionId, messageId);

        using var history = await fixture.Api.GetHistoryAsync(sessionId);
        var messageContents = ResponseHelpers.FlattenMessageContents(history);

        Assert.Contains(messageContents, content =>
            content.GetProperty("type").GetString() == "tool_call" &&
            content.GetProperty("toolName").GetString() == "delete_scheduled_job");

        // Verify the job was actually deleted
        using var jobs = await fixture.Api.ListScheduledJobsAsync();
        var jobList = jobs.RootElement.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.DoesNotContain(jobList, j => j.GetProperty("id").GetInt64() == jobId);
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
