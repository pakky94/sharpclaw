# Backup And Restore - Requirements And Implementation Plan

## Goals

- Add data-only backup and restore for SharpClaw.
- Support full and incremental backups.
- Keep format simple and portable (no schema lock-in).
- Support daily scheduled backups with configurable full cadence.

## Scope

- In scope:
  - Backend schema updates for incremental correctness.
  - Backup artifact writer (full + incremental).
  - Restore engine (full chain + incrementals).
  - Scheduler for daily execution.
  - API endpoints for configuration, run history, manual run, restore.
- Out of scope (v1):
  - S3/object storage.
  - Cross-engine restore implementation (format remains portable).
  - UI page (can be added after backend stabilizes).

## Backup Format (v1)

- Container: `.scbackup.zip`
- Contents:
  - `manifest.json`
  - `full/<table>.ndjson` for full backups
  - `inc/<table>.ndjson` for incremental table deltas
  - `inc/tombstones.ndjson` for incremental deletions
  - `checksums.sha256`
- Encoding: UTF-8 NDJSON, one JSON row per line.

Rationale:
- Simple to inspect with standard tools.
- Supports nested payloads (`jsonb`) without CSV escaping complexity.
- Portable and easy to re-implement in other runtimes.

## Incremental Correctness Strategy

### Table classes

- Mutable tables: incremental by `updated_at`.
- Append-only tables: incremental by `created_at`.
- Deletable tables: add tombstone capture.

### Required schema changes

- Add `updated_at` to tables that are updated but currently missing it:
  - `messages`
  - `summaries`
  - `conversation_history`
  - `workspace_approval_events`
  - `channel_sessions`
  - `bridge_execution_events`
- Ensure repository `UPDATE` statements set `updated_at = now()`.
- Add `backup_tombstones` table and delete triggers for deletable tables.

### Tombstone-target tables

- `fragments`
- `fragment_shares`
- `workspaces`
- `agent_workspace_assignments`
- `session_active_workspaces`
- `scheduled_jobs`
- `channels`
- `channel_sessions`

## Restore Semantics

- Restore is data-only.
- Full restore applies full artifact rows with upsert semantics.
- Incremental restore applies row upserts, then tombstone deletes.
- Modes:
  - strict: fail on checksum/chain incompatibilities.
  - relaxed: tolerate compatible schema drift where possible.

## Scheduling

- Daily schedule using configured `daily_time` and `timezone`.
- Full cadence via `full_every_n` (for example 7 means one full every 7 runs).
- If no prior full exists, run full.
- Single-run lock to avoid concurrent backups.

## Data Model Additions

- `backup_configs`: scheduler + retention configuration.
- `backup_runs`: execution history and chain metadata.
- `backup_tombstones`: delete capture for incremental backups.

## API (v1)

- `GET /backups/config`
- `PUT /backups/config`
- `GET /backups/runs`
- `POST /backups/run`
- `POST /backups/restore`

## Implementation Plan

1. Add DB schema/migration updates in `DatabaseSeeder`.
2. Add backup domain models and repository.
3. Implement backup writer service:
   - full export,
   - incremental export by window,
   - manifest + checksum generation.
4. Implement restore service:
   - chain loading,
   - checksum validation,
   - ordered apply.
5. Add scheduler background service for daily execution.
6. Add API endpoints and register services in `Program.cs`.
7. Add backend tests:
   - full roundtrip,
   - incremental update + delete,
   - idempotent restore,
   - checksum failure handling.

## Acceptance Criteria

- Full backup + restore recreates all SharpClaw application data.
- Incremental captures updates and deletes correctly.
- Daily scheduling respects configured local time and full cadence.
- Backups are inspectable with standard zip/json tooling.
- Restore validates checksums and chain ordering before mutating data.
