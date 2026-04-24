using System.Text.Json;
using Dapper;
using Microsoft.Extensions.AI;
using Npgsql;
using SharpClaw.API.Agents;

namespace SharpClaw.API.Database;

public class Repository(IConfiguration configuration)
{
    private const string DbEntryTypeKey = "db_entry_type";
    private const string DbEntryIdKey = "db_entry_id";
    private const string ParentSummaryIdKey = "parent_summary_id";

    private string ConnectionString => configuration.GetConnectionString("sharpclaw")
                                       ?? throw new InvalidOperationException("Missing connection string 'sharpclaw'.");

    public async Task<IReadOnlyList<AgentConfig>> GetAgents()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<AgentConfig>(
            """
            select id as Id,
                   name as Name,
                   llm_model as LlmModel,
                   temperature as Temperature,
                   created_at as CreatedAt,
                   updated_at as UpdatedAt
            from agents
            order by id;
            """);

        return rows.ToArray();
    }

    public async Task<AgentConfig?> GetAgent(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<AgentConfig>(
            """
            select id as Id,
                   name as Name,
                   llm_model as LlmModel,
                   temperature as Temperature,
                   created_at as CreatedAt,
                   updated_at as UpdatedAt
            from agents
            where id = @agentId;
            """,
            new { agentId });
    }

    public async Task<AgentConfig> CreateAgent(string name, string llmModel, float temperature)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<AgentConfig>(
            """
            insert into agents (name, llm_model, temperature)
            values (@name, @llmModel, @temperature)
            returning id as Id,
                      name as Name,
                      llm_model as LlmModel,
                      temperature as Temperature,
                      created_at as CreatedAt,
                      updated_at as UpdatedAt;
            """,
            new { name, llmModel, temperature });
    }

    public async Task<AgentConfig?> UpdateAgent(long agentId, string name, string llmModel, float temperature)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<AgentConfig>(
            """
            update agents
            set name = @name,
                llm_model = @llmModel,
                temperature = @temperature,
                updated_at = now()
            where id = @agentId
            returning id as Id,
                      name as Name,
                      llm_model as LlmModel,
                      temperature as Temperature,
                      created_at as CreatedAt,
                      updated_at as UpdatedAt;
            """,
            new { agentId, name, llmModel, temperature });
    }

    public async Task<IReadOnlyList<AgentDocumentSummary>> GetAgentDocuments(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<AgentDocumentSummary>(
            """
            select d.name as Name
            from agents_documents ad
            join documents d on d.id = ad.document_id
            where ad.agent_id = @agentId
            order by d.name;
            """,
            new { agentId });

        return rows.ToArray();
    }

    public async Task<AgentDocument?> GetAgentDocument(long agentId, string path)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<AgentDocument>(
            """
            select d.name as Path,
                   d.content as Content
            from agents_documents ad
            join documents d on d.id = ad.document_id
            where ad.agent_id = @agentId
              and d.name = @path;
            """,
            new { agentId, path });
    }

    public async Task UpsertAgentDocument(long agentId, string path, string content)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            update documents as d
            set content = @content
            from agents_documents ad
            where d.id = ad.document_id
              and ad.agent_id = @agentId
              and d.name = @path;

            with inserted as (
                insert into documents (name, content)
                select @path, @content
                where not exists (
                    select 1
                    from agents_documents ad
                    join documents d on d.id = ad.document_id
                    where ad.agent_id = @agentId and d.name = @path
                )
                returning id
            )
            insert into agents_documents (agent_id, document_id)
            select @agentId, id
            from inserted;
            """,
            new { agentId, path, content });
    }

    public async Task<bool> DeleteAgentDocument(long agentId, string path)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var documentId = await connection.QueryFirstOrDefaultAsync<long?>(
            """
            select d.id
            from agents_documents ad
            join documents d on d.id = ad.document_id
            where ad.agent_id = @agentId and d.name = @path
            limit 1;
            """,
            new { agentId, path },
            tx);

        if (documentId is null)
            return false;

        await connection.ExecuteAsync(
            """
            delete from agents_documents
            where agent_id = @agentId and document_id = @documentId;
            """,
            new { agentId, documentId },
            tx);

        await connection.ExecuteAsync(
            """
            delete from documents
            where id = @documentId
              and not exists (
                  select 1
                  from agents_documents
                  where document_id = @documentId
              );
            """,
            new { documentId },
            tx);

        await tx.CommitAsync();
        return true;
    }

    public async Task CreateSession(Guid sessionId, long agentId, Guid? parentSessionId, string? name, bool visibleInSidebar)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            insert into sessions (id, agent_id, parent_session_id, name, visible_in_sidebar)
            values (@sessionId, @agentId, @parentSessionId, @name, @visibleInSidebar)
            on conflict (id) do nothing;
            """,
            new { sessionId, agentId, parentSessionId, name, visibleInSidebar });
    }

    public async Task<PersistedSession?> GetSession(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<PersistedSession>(
            """
            select id as SessionId,
                   agent_id as AgentId,
                   name as Name,
                   visible_in_sidebar as VisibleInSidebar,
                   parent_session_id as ParentSessionId,
                   created_at as CreatedAt,
                   updated_at as UpdatedAt
            from sessions
            where id = @sessionId;
            """,
            new { sessionId });
    }

    public async Task UpdateSession(Guid sessionId, AgentRunStatus status)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            update sessions
            set status = @status,
                updated_at = now()
            where id = @sessionId
            """,
            new
            {
                sessionId,
                status = status.ToString().ToLowerInvariant(),
            });
    }

    public async Task<PersistedSession?> RenameSession(Guid sessionId, string name)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<PersistedSession>(
            """
            update sessions
            set name = @name,
                updated_at = now()
            where id = @sessionId
            returning id as SessionId,
                      agent_id as AgentId,
                      name as Name,
                      visible_in_sidebar as VisibleInSidebar,
                      parent_session_id as ParentSessionId,
                      created_at as CreatedAt,
                      updated_at as UpdatedAt;
            """,
            new { sessionId, name });
    }

    public async Task AddSessionTask(Guid sessionId,
        string callId,
        Guid? childSessionId,
        bool blocking = true
    )
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            insert into session_tasks (session_id, type, call_id, child_session_id, blocking)
            values (@sessionId, 'task', @callId, @childSessionId, @blocking)
            on conflict (id) do nothing;
            """,
            new
            {
                sessionId,
                callId,
                childSessionId,
                blocking,
            });
    }

    public async Task<string> CompleteSessionTask(Guid childSessionId, Guid parentSessionId, string? result)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var res = await connection.ExecuteAsync(
            """
            update session_tasks
            set result = @result,
                completed = true,
                updated_at = now()
            where completed = false
              and child_session_id = @childSessionId
              and session_id = @parentSessionId;
            """,
            new
            {
                parentSessionId,
                childSessionId,
                result,
            });

        if (res != 1)
            throw new Exception($"Expected task to affect exactly 1 row, got {res}");

        return await connection.QueryFirstAsync<string>(
            """
            select call_id
            from session_tasks
            where child_session_id = @childSessionId
              and session_id = @parentSessionId;
            """,
            new
            {
                parentSessionId,
                childSessionId,
            });
    }

    public async Task<IReadOnlyList<PersistedSessionSummary>> GetSessions(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<PersistedSessionSummary>(
            """
            select s.id as SessionId,
                   s.agent_id as AgentId,
                   s.name as Name,
                   s.visible_in_sidebar as VisibleInSidebar,
                   s.parent_session_id as ParentSessionId,
                   s.created_at as CreatedAt,
                   s.updated_at as UpdatedAt,
                   coalesce((
                       select count(*)
                       from conversation_history ch
                       where ch.session_id = s.id and ch.is_active
                   ), 0) as MessagesCount
            from sessions s
            where s.agent_id = @agentId
            order by s.updated_at desc, s.created_at desc;
            """,
            new { agentId });

        return rows.ToArray();
    }

    public async Task<IReadOnlyList<SessionTaskLink>> GetSessionTaskLinks(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<SessionTaskLink>(
            """
            select call_id as CallId,
                   child_session_id as ChildSessionId,
                   completed as Completed
            from session_tasks
            where session_id = @sessionId
              and child_session_id is not null
            order by created_at, id;
            """,
            new { sessionId });

        return rows.ToArray();
    }

    public async Task<long> PersistMessage(Guid sessionId, ChatResponse response)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var payload = SerializeResponse(response);
        var (role, searchText) = BuildIndexFields(response);

        var messageId = await connection.ExecuteScalarAsync<long>(
            """
            insert into messages (session_id, payload, role, search_text)
            values (@sessionId, cast(@payload as jsonb), @role, @searchText)
            returning id;
            """,
            new { sessionId, payload, role, searchText },
            tx);

        var nextSequence = await connection.ExecuteScalarAsync<long>(
            """
            select coalesce(max(sequence), -1) + 1
            from conversation_history
            where session_id = @sessionId and is_active;
            """,
            new { sessionId },
            tx);

        await connection.ExecuteAsync(
            """
            insert into conversation_history (session_id, sequence, entry_type, message_id)
            values (@sessionId, @sequence, 'message', @messageId);
            """,
            new { sessionId, sequence = nextSequence, messageId },
            tx);

        await tx.CommitAsync();

        SetDbReference(response, "message", messageId, nextSequence);
        return messageId;
    }

    public async Task UpdateMessage(ChatResponse response)
    {
        var payload = SerializeResponse(response);
        var (_, searchText) = BuildIndexFields(response);

        await using var connection = new NpgsqlConnection(ConnectionString);
        var res = await connection.ExecuteAsync(
            """
            update messages
            set payload = cast(@payload as jsonb),
                search_text = @searchText
            where id = @id
            """,
            new
            {
                id = response.AdditionalProperties![DbEntryIdKey],
                payload,
                searchText,
            });

        if (res != 1)
            throw new Exception($"Expected update to affect exactly 1 row, got {res}");
    }

    public async Task<long> PersistSummaryAndCompactHistory(Guid sessionId, ChatResponse summary,
        IReadOnlyList<ChatResponse> summarizedItems)
    {
        var messageIds = summarizedItems
            .Select(TryGetDbReference)
            .Where(x => x is { Type: "message" })
            .Select(x => x!.Value.Id)
            .Distinct()
            .ToArray();

        var summaryIds = summarizedItems
            .Select(TryGetDbReference)
            .Where(x => x is { Type: "summary" })
            .Select(x => x!.Value.Id)
            .Distinct()
            .ToArray();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var payload = SerializeResponse(summary);
        var (_, searchText) = BuildIndexFields(summary);
        var (lcmSummaryId, lcmSummaryLevel) = ExtractSummaryMetadata(summary);

        var summaryId = await connection.ExecuteScalarAsync<long>(
            """
            insert into summaries (session_id, payload, search_text, lcm_summary_id, lcm_summary_level)
            values (@sessionId, cast(@payload as jsonb), @searchText, @lcmSummaryId, @lcmSummaryLevel)
            returning id;
            """,
            new { sessionId, payload, searchText, lcmSummaryId, lcmSummaryLevel },
            tx);

        if (messageIds.Length > 0)
        {
            await connection.ExecuteAsync(
                """
                update messages
                set parent_summary_id = @summaryId
                where session_id = @sessionId
                  and id = any(@messageIds);
                """,
                new { sessionId, summaryId, messageIds },
                tx);
        }

        if (summaryIds.Length > 0)
        {
            await connection.ExecuteAsync(
                """
                update summaries
                set parent_summary_id = @summaryId
                where session_id = @sessionId
                  and id = any(@summaryIds);
                """,
                new { sessionId, summaryId, summaryIds },
                tx);
        }

        long? minSequence = null;
        if (messageIds.Length > 0 || summaryIds.Length > 0)
        {
            minSequence = await connection.ExecuteScalarAsync<long?>(
                """
                select min(sequence)
                from conversation_history
                where session_id = @sessionId
                  and is_active
                  and (
                      (entry_type = 'message' and message_id = any(@messageIds))
                      or (entry_type = 'summary' and summary_id = any(@summaryIds))
                  );
                """,
                new { sessionId, messageIds, summaryIds },
                tx);

            await connection.ExecuteAsync(
                """
                update conversation_history
                set is_active = false
                where session_id = @sessionId
                  and is_active
                  and (
                      (entry_type = 'message' and message_id = any(@messageIds))
                      or (entry_type = 'summary' and summary_id = any(@summaryIds))
                  );
                """,
                new { sessionId, messageIds, summaryIds },
                tx);
        }

        if (minSequence is null)
        {
            minSequence = await connection.ExecuteScalarAsync<long>(
                """
                select coalesce(max(sequence), -1) + 1
                from conversation_history
                where session_id = @sessionId and is_active;
                """,
                new { sessionId },
                tx);
        }

        await connection.ExecuteAsync(
            """
            insert into conversation_history (session_id, sequence, entry_type, summary_id)
            values (@sessionId, @sequence, 'summary', @summaryId);
            """,
            new { sessionId, sequence = minSequence.Value, summaryId },
            tx);

        await tx.CommitAsync();

        SetDbReference(summary, "summary", summaryId);
        foreach (var item in summarizedItems)
            SetParentSummaryReference(item, summaryId);

        return summaryId;
    }

    public async Task<IReadOnlyList<ChatResponse>> LoadActiveConversation(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<ConversationPayloadRow>(
            """
            select case
                       when ch.entry_type = 'message' then m.payload::text
                       else s.payload::text
                   end as Payload,
                   ch.entry_type as EntryType,
                   ch.sequence as SequenceId,
                   m.id as MessageId,
                   s.id as SummaryId,
                   m.parent_summary_id as MessageParentSummaryId,
                   s.parent_summary_id as SummaryParentSummaryId
            from conversation_history ch
            left join messages m on ch.message_id = m.id
            left join summaries s on ch.summary_id = s.id
            where ch.session_id = @sessionId
              and ch.is_active
            order by ch.sequence, ch.id;
            """,
            new { sessionId });

        return rows.Select(r =>
        {
            var response = DeserializeResponse(r.Payload);
            if (r.EntryType == "message" && r.MessageId is not null)
                SetDbReference(response, "message", r.MessageId.Value, r.SequenceId, r.MessageParentSummaryId);
            else if (r.EntryType == "summary" && r.SummaryId is not null)
                SetDbReference(response, "summary", r.SummaryId.Value, r.SummaryParentSummaryId);
            return response;
        }).ToArray();
    }

    public async Task<IReadOnlyList<PersistedRawMessage>> LoadRawMessages(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<RawMessageRow>(
            """
            select m.id as MessageId,
                   m.created_at as CreatedAt,
                   m.payload::text as Payload,
                   m.parent_summary_id as ParentSummaryId,
                   ch.sequence as SequenceId
            from messages m
            join conversation_history ch on m.id = ch.message_id
            where m.session_id = @sessionId
            order by ch.sequence;
            """,
            new { sessionId });

        return rows
            .Select(r =>
            {
                var response = DeserializeResponse(r.Payload);
                SetDbReference(response, "message", r.MessageId, r.SequenceId, r.ParentSummaryId);
                return new PersistedRawMessage(r.MessageId, r.CreatedAt, response);
            })
            .ToArray();
    }

    public async Task<LcmSummaryRecord?> GetLcmSummary(Guid sessionId, string lcmSummaryId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var row = await connection.QueryFirstOrDefaultAsync<LcmSummaryRow>(
            """
            select s.id as DbId,
                   s.session_id as SessionId,
                   s.parent_summary_id as ParentSummaryDbId,
                   s.created_at as CreatedAt,
                   s.lcm_summary_id as LcmSummaryId,
                   s.lcm_summary_level as SummaryLevel,
                   s.search_text as SearchText,
                   s.payload::text as Payload
            from summaries s
            where s.session_id = @sessionId
              and s.lcm_summary_id = @lcmSummaryId
            order by s.id desc
            limit 1;
            """,
            new { sessionId, lcmSummaryId });

        return row is null ? null : ToLcmSummaryRecord(row);
    }

    public async Task<IReadOnlyList<LcmSummaryRecord>> GetLcmSummaryParents(long summaryDbId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<LcmSummaryRow>(
            """
            with recursive parents as (
                select s.id as db_id,
                       s.session_id,
                       s.parent_summary_id,
                       s.created_at,
                       s.lcm_summary_id,
                       s.lcm_summary_level,
                       s.search_text,
                       s.payload
                from summaries s
                where s.id = @summaryDbId

                union all

                select parent.id as db_id,
                       parent.session_id,
                       parent.parent_summary_id,
                       parent.created_at,
                       parent.lcm_summary_id,
                       parent.lcm_summary_level,
                       parent.search_text,
                       parent.payload
                from summaries parent
                join parents p on p.parent_summary_id = parent.id
            )
            select p.db_id as DbId,
                   p.session_id as SessionId,
                   p.parent_summary_id as ParentSummaryDbId,
                   p.created_at as CreatedAt,
                   p.lcm_summary_id as LcmSummaryId,
                   p.lcm_summary_level as SummaryLevel,
                   p.search_text as SearchText,
                   p.payload::text as Payload
            from parents p
            where p.db_id <> @summaryDbId
            order by p.created_at, p.db_id;
            """,
            new { summaryDbId });

        return rows.Select(ToLcmSummaryRecord).ToArray();
    }

    public async Task<IReadOnlyList<LcmExpandedMessageRecord>> ExpandLcmSummaryToMessages(Guid sessionId, string lcmSummaryId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<LcmExpandedMessageRow>(
            """
            with root as (
                select s.id
                from summaries s
                where s.session_id = @sessionId
                  and s.lcm_summary_id = @lcmSummaryId
                order by s.id desc
                limit 1
            ),
            summary_closure as (
                select s.id
                from summaries s
                join root r on r.id = s.id

                union all

                select child.id
                from summaries child
                join summary_closure c on child.parent_summary_id = c.id
                where child.session_id = @sessionId
            )
            select m.id as MessageDbId,
                   m.created_at as CreatedAt,
                   m.payload::text as Payload
            from messages m
            where m.session_id = @sessionId
              and m.parent_summary_id in (select id from summary_closure)
            order by m.created_at, m.id;
            """,
            new { sessionId, lcmSummaryId });

        return rows.Select(ToLcmExpandedMessageRecord).ToArray();
    }

    public async Task<IReadOnlyList<LcmGrepMessageRecord>> SearchLcmMessagesRegex(
        Guid sessionId,
        string pattern,
        string? lcmSummaryId,
        int limit,
        int offset)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);

        IEnumerable<LcmGrepRow> rows;
        if (string.IsNullOrWhiteSpace(lcmSummaryId))
        {
            rows = await connection.QueryAsync<LcmGrepRow>(
                """
                select m.id as MessageDbId,
                       m.created_at as CreatedAt,
                       m.payload::text as Payload,
                       cover.lcm_summary_id as CoveringSummaryId,
                       cover.lcm_summary_level as CoveringSummaryLevel
                from messages m
                left join summaries cover on cover.id = m.parent_summary_id
                where m.session_id = @sessionId
                  and m.search_text ~* @pattern
                order by m.created_at, m.id
                limit @limit offset @offset;
                """,
                new { sessionId, pattern, limit, offset });
        }
        else
        {
            rows = await connection.QueryAsync<LcmGrepRow>(
                """
                with root as (
                    select s.id
                    from summaries s
                    where s.session_id = @sessionId
                      and s.lcm_summary_id = @lcmSummaryId
                    order by s.id desc
                    limit 1
                ),
                summary_closure as (
                    select s.id
                    from summaries s
                    join root r on r.id = s.id

                    union all

                    select child.id
                    from summaries child
                    join summary_closure c on child.parent_summary_id = c.id
                    where child.session_id = @sessionId
                )
                select m.id as MessageDbId,
                       m.created_at as CreatedAt,
                       m.payload::text as Payload,
                       cover.lcm_summary_id as CoveringSummaryId,
                       cover.lcm_summary_level as CoveringSummaryLevel
                from messages m
                left join summaries cover on cover.id = m.parent_summary_id
                where m.session_id = @sessionId
                  and m.parent_summary_id in (select id from summary_closure)
                  and m.search_text ~* @pattern
                order by m.created_at, m.id
                limit @limit offset @offset;
                """,
                new { sessionId, lcmSummaryId, pattern, limit, offset });
        }

        return rows.Select(ToLcmGrepMessageRecord).ToArray();
    }

    private static void SetDbReference(ChatResponse response, string type, long id, long sequenceId, long? parentSummaryId = null)
    {
        response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        response.AdditionalProperties[DbEntryTypeKey] = type;
        response.AdditionalProperties[DbEntryIdKey] = id;
        response.AdditionalProperties[Constants.SequenceIdKey] = sequenceId;

        if (parentSummaryId is not null)
            response.AdditionalProperties[ParentSummaryIdKey] = parentSummaryId.Value;
    }

    private static void SetDbReference(ChatResponse response, string type, long id, long? parentSummaryId = null)
    {
        response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        response.AdditionalProperties[DbEntryTypeKey] = type;
        response.AdditionalProperties[DbEntryIdKey] = id;

        if (parentSummaryId is not null)
            response.AdditionalProperties[ParentSummaryIdKey] = parentSummaryId.Value;
    }

    private static void SetParentSummaryReference(ChatResponse response, long parentSummaryId)
    {
        response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        response.AdditionalProperties[ParentSummaryIdKey] = parentSummaryId;
    }

    private static (string Type, long Id)? TryGetDbReference(ChatResponse response)
    {
        var properties = response.AdditionalProperties;
        if (properties is null)
            return null;

        if (!properties.TryGetValue(DbEntryTypeKey, out var typeObj)
            || !properties.TryGetValue(DbEntryIdKey, out var idObj))
            return null;

        var type = typeObj?.ToString();
        if (string.IsNullOrWhiteSpace(type))
            return null;

        var id = TryReadInt64(idObj);
        if (id is null)
            return null;

        return (type, id.Value);
    }

    private static LcmSummaryRecord ToLcmSummaryRecord(LcmSummaryRow row)
    {
        var response = DeserializeResponse(row.Payload);
        var (payloadSummaryId, payloadSummaryLevel) = ExtractSummaryMetadata(response);
        var level = row.SummaryLevel ?? payloadSummaryLevel;
        var externalSummaryId = row.LcmSummaryId ?? payloadSummaryId;
        var content = !string.IsNullOrWhiteSpace(row.SearchText)
            ? row.SearchText
            : response.Messages.Select(m => m.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;

        return new LcmSummaryRecord(
            row.DbId,
            row.SessionId,
            externalSummaryId,
            level,
            content,
            row.ParentSummaryDbId,
            row.CreatedAt);
    }

    private static LcmExpandedMessageRecord ToLcmExpandedMessageRecord(LcmExpandedMessageRow row)
    {
        var (role, content) = FlattenResponsePayload(row.Payload);

        return new LcmExpandedMessageRecord(
            row.MessageDbId,
            role,
            content,
            row.CreatedAt);
    }

    private static LcmGrepMessageRecord ToLcmGrepMessageRecord(LcmGrepRow row)
    {
        var (role, content) = FlattenResponsePayload(row.Payload);

        return new LcmGrepMessageRecord(
            row.MessageDbId,
            role,
            content,
            row.CreatedAt,
            row.CoveringSummaryId,
            row.CoveringSummaryLevel);
    }

    private static (string Role, string Content) FlattenResponsePayload(string payload)
    {
        var response = DeserializeResponse(payload);
        return FlattenChatResponse(response);
    }

    private static (string Role, string Content) FlattenChatResponse(ChatResponse response)
    {
        var flattenedMessages = response.Messages
            .Select(chatMessage =>
            {
                var pieces = new List<string>();
                if (!string.IsNullOrWhiteSpace(chatMessage.Text))
                    pieces.Add(chatMessage.Text!);

                foreach (var content in chatMessage.Contents.Select(PersistedContent.From))
                {
                    var fallback = content.ToFallbackText();
                    if (!string.IsNullOrWhiteSpace(fallback))
                        pieces.Add(fallback);
                }

                var text = string.Join('\n', pieces.Where(x => !string.IsNullOrWhiteSpace(x)));
                return (Role: chatMessage.Role.Value, Text: text);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToArray();

        var role = flattenedMessages.FirstOrDefault().Role ?? "assistant";
        var content = string.Join("\n\n", flattenedMessages.Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)));
        return (role, content);
    }

    private static (string Role, string SearchText) BuildIndexFields(ChatResponse response)
    {
        var (role, text) = FlattenChatResponse(response);
        return (role, text);
    }

    private static (string? LcmSummaryId, int? LcmSummaryLevel) ExtractSummaryMetadata(ChatResponse response)
    {
        var properties = response.AdditionalProperties;
        if (properties is null)
            return (null, null);

        string? summaryId = null;
        int? summaryLevel = null;

        if (properties.TryGetValue("lcm_summary_id", out var idObj))
            summaryId = idObj?.ToString();

        if (properties.TryGetValue("lcm_summary_level", out var levelObj))
            summaryLevel = TryReadInt32(levelObj);

        return (summaryId, summaryLevel);
    }

    private static long? TryReadInt64(object? value)
    {
        return value switch
        {
            null => null,
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt64(out var n) => n,
            JsonElement { ValueKind: JsonValueKind.String } element when long.TryParse(element.GetString(), out var n) => n,
            _ when long.TryParse(value.ToString(), out var n) => n,
            _ => null,
        };
    }

    private static int? TryReadInt32(object? value)
    {
        return value switch
        {
            null => null,
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            short s => s,
            byte b => b,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var n) => n,
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var n) => n,
            _ when int.TryParse(value.ToString(), out var n) => n,
            _ => null,
        };
    }

    private static string SerializeResponse(ChatResponse response)
    {
        var dto = PersistedChatResponse.From(response);
        return JsonSerializer.Serialize(dto);
    }

    private static ChatResponse DeserializeResponse(string payload)
    {
        var dto = JsonSerializer.Deserialize<PersistedChatResponse>(payload)
                  ?? throw new InvalidOperationException("Failed to deserialize persisted chat response.");

        return dto.ToChatResponse();
    }

    private sealed class ConversationPayloadRow
    {
        public required string Payload { get; init; }
        public required string EntryType { get; init; }
        public required long SequenceId { get; init; }
        public long? MessageId { get; init; }
        public long? SummaryId { get; init; }
        public long? MessageParentSummaryId { get; init; }
        public long? SummaryParentSummaryId { get; init; }
    }

    private sealed class RawMessageRow
    {
        public long MessageId { get; init; }
        public long SequenceId { get; init; }
        public DateTime CreatedAt { get; init; }
        public required string Payload { get; init; }
        public long? ParentSummaryId { get; init; }
    }

    private sealed class LcmSummaryRow
    {
        public long DbId { get; init; }
        public Guid SessionId { get; init; }
        public long? ParentSummaryDbId { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? LcmSummaryId { get; init; }
        public int? SummaryLevel { get; init; }
        public string? SearchText { get; init; }
        public required string Payload { get; init; }
    }

    private sealed class LcmExpandedMessageRow
    {
        public long MessageDbId { get; init; }
        public DateTime CreatedAt { get; init; }
        public required string Payload { get; init; }
    }

    private sealed class LcmGrepRow
    {
        public long MessageDbId { get; init; }
        public DateTime CreatedAt { get; init; }
        public required string Payload { get; init; }
        public string? CoveringSummaryId { get; init; }
        public int? CoveringSummaryLevel { get; init; }
    }
}

