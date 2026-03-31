# LCM Tools Reimplementation Plan (SharpClaw)

This plan is written before any implementation changes.

## Goal

Implement the missing `lcm-*` tools in SharpClaw, modeled on Volt's TypeScript behavior, and expose them to the agent via `BuildTools()`.

## Scope

Target tools to add:
1. `lcm_describe`
2. `lcm_expand`
3. `lcm_grep`
4. `lcm_read`
5. `lcm_expand_query`

Also needed:
1. supporting LCM data-access methods in the repository layer
2. optional sub-agent gating strategy (if reproduced in SharpClaw)
3. tool registration/wiring in agent runtime
4. integration tests / manual verification scripts

## Step-by-Step Plan

1. Baseline and gap analysis
- Inspect current SharpClaw LCM schema/entities and compare against Volt fields used by each tool (`summary_id`, lineage pointers, large files, archive metadata, conversation scope).
- Produce a field mapping sheet (`Volt TS -> SharpClaw DB/DTO`) and identify missing DB capabilities.

2. Define SharpClaw LCM contracts
- Create C# request/response contracts for each tool (inputs, metadata, output text shape).
- Standardize ID validation rules (`sum_*`, `file_*`) and error message patterns.
- Decide what is kept identical vs intentionally simplified from Volt behavior.

3. Extend repository/query layer
- Add repository methods required by tools, likely including:
  - summary lookup by ID (conversation + ancestry scope)
  - summary parent IDs / lineage pointers / lineage closure IDs
  - summary expansion to source messages
  - regex search over historical messages with optional summary scope and pagination
  - large file lookup and content retrieval (with truncation)
- Add SQL plus indexes where needed for regex and lineage queries.
- Add focused unit/integration checks for each new repository method.

4. Implement `lcm_describe`
- Build tool class under `SharpClaw.API/Agents/Tools/Lcm/`.
- Support both `file_*` and `sum_*` IDs.
- Return metadata-rich, human-readable output and structured metadata object.

5. Implement `lcm_expand`
- Validate summary ID format and existence.
- Expand summary to linked messages in chronological order.
- Include summary metadata block (kind/level/type/lineage) in output.
- Return structured metadata (`messageCount`, lineage IDs, archive flags).

6. Implement `lcm_grep`
- Add regex search by conversation with optional summary scope.
- Group matches by covering summary.
- Enforce page size and output byte cap behavior.
- Return pagination and archive-coverage metadata.

7. Implement `lcm_read`
- Validate `file_*` IDs and optional max-bytes argument.
- Retrieve inline/path-backed file content with truncation reporting.
- Handle binary and missing-backing-file cases with explicit outputs.
- Return `storedInLcm` metadata marker equivalent for downstream processors.

8. Implement `lcm_expand_query`
- Resolve candidate summaries from explicit IDs and/or query.
- Implement retrieval adapter/facade call (or temporary deterministic fallback if retrieval is not yet available).
- Delegate expansion+answering strategy:
  - if SharpClaw has sub-agent support, mirror Volt's delegated execution
  - otherwise implement a safe in-process fallback with bounded expansion
- Parse/normalize delegated response and return diagnostics/citations metadata.

9. Agent wiring
- Add an `LcmTools.Functions` registry like existing `FileTools.Functions`.
- Register all new tools in `Agent.BuildTools()`.
- Ensure any required services (repository/context helpers) are injectable from tool calls.

10. Policy and safety behavior
- Decide and implement whether "main-agent forbidden, sub-agent only" rules apply to `lcm_expand`/`lcm_read`.
- Add consistent guardrails for context blowup (byte caps, page caps, max expanded summaries).
- Ensure errors are actionable and tool-to-tool guidance is explicit.

11. Verification
- Add test cases (or scripted checks) for each tool:
  - valid ID flow
  - invalid ID format
  - missing entity
  - ancestry scope behavior
  - truncation/pagination behavior
  - archive-lineage metadata presence
- Run full API build/tests and verify no regressions in existing file tools and summarizer compaction.

12. Rollout and documentation
- Add concise docs for new tools (purpose, inputs, outputs, typical usage flow).
- Add example agent prompts demonstrating `lcm_describe -> lcm_expand/lcm_read -> lcm_grep/lcm_expand_query`.
- Note any known deviations from Volt and backlog items for parity completion.

## Suggested Implementation Order

1. Repository primitives
2. `lcm_describe`
3. `lcm_expand`
4. `lcm_grep`
5. `lcm_read`
6. `lcm_expand_query`
7. Wiring + tests + docs

## Open Decisions Before Coding

1. Should SharpClaw enforce the same sub-agent-only restrictions for `lcm_expand`/`lcm_read`?
2. Do you want strict Volt output text parity, or just behavior parity with idiomatic C# formatting?
3. For `lcm_expand_query`, should we implement retrieval now or ship with explicit-ID mode first and add query retrieval in phase 2?
