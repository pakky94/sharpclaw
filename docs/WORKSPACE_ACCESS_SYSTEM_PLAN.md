# Workspace Access System Plan (SharpClaw)

This plan is written before implementation changes.

## Goal

Enable each agent to operate on a real host folder (workspace) with tools to list/read/edit files and run commands, while enforcing configurable permission and approval policies. The system must integrate with LCM so workspace interactions remain scalable in long sessions.

## Non-Goals (Phase 1)

1. No remote workspace providers (SSH, S3, etc.).
2. No IDE plugin protocol changes beyond API surface needed for approvals.
3. No migration for old `documents`/`agents_documents` tables unless required for cleanup.

## Current-State Notes

1. `Agent.BuildTools()` currently wires `FragmentTools` + `LcmTools`; `FileTools` are not registered.
2. Existing `FileTools` operate on DB-backed documents, not host filesystem.
3. `Environment.EnvPrompt` has a TODO for workspace support.
4. LCM currently handles summary metadata and grep/expand over conversation; file-backed `file_xxx` retrieval is intentionally incomplete.

## Target Architecture

### 1) Workspace model

Introduce a workspace assignment per agent (or per session override):
1. `workspace_root` absolute host path.
2. Optional allowlist/denylist patterns inside that root.
3. Policy mode controlling execution and write behavior.

Recommended DB entities:
1. `agent_workspaces` (agent_id, root_path, policy_mode, created_at, updated_at).
2. `workspace_policy_overrides` (optional granular toggles).
3. `workspace_approval_events` (audit trail of prompted approvals/decisions).

### 2) Permission modes (configurable)

Define explicit modes:
1. `unrestricted`: read/write/command allowed inside workspace root without prompts.
2. `confirm_writes_and_exec`: reads allowed, writes and commands require approval.
3. `confirm_exec_only`: reads + writes allowed, command execution requires approval.
4. `read_only`: reads only, deny writes and commands.
5. `disabled`: no workspace tools available.

All modes enforce path containment within `workspace_root`.

### 3) Tool surface (workspace-native)

Add/replace tools (names can stay simple):
1. `list_files(path=".", recursive=false, include_hidden=false)`
2. `read_file(path, offset?, length?)`
3. `write_file(path, content, mode="overwrite|append|create_only")`
4. `delete_file(path, recursive=false)`
5. `move_file(source, destination)`
6. `make_directory(path)`
7. `run_command(command, cwd=".", timeout_ms?, env?)`

Optional phase-1.5:
1. `stat_path`
2. `search_files` (name/content grep wrapper)

### 4) Approval flow

Tool invocation pipeline:
1. Resolve workspace + policy for current `AgentExecutionContext`.
2. Normalize and validate path/cwd (canonical path under root).
3. Classify action risk (`read`, `write`, `exec`, `destructive_exec`).
4. If policy requires confirmation, return structured `approval_required` response with:
   - action summary
   - normalized target paths
   - command preview
   - risk level
   - approval token/id
5. Client/user approves or rejects via API.
6. On approval, tool replays with approval token and executes.

Add expiration and single-use semantics for approval tokens.

### 5) Command execution sandboxing

Baseline safeguards:
1. Force command working directory to workspace root or validated subdir.
2. Set max runtime + output byte limits.
3. Capture stdout/stderr/exit code deterministically.
4. Optional command denylist for high-risk binaries in non-unrestricted modes.
5. Never permit paths outside workspace via `..`, symlinks, or junction traversal.

## LCM Integration Plan

### 1) Treat large workspace outputs as LCM file artifacts

When tool output is too large:
1. Persist artifact metadata and content reference.
2. Emit `[Large file: file_xxx]`-style markers already expected by LCM prompts.
3. Make `lcm_describe(file_xxx)` and file retrieval work for workspace artifacts.

### 2) Extend repository and contracts

Add repository methods and tables for file artifacts:
1. `lcm_files` table with id (`file_xxx`), session_id, origin_tool, path, byte_count, storage_kind, payload/ref, created_at.
2. Retrieval APIs for metadata and bounded content read.
3. Link artifacts to conversation history entries for traceability.

### 3) Update LCM tools

1. `lcm_describe` should fully resolve `file_xxx` metadata.
2. Add/complete `lcm_read(file_id, max_bytes?)`.
3. Ensure `lcm_expand` remains summary-only (`sum_xxx`), with clear error guidance.

## API and Runtime Changes

1. Extend `AgentExecutionContext` with workspace and policy fields (or a resolved workspace capability object).
2. Add endpoints:
   - configure workspace for agent
   - preview/evaluate policy for an action (optional)
   - approve/reject pending action
   - list approval history (audit)
3. Wire new workspace tools into `Agent.BuildTools()` based on policy mode.
4. Update environment prompt with workspace facts:
   - workspace root
   - policy mode
   - approval behavior summary

## Data Model Draft

Suggested new schema objects:
1. `agent_workspaces`
2. `workspace_approvals`
3. `workspace_approval_events`
4. `lcm_files` (or generic `lcm_artifacts`)

Suggested indexes:
1. `(agent_id)` unique for `agent_workspaces`
2. `(session_id, created_at desc)` for artifacts
3. `(approval_token)` unique
4. `(status, created_at)` for approval queue views

## Security and Safety Requirements

1. Canonicalize and validate every path operation against workspace root.
2. Block symlink/junction escape.
3. Enforce output truncation and binary detection for safe tool responses.
4. Add audit logs for every write/delete/exec action.
5. Redact obvious secrets from command output where feasible.
6. Keep explicit user-visible indication when an action was approved vs auto-allowed.

## Rollout Plan

### Phase 1: Foundation

1. Add workspace config tables + repository methods.
2. Resolve workspace capability in runtime context.
3. Implement filesystem tools (read/list/write/delete/mkdir/move) with policy checks.

### Phase 2: Commands + approval UX

1. Implement `run_command` with bounded execution.
2. Add approval token workflow and endpoints.
3. Integrate client flow for prompt/approve/reject.

### Phase 3: LCM artifacts

1. Implement `lcm_files` persistence and retrieval.
2. Complete `lcm_describe(file_xxx)` and `lcm_read`.
3. Ensure large tool outputs route to artifacts and compact context markers.

### Phase 4: Hardening

1. Add tests for containment, symlink escapes, and policy enforcement.
2. Add rate limits and concurrency guardrails.
3. Add observability dashboards/metrics.

## Testing Strategy

1. Unit tests: path normalization, policy matrix, approval token validation.
2. Integration tests: full tool calls under each policy mode.
3. Security tests: traversal/symlink escape attempts.
4. LCM tests: large output -> `file_xxx` artifact -> describe/read flow.
5. Regression tests: existing fragment and summary LCM behaviors unchanged.

## Open Decisions

1. Scope binding: workspace per agent only, or per session override too?
2. Approval UX ownership: API-driven callbacks vs polling endpoint only?
3. Should unrestricted mode still block a minimal destructive command set?
4. Storage for large artifacts: DB blob vs filesystem pointer with retention policy?
5. Should tool names replace existing DB-backed `FileTools` or coexist temporarily behind feature flags?

## Suggested First Implementation Slice

1. Workspace config + path containment library.
2. Read-only filesystem tools (`list_files`, `read_file`) behind policy.
3. Approval framework skeleton for future write/exec actions.
4. Prompt/context updates so the model understands workspace + policy constraints.
