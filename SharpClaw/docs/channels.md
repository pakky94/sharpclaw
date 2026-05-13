# Channels — Design Document

## Overview

Add multi-channel communication to SharpClaw. The agent can receive messages and send responses through external platforms (Discord, Telegram, etc.) in addition to the built-in web UI. Each channel has configurable routing behavior: shared sessions, per-user isolation, or per-channel grouping.

---

## 1. Core Concepts

### Channel
A communication medium. Has a type (Discord, Telegram), platform-specific config (tokens, webhooks), and a routing mode that determines how incoming messages map to agent sessions.

### Channel Adapter
Translates between a platform's protocol and SharpClaw's internal model. Receives messages from the platform, forwards them to the router. Receives responses from the router, formats and sends them to the platform.

### Channel Router
The glue layer. Owns the mapping of `(channel, identity) → session`. Enqueues incoming messages to the agent, waits for run completion, then broadcasts new messages to all channels connected to that session.

### Identity
Who is talking. A platform-specific identifier: Discord user ID, Telegram chat ID, etc. Used by `per_user` routing to isolate sessions.

### Routing Mode
| Mode | Behavior | Use case |
|---|---|---|
| `shared` | All messages go to one session per channel | Personal bot — same conversation from any device |
| `per_user` | Each identity gets a private session | Public bot — each Discord user talks to their own instance |

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────┐
│  External Platforms                                      │
│                                                          │
│  ┌──────────┐    ┌──────────┐    ┌──────────┐           │
│  │ Discord  │    │ Telegram │    │  Future  │           │
│  │ Gateway  │    │  Webhook │    │  ...     │           │
│  └────┬─────┘    └────┬─────┘    └────┬─────┘           │
│       │                │               │                  │
├───────┼────────────────┼───────────────┼──────────────────┤
│       ▼                ▼               ▼                  │
│  ┌──────────────────────────────────────────────────┐    │
│  │              Channel Adapters                     │    │
│  │  ┌────────────┐  ┌────────────┐  ┌────────────┐  │    │
│  │  │  Discord   │  │  Telegram  │  │   Future   │  │    │
│  │  │  Adapter   │  │  Adapter   │  │  Adapter   │  │    │
│  │  └─────┬──────┘  └─────┬──────┘  └─────┬──────┘  │    │
│  └────────┼───────────────┼───────────────┼─────────┘    │
│           │               │               │               │
│           ▼               ▼               ▼               │
│  ┌──────────────────────────────────────────────────┐    │
│  │              Channel Router                       │    │
│  │                                                   │    │
│  │  OnMessage(channel, identity, text)               │    │
│  │    → Lookup/create session                        │    │
│  │    → Enqueue to agent                             │    │
│  │    → Wait for completion                          │    │
│  │    → Broadcast new messages to all channels       │    │
│  └──────────────────┬───────────────────────────────┘    │
│                     │                                     │
│                     ▼                                     │
│  ┌──────────────────────────────────────────────────┐    │
│  │              Agent (unchanged)                    │    │
│  │  - CreateSession / EnqueueMessage                │    │
│  │  - GetMessageResponse (run loop)                 │    │
│  │  - SSE streaming (web UI only)                   │    │
│  └──────────────────────────────────────────────────┘    │
│                                                          │
│  ┌──────────────────────────────────────────────────┐    │
│  │              Web UI (unchanged)                   │    │
│  │  - Direct SSE connection for real-time streaming │    │
│  │  - Full tool event visibility                    │    │
│  │  - Sees messages from all channels in history    │    │
│  └──────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────┘
```

**Key design decisions:**

- **Web UI stays special.** It connects directly to the agent via SSE for real-time streaming, tool call visibility, and full session control. It is not "just another channel."

- **Channels get completed messages only.** No streaming to external platforms in v1. After a run completes, the router pushes the final assistant message to all connected channels.

- **Agent is channel-agnostic.** The agent doesn't know about channels. It processes sessions exactly as it does today. The router is the only new piece that understands channels.

- **User messages from channels are visible in web UI.** They're persisted to the session normally. The web UI sees them on its next history poll.

---

## 3. Data Model

### 3.1 `channels` table

```sql
CREATE TABLE channels (
    id           BIGSERIAL PRIMARY KEY,
    name         TEXT NOT NULL,
    type         TEXT NOT NULL CHECK (type IN ('discord', 'telegram')),
    agent_id     BIGINT NOT NULL REFERENCES agents(id),
    routing_mode TEXT NOT NULL CHECK (routing_mode IN ('shared', 'per_user')),
    config       JSONB NOT NULL DEFAULT '{}',
    enabled      BOOLEAN NOT NULL DEFAULT true,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

**`config` examples:**

Discord:
```json
{
  "bot_token": "...",
  "allowed_guild_ids": ["..."],
  "allowed_channel_ids": ["..."],
  "allowed_user_ids": null,
  "dm_enabled": true
}
```

Telegram (future):
```json
{
  "bot_token": "...",
  "allowed_chat_ids": null
}
```

### 3.2 `channel_sessions` table

```sql
CREATE TABLE channel_sessions (
    id                      BIGSERIAL PRIMARY KEY,
    channel_id              BIGINT NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
    identity_id             TEXT NOT NULL,
    session_id              UUID NOT NULL REFERENCES sessions(id),
    last_broadcast_sequence BIGINT NOT NULL DEFAULT 0,
    created_at              TIMESTAMPTZ NOT NULL DEFAULT now(),
    UNIQUE(channel_id, identity_id)
);
```

- `identity_id` — platform-specific identifier. Discord user ID, Telegram chat ID. Empty string for `shared` mode.
- `last_broadcast_sequence` — cursor tracking which messages have already been sent to this channel. After broadcasting, updated to the latest message sequence in the session.

---

## 4. Flow: Discord Message → Agent Response

```
1. User sends "hello" in a Discord channel
2. Discord gateway sends MESSAGE_CREATE to the adapter
3. Adapter extracts: channel_id, author_id, content
4. Adapter calls: router.OnMessageAsync(channel_id, author_id, "hello")

5. Router looks up channel config → routing_mode = "per_user"
6. Router queries channel_sessions for (channel_id, author_id)
   - Found → use existing session_id
   - Not found → agent.CreateSession(agent_id) → store mapping

7. Router calls: agent.EnqueueMessage(session_id, "hello")
8. Router waits for run to complete (polls session.Run.Status)

9. Run completes. Router queries session for new messages
   (sequence > last_broadcast_sequence for this channel_session)

10. Router finds all channel_sessions with this session_id
    (there might be multiple channels connected to the same session)

11. For each connected channel:
    - Router calls: adapter.SendMessageAsync(channel, identity_id, text)
    - Adapter formats text for Discord, sends via REST API
    - Router updates last_broadcast_sequence

12. Web UI sees the new messages on next history poll
    (or via SSE if the run was initiated from web UI)
```

**Broadcasting detail:** When channel A and channel B are both connected to session X, and a message arrives from channel A — the agent's response is pushed to both A and B. The user message from A is NOT sent to B (only agent responses are broadcast).

---

## 5. Component Interfaces

### 5.1 IChannelAdapter

```csharp
public interface IChannelAdapter
{
    /// Called once at startup with all enabled channels of this type.
    Task StartAsync(
        IEnumerable<Channel> channels,
        Func<Channel, string, string, Task> onMessage);

    /// Called when a channel is disabled or the app shuts down.
    Task StopAsync(Channel channel);

    /// Send a text message to a specific identity on this channel.
    Task SendMessageAsync(Channel channel, string identityId, string text);

    /// Send an approval request (if the platform supports interactive components).
    Task SendApprovalAsync(Channel channel, string identityId, ApprovalInfo approval);
}
```

### 5.2 ChannelRouter

```csharp
public class ChannelRouter
{
    // Called by adapters when a message arrives from an external platform.
    public async Task OnMessageAsync(Channel channel, string identityId, string text);

    // Internal: after a run completes, broadcast new messages to all connected channels.
    private async Task BroadcastAsync(Guid sessionId);
}
```

### 5.3 ApprovalInfo

```csharp
public record ApprovalInfo(
    string Token,
    string Action,
    string Target,
    string Risk,
    string Description,
    Guid SessionId
);
```

---

## 6. Discord Adapter Specifics

### 6.1 Connection
Uses the Discord Gateway WebSocket protocol. On startup:
1. Identify with bot token
2. Listen for MESSAGE_CREATE events
3. Filter to allowed guilds/channels/users from channel config

### 6.2 Message Sending
Uses Discord REST API (`POST /channels/{id}/messages`). Formats agent text as Discord markdown.

### 6.3 Approvals
Discord supports interactive components (buttons). When an approval is needed:
1. Send a message with Approve/Reject buttons
2. Listen for INTERACTION_CREATE events
3. On button click, resolve the approval via the existing API

### 6.4 Library
Use `Discord.Net` or `Remora.Discord` — both are mature .NET Discord libraries. Discord.Net is more widely used and has better documentation for gateway + REST + interactions.

---

## 7. Web UI Integration

The web UI requires no changes to work with channels:

- **User messages from channels appear in history.** They're persisted to the session via `EnqueueMessage`, same as web UI messages. The web UI's history poll picks them up.

- **Agent responses appear in web UI.** If the web UI has an active SSE connection to the session, it sees the response in real-time. Otherwise, it picks it up on the next history poll.

- **Session sidebar shows channel-spawned sessions.** Sessions created by channels are visible in the sidebar. We may want to add a visual indicator of the source channel.

- **Approvals from channels appear in web UI.** The existing approval polling picks them up. The web UI can resolve them, and the resolution propagates back to the channel (the adapter re-queries pending approvals after resolution).

---

## 8. Implementation Plan

### Phase 1: Core Infrastructure

| Step | What | Files |
|---|---|---|
| 1 | Migration: `channels` + `channel_sessions` tables | `DatabaseSeeder.cs` |
| 2 | `Channel.cs` model | `Agents/Channels/Channel.cs` |
| 3 | `ChannelRepository.cs` | `Database/Repositories/ChannelRepository.cs` |
| 4 | `ChannelEndpoints.cs` (CRUD API) | `Endpoints/ChannelEndpoints.cs` |
| 5 | `IChannelAdapter` interface | `Agents/Channels/IChannelAdapter.cs` |
| 6 | `ChannelRouter.cs` | `Agents/Channels/ChannelRouter.cs` |
| 7 | Register in `Program.cs` | `Program.cs` |

### Phase 2: Discord Adapter

| Step | What | Files |
|---|---|---|
| 8 | Add Discord.Net NuGet package | `SharpClaw.API.csproj` |
| 9 | `DiscordAdapter.cs` | `Agents/Channels/Discord/DiscordAdapter.cs` |
| 10 | Discord config model | `Agents/Channels/Discord/DiscordConfig.cs` |
| 11 | Register adapter in DI | `Program.cs` |

### Phase 3: Frontend

| Step | What | Files |
|---|---|---|
| 12 | Channel types | `types/chat.ts` |
| 13 | `ChannelsPage.tsx` | `pages/ChannelsPage.tsx` |
| 14 | Navigation wiring | `App.tsx` |

### Phase 4: Tests

| Step | What |
|---|---|
| 15 | `ChannelEndpointsTests.cs` — CRUD API tests |
| 16 | `ChannelRouterTests.cs` — routing logic tests |
| 17 | `DiscordAdapterTests.cs` — mocked Discord gateway tests |

---

## 9. Testing Strategy

### Channel Endpoints Tests
Standard CRUD pattern (same as `ScheduledJobEndpointsTests`):
- Create with valid data → returns channel with computed fields
- Create with invalid type → 400
- List returns all channels
- Update changes fields
- Delete removes channel

### Channel Router Tests
- `OnMessage_SharedMode_UsesExistingSession` — second message goes to same session
- `OnMessage_PerUser_CreatesSeparateSessions` — different users get different sessions
- `OnMessage_PerUser_SameUserReusesSession` — same user returns to their session
- `Broadcast_SendsToAllConnectedChannels` — response reaches multiple channels
- `Broadcast_SkipsAlreadySentMessages` — respects last_broadcast_sequence

### Discord Adapter Tests
- Mock the Discord gateway WebSocket
- Verify MESSAGE_CREATE → router.OnMessageAsync called
- Verify SendMessageAsync → correct REST API call
- Verify approval buttons → INTERACTION_CREATE → approval resolved

---

## 10. Open Decisions

1. **Discord library:** Discord.Net vs Remora.Discord. Discord.Net is more mature and widely used. Start there.

2. **Channel config validation:** Validate config JSON against a schema per channel type? Or just store it and let the adapter fail at runtime? Schema validation is better UX — return 400 on create if config is invalid.

3. **Session naming for channels:** Auto-name channel-spawned sessions? E.g., `Discord: username` for per_user, `Discord: #channel-name` for shared. Helps identify them in the sidebar.

4. **Channel tools for agent:** Should the agent be able to create/manage channels? Defer to v2 — channels are created via UI/API for now.

5. **Multiple Discord bot instances:** If you have one bot token but want it in multiple servers with different routing, you create multiple channel entries with the same token but different `allowed_guild_ids`. The adapter deduplicates connections (one gateway connection per unique token).
