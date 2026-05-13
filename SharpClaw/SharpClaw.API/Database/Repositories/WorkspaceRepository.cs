using Dapper;
using Npgsql;
using SharpClaw.API.Agents.Workspace;

namespace SharpClaw.API.Database.Repositories;

public class WorkspaceRepository(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("sharpclaw")!;

    public async Task<Workspace?> GetWorkspaceById(long id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleOrDefaultAsync<WorkspaceRow>(
            "select * from workspaces where id = @id",
            new { id });
        return result?.ToModel();
    }

    public async Task<IReadOnlyList<Workspace>> GetAllWorkspaces()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var results = await connection.QueryAsync<WorkspaceRow>(
            "select * from workspaces order by name");
        return results.Select(r => r.ToModel()).ToArray();
    }

    public async Task<Workspace> UpsertWorkspace(string name, string rootPath, string[] allowlistPatterns, string[] denylistPatterns)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleAsync<WorkspaceRow>(
            """
            insert into workspaces (name, root_path, allowlist_patterns, denylist_patterns)
            values (@name, @rootPath, @allowlist::jsonb, @denylist::jsonb)
            on conflict (name) do update set
                root_path = excluded.root_path,
                allowlist_patterns = excluded.allowlist_patterns,
                denylist_patterns = excluded.denylist_patterns,
                updated_at = now()
            returning *;
            """,
            new
            {
                name,
                rootPath,
                allowlist = System.Text.Json.JsonSerializer.Serialize(allowlistPatterns),
                denylist = System.Text.Json.JsonSerializer.Serialize(denylistPatterns),
            });
        return result.ToModel();
    }

    public async Task<bool> DeleteWorkspace(long id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var deleted = await connection.ExecuteAsync(
            "delete from workspaces where id = @id",
            new { id });
        return deleted > 0;
    }

    public async Task<bool> UpdateWorkspaceRuntime(long id, WorkspaceRuntimeKind runtimeKind, string? runtimeTarget)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var updated = await connection.ExecuteAsync(
            """
            update workspaces
            set runtime_kind = @runtimeKind, runtime_target = @runtimeTarget, updated_at = now()
            where id = @id
            """,
            new
            {
                id,
                runtimeKind = runtimeKind.ToString().ToLowerInvariant(),
                runtimeTarget,
            });
        return updated > 0;
    }

    public async Task UpsertBridgeClient(string bridgeId, string displayName, string status, bool isDevContainer = false, string? containerId = null, string? workspacePathInContainer = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            insert into bridge_clients (bridge_id, display_name, status, last_seen_at, is_devcontainer, container_id, workspace_path_in_container)
            values (@bridgeId, @displayName, @status, now(), @isDevContainer, @containerId, @workspacePathInContainer)
            on conflict (bridge_id) do update set
                display_name = excluded.display_name,
                status = excluded.status,
                last_seen_at = now(),
                updated_at = now(),
                is_devcontainer = excluded.is_devcontainer,
                container_id = excluded.container_id,
                workspace_path_in_container = excluded.workspace_path_in_container
            """,
            new { bridgeId, displayName, status, isDevContainer, containerId, workspacePathInContainer });
    }

    public async Task UpdateBridgeStatus(string bridgeId, string status)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            update bridge_clients
            set status = @status, last_seen_at = now(), updated_at = now()
            where bridge_id = @bridgeId
            """,
            new { bridgeId, status });
    }

    public async Task<IReadOnlyList<BridgeClientInfo>> GetBridgeClients()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var results = await connection.QueryAsync(
            "select bridge_id, display_name, status, last_seen_at, created_at from bridge_clients order by created_at desc");
        return results.Select(r => new BridgeClientInfo
        {
            BridgeId = r.bridge_id,
            DisplayName = r.display_name,
            Status = r.status,
            LastSeenAt = r.last_seen_at,
            CreatedAt = r.created_at,
        }).ToArray();
    }

    public async Task<AgentWorkspaceAssignment?> GetAssignment(long agentId, long workspaceId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleOrDefaultAsync<AssignmentRow>(
            "select * from agent_workspace_assignments where agent_id = @agentId and workspace_id = @workspaceId",
            new { agentId, workspaceId });
        return result?.ToModel();
    }

    public async Task<AgentWorkspaceAssignment?> GetDefaultAssignment(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleOrDefaultAsync<AssignmentRow>(
            "select * from agent_workspace_assignments where agent_id = @agentId and is_default = true",
            new { agentId });
        return result?.ToModel();
    }

    public async Task<IReadOnlyList<AgentWorkspaceAssignment>> GetAssignmentsForAgent(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var results = await connection.QueryAsync<AssignmentRow>(
            "select * from agent_workspace_assignments where agent_id = @agentId order by is_default desc, updated_at desc",
            new { agentId });
        return results.Select(r => r.ToModel()).ToArray();
    }

    public async Task<ResolvedWorkspace?> ResolveDefaultWorkspace(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleOrDefaultAsync<ResolvedWorkspaceRow>(
            """
            select w.*, a.policy_mode, a.is_default
            from agent_workspace_assignments a
            join workspaces w on w.id = a.workspace_id
            where a.agent_id = @agentId and a.is_default = true;
            """,
            new { agentId });
        return result?.ToModel();
    }

    public async Task<ResolvedWorkspace?> ResolveWorkspaceByName(long agentId, string name)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleOrDefaultAsync<ResolvedWorkspaceRow>(
            """
            select w.*, a.policy_mode, a.is_default
            from agent_workspace_assignments a
            join workspaces w on w.id = a.workspace_id
            where a.agent_id = @agentId and w.name = @name;
            """,
            new { agentId, name });
        return result?.ToModel();
    }

    public async Task<AgentWorkspaceAssignment> UpsertAssignment(long agentId, long workspaceId, WorkspacePolicyMode policyMode, bool isDefault)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        if (isDefault)
        {
            await connection.ExecuteAsync(
                "update agent_workspace_assignments set is_default = false where agent_id = @agentId and is_default = true",
                new { agentId });
        }

        var result = await connection.QuerySingleAsync<AssignmentRow>(
            """
            insert into agent_workspace_assignments (agent_id, workspace_id, policy_mode, is_default)
            values (@agentId, @workspaceId, @policyMode, @isDefault)
            on conflict (agent_id, workspace_id) do update set
                policy_mode = excluded.policy_mode,
                is_default = excluded.is_default,
                updated_at = now()
            returning *;
            """,
            new
            {
                agentId,
                workspaceId,
                policyMode = PolicyModeToDbString(policyMode),
                isDefault,
            });
        return result.ToModel();
    }

    public async Task<bool> DeleteAssignment(long agentId, long workspaceId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var deleted = await connection.ExecuteAsync(
            "delete from agent_workspace_assignments where agent_id = @agentId and workspace_id = @workspaceId",
            new { agentId, workspaceId });
        return deleted > 0;
    }

    public async Task<WorkspaceApprovalEvent?> CreateApprovalEvent(
        Guid sessionId,
        long agentId,
        string approvalToken,
        ApprovalActionType actionType,
        string? targetPath,
        string? commandPreview,
        ApprovalRiskLevel riskLevel,
        string? description = null,
        string? callId = null,
        string? toolName = null,
        string? toolArguments = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleAsync<WorkspaceApprovalEventRow>(
            """
            insert into workspace_approval_events (session_id, agent_id, approval_token, action_type, target_path, command_preview, description, call_id, tool_name, tool_arguments, risk_level)
            values (@sessionId, @agentId, @approvalToken, @actionType, @targetPath, @commandPreview, @description, @callId, @toolName, cast(@toolArguments as jsonb), @riskLevel)
            returning *;
            """,
            new
            {
                sessionId,
                agentId,
                approvalToken,
                actionType = ActionTypeToDbString(actionType),
                targetPath,
                commandPreview,
                description,
                callId,
                toolName,
                toolArguments,
                riskLevel = riskLevel.ToString().ToLowerInvariant(),
            });
        return result.ToModel();
    }

    public async Task<WorkspaceApprovalEvent?> GetApprovalEventByToken(string approvalToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleOrDefaultAsync<WorkspaceApprovalEventRow>(
            "select * from workspace_approval_events where approval_token = @approvalToken",
            new { approvalToken });
        return result?.ToModel();
    }

    public async Task<bool> ResolveApprovalEvent(string approvalToken, ApprovalStatus status)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var updated = await connection.ExecuteAsync(
            """
            update workspace_approval_events
            set status = @status, resolved_at = now()
            where approval_token = @approvalToken and status = 'pending';
            """,
            new
            {
                approvalToken,
                status = status.ToString().ToLowerInvariant(),
            });
        return updated > 0;
    }

    public async Task<IReadOnlyList<WorkspaceApprovalEvent>> GetPendingApprovalsForSession(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var results = await connection.QueryAsync<WorkspaceApprovalEventRow>(
            "select * from workspace_approval_events where session_id = @sessionId and status = 'pending' order by created_at desc",
            new { sessionId });
        return results.Select(r => r.ToModel()).ToArray();
    }

    public async Task<IReadOnlyList<WorkspaceApprovalEvent>> GetPendingApprovalsForSessions(Guid[] sessionIds)
    {
        if (sessionIds.Length == 0)
            return [];

        await using var connection = new NpgsqlConnection(ConnectionString);
        var results = await connection.QueryAsync<WorkspaceApprovalEventRow>(
            """
            select *
            from workspace_approval_events
            where session_id = any(@sessionIds)
              and status = 'pending'
            order by created_at desc
            """,
            new { sessionIds });
        return results.Select(r => r.ToModel()).ToArray();
    }

    public async Task<LcmFileArtifact> CreateLcmFileArtifact(Guid sessionId, string fileId, string originTool, string workspacePath, long byteCount, string? filesystemPath)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleAsync<LcmFileArtifactRow>(
            """
            insert into lcm_files (file_id, session_id, origin_tool, workspace_path, byte_count, storage_kind, filesystem_path)
            values (@fileId, @sessionId, @originTool, @workspacePath, @byteCount, 'filesystem', @filesystemPath)
            returning *;
            """,
            new
            {
                fileId,
                sessionId,
                originTool,
                workspacePath,
                byteCount,
                filesystemPath,
            });
        return result.ToModel();
    }

    public async Task<LcmFileArtifact?> GetLcmFileArtifact(string fileId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var result = await connection.QuerySingleOrDefaultAsync<LcmFileArtifactRow>(
            "select * from lcm_files where file_id = @fileId",
            new { fileId });
        return result?.ToModel();
    }

    public async Task ExpireOldPendingApprovals()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            update workspace_approval_events
            set status = 'expired', resolved_at = now()
            where status = 'pending' and created_at < now() - interval '30 minutes';
            """);
    }

    public async Task<IReadOnlyList<SessionActiveWorkspace>> GetActiveWorkspacesForSession(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var results = await connection.QueryAsync<SessionActiveWorkspaceRow>(
            "select * from session_active_workspaces where session_id = @sessionId order by created_at",
            new { sessionId });
        return results.Select(r => r.ToModel()).ToArray();
    }

    public async Task SetActiveWorkspacesForSession(Guid sessionId, long agentId, string[] workspaceNames)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync(
            "delete from session_active_workspaces where session_id = @sessionId",
            new { sessionId }, tx);

        foreach (var name in workspaceNames)
        {
            var assignment = await connection.QuerySingleOrDefaultAsync<AssignmentRow>(
                """
                select a.* from agent_workspace_assignments a
                join workspaces w on w.id = a.workspace_id
                where a.agent_id = @agentId and w.name = @name;
                """,
                new { agentId, name }, tx);

            if (assignment is not null)
            {
                await connection.ExecuteAsync(
                    """
                    insert into session_active_workspaces (session_id, workspace_name, policy_mode)
                    values (@sessionId, @name, @policyMode)
                    on conflict (session_id, workspace_name) do nothing;
                    """,
                    new { sessionId, name, policyMode = assignment.policy_mode }, tx);
            }
        }

        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<ResolvedWorkspace>> GetAvailableWorkspacesForAgent(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var results = await connection.QueryAsync<ResolvedWorkspaceRow>(
            """
            select w.*, a.policy_mode, a.is_default
            from agent_workspace_assignments a
            join workspaces w on w.id = a.workspace_id
            where a.agent_id = @agentId
            order by a.is_default desc, w.name;
            """,
            new { agentId });
        return results.Select(r => r.ToModel()).ToArray();
    }

    private static string PolicyModeToDbString(WorkspacePolicyMode mode) => mode switch
    {
        WorkspacePolicyMode.Unrestricted => "unrestricted",
        WorkspacePolicyMode.TrueUnrestricted => "true_unrestricted",
        WorkspacePolicyMode.ConfirmWritesAndExec => "confirm_writes_and_exec",
        WorkspacePolicyMode.ConfirmExecOnly => "confirm_exec_only",
        WorkspacePolicyMode.ReadOnly => "read_only",
        WorkspacePolicyMode.Disabled => "disabled",
        _ => "confirm_writes_and_exec",
    };

    private static string ActionTypeToDbString(ApprovalActionType actionType) => actionType switch
    {
        ApprovalActionType.Write => "write",
        ApprovalActionType.Delete => "delete",
        ApprovalActionType.Move => "move",
        ApprovalActionType.Execute => "execute",
        ApprovalActionType.DestructiveExecute => "destructive_execute",
        _ => "write",
    };

    private sealed class WorkspaceRow
    {
        public long id { get; set; }
        public string name { get; set; } = string.Empty;
        public string root_path { get; set; } = string.Empty;
        public string allowlist_patterns { get; set; } = "[]";
        public string denylist_patterns { get; set; } = "[]";
        public string runtime_kind { get; set; } = "local";
        public string? runtime_target { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public Workspace ToModel() => new()
        {
            Id = id,
            Name = name,
            RootPath = root_path,
            AllowlistPatterns = System.Text.Json.JsonSerializer.Deserialize<string[]>(allowlist_patterns) ?? [],
            DenylistPatterns = System.Text.Json.JsonSerializer.Deserialize<string[]>(denylist_patterns) ?? [],
            RuntimeKind = runtime_kind.ToLowerInvariant() switch
            {
                "bridge" => WorkspaceRuntimeKind.Bridge,
                _ => WorkspaceRuntimeKind.Local,
            },
            RuntimeTarget = runtime_target,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };
    }

    private sealed class AssignmentRow
    {
        public long id { get; set; }
        public long agent_id { get; set; }
        public long workspace_id { get; set; }
        public string policy_mode { get; set; } = string.Empty;
        public bool is_default { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }

        public AgentWorkspaceAssignment ToModel() => new()
        {
            Id = id,
            AgentId = agent_id,
            WorkspaceId = workspace_id,
            PolicyMode = policy_mode.ToLowerInvariant() switch
            {
                "unrestricted" => WorkspacePolicyMode.Unrestricted,
                "true_unrestricted" => WorkspacePolicyMode.TrueUnrestricted,
                "confirm_exec_only" => WorkspacePolicyMode.ConfirmExecOnly,
                "read_only" => WorkspacePolicyMode.ReadOnly,
                "disabled" => WorkspacePolicyMode.Disabled,
                _ => WorkspacePolicyMode.ConfirmWritesAndExec,
            },
            IsDefault = is_default,
            CreatedAt = created_at,
            UpdatedAt = updated_at,
        };
    }

    private sealed class ResolvedWorkspaceRow
    {
        public long id { get; set; }
        public string name { get; set; } = string.Empty;
        public string root_path { get; set; } = string.Empty;
        public string allowlist_patterns { get; set; } = "[]";
        public string denylist_patterns { get; set; } = "[]";
        public string runtime_kind { get; set; } = "local";
        public string? runtime_target { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
        public string policy_mode { get; set; } = string.Empty;
        public bool is_default { get; set; }

        public ResolvedWorkspace ToModel() => new()
        {
            Workspace = new Workspace
            {
                Id = id,
                Name = name,
                RootPath = root_path,
                AllowlistPatterns = System.Text.Json.JsonSerializer.Deserialize<string[]>(allowlist_patterns) ?? [],
                DenylistPatterns = System.Text.Json.JsonSerializer.Deserialize<string[]>(denylist_patterns) ?? [],
                RuntimeKind = runtime_kind.ToLowerInvariant() switch
                {
                    "bridge" => WorkspaceRuntimeKind.Bridge,
                    _ => WorkspaceRuntimeKind.Local,
                },
                RuntimeTarget = runtime_target,
                CreatedAt = created_at,
                UpdatedAt = updated_at,
            },
            PolicyMode = policy_mode.ToLowerInvariant() switch
            {
                "unrestricted" => WorkspacePolicyMode.Unrestricted,
                "true_unrestricted" => WorkspacePolicyMode.TrueUnrestricted,
                "confirm_exec_only" => WorkspacePolicyMode.ConfirmExecOnly,
                "read_only" => WorkspacePolicyMode.ReadOnly,
                "disabled" => WorkspacePolicyMode.Disabled,
                _ => WorkspacePolicyMode.ConfirmWritesAndExec,
            },
            IsDefault = is_default,
        };
    }

    private sealed class WorkspaceApprovalEventRow
    {
        public long id { get; set; }
        public Guid session_id { get; set; }
        public long agent_id { get; set; }
        public string approval_token { get; set; } = string.Empty;
        public string action_type { get; set; } = string.Empty;
        public string? target_path { get; set; }
        public string? command_preview { get; set; }
        public string? description { get; set; }
        public string? call_id { get; set; }
        public string? tool_name { get; set; }
        public string? tool_arguments { get; set; }
        public string risk_level { get; set; } = string.Empty;
        public string status { get; set; } = string.Empty;
        public DateTime created_at { get; set; }
        public DateTime? resolved_at { get; set; }

        public WorkspaceApprovalEvent ToModel() => new()
        {
            Id = id,
            SessionId = session_id,
            AgentId = agent_id,
            ApprovalToken = approval_token,
            ActionType = action_type.ToLowerInvariant() switch
            {
                "delete" => ApprovalActionType.Delete,
                "move" => ApprovalActionType.Move,
                "execute" => ApprovalActionType.Execute,
                "destructive_execute" => ApprovalActionType.DestructiveExecute,
                _ => ApprovalActionType.Write,
            },
            TargetPath = target_path,
            CommandPreview = command_preview,
            Description = description,
            CallId = call_id,
            ToolName = tool_name,
            ToolArguments = tool_arguments,
            RiskLevel = risk_level.ToLowerInvariant() switch
            {
                "medium" => ApprovalRiskLevel.Medium,
                "high" => ApprovalRiskLevel.High,
                "critical" => ApprovalRiskLevel.Critical,
                _ => ApprovalRiskLevel.Low,
            },
            Status = status.ToLowerInvariant() switch
            {
                "approved" => ApprovalStatus.Approved,
                "rejected" => ApprovalStatus.Rejected,
                "expired" => ApprovalStatus.Expired,
                _ => ApprovalStatus.Pending,
            },
            CreatedAt = created_at,
            ResolvedAt = resolved_at,
        };
    }

    private sealed class LcmFileArtifactRow
    {
        public long id { get; set; }
        public string file_id { get; set; } = string.Empty;
        public Guid session_id { get; set; }
        public string origin_tool { get; set; } = string.Empty;
        public string workspace_path { get; set; } = string.Empty;
        public long byte_count { get; set; }
        public string storage_kind { get; set; } = "filesystem";
        public string? filesystem_path { get; set; }
        public DateTime created_at { get; set; }

        public LcmFileArtifact ToModel() => new()
        {
            Id = id,
            FileId = file_id,
            SessionId = session_id,
            OriginTool = origin_tool,
            WorkspacePath = workspace_path,
            ByteCount = byte_count,
            StorageKind = storage_kind,
            FilesystemPath = filesystem_path,
            CreatedAt = created_at,
        };
    }

    private sealed class SessionActiveWorkspaceRow
    {
        public long id { get; set; }
        public Guid session_id { get; set; }
        public string workspace_name { get; set; } = string.Empty;
        public string policy_mode { get; set; } = string.Empty;
        public DateTime created_at { get; set; }

        public SessionActiveWorkspace ToModel() => new()
        {
            Id = id,
            SessionId = session_id,
            WorkspaceName = workspace_name,
            PolicyMode = policy_mode.ToLowerInvariant() switch
            {
                "unrestricted" => WorkspacePolicyMode.Unrestricted,
                "true_unrestricted" => WorkspacePolicyMode.TrueUnrestricted,
                "confirm_exec_only" => WorkspacePolicyMode.ConfirmExecOnly,
                "read_only" => WorkspacePolicyMode.ReadOnly,
                "disabled" => WorkspacePolicyMode.Disabled,
                _ => WorkspacePolicyMode.ConfirmWritesAndExec,
            },
            CreatedAt = created_at,
        };
    }
}

public record BridgeClientInfo
{
    public string BridgeId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime? LastSeenAt { get; init; }
    public DateTime CreatedAt { get; init; }
}