public record PersistedSession(Guid SessionId, long AgentId, string? Name, bool VisibleInSidebar, Guid? ParentSessionId, DateTime CreatedAt, DateTime UpdatedAt);
public record PersistedSessionSummary(Guid SessionId, long AgentId, string? Name, bool VisibleInSidebar, Guid? ParentSessionId, DateTime CreatedAt, DateTime UpdatedAt, long MessagesCount);
public record SessionTaskLink(string CallId, Guid ChildSessionId, bool Completed);
public record PersistedRawMessage(long MessageId, DateTime CreatedAt, ChatResponse Response);
public record LcmSummaryRecord(
    long DbId,
    Guid SessionId,
    string? LcmSummaryId,
    int? SummaryLevel,
    string Content,
    long? ParentSummaryDbId,
    DateTime CreatedAt);
public record LcmExpandedMessageRecord(
    long MessageDbId,
    string Role,
    string Content,
    DateTime CreatedAt);
public record LcmGrepMessageRecord(
    long MessageDbId,
    string Role,
    string Content,
    DateTime CreatedAt,
    string? CoveringSummaryId,
    int? CoveringSummaryLevel);
public record AgentConfig(long Id, string Name, string LlmModel, float Temperature, DateTime CreatedAt, DateTime UpdatedAt);
public record AgentDocumentSummary(string Name);
public record AgentFragmentSummary(string Name, string Path, bool HasChildren);
public record AgentDocument(string Path, string Content);

