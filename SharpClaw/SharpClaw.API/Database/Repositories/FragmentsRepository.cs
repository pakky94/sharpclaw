using System.Text.Json;
using Dapper;
using Npgsql;

namespace SharpClaw.API.Database.Repositories;

public class FragmentsRepository(IConfiguration configuration, FragmentEmbeddingService embeddingService)
{
    private const string RootName = "__root__";

    private string ConnectionString => configuration.GetConnectionString("sharpclaw")
                                       ?? throw new InvalidOperationException("Missing connection string 'sharpclaw'.");

    public async Task<string> EnsureRootFragment(long agentId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<string>(
            """
            with existing as (
                select id
                from fragments
                where owner_agent_id = @agentId
                  and parent_id is null
                  and name = @rootName
                limit 1
            ),
            inserted as (
                insert into fragments (owner_agent_id, parent_id, name, content, fragment_type, tags)
                select @agentId, null, @rootName, '', 'root', cast('{}' as jsonb)
                where not exists (select 1 from existing)
                returning id
            )
            select id from inserted
            union all
            select id from existing
            limit 1;
            """,
            new { agentId, rootName = RootName });
    }

    public async Task<string> CreateFragment(
        long agentId,
        string name,
        string? parentId,
        string content,
        string? type,
        IReadOnlyDictionary<string, string>? tags)
    {
        var effectiveParentId = parentId ?? await EnsureRootFragment(agentId);
        var tagsJson = SerializeTags(tags);

        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QuerySingleAsync<string>(
            """
            insert into fragments (owner_agent_id, parent_id, name, content, fragment_type, tags, embedding)
            values (@agentId, @parentId, @name, @content, @type, cast(@tagsJson as jsonb), null)
            returning id;
            """,
            new
            {
                agentId,
                parentId = effectiveParentId,
                name,
                content,
                type,
                tagsJson,
            });
    }

    public async Task<FragmentReadResponse?> ReadFragment(
        long agentId,
        string id,
        bool includeChildren = true,
        int maxDepth = 1,
        bool childNamesOnly = true)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var root = await connection.QueryFirstOrDefaultAsync<FragmentRow>(
            """
            select f.id as Id,
                   f.name as Name,
                   f.parent_id as ParentId,
                   f.content as Content,
                   f.fragment_type as Type,
                   f.tags::text as TagsJson,
                   f.created_at as CreatedAt,
                   f.updated_at as UpdatedAt,
                   case
                       when f.owner_agent_id = @agentId then 'owned'
                       when exists (
                           select 1
                           from fragment_shares fs
                           where fs.fragment_id = f.id
                             and fs.target_agent_id = @agentId
                             and fs.permission = 'read-write'
                       ) then 'read-write'
                       when exists (
                           select 1
                           from fragment_shares fs
                           where fs.fragment_id = f.id
                             and fs.target_agent_id = @agentId
                             and fs.permission = 'read-only'
                       ) then 'read-only'
                       else null
                   end as Permission
            from fragments f
            where f.id = @id
            limit 1;
            """,
            new { agentId, id });

        if (root is null || string.IsNullOrWhiteSpace(root.Permission))
            return null;

