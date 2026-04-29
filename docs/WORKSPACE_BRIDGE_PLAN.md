# Workspace Bridge Plan

Date: 2026-04-29
Status: Draft (planning only)

## Problem

Today all workspace filesystem and command tools execute in the same environment as the main SharpClaw API (currently a Docker container).  
We need workspaces that can execute host-bound operations in other environments (for example local OS or devcontainer) while keeping core/stateful operations (database, fragments, session state, approvals persistence, LCM metadata) inside the main SharpClaw instance.

## Goals

1. Keep current tool surface (`ws_*`) stable for the model/user.
2. Route host-interacting workspace operations to the right execution environment per workspace.
3. Keep database-backed and orchestration logic centralized in main SharpClaw.
4. Preserve existing policy and approval behavior (or stricter) across local and remote execution.
5. Support multiple bridge runtimes in the future (host OS, devcontainer, other controlled runners).

## Non-Goals (for first implementation)

1. Moving fragment/Lcm/chat repositories out of main SharpClaw.
2. Replacing current agent loop/tool catalog architecture.
3. Full distributed transaction semantics across bridge and DB.
4. Generic plugin execution framework for every tool type.

## Proposed Architecture

## 1) Execution split

Keep two execution categories:

1. `Core tools` (always in main instance): fragments, session/workspace assignment APIs, approvals persistence, chat/LCM repositories, web search provider integrations, task orchestration.
2. `Workspace host tools` (routable): `ws_list_files`, `ws_read_file`, `ws_write_file`, `ws_edit_file`, `ws_delete_file`, `ws_move_file`, `ws_make_directory`, `ws_run_command` (and later other host-bound operations).

## 2) Workspace runtime binding

Each workspace gets a runtime binding:

1. `local` (existing behavior; execute in main API process/container).
2. `bridge` (execute through a connected bridge client).

Routing key should be workspace-level, not tool-level, so one workspace maps to one execution runtime.

## 3) Bridge application

New process running outside main container:

1. Maintains outbound authenticated connection to SharpClaw (`bridge -> sharpclaw`).
2. Advertises capabilities (os, shell, max timeout, supported operations, root mappings).
3. Receives execution requests for assigned workspaces.
4. Executes host-bound operations with local containment/policy validation.
5. Returns structured results matching existing tool response shape.

## 4) Tool dispatch layer

Introduce an internal `IWorkspaceExecutionRouter` used by workspace tools and command tools:

1. Resolve workspace + policy as today.
2. Decide runtime from workspace binding.
3. Call local executor or remote bridge executor.
4. Normalize result/errors into current tool payload format.

This keeps public tool functions stable while allowing execution backend changes.

## Data Model Changes (planned)

Add runtime metadata for workspaces and bridge clients.

## New/updated tables (conceptual)

1. `workspaces` (add columns):
   1. `runtime_kind` (`local` | `bridge`) default `local`
   2. `runtime_target` (nullable string; bridge id/alias when `runtime_kind=bridge`)
2. `bridge_clients`:
   1. `bridge_id` (unique)
   2. `display_name`
   3. `status` (`online`/`offline`)
   4. `last_seen_at`
   5. `capabilities` (jsonb)
   6. auth metadata (fingerprint/key id)
3. `bridge_execution_events` (optional but recommended):
   1. correlation ids, workspace, tool/action, timestamps, status, error summary

Keep current approval tables; approvals are created/resolved by main API before/after bridge execution.

## API/Protocol Plan

## Control plane (main API)

1. Bridge register/connect endpoint (streaming channel preferred).
2. Heartbeat/health updates.
3. Workspace -> bridge assignment validation endpoint(s).
4. Optional admin endpoints to list/revoke bridges.

## Execution plane (stream messages)

Request envelope:

1. `request_id`, `session_id`, `agent_id`, `workspace_name`
2. `operation` (`list_files`, `read_file`, `write_file`, `run_command`, ...)
3. `args` (tool-specific payload)
4. policy context (`allowlist`, `denylist`, `policy_mode`, resolved root path)
5. limits (`timeout_ms`, max output bytes)

