# LCM Gap Analysis (Volt TS -> SharpClaw)

## Current SharpClaw LCM Capabilities

1. Summarization and compaction exist:
- `Summarizer` writes `lcm_summary_level` and `lcm_summary_id` on summary responses.
- Compaction links summarized entries using `parent_summary_id`.
- Active conversation is represented by `conversation_history`.

2. What is already queryable with current schema:
- Summary by internal DB id.
- Parent summary chain via `parent_summary_id`.
- Messages directly/indirectly summarized under a summary tree.

## Missing vs Volt for Full `lcm-*` Parity

1. No large-file persistence layer:
- Missing `large_files` table and content storage metadata (`storage_kind`, mime, token count, explorer summary).
- Blocks full `lcm_read` and file path/inline payload behavior from Volt.

2. No lineage-pointer model:
- Missing pointer tables / closure graph used by Dolt mode (`archive_stub`, `archive_full`, lineage pointers).
- Blocks full archive traversal metadata in `lcm_describe`, `lcm_expand`, and richer `lcm_grep`.

3. No retrieval index adapter:
- Missing retrieval facade/query diagnostics path used by `lcm_expand_query` for query-to-summary candidate resolution.

4. No session ancestry chain:
- SharpClaw sessions are independent; no parent session/conversation hierarchy currently available.
- Volt tools search conversation + ancestors. SharpClaw can currently scope to a single session.

## Phase-1 Implementation Baseline (now)

1. Add C# LCM tool contracts (`LcmContracts.cs`).
2. Add repository primitives compatible with current schema:
- summary lookup by external `lcm_summary_id`
- parent summary chain retrieval
- summary expansion to underlying messages (recursive summary closure)
3. Use these primitives to implement first tool set with reduced metadata:
- `lcm_describe` (summary-focused first)
- `lcm_expand`
- initial `lcm_grep` over expanded/session message payloads

## Phase-2 Schema Extensions (for Volt parity)

1. Add `lcm_large_files` table (+ indexes) and repository APIs.
2. Add summary lineage pointer tables and closure queries.
3. Add retrieval candidate pipeline and diagnostics storage.
4. Add optional session ancestry linkage for cross-session scope traversal.
