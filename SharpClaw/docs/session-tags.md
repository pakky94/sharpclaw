# Session Tags & Inter-Session Messaging — Design Document

## Overview

Give sessions stable, human-readable tags so they can be referenced across channels, scheduled jobs, and agents. Add channel commands for session management and an agent tool for cross-session messaging. This bridges the gap between scheduled jobs and user-facing channels.

---

## 1. Core Concepts

### Session Tag
A short, stable nickname for a session, unique per agent. Tags survive session deletion/recreation — the tag is the identity, the UUID is an implementation detail.

```
session:Main:main        → your primary session
session:Main:coding      → coding work session
session:Main:daily       → daily briefing session
session:OtherAgent:main  → another agent's primary session
```

### Session Reference
A string in the format `session:{agentName}:{tag}`. Used by the `send_message` tool and channel commands to address sessions without knowing UUIDs.

### Channel Commands
Messages starting with `/` that the ChannelRouter intercepts and handles locally — the agent never sees them.

### Cross-Session Messaging
An agent can post a message into any session by reference. The message is persisted but does NOT trigger a run in the target session. It's fire-and-forget delivery.

---

## 2. Data Model Changes

### 2.1 `sessions` table — add `tag`

```sql
alter table sessions
    add column if not exists tag varchar(255) null;

create unique index if not exists idx_sessions_agent_tag
    on sessions(agent_id, tag)
    where tag is not null;
```