internal sealed class PersistedChatResponse
{
    public Dictionary<string, object?>? AdditionalProperties { get; init; }
    public List<PersistedChatMessage> Messages { get; init; } = [];

    public static PersistedChatResponse From(ChatResponse response)
    {
        return new PersistedChatResponse
        {
            AdditionalProperties = ToPrimitiveDictionary(response.AdditionalProperties),
            Messages = response.Messages.Select(PersistedChatMessage.From).ToList(),
        };
    }

    public ChatResponse ToChatResponse()
    {
        var messages = Messages.Select(m => m.ToChatMessage()).ToList();
        var response = new ChatResponse(messages);

        if (AdditionalProperties is not null)
            response.AdditionalProperties = ToAdditionalProperties(AdditionalProperties);

        return response;
    }

    private static Dictionary<string, object?>? ToPrimitiveDictionary(IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null || source.Count == 0)
            return null;

        return source.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    private static AdditionalPropertiesDictionary ToAdditionalProperties(Dictionary<string, object?> source)
    {
        var properties = new AdditionalPropertiesDictionary();
        foreach (var (key, value) in source)
            properties[key] = ConvertJsonLikeValue(value);

        return properties;
    }

    private static object? ConvertJsonLikeValue(object? value)
    {
        if (value is JsonElement element)
            return ConvertJsonElement(element);

