using Dapper;
using Npgsql;

namespace SharpClaw.API.Database;

public partial class DatabaseSeeder(IConfiguration configuration)
{
    public async Task Seed()
    {
        var connectionString = configuration.GetConnectionString("sharpclaw");
        await using var connection = new NpgsqlConnection(connectionString);

        try
        {
            connection.Open();

            await connection.ExecuteAsync(
                """
                create extension if not exists pg_trgm;
                create extension if not exists pgcrypto;
                create extension if not exists vector;
                
                create or replace function generate_fragment_id()
                returns varchar(21)
                language sql
                as $$
                    with entropy as (
                        select gen_random_bytes(16) as bytes
                    )
                    select 'frag_' || string_agg(
                        substr('0123456789abcdefghijklmnopqrstuvwxyz', (get_byte(e.bytes, i) % 36) + 1, 1),
                        ''
                        order by i
                    )
                    from entropy e
                    cross join generate_series(0, 15) as i;
                $$;

                create table if not exists agents(
                    id bigserial primary key,
                    name varchar(511) not null,
                    llm_model varchar(255) not null default 'openai/gpt-oss-20b',
                    temperature real not null default 0.1,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create table if not exists sessions(
                    id uuid primary key,
                    agent_id bigint not null references agents(id),
                    system_prompt text not null,
                    created_at timestamptz not null default now()
                );

                create table if not exists fragments(
                    id varchar(21) primary key default generate_fragment_id(),
                    owner_agent_id bigint not null references agents(id) on delete cascade,
                    parent_id varchar(21) null references fragments(id) on delete cascade,
                    name varchar(511) not null,
                    content text not null default '',
                    fragment_type varchar(64) null,
                    tags jsonb not null default '{}'::jsonb,
                    embedding vector null,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now(),
                    constraint fragments_id_format_chk check (id ~ '^frag_[a-z0-9]{16}$'),
                    constraint fragments_parent_id_format_chk check (parent_id is null or parent_id ~ '^frag_[a-z0-9]{16}$')
                );

                create table if not exists fragment_shares(
                    id bigserial primary key,
                    fragment_id varchar(21) not null references fragments(id) on delete cascade,
                    target_agent_id bigint not null references agents(id) on delete cascade,
                    permission varchar(16) not null check (permission in ('read-only', 'read-write')),
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now(),
                    constraint fragment_shares_fragment_id_format_chk check (fragment_id ~ '^frag_[a-z0-9]{16}$'),
                    unique(fragment_id, target_agent_id)
                );

                create table if not exists summaries(
                    id bigserial primary key,
                    session_id uuid not null references sessions(id) on delete cascade,
                    run_id uuid null,
                    parent_summary_id bigint null references summaries(id),
                    payload jsonb not null,
                    search_text text not null default '',
                    lcm_summary_id varchar(128) null,
                    lcm_summary_level int null,
                    created_at timestamptz not null default now()
                );

                create table if not exists messages(
                    id bigserial primary key,
                    session_id uuid not null references sessions(id) on delete cascade,
                    run_id uuid null,
                    parent_summary_id bigint null references summaries(id),
                    payload jsonb not null,
                    role varchar(32) null,
                    search_text text not null default '',
                    created_at timestamptz not null default now()
                );

                create table if not exists conversation_history(
                    id bigserial primary key,
                    session_id uuid not null references sessions(id) on delete cascade,
                    sequence bigint not null,
                    entry_type varchar(16) not null check (entry_type in ('message', 'summary')),
                    message_id bigint null references messages(id) on delete cascade,
                    summary_id bigint null references summaries(id) on delete cascade,
                    is_active boolean not null default true,
                    created_at timestamptz not null default now(),
                    constraint conversation_history_target_chk check (
                        (entry_type = 'message' and message_id is not null and summary_id is null) or
                        (entry_type = 'summary' and summary_id is not null and message_id is null)
                    )
                );

                create index if not exists idx_sessions_agent_created_at
                    on sessions(agent_id, created_at desc);

                create index if not exists idx_messages_session_created_at
                    on messages(session_id, created_at, id);
                create index if not exists idx_messages_role
                    on messages(role);
                create index if not exists idx_messages_search_text_trgm
                    on messages using gin (search_text gin_trgm_ops);

                create index if not exists idx_summaries_session_created_at
                    on summaries(session_id, created_at, id);
                create index if not exists idx_summaries_session_lcm_summary_id
                    on summaries(session_id, lcm_summary_id);
                create index if not exists idx_summaries_search_text_trgm
                    on summaries using gin (search_text gin_trgm_ops);

                create index if not exists idx_conversation_history_session_active_sequence
                    on conversation_history(session_id, is_active, sequence, id);

                create index if not exists idx_fragments_owner_parent
                    on fragments(owner_agent_id, parent_id);
                create index if not exists idx_fragments_owner_name
                    on fragments(owner_agent_id, name);
                create index if not exists idx_fragments_content_trgm
                    on fragments using gin (content gin_trgm_ops);
                create index if not exists idx_fragments_tags
                    on fragments using gin (tags);
                create index if not exists idx_fragment_shares_target
                    on fragment_shares(target_agent_id);
                """);

            if (await connection.ExecuteScalarAsync<int>("select count(*) from agents where name = 'Main'") == 0)
                await connection.ExecuteAsync(
                    """
                    insert into agents (name, llm_model, temperature)
                    values ('Main', 'zai-org/glm-4.7-flash', 0.1);
                    """);

            await connection.ExecuteAsync(
                """
                with roots as (
                    insert into fragments (owner_agent_id, parent_id, name, content, fragment_type, tags)
                    select a.id, null, '__root__', '', 'root', cast('{}' as jsonb)
                    from agents a
                    where not exists (
                        select 1
                        from fragments f
                        where f.owner_agent_id = a.id
                          and f.parent_id is null
                          and f.name = '__root__'
                    )
                    returning id, owner_agent_id
                ),
                all_roots as (
                    select f.id, f.owner_agent_id
                    from fragments f
                    where f.parent_id is null and f.name = '__root__'
                    union all
                    select r.id, r.owner_agent_id
                    from roots r
                ),
                seed_documents(name, content) as (
                    values ('AGENTS.md', @AgentsMd),
                           ('BOOTSTRAP.md', @BootstrapMd),
                           ('HEARTBEAT.md', @HeartbeatMd),
                           ('IDENTITY.md', @IdentityMd),
                           ('SOUL.md', @SoulMd),
                           ('TOOLS.md', @ToolsMd),
                           ('USER.md', @UserMd)
                )
                insert into fragments (owner_agent_id, parent_id, name, content, fragment_type, tags)
                select ar.owner_agent_id,
                       ar.id as parent_id,
                       d.name,
                       d.content,
                       'knowledge',
                       cast('{}' as jsonb)
                from all_roots ar
                cross join seed_documents d
                where not exists (
                    select 1
                    from fragments f
                    where f.owner_agent_id = ar.owner_agent_id
                      and f.parent_id = ar.id
                      and f.name = d.name
                );
                """, new
                {
                    AgentsMd,
                    BootstrapMd,
                    HeartbeatMd,
                    IdentityMd,
                    SoulMd,
                    ToolsMd,
                    UserMd,
                });

        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }
}
