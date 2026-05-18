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
                    soft_compact_threshold bigint not null default 76800,
                    hard_compact_threshold bigint not null default 87040,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create table if not exists sessions(
                    id uuid primary key,
                    agent_id bigint not null references agents(id),
                    name varchar(255) null,
                    visible_in_sidebar boolean not null default true,
                    status varchar(32) not null default 'completed'
                        check (status in ('waiting', 'pending', 'running', 'completed', 'failed')),
                    parent_session_id uuid null references sessions(id),
                    tag varchar(255) null,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create table if not exists session_tasks
                (
                    id bigserial primary key,
                    session_id uuid not null references sessions(id) on delete cascade,
                    type varchar(255) not null constraint session_task_type_check check
                        (type in ('task')),
                    call_id varchar(255) not null,
                    child_session_id uuid null references sessions(id) on delete cascade,
                    result text null,
                    completed boolean not null default false,
                    blocking boolean not null default true,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
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
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create table if not exists messages(
                    id bigserial primary key,
                    session_id uuid not null references sessions(id) on delete cascade,
                    parent_summary_id bigint null references summaries(id),
                    payload jsonb not null,
                    role varchar(32) null,
                    search_text text not null default '',
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
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
                    updated_at timestamptz not null default now(),
                    constraint conversation_history_target_chk check (
                        (entry_type = 'message' and message_id is not null and summary_id is null) or
                        (entry_type = 'summary' and summary_id is not null and message_id is null)
                    )
                );

                create index if not exists idx_sessions_agent_created_at
                    on sessions(agent_id, created_at desc);
                create index if not exists idx_sessions_agent_updated_at
                    on sessions(agent_id, updated_at desc);

                alter table sessions
                    add column if not exists name varchar(255) null;
                alter table sessions
                    add column if not exists visible_in_sidebar boolean not null default true;

                alter table sessions
                    add column if not exists tag varchar(255) null;

                create unique index if not exists idx_sessions_agent_tag
                    on sessions(agent_id, tag)
                    where tag is not null;

                alter table agents
                    add column if not exists soft_compact_threshold bigint not null default 76800;
                alter table agents
                    add column if not exists hard_compact_threshold bigint not null default 87040;

                update sessions
                set visible_in_sidebar = false
                where parent_session_id is not null
                  and visible_in_sidebar = true;

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

                create table if not exists workspaces(
                    id bigserial primary key,
                    name varchar(255) not null,
                    root_path text not null,
                    allowlist_patterns jsonb not null default '[]'::jsonb,
                    denylist_patterns jsonb not null default '[]'::jsonb,
                    runtime_kind varchar(32) not null default 'local'
                        check (runtime_kind in ('local', 'bridge')),
                    runtime_target varchar(255) null,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now(),
                    unique(name)
                );

                alter table workspaces
                    add column if not exists runtime_kind varchar(32) not null default 'local'
                        check (runtime_kind in ('local', 'bridge'));

                alter table workspaces
                    add column if not exists runtime_target varchar(255) null;

                create table if not exists agent_workspace_assignments(
                    id bigserial primary key,
                    agent_id bigint not null references agents(id) on delete cascade,
                    workspace_id bigint not null references workspaces(id) on delete cascade,
                    policy_mode varchar(32) not null default 'confirm_writes_and_exec'
                        check (policy_mode in ('unrestricted', 'true_unrestricted', 'confirm_writes_and_exec', 'confirm_exec_only', 'read_only', 'disabled')),
                    is_default boolean not null default false,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now(),
                    unique(agent_id, workspace_id)
                );

                create table if not exists workspace_approval_events(
                    id bigserial primary key,
                    session_id uuid not null references sessions(id) on delete cascade,
                    agent_id bigint not null references agents(id),
                    approval_token varchar(64) not null unique,
                    action_type varchar(32) not null,
                    target_path text null,
                    command_preview text null,
                    description text null,
                    call_id varchar(255) null,
                    tool_name varchar(255) null,
                    tool_arguments jsonb null,
                    risk_level varchar(16) not null,
                    status varchar(16) not null default 'pending'
                        check (status in ('pending', 'approved', 'rejected', 'expired')),
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now(),
                    resolved_at timestamptz null
                );

                alter table workspace_approval_events
                    add column if not exists description text null;

                alter table workspace_approval_events
                    add column if not exists call_id varchar(255) null;

                alter table workspace_approval_events
                    add column if not exists tool_name varchar(255) null;

                alter table workspace_approval_events
                    add column if not exists tool_arguments jsonb null;

                create table if not exists lcm_files(
                    id bigserial primary key,
                    file_id varchar(128) not null unique,
                    session_id uuid not null references sessions(id) on delete cascade,
                    origin_tool varchar(64) not null,
                    workspace_path text not null,
                    byte_count bigint not null,
                    storage_kind varchar(16) not null default 'filesystem'
                        check (storage_kind in ('filesystem', 'inline')),
                    filesystem_path text null,
                    created_at timestamptz not null default now()
                );

                create table if not exists session_active_workspaces(
                    id bigserial primary key,
                    session_id uuid not null references sessions(id) on delete cascade,
                    workspace_name varchar(255) not null,
                    policy_mode varchar(32) not null default 'confirm_writes_and_exec'
                        check (policy_mode in ('unrestricted', 'true_unrestricted', 'confirm_writes_and_exec', 'confirm_exec_only', 'read_only', 'disabled')),
                    created_at timestamptz not null default now(),
                    unique(session_id, workspace_name)
                );

                create index if not exists idx_session_active_workspaces_session
                    on session_active_workspaces(session_id);

                create index if not exists idx_workspaces_name
                    on workspaces(name);

                create index if not exists idx_agent_workspace_assignments_agent
                    on agent_workspace_assignments(agent_id);

                create index if not exists idx_agent_workspace_assignments_workspace
                    on agent_workspace_assignments(workspace_id);

                create index if not exists idx_agent_workspace_assignments_default
                    on agent_workspace_assignments(agent_id, is_default);

                create index if not exists idx_workspace_approval_events_session_created
                    on workspace_approval_events(session_id, created_at desc);

                create index if not exists idx_workspace_approval_events_status_created
                    on workspace_approval_events(status, created_at desc);

                create index if not exists idx_workspace_approval_events_token
                    on workspace_approval_events(approval_token);

                create index if not exists idx_lcm_files_session_created
                    on lcm_files(session_id, created_at desc);

                create index if not exists idx_lcm_files_file_id
                    on lcm_files(file_id);

                create table if not exists bridge_clients(
                    bridge_id varchar(128) primary key,
                    display_name varchar(255) not null,
                    status varchar(32) not null default 'offline'
                        check (status in ('online', 'offline')),
                    last_seen_at timestamptz null,
                    capabilities jsonb not null default '{}'::jsonb,
                    auth_fingerprint varchar(255) null,
                    is_devcontainer boolean not null default false,
                    container_id varchar(255) null,
                    workspace_path_in_container text null,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                alter table bridge_clients
                    add column if not exists is_devcontainer boolean not null default false;

                alter table bridge_clients
                    add column if not exists container_id varchar(255) null;

                alter table bridge_clients
                    add column if not exists workspace_path_in_container text null;

                create table if not exists bridge_execution_events(
                    id bigserial primary key,
                    request_id varchar(128) not null unique,
                    bridge_id varchar(128) not null references bridge_clients(bridge_id),
                    session_id uuid not null references sessions(id) on delete cascade,
                    agent_id bigint not null references agents(id),
                    workspace_id bigint not null references workspaces(id),
                    operation varchar(64) not null,
                    status varchar(32) not null default 'pending',
                    error_message text null,
                    started_at timestamptz not null default now(),
                    updated_at timestamptz not null default now(),
                    completed_at timestamptz null
                );

                create index if not exists idx_bridge_clients_status
                    on bridge_clients(status);

                create index if not exists idx_bridge_execution_events_bridge
                    on bridge_execution_events(bridge_id, started_at desc);

                create index if not exists idx_bridge_execution_events_session
                    on bridge_execution_events(session_id, started_at desc);

                create table if not exists scheduled_jobs(
                    id bigserial primary key,
                    name text not null,
                    cron_expression text not null,
                    timezone text not null default 'Europe/Rome',
                    prompt text not null,
                    agent_id bigint not null references agents(id),
                    enabled boolean not null default true,
                    last_run_at timestamptz,
                    last_session_id uuid,
                    next_run_at timestamptz not null,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create index if not exists idx_scheduled_jobs_enabled_next_run
                    on scheduled_jobs(enabled, next_run_at);

                create table if not exists channels(
                    id bigserial primary key,
                    name text not null,
                    type text not null check (type in ('discord', 'telegram')),
                    agent_id bigint not null references agents(id),
                    routing_mode text not null default 'shared'
                        check (routing_mode in ('shared', 'per_user')),
                    config jsonb not null default '{}'::jsonb,
                    enabled boolean not null default true,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create table if not exists channel_sessions(
                    id bigserial primary key,
                    channel_id bigint not null references channels(id) on delete cascade,
                    identity_id text not null,
                    session_id uuid not null references sessions(id),
                    last_broadcast_sequence bigint not null default 0,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now(),
                    unique(channel_id, identity_id)
                );

                create index if not exists idx_channel_sessions_session
                    on channel_sessions(session_id);

                create table if not exists secrets(
                    id bigserial primary key,
                    name text not null unique,
                    encrypted_value text not null,
                    scope text not null default 'global'
                        check (scope in ('global', 'user', 'agent')),
                    owner_id bigint null,
                    allow_bridge boolean not null default false,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create table if not exists backup_configs(
                    id bigserial primary key,
                    enabled boolean not null default true,
                    timezone text not null default 'Europe/Rome',
                    daily_time time not null default '03:00:00',
                    full_every_n int not null default 7,
                    retention_days int null,
                    retention_full_chains int null,
                    strict_restore_default boolean not null default true,
                    storage_root text not null default '/data/backups',
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create table if not exists backup_runs(
                    id bigserial primary key,
                    backup_id uuid not null unique,
                    backup_type text not null check (backup_type in ('full', 'incremental')),
                    status text not null check (status in ('running', 'succeeded', 'failed', 'partial')),
                    base_full_backup_id uuid not null,
                    previous_backup_id uuid null,
                    window_from_utc timestamptz null,
                    window_to_utc timestamptz not null,
                    artifact_path text null,
                    error_message text null,
                    started_at timestamptz not null default now(),
                    completed_at timestamptz null
                );

                create table if not exists backup_tombstones(
                    id bigserial primary key,
                    table_name text not null,
                    pk jsonb not null,
                    deleted_at timestamptz not null default now(),
                    source_txid bigint not null default txid_current()
                );

                create index if not exists idx_backup_tombstones_deleted_at
                    on backup_tombstones(deleted_at, id);

                create index if not exists idx_backup_runs_started_at
                    on backup_runs(started_at desc);

                create index if not exists idx_backup_runs_status_started_at
                    on backup_runs(status, started_at desc);

                -- Migration: add allow_bridge to existing databases
                alter table secrets add column if not exists allow_bridge boolean not null default false;

                alter table messages add column if not exists updated_at timestamptz not null default now();
                alter table summaries add column if not exists updated_at timestamptz not null default now();
                alter table conversation_history add column if not exists updated_at timestamptz not null default now();
                alter table workspace_approval_events add column if not exists updated_at timestamptz not null default now();
                alter table bridge_execution_events add column if not exists updated_at timestamptz not null default now();
                alter table channel_sessions add column if not exists updated_at timestamptz not null default now();

                create index if not exists idx_messages_updated_at_id
                    on messages(updated_at, id);
                create index if not exists idx_summaries_updated_at_id
                    on summaries(updated_at, id);
                create index if not exists idx_conversation_history_updated_at_id
                    on conversation_history(updated_at, id);
                create index if not exists idx_workspace_approval_events_updated_at_id
                    on workspace_approval_events(updated_at, id);
                create index if not exists idx_bridge_execution_events_updated_at_id
                    on bridge_execution_events(updated_at, id);
                create index if not exists idx_channel_sessions_updated_at_id
                    on channel_sessions(updated_at, id);

                create index if not exists idx_fragments_updated_at_id
                    on fragments(updated_at, id);
                create index if not exists idx_fragment_shares_updated_at_id
                    on fragment_shares(updated_at, id);
                create index if not exists idx_workspaces_updated_at_id
                    on workspaces(updated_at, id);
                create index if not exists idx_agent_workspace_assignments_updated_at_id
                    on agent_workspace_assignments(updated_at, id);
                create index if not exists idx_scheduled_jobs_updated_at_id
                    on scheduled_jobs(updated_at, id);
                create index if not exists idx_channels_updated_at_id
                    on channels(updated_at, id);
                create index if not exists idx_sessions_updated_at_id
                    on sessions(updated_at, id);
                create index if not exists idx_session_tasks_updated_at_id
                    on session_tasks(updated_at, id);
                create index if not exists idx_agents_updated_at_id
                    on agents(updated_at, id);
                create index if not exists idx_bridge_clients_updated_at_bridge_id
                    on bridge_clients(updated_at, bridge_id);
                create index if not exists idx_lcm_files_created_at_id
                    on lcm_files(created_at, id);
                create index if not exists idx_session_active_workspaces_created_at_id
                    on session_active_workspaces(created_at, id);

                insert into backup_configs (enabled, timezone, daily_time, full_every_n, strict_restore_default, storage_root)
                select true, 'Europe/Rome', '03:00:00', 7, true, '/data/backups'
                where not exists (select 1 from backup_configs);

                create or replace function backup_capture_delete() returns trigger
                language plpgsql
                as $$
                begin
                    insert into backup_tombstones(table_name, pk, deleted_at)
                    values (TG_TABLE_NAME, jsonb_build_object('id', OLD.id), now());
                    return OLD;
                end;
                $$;

                drop trigger if exists trg_backup_tombstones_fragments on fragments;
                create trigger trg_backup_tombstones_fragments
                    after delete on fragments
                    for each row execute function backup_capture_delete();

                drop trigger if exists trg_backup_tombstones_fragment_shares on fragment_shares;
                create trigger trg_backup_tombstones_fragment_shares
                    after delete on fragment_shares
                    for each row execute function backup_capture_delete();

                drop trigger if exists trg_backup_tombstones_workspaces on workspaces;
                create trigger trg_backup_tombstones_workspaces
                    after delete on workspaces
                    for each row execute function backup_capture_delete();

                drop trigger if exists trg_backup_tombstones_agent_workspace_assignments on agent_workspace_assignments;
                create trigger trg_backup_tombstones_agent_workspace_assignments
                    after delete on agent_workspace_assignments
                    for each row execute function backup_capture_delete();

                drop trigger if exists trg_backup_tombstones_session_active_workspaces on session_active_workspaces;
                create trigger trg_backup_tombstones_session_active_workspaces
                    after delete on session_active_workspaces
                    for each row execute function backup_capture_delete();

                drop trigger if exists trg_backup_tombstones_scheduled_jobs on scheduled_jobs;
                create trigger trg_backup_tombstones_scheduled_jobs
                    after delete on scheduled_jobs
                    for each row execute function backup_capture_delete();

                drop trigger if exists trg_backup_tombstones_channels on channels;
                create trigger trg_backup_tombstones_channels
                    after delete on channels
                    for each row execute function backup_capture_delete();

                drop trigger if exists trg_backup_tombstones_channel_sessions on channel_sessions;
                create trigger trg_backup_tombstones_channel_sessions
                    after delete on channel_sessions
                    for each row execute function backup_capture_delete();
                """);

            if (await connection.ExecuteScalarAsync<int>("select count(*) from agents where name = 'Main'") == 0)
                await connection.ExecuteAsync(
                    """
                    insert into agents (name, llm_model, temperature, soft_compact_threshold, hard_compact_threshold)
                    values ('Main', 'qwen/qwen3.5-35b-a3b', 0.1, 76800, 87040);
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