- `tag` is nullable — untagged sessions still work normally
- Unique per agent (partial index, nulls don't conflict)
- When a new session is created with a tag that already exists, the old session's tag is set to null (unlinked)

### 2.2 `channel_sessions` — no schema changes

The existing table already maps `(channel_id, identity_id) → session_id`. Commands update this mapping at runtime.

---

## 3. Channel Commands

All commands start with `/`. The ChannelRouter intercepts them before forwarding to the agent.

### `/select {tag}`
Bind this channel identity to the session with the given tag. If no session has that tag, return an error.

```
User: /select coding
Bot:  Switched to session "coding".
```

### `/new [{tag|name}] [{name}]`

Create a new session and bind this channel identity to it.

- **No args (`/new`):** Create a fresh session inheriting the current session's tag. The old session's tag is cleared.
- **One arg (`/new {name}`):** Same as above, with the given name.
- **Two args (`/new {tag} {name}`):** Create a new session with an explicit tag and name. If another session already has that tag, it gets untagged first.

```
User: /new "Coding work"
Bot:  Created new session `main` (Coding work).

User: /new coding "Coding work"
Bot:  Created new session `coding` (Coding work).

User: /new
Bot:  Created new session `untitled`.
```

### `/sessions`
List all tagged sessions for the current channel's agent.

```
Bot:  Sessions for Main:
      • main — "Primary session" (active)
      • coding — "Coding work"
      • daily — "Daily briefing"
```

### `/help`
List available commands.

---

## 4. Agent Tool: `send_message`

```
send_message(session_ref, text)
```

### Behavior

1. Parse `session_ref` → extract agent name and tag
2. Look up the session by `(agent_id, tag)`
   - If not found, create a new session with that tag
3. Persist the message as an assistant message in the target session
4. Call `ChannelRouter.BroadcastNewMessages(sessionId)` — pushes to all connected channels
5. Return immediately (fire and forget, no run triggered)

### Cross-Agent Messaging

When `session_ref` targets a different agent:
- The message is persisted in the target agent's session
- No run is triggered
- If the user later messages that session, the notification is in context
- This is the foundation for inter-agent communication

### Example

```
Agent calls: send_message(
    session_ref="session:Main:main",
    text="Good morning! Here's your daily summary:\n\n- 3 meetings today\n- 2 urgent emails\n- Weather: sunny, 22°C"
)

Result:
  → Message persisted in session:Main:main
  → ChannelRouter broadcasts to Discord, Telegram, web UI
  → User sees the summary on all connected devices
```

---

## 5. Default Session Binding

When a channel identity sends its first message (no existing `channel_sessions` row):

1. Look for a session tagged `main` for this channel's agent
2. If found → auto-bind to it
3. If not found → create a new session tagged `main` and bind to it

This means your Discord bot "just works" on first message — no `/select` needed.

---

## 6. Flow Examples

### Scheduled Job → User Notification

```
8:00 AM — CronScheduler fires "Morning Briefing" job
  → Agent reads calendar, emails, weather
  → Agent calls send_message(session_ref="session:Main:main", text="...")
  → Message lands in session:Main:main
  → ChannelRouter broadcasts to Discord, Telegram, web UI
  → User wakes up to a Discord DM with the briefing
```

### Multi-Channel, Multi-Session

```
User has:
  - Discord DM bound to session:Main:main
  - Telegram bound to session:Main:coding
  - Web UI viewing session:Main:main

From Discord:
  User: "What's on my calendar today?"
  → Agent responds in Discord (and web UI sees it)

From Telegram:
  User: /select main
  Bot:  Switched to session "main".
  User: "Also, remind me about the 2pm meeting"
  → Agent responds in Telegram (and Discord sees it too, since both now on main)
```

### Cross-Agent Notification

```
Agent "Main" calls: send_message(
    session_ref="session:Helper:main",
    text="I've finished processing the data. Results are in session:Main:results."
)

  → Message appears in Helper's main session
  → No run triggered on Helper
  → If Helper's user checks that session later, they see the notification
```

---

## 7. Implementation Plan

### Phase 1: Database & Model

| Step | What | Files |
|---|---|---|
| 1 | Migration: add `tag` column + index to `sessions` | `DatabaseSeeder.cs` |
| 2 | Add `Tag` to session model/DTOs | `AgentRunState.cs`, `ChatRepository.cs` |

### Phase 2: Session Tag Management

| Step | What | Files |
|---|---|---|
| 3 | `SetSessionTag(sessionId, tag)` — sets tag, unlinks old | `ChatRepository.cs` |
| 4 | `GetSessionByTag(agentId, tag)` — lookup by tag | `ChatRepository.cs` |
| 5 | `GetTaggedSessions(agentId)` — list tagged sessions | `ChatRepository.cs` |
| 6 | `Agent.SetSessionTag()` / `Agent.GetSessionByTag()` | `Agent.cs` |

### Phase 3: Channel Commands

| Step | What | Files |
|---|---|---|
| 7 | Command parser in ChannelRouter | `ChannelRouter.cs` |
| 8 | `/select`, `/new`, `/sessions`, `/help` handlers | `ChannelRouter.cs` |
| 9 | Default session binding on first message | `ChannelRouter.cs` |

### Phase 4: send_message Tool

| Step | What | Files |
|---|---|---|
| 10 | `PostNotification(sessionId, text)` — persist without run | `Agent.cs` |
| 11 | `SendMessageTool` — parse ref, resolve session, post, broadcast | `Agents/Tools/SendMessageTool.cs` |
| 12 | Register tool in `ToolCatalog` | `ToolCatalog.cs` |

### Phase 5: Frontend

| Step | What | Files |
|---|---|---|
| 13 | Show tags in session sidebar | `SessionsPanel.tsx` |
| 14 | Tag field in session rename/edit | `AgentConsolePage.tsx` |

### Phase 6: Tests

| Step | What |
|---|---|
| 15 | `SessionTagTests.cs` — tag CRUD, uniqueness, unlinking |
| 16 | `ChannelCommandTests.cs` — command parsing and routing |
| 17 | `SendMessageToolTests.cs` — cross-session messaging |

---

## 8. Edge Cases

- **Tag collision on `/new`:** Old session gets untagged, new session gets the tag. Old session still exists and is accessible by UUID.
- **Deleted session:** Tag becomes available for reuse.
- **Command in middle of agent run:** Commands are intercepted before enqueue, so they never race with an active run.
- **Empty `/new`:** Creates untagged session. The old session is unbound but not deleted.
- **`/select` to nonexistent tag:** Returns error, current binding unchanged.
- **`send_message` to self:** Posting to your own session is allowed — message appears, no run triggered.
- **Broadcast after `send_message`:** Only the new message is broadcast (respects cursor). Existing messages in the session are not re-sent.