        return value;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var i) => i,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.ToString(),
        };
    }
}

internal sealed class PersistedChatMessage
{
    public required string Role { get; init; }
    public string? Text { get; init; }
    public string? AuthorName { get; init; }
    public string? MessageId { get; init; }
    public Dictionary<string, object?>? AdditionalProperties { get; init; }
    public List<PersistedContent> Contents { get; init; } = [];

    public static PersistedChatMessage From(ChatMessage message)
    {
        return new PersistedChatMessage
        {
            Role = message.Role.Value,
            Text = message.Text,
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            AdditionalProperties = message.AdditionalProperties?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Contents = message.Contents.Select(PersistedContent.From).ToList(),
        };
    }

    public ChatMessage ToChatMessage()
    {
        var message = new ChatMessage(new ChatRole(Role), Text)
        {
            AuthorName = AuthorName,
            MessageId = MessageId,
        };

        if (AdditionalProperties is not null && AdditionalProperties.Count > 0)
        {
            var props = new AdditionalPropertiesDictionary();
            foreach (var (key, value) in AdditionalProperties)
                props[key] = value is JsonElement element ? ConvertJsonElement(element) : value;

            message.AdditionalProperties = props;
        }

        var skipTextContents = !string.IsNullOrWhiteSpace(Text);
        foreach (var content in Contents)
        {
            var aiContent = content.ToAiContent();
            if (aiContent is null)
                continue;

            if (skipTextContents && aiContent is TextContent)
                continue;

            message.Contents.Add(aiContent);
        }

        return message;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var i) => i,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.ToString(),
        };
    }
}

