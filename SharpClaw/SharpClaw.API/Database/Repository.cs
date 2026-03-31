using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.AI;
using Npgsql;

namespace SharpClaw.API.Database;

public class Repository(IConfiguration configuration)
{
    private const string DbEntryTypeKey = "db_entry_type";
    private const string DbEntryIdKey = "db_entry_id";
    private const string ParentSummaryIdKey = "parent_summary_id";
    private const string DefaultAgentPrompt =
        """
        # AGENTS.md

        You are a helpful assistant. Follow workspace files and user instructions precisely.
        """;

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
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var created = await connection.QuerySingleAsync<AgentConfig>(
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
            new { name, llmModel, temperature },
            tx);

        var documentId = await connection.QuerySingleAsync<long>(
            """
            insert into documents (name, content)
            values ('AGENTS.md', @content)
            returning id;
            """,
            new { content = DefaultAgentPrompt },
            tx);

        await connection.ExecuteAsync(
            """
            insert into agents_documents (agent_id, document_id)
            values (@agentId, @documentId);
            """,
            new { agentId = created.Id, documentId },
            tx);

        await tx.CommitAsync();
        return created;
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

    public async Task CreateSession(Guid sessionId, long agentId, string systemPrompt)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            """
            insert into sessions (id, agent_id, system_prompt)
            values (@sessionId, @agentId, @systemPrompt)
            on conflict (id) do nothing;
            """,
            new { sessionId, agentId, systemPrompt });
    }

    public async Task<PersistedSession?> GetSession(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<PersistedSession>(
            """
            select id as SessionId,
                   agent_id as AgentId,
                   system_prompt as SystemPrompt,
                   created_at as CreatedAt
            from sessions
            where id = @sessionId;
            """,
            new { sessionId });
    }

    public async Task<IReadOnlyList<PersistedSessionSummary>> GetSessions(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<PersistedSessionSummary>(
            """
            select s.id as SessionId,
                   s.agent_id as AgentId,
                   s.created_at as CreatedAt,
                   coalesce((
                       select count(*)
                       from conversation_history ch
                       where ch.session_id = s.id and ch.is_active
                   ), 0) as MessagesCount
            from sessions s
            where s.agent_id = @agentId
            order by s.created_at desc;
            """,
            new { agentId });

        return rows.ToArray();
    }

    public async Task<long> PersistMessage(Guid sessionId, Guid? runId, ChatResponse response)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var payload = SerializeResponse(response);

        var messageId = await connection.ExecuteScalarAsync<long>(
            """
            insert into messages (session_id, run_id, payload)
            values (@sessionId, @runId, cast(@payload as jsonb))
            returning id;
            """,
            new { sessionId, runId, payload },
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
        return messageId;
    }

    public async Task<long> PersistSummaryAndCompactHistory(Guid sessionId, Guid? runId, ChatResponse summary,
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

        var summaryId = await connection.ExecuteScalarAsync<long>(
            """
            insert into summaries (session_id, run_id, payload)
            values (@sessionId, @runId, cast(@payload as jsonb))
            returning id;
            """,
            new { sessionId, runId, payload },
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
                   end as Payload
            from conversation_history ch
            left join messages m on ch.message_id = m.id
            left join summaries s on ch.summary_id = s.id
            where ch.session_id = @sessionId
              and ch.is_active
            order by ch.sequence, ch.id;
            """,
            new { sessionId });

        return rows.Select(r => DeserializeResponse(r.Payload)).ToArray();
    }

    public async Task<IReadOnlyList<PersistedRawMessage>> LoadRawMessages(Guid sessionId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<RawMessageRow>(
            """
            select id as MessageId,
                   run_id as RunId,
                   created_at as CreatedAt,
                   payload::text as Payload
            from messages
            where session_id = @sessionId
            order by created_at, id;
            """,
            new { sessionId });

        return rows
            .Select(r => new PersistedRawMessage(r.MessageId, r.RunId, r.CreatedAt, DeserializeResponse(r.Payload)))
            .ToArray();
    }

    public static void SetDbReference(ChatResponse response, string type, long id, long? parentSummaryId = null)
    {
        response.AdditionalProperties ??= new AdditionalPropertiesDictionary();
        response.AdditionalProperties[DbEntryTypeKey] = type;
        response.AdditionalProperties[DbEntryIdKey] = id;

        if (parentSummaryId is not null)
            response.AdditionalProperties[ParentSummaryIdKey] = parentSummaryId.Value;
    }

    public static void SetParentSummaryReference(ChatResponse response, long parentSummaryId)
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
    }

    private sealed class RawMessageRow
    {
        public long MessageId { get; init; }
        public Guid? RunId { get; init; }
        public DateTime CreatedAt { get; init; }
        public required string Payload { get; init; }
    }
}

public record PersistedSession(Guid SessionId, long AgentId, string SystemPrompt, DateTime CreatedAt);
public record PersistedSessionSummary(Guid SessionId, long AgentId, DateTime CreatedAt, long MessagesCount);
public record PersistedRawMessage(long MessageId, Guid? RunId, DateTime CreatedAt, ChatResponse Response);
public record AgentConfig(long Id, string Name, string LlmModel, float Temperature, DateTime CreatedAt, DateTime UpdatedAt);
public record AgentDocumentSummary(string Name);
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