Response envelope:

1. `request_id`, `status` (`ok`/`error`/`timeout`)
2. `result` payload (compatible with current tool schemas)
3. optional `stderr/stdout` chunks or final aggregated output
4. execution metadata (duration, bytes, truncated flags)

Transport choices to decide during implementation:

1. WebSocket (simple and good enough for first version).
2. gRPC bidirectional streaming (strong typing, more setup).

## Security Model

1. Bridge uses short-lived token or mTLS cert to authenticate.
2. Main API authorizes bridge per workspace (`runtime_target` must match).
3. Path containment validated both in main API and bridge.
4. Bridge never receives DB credentials or fragment APIs.
5. Command execution remains subject to existing risk classification + approval policy.
6. Add replay protection and request TTL using `request_id` + timestamp.

## Policy and Approval Behavior

1. Approval decision source remains main API (same tables/endpoints).
2. For approved actions, main API sends execution request to bridge with approval context.
3. Bridge refuses execution if policy/path checks fail locally even if approved (defense in depth).
4. Approval event resolution should happen only after final bridge result.

## Rollout Phases

## Phase 1: Foundations

1. Add runtime metadata to workspace model/repository/endpoints.
2. Add router abstraction with local executor preserving current behavior.
3. No bridge execution yet (`runtime_kind=local` only).

Exit criteria: no functional regressions; all current tests pass.

## Phase 2: Bridge MVP (read-only)

1. Bridge client process with registration + heartbeat.
2. Remote execution for `ws_list_files`, `ws_read_file`.
3. Basic offline handling (`bridge unavailable` errors).

Exit criteria: read operations succeed for bridge-bound workspace on host OS.

## Phase 3: Mutating file operations

1. Remote `ws_write_file`, `ws_edit_file`, `ws_make_directory`, `ws_move_file`, `ws_delete_file`.
2. Keep approval flow in main API; execute post-approval via bridge.
3. Add audit logs for remote mutations.

Exit criteria: approval + mutation flow parity with local execution.

## Phase 4: Command execution

1. Remote `ws_run_command` with timeout, output truncation, risk handling parity.
2. Optional streamed output for long commands.
3. Harden shell escaping and platform-specific command launch behavior.

Exit criteria: command behavior consistent across local and bridge modes.

## Phase 5: Devcontainer support

1. Bridge runtime profile for devcontainer connection/targeting.
2. Workspace assignment UX for selecting bridge target.
3. Bridge capability reporting (shell, OS, toolchain hints).

Exit criteria: same session can target container workspace and host/devcontainer workspace by name.

## Failure Handling

1. Bridge offline during call: return deterministic tool error with reconnect hint.
2. Bridge timeout: mark request timeout and return structured timeout payload.
3. Partial/stream interruption: finalize with error and correlation id.
4. Main API restart: bridges reconnect and re-register.

## Testing Plan (implementation phase)

1. Unit tests for routing decisions and runtime binding validation.
2. Contract tests for request/response payload compatibility.
3. Integration tests:
   1. local runtime regression
   2. bridge runtime read/write/command flows
   3. approval-required remote operations
4. Chaos tests: bridge disconnect mid-execution, heartbeat loss, delayed responses.

## Open Questions

1. Should one workspace map to exactly one bridge, or support failover list?
2. Should bridge execute commands directly or through a sandbox adapter (e.g., devcontainer exec)?
3. Do we need interactive/streaming command I/O in MVP, or final output only?
4. What is the expected trust model for bridges on shared networks?
5. Should `lcm_read` for large files remain local-only initially or support bridge fetch for bridge-created artifacts?

## Suggested Next Doc

After alignment on this plan, create `docs/WORKSPACE_BRIDGE_PROTOCOL.md` with:

1. message schemas
2. auth handshake
3. heartbeat/reconnect rules
4. error codes
5. end-to-end sequence diagrams for approval + execution