internal sealed class PersistedContent
{
    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? CallId { get; init; }
    public string? ToolName { get; init; }
    public string? Arguments { get; init; }
    public string? Result { get; init; }
    public string? Payload { get; init; }

    public static PersistedContent From(AIContent content)
    {
        return content switch
        {
            TextContent textContent => new PersistedContent
            {
                Type = "text",
                Text = textContent.Text,
            },
            FunctionCallContent functionCall => new PersistedContent
            {
                Type = "tool_call",
                CallId = functionCall.CallId,
                ToolName = functionCall.Name,
                Arguments = Serialize(functionCall.Arguments),
            },
            FunctionResultContent functionResult => new PersistedContent
            {
                Type = "tool_result",
                CallId = functionResult.CallId,
                Result = Serialize(functionResult.Result),
            },
            TextReasoningContent reasoningContent => new PersistedContent
            {
                Type = "reasoning",
                Text = reasoningContent.Text,
            },
            _ => new PersistedContent
            {
                Type = "unknown",
                Payload = Serialize(content),
            },
        };
    }

    public string ToFallbackText()
    {
        return Type switch
        {
            "tool_call" => $"[tool_call] {ToolName}({Arguments})",
            "tool_result" => $"[tool_result] {CallId}: {Result}",
            "reasoning" => string.Empty,
            "unknown" => $"[content] {Payload}",
            _ => Text ?? string.Empty,
        };
    }

    public AIContent? ToAiContent()
    {
        return Type switch
        {
            "text" when !string.IsNullOrWhiteSpace(Text) => new TextContent(Text),
            "tool_call" when !string.IsNullOrWhiteSpace(CallId) && !string.IsNullOrWhiteSpace(ToolName)
                => new FunctionCallContent(CallId, ToolName, DeserializeArguments(Arguments)),
            "tool_result" when !string.IsNullOrWhiteSpace(CallId)
                => new FunctionResultContent(CallId, DeserializeJsonLikeValue(Result)),
            "reasoning" when !string.IsNullOrWhiteSpace(Text)
                => new TextReasoningContent(Text),
            _ => null,
        };
    }

    private static string? Serialize(object? value)
    {
        if (value is null)
            return null;

        if (value is string text)
            return text;

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return value.ToString();
        }
    }

    private static object? DeserializeJsonLikeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            using var json = JsonDocument.Parse(value);
            return ConvertJsonElement(json.RootElement);
        }
        catch
        {
            return value;
        }
    }

    private static IDictionary<string, object?>? DeserializeArguments(string? value)
    {
        var parsed = DeserializeJsonLikeValue(value);
        return parsed as IDictionary<string, object?>;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var i) => i,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.ToString(),
        };
    }
}