        var children = Array.Empty<FragmentReadItem>();
        if (includeChildren)
        {
            var boundedDepth = Math.Max(1, maxDepth);
            var rows = await connection.QueryAsync<FragmentDepthRow>(
                """
                with recursive tree as (
                    select f.id,
                           f.name,
                           f.parent_id,
                           f.content,
                           f.fragment_type,
                           f.tags,
                           f.created_at,
                           f.updated_at,
                           1 as depth
                    from fragments f
                    where f.parent_id = @id

                    union all

                    select c.id,
                           c.name,
                           c.parent_id,
                           c.content,
                           c.fragment_type,
                           c.tags,
                           c.created_at,
                           c.updated_at,
                           t.depth + 1 as depth
                    from fragments c
                    join tree t on c.parent_id = t.id
                    where t.depth < @maxDepth
                )
                select t.id as Id,
                       t.name as Name,
                       t.parent_id as ParentId,
                       case when @childNamesOnly then '' else t.content end as Content,
                       t.fragment_type as Type,
                       t.tags::text as TagsJson,
                       t.created_at as CreatedAt,
                       t.updated_at as UpdatedAt,
                       t.depth as Depth,
                       case
                           when exists (
                               select 1
                               from fragments owner_match
                               where owner_match.id = t.id
                                 and owner_match.owner_agent_id = @agentId
                           ) then 'owned'
                           when exists (
                               select 1
                               from fragment_shares fs
                               where fs.fragment_id = t.id
                                 and fs.target_agent_id = @agentId
                                 and fs.permission = 'read-write'
                           ) then 'read-write'
                           when exists (
                               select 1
                               from fragment_shares fs
                               where fs.fragment_id = t.id
                                 and fs.target_agent_id = @agentId
                                 and fs.permission = 'read-only'
                           ) then 'read-only'
                           else null
                       end as Permission
                from tree t
                order by t.depth, t.created_at, t.id;
                """,
                new { agentId, id, maxDepth = boundedDepth, childNamesOnly });

            children = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Permission))
                .Select(ToReadItem)
                .ToArray();
        }

        return new FragmentReadResponse(
            Id: root.Id,
            Name: root.Name,
            ParentId: root.ParentId,
            Content: root.Content,
            Type: root.Type,
            Tags: ParseTags(root.TagsJson),
            Permissions: root.Permission!,
            Children: children);
    }

    public async Task<bool> UpdateFragment(
        long agentId,
        string id,
        string? content = null,
        IReadOnlyDictionary<string, string>? tags = null,
        string? type = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        var affected = await connection.ExecuteAsync(
            """
            update fragments f
            set content = coalesce(@content, f.content),
                tags = case when @tagsJson is null then f.tags else cast(@tagsJson as jsonb) end,
                fragment_type = coalesce(@type, f.fragment_type),
                embedding = case when @content is null and @type is null then f.embedding else null end,
                updated_at = now()
            where f.id = @id
              and (
                  f.owner_agent_id = @agentId
                  or exists (
                      select 1
                      from fragment_shares fs
                      where fs.fragment_id = f.id
                        and fs.target_agent_id = @agentId
                        and fs.permission = 'read-write'
                  )
              );
            """,
            new
            {
                agentId,
                id,
                content,
                tagsJson = tags is null ? null : SerializeTags(tags),
                type,
            });

        return affected > 0;
    }

    public async Task<bool> DeleteFragment(long agentId, string id, bool recursive = true)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var permission = await connection.QueryFirstOrDefaultAsync<string?>(
            """
            select case
                       when f.owner_agent_id = @agentId then 'owned'
                       else null
                   end
            from fragments f
            where f.id = @id
            limit 1;
            """,
            new { agentId, id },
            tx);

        if (!string.Equals(permission, "owned", StringComparison.Ordinal))
            return false;

        if (!recursive)
        {
            var childrenCount = await connection.QuerySingleAsync<int>(
                """
                select count(*)
                from fragments
                where parent_id = @id;
                """,
                new { id },
                tx);

            if (childrenCount > 0)
                return false;
        }

        var affected = await connection.ExecuteAsync(
            """
            delete from fragments
            where id = @id;
            """,
            new { id },
            tx);

        await tx.CommitAsync();
        return affected > 0;
    }

    public async Task<bool> MoveFragment(long agentId, string fragmentId, string newParentId, string? newName)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        var hasWritePermission = await connection.QueryFirstOrDefaultAsync<bool?>(
            """
            select (
                f.owner_agent_id = @agentId
                or exists (
                    select 1
                    from fragment_shares fs
                    where fs.fragment_id = f.id
                      and fs.target_agent_id = @agentId
                      and fs.permission = 'read-write'
                )
            )
            from fragments f
            where f.id = @fragmentId
            limit 1;
            """,
            new { agentId, fragmentId },
            tx);

        if (hasWritePermission is not true)
            return false;

        var isSelfOrDescendant = await connection.QueryFirstOrDefaultAsync<bool>(
            """
            with recursive descendants as (
                select f.id
                from fragments f
                where f.id = @fragmentId

                union all

                select c.id
                from fragments c
                join descendants d on c.parent_id = d.id
            )
            select exists(select 1 from descendants where id = @newParentId);
            """,
            new { fragmentId, newParentId },
            tx);

        if (isSelfOrDescendant)
            return false;

        var affected = await connection.ExecuteAsync(
            """
            update fragments
            set parent_id = @newParentId,
                name = coalesce(@newName, name),
                embedding = case when @newName is null then embedding else null end,
                updated_at = now()
            where id = @fragmentId;
            """,
            new { fragmentId, newParentId, newName },
            tx);

        await tx.CommitAsync();
        return affected > 0;
    }

    public async Task<IReadOnlyList<FragmentSearchResult>> SearchFragments(
        long agentId,
        string query,
        int topK = 5,
        IReadOnlyDictionary<string, string>? tagFilter = null,
        string? typeFilter = null,
        string? parentId = null)
    {
        var effectiveTopK = Math.Clamp(topK, 1, 50);
        var tagsJson = tagFilter is null ? null : SerializeTags(tagFilter);
        var queryEmbedding = await embeddingService.TryGenerateEmbedding(query);

        await using var connection = new NpgsqlConnection(ConnectionString);
        if (queryEmbedding is { Length: > 0 })
        {
            var vectorRows = await connection.QueryAsync<FragmentSearchRow>(
                """
                with recursive subtree as (
                    select f.id
                    from fragments f
                    where @parentId is not null and f.id = @parentId

                    union all

                    select c.id
                    from fragments c
                    join subtree s on c.parent_id = s.id
                ),
                accessible as (
                    select f.id,
                           f.name,
                           f.content,
                           case
                               when f.owner_agent_id = @agentId then 'owned'
                               when exists (
                                   select 1
                                   from fragment_shares fs
                                   where fs.fragment_id = f.id
                                     and fs.target_agent_id = @agentId
                                     and fs.permission = 'read-write'
                               ) then 'read-write'
                               when exists (
                                   select 1
                                   from fragment_shares fs
                                   where fs.fragment_id = f.id
                                     and fs.target_agent_id = @agentId
                                     and fs.permission = 'read-only'
                               ) then 'read-only'
                               else null
                           end as permission,
                           f.embedding <=> cast(@queryEmbedding as vector) as score
                    from fragments f
                    where (
                              f.owner_agent_id = @agentId
                              or exists (
                                  select 1
                                  from fragment_shares fs
                                  where fs.fragment_id = f.id
                                    and fs.target_agent_id = @agentId
                              )
                          )
                      and f.embedding is not null
                      and (@parentId is null or f.id in (select id from subtree))
                      and (@typeFilter is null or f.fragment_type = @typeFilter)
                      and (@tagsJson is null or f.tags @> cast(@tagsJson as jsonb))
                )
                select id as Id,
                       name as Name,
                       left(content, 240) as Snippet,
                       permission as Permission,
                       score as Score
                from accessible
                where permission is not null
                order by score asc, id
                limit @topK;
                """,
                new
                {
                    agentId,
                    topK = effectiveTopK,
                    tagsJson,
                    typeFilter,
                    parentId,
                    queryEmbedding = ToVectorLiteral(queryEmbedding),
                });

            var materialized = vectorRows.ToArray();
            if (materialized.Length > 0)
            {
                return materialized
                    .Select(r => new FragmentSearchResult(r.Id, r.Name, r.Snippet, r.Permission))
                    .ToArray();
            }
        }

        var rows = await connection.QueryAsync<FragmentSearchRow>(
            """
            with recursive subtree as (
                select f.id
                from fragments f
                where @parentId is not null and f.id = @parentId

                union all

                select c.id
                from fragments c
                join subtree s on c.parent_id = s.id
            ),
            accessible as (
                select f.id,
                       f.name,
                       f.content,
                       case
                           when f.owner_agent_id = @agentId then 'owned'
                           when exists (
                               select 1
                               from fragment_shares fs
                               where fs.fragment_id = f.id
                                 and fs.target_agent_id = @agentId
                                 and fs.permission = 'read-write'
                           ) then 'read-write'
                           when exists (
                               select 1
                               from fragment_shares fs
                               where fs.fragment_id = f.id
                                 and fs.target_agent_id = @agentId
                                 and fs.permission = 'read-only'
                           ) then 'read-only'
                           else null
                       end as permission,
                       greatest(similarity(f.name, @query), similarity(f.content, @query)) as score
                from fragments f
                where (
                          f.owner_agent_id = @agentId
                          or exists (
                              select 1
                              from fragment_shares fs
                              where fs.fragment_id = f.id
                                and fs.target_agent_id = @agentId
                          )
                      )
                  and (@parentId is null or f.id in (select id from subtree))
                  and (@typeFilter is null or f.fragment_type = @typeFilter)
                  and (@tagsJson is null or f.tags @> cast(@tagsJson as jsonb))
                  and (
                      f.name % @query
                      or f.content % @query
                      or f.name ilike '%' || @query || '%'
                      or f.content ilike '%' || @query || '%'
                  )
            )
            select id as Id,
                   name as Name,
                   left(content, 240) as Snippet,
                   permission as Permission,
                   score as Score
            from accessible
            where permission is not null
            order by score desc, id
            limit @topK;
            """,
            new
            {
                agentId,
                query,
                topK = effectiveTopK,
                tagsJson,
                typeFilter,
                parentId,
            });

        return rows
            .Select(r => new FragmentSearchResult(r.Id, r.Name, r.Snippet, r.Permission))
            .ToArray();
    }

    public async Task<string?> ResolveChild(long agentId, string parentId, string childName)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<string?>(
            """
            select f.id
            from fragments f
            where f.parent_id = @parentId
              and f.name = @childName
              and (
                  f.owner_agent_id = @agentId
                  or exists (
                      select 1
                      from fragment_shares fs
                      where fs.fragment_id = f.id
                        and fs.target_agent_id = @agentId
                  )
              )
            order by f.created_at, f.id
            limit 1;
            """,
            new { agentId, parentId, childName });
    }

    public async Task<bool> ShareFragment(long agentId, string fragmentId, long targetAgentId, string permission)
    {
        if (permission is not ("read-only" or "read-write"))
            return false;

        await using var connection = new NpgsqlConnection(ConnectionString);
        var affected = await connection.ExecuteAsync(
            """
            insert into fragment_shares (fragment_id, target_agent_id, permission)
            select @fragmentId, @targetAgentId, @permission
            where exists (
                select 1
                from fragments f
                where f.id = @fragmentId
                  and f.owner_agent_id = @agentId
            )
            on conflict (fragment_id, target_agent_id)
            do update set permission = excluded.permission, updated_at = now();
            """,
            new { agentId, fragmentId, targetAgentId, permission });

        return affected > 0;
    }

    public async Task<IReadOnlyList<AgentDocumentSummary>> ListFragmentsAsFiles(long agentId)
    {
        var rootId = await EnsureRootFragment(agentId);

        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<string>(
            """
            with recursive tree as (
                select f.id,
                       f.parent_id,
                       f.name,
                       f.name::text as path
                from fragments f
                where f.owner_agent_id = @agentId
                  and f.parent_id = @rootId

                union all

                select c.id,
                       c.parent_id,
                       c.name,
                       t.path || '/' || c.name as path
                from fragments c
                join tree t on c.parent_id = t.id
                where c.owner_agent_id = @agentId
            )
            select t.path
            from tree t
            order by t.path;
            """,
            new { agentId, rootId });

        return rows.Select(path => new AgentDocumentSummary(path)).ToArray();
    }

    public async Task<IReadOnlyList<AgentFragmentSummary>> ListFragmentChildren(long agentId, string? parentPath = null)
    {
        var rootId = await EnsureRootFragment(agentId);
        var effectiveParentPath = string.IsNullOrWhiteSpace(parentPath) ? null : parentPath.Trim();
        var parentId = rootId;

        if (effectiveParentPath is not null)
        {
            var parentFragment = await ResolvePath(agentId, effectiveParentPath);
            if (parentFragment is null)
                return [];
            parentId = parentFragment.Id;
        }

        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<FragmentTreeRow>(
            """
            select c.name as Name,
                   case
                       when @parentPath is null or @parentPath = '' then c.name
                       else @parentPath || '/' || c.name
                   end as Path,
                   exists(
                       select 1
                       from fragments gc
                       where gc.owner_agent_id = @agentId
                         and gc.parent_id = c.id
                   ) as HasChildren
            from fragments c
            where c.owner_agent_id = @agentId
              and c.parent_id = @parentId
            order by c.name, c.id;
            """,
            new { agentId, parentId, parentPath = effectiveParentPath });

        return rows.Select(r => new AgentFragmentSummary(r.Name, r.Path, r.HasChildren)).ToArray();
    }

    public async Task<AgentDocument?> ReadFragmentByPath(long agentId, string path)
    {
        var fragment = await ResolvePath(agentId, path);
        if (fragment is null)
            return null;

        return new AgentDocument(path, fragment.Content);
    }

    public async Task UpsertFragmentByPath(long agentId, string path, string content)
    {
        var segments = SplitPath(path);
        if (segments.Length == 0)
            throw new ArgumentException("Path cannot be empty.", nameof(path));

        var rootId = await EnsureRootFragment(agentId);
        var currentParent = rootId;

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            var existingId = await connection.QueryFirstOrDefaultAsync<string?>(
                """
                select id
                from fragments
                where owner_agent_id = @agentId
                  and parent_id = @parentId
                  and name = @name
                order by created_at, id
                limit 1;
                """,
                new { agentId, parentId = currentParent, name = segment },
                tx);

            if (existingId is not null)
            {
                currentParent = existingId;
                continue;
            }

            currentParent = await connection.QuerySingleAsync<string>(
                """
                insert into fragments (owner_agent_id, parent_id, name, content, fragment_type, tags, embedding)
                values (@agentId, @parentId, @name, '', 'knowledge', cast('{}' as jsonb), null)
                returning id;
                """,
                new { agentId, parentId = currentParent, name = segment },
                tx);
        }

        var finalName = segments[^1];
        var existingLeaf = await connection.QueryFirstOrDefaultAsync<string?>(
            """
            select id
            from fragments
            where owner_agent_id = @agentId
              and parent_id = @parentId
              and name = @name
            order by created_at, id
            limit 1;
            """,
            new { agentId, parentId = currentParent, name = finalName },
            tx);

        if (existingLeaf is null)
        {
            await connection.ExecuteAsync(
                """
                insert into fragments (owner_agent_id, parent_id, name, content, fragment_type, tags, embedding)
                values (@agentId, @parentId, @name, @content, 'knowledge', cast('{}' as jsonb), null);
                """,
                new { agentId, parentId = currentParent, name = finalName, content },
                tx);
        }
        else
        {
            await connection.ExecuteAsync(
                """
                update fragments
                set content = @content,
                    embedding = null,
                    updated_at = now()
                where id = @id;
                """,
                new { id = existingLeaf, content },
                tx);
        }

        await tx.CommitAsync();
    }

    public async Task<bool> DeleteFragmentByPath(long agentId, string path)
    {
        var fragment = await ResolvePath(agentId, path);
        if (fragment is null)
            return false;

        return await DeleteFragment(agentId, fragment.Id, recursive: true);
    }

    private async Task<FragmentRow?> ResolvePath(long agentId, string path)
    {
        var segments = SplitPath(path);
        if (segments.Length == 0)
            return null;

        var currentParent = await EnsureRootFragment(agentId);
        await using var connection = new NpgsqlConnection(ConnectionString);
        FragmentRow? current = null;

        foreach (var segment in segments)
        {
            current = await connection.QueryFirstOrDefaultAsync<FragmentRow>(
                """
                select f.id as Id,
                       f.name as Name,
                       f.parent_id as ParentId,
                       f.content as Content,
                       f.fragment_type as Type,
                       f.tags::text as TagsJson,
                       f.created_at as CreatedAt,
                       f.updated_at as UpdatedAt,
                       'owned' as Permission
                from fragments f
                where f.owner_agent_id = @agentId
                  and f.parent_id = @parentId
                  and f.name = @segment
                order by f.created_at, f.id
                limit 1;
                """,
                new { agentId, parentId = currentParent, segment });

            if (current is null)
                return null;

            currentParent = current.Id;
        }

        return current;
    }

    public async Task<IReadOnlyList<PendingFragmentEmbedding>> GetPendingEmbeddings(int limit = 32, CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, 256);
        await using var connection = new NpgsqlConnection(ConnectionString);
        var rows = await connection.QueryAsync<PendingFragmentEmbeddingRow>(
            new CommandDefinition(
                """
                select f.id as Id,
                       f.name as Name,
                       f.fragment_type as Type,
                       f.content as Content,
                       f.updated_at as UpdatedAt
                from fragments f
                where f.embedding is null
                  and not (f.parent_id is null and f.name = @rootName)
                order by f.updated_at, f.id
                limit @limit;
                """,
                new { rootName = RootName, limit = boundedLimit },
                cancellationToken: cancellationToken));

        return rows
            .Select(r => new PendingFragmentEmbedding(r.Id, r.Name, r.Type, r.Content, r.UpdatedAt))
            .ToArray();
    }

    public async Task<bool> TrySetFragmentEmbedding(string fragmentId, DateTime updatedAt, IReadOnlyList<float> embedding, CancellationToken cancellationToken = default)
    {
        var embeddingLiteral = ToVectorLiteral(embedding);
        await using var connection = new NpgsqlConnection(ConnectionString);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                """
                update fragments
                set embedding = cast(@embedding as vector)
                where id = @fragmentId
                  and embedding is null
                  and updated_at = @updatedAt;
                """,
                new { fragmentId, updatedAt, embedding = embeddingLiteral },
                cancellationToken: cancellationToken));

        return affected > 0;
    }

    private static string[] SplitPath(string path)
    {
        return (path ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string SerializeTags(IReadOnlyDictionary<string, string>? tags)
    {
        return JsonSerializer.Serialize(tags ?? new Dictionary<string, string>());
    }

    internal static string ToVectorLiteral(IReadOnlyList<float> embedding)
    {
        return "[" + string.Join(",", embedding.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))) + "]";
    }

    private static Dictionary<string, string> ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(tagsJson) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static FragmentReadItem ToReadItem(FragmentDepthRow row)
    {
        return new FragmentReadItem(
            Id: row.Id,
            Name: row.Name,
            ParentId: row.ParentId,
            Content: row.Content,
            Type: row.Type,
            Tags: ParseTags(row.TagsJson),
            Permissions: row.Permission!,
            Depth: row.Depth);
    }

    private sealed class FragmentRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? ParentId { get; init; }
        public required string Content { get; init; }
        public string? Type { get; init; }
        public string? TagsJson { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string? Permission { get; init; }
    }

    private sealed class FragmentDepthRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? ParentId { get; init; }
        public required string Content { get; init; }
        public string? Type { get; init; }
        public string? TagsJson { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string? Permission { get; init; }
        public int Depth { get; init; }
    }

    private sealed class FragmentSearchRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Snippet { get; init; }
        public required string Permission { get; init; }
        public float Score { get; init; }
    }

    private sealed class PendingFragmentEmbeddingRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Type { get; init; }
        public required string Content { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class FragmentTreeRow
    {
        public required string Name { get; init; }
        public required string Path { get; init; }
        public bool HasChildren { get; init; }
    }
}

internal sealed record FragmentContentRow
{
    public required string Id { get; init; }
    public required string Content { get; init; }
    public required string Permission { get; init; }
}

public sealed record FragmentReadResponse(
    string Id,
    string Name,
    string? ParentId,
    string Content,
    string? Type,
    IReadOnlyDictionary<string, string> Tags,
    string Permissions,
    IReadOnlyList<FragmentReadItem> Children);

public sealed record FragmentReadItem(
    string Id,
    string Name,
    string? ParentId,
    string Content,
    string? Type,
    IReadOnlyDictionary<string, string> Tags,
    string Permissions,
    int Depth);

public sealed record FragmentSearchResult(
    string Id,
    string Name,
    string Snippet,
    string Permissions);

public sealed record PendingFragmentEmbedding(
    string Id,
    string Name,
    string? Type,
    string Content,
    DateTime UpdatedAt);

public record AgentDocumentSummary(string Name);
public record AgentFragmentSummary(string Name, string Path, bool HasChildren);
public record AgentDocument(string Path, string Content);
