The current database backed implementation of files is going to be renamed and converted to fragments.

# Fragments

Fragments are the fundamental memory unit used by the LLM agent to store, retrieve, and organize information.

A fragment represents a piece of information such as text, code, or structured data.

Each fragment also has a vector representation, which is used for similarity search and retrieval.

The agent should be able, and encouraged to, manage its fragments freely and effectively.
For example an agent might store the instructions for a task in a fragment named `task_instructions` and it could store the code needed to execute the task in a fragment named `task_instruction/task_code` so the code can be executed without loading the fragment into the agent's context.

---

## Identity & Structure

Each fragment has:

* `id`: unique, stable, immutable identifier (canonical reference)
* `name`: simple string (e.g. `task_1`, `code.py`)
* `parent_id`: optional reference to parent fragment
* `content`: raw data (text, code, etc.)
* `type`: semantic role (optional but recommended)
* `tags`: metadata for filtering and organization
* `embedding`: vector representation for semantic search
* `created_at` / `updated_at`: timestamps

Fragments form a **hierarchical tree structure** via `parent_id`, starting from a root fragment.

---

## Paths (Derived)

Fragments can be addressed using paths:

```
parent_1/parent_2/fragment_name
```

However:

* Paths are **derived**, not stored as canonical identity
* Paths are **mutable** and may change when fragments are moved
* Paths should not be relied upon for stable references

---

## Referencing

### Stable References

All durable references between fragments must use fragment IDs:

```
fragment://<id>
```

These references are guaranteed not to break when fragments are moved or renamed.

---

### Child References (Local Paths)

Fragments may reference their **direct children** using a shorthand path syntax:

```
fragment://#child_name
```

Rules:

* `#` denotes lookup within the current fragment’s children
* Only resolves one level down (no deep traversal)
* Intended for tightly coupled content (e.g. code, assets)

Example:

A fragment `skill` may reference:

```
fragment://#code.py
```

---

### Behavior of Child References

* These references are **not guaranteed to be stable**
* If the child fragment is moved, renamed, or deleted:

    * the reference may break
    * the agent is expected to detect and clean up invalid references

This is an intentional tradeoff to keep the system simple.

---

## Fragment Types (Optional)

Fragments may include a `type` field to guide usage:

* `knowledge`
* `task`
* `plan`
* `code`
* `log`
* `summary`

Types are advisory and may be used to influence retrieval or behavior.

---

## Tools

The agent interacts with fragments using the following tools:

* `create_fragment`
* `read_fragment`
* `update_fragment`
* `delete_fragment`
* `move_fragment`
* `search_fragments`

---

### move_fragment

Moves a fragment within the hierarchy.

```
move_fragment({
  fragment_id: string,
  new_parent_id: string,
  new_name?: string
})
```

Behavior:

* Updates `parent_id`
* Optionally updates `name`
* Preserves fragment `id`
* Moves the entire subtree implicitly (children remain attached)

---

## Retrieval

Fragments can be retrieved via:

### 1. Direct Access

* By `id` (preferred)
* By path (resolved dynamically)

### 2. Search

* Vector similarity (embedding-based)
* Tag filtering

---

## Agent Memory Behavior

### Writing

* Store meaningful, reusable, or decision-relevant information
* Prefer creating structured fragments over large monolithic content

### Reading

* Retrieve only necessary fragments
* Prefer navigating the tree when structure is known

### Updating

* Update existing fragments instead of duplicating when appropriate

---

## Organization Guidelines

* Use the hierarchy to group related information
* Keep related fragments close in the tree
* Use child fragments for tightly coupled data (e.g. code under a task)

---

## Scoping

* Each agent has its own fragment tree
* Fragments are private by default
* The agent can also share fragments with other agents, either as read-only or read-write access, allowing for collaboration and knowledge sharing.

---

## Non-Goals (for current version)

The following are intentionally excluded for simplicity:

* Graph-based relationships between fragments
* Global aliasing or symbolic links
* Complex reference resolution systems
* Automatic memory pruning or compression

These may be added in future iterations.

---

## Design Principles

* **IDs over paths**: IDs are the only stable reference
* **Structure over links**: hierarchy is the primary organization mechanism
* **Simplicity first**: prefer minimal, predictable behavior
* **Agent responsibility**: agents are expected to manage and clean up their own memory







# Tool API Specification (v1)

This specification defines the core tools available to an LLM agent for interacting with the fragment-based memory system.

---

## 1. `create_fragment`

Create a new fragment.

### Input

```json
{
  "name": "string",
  "parent_id": "string | null",
  "content": "string",
  "type": "string | null",
  "tags": { "key": "value" }
}
```

### Returns

```json
{
  "id": "string"
}
```

### Notes

* If `parent_id = null`, the fragment is attached to the root.
* The created fragment is owned by the agent (`permissions = "owned"`).

---

## 2. `read_fragment`

Read a fragment and optionally its children.

### Input

```json
{
  "id": "string",
  "include_children": false,
  "max_depth": 1,
  "child_names_only": false
}
```

### Returns

```json
{
  "id": "string",
  "name": "string",
  "parent_id": "string | null",
  "content": "string",
  "type": "string | null",
  "tags": {},
  "permissions": "owned | read-write | read-only",
  "children": [
    {
      "id": "string",
      "name": "string",
      "parent_id": "string",
      "content": "string",
      "type": "string | null",
      "tags": {},
      "permissions": "owned | read-write | read-only"
    }
  ]
}
```

### Notes

* `include_children=true` enables tree traversal.
* `max_depth` limits recursion depth.
* If `child_names_only=true`, children are returned without content (lightweight structure view).

---

## 3. `update_fragment`

Update an existing fragment.

### Input

```json
{
  "id": "string",
  "content": "string | null",
  "tags": { "key": "value" } | null,
  "type": "string | null"
}
```

### Behavior

* Performs partial updates (only provided fields are modified).
* Requires `permissions = "owned"` or `"read-write"`.

---

## 4. `delete_fragment`

Delete a fragment.

### Input

```json
{
  "id": "string",
  "recursive": true
}
```

### Notes

* If `recursive=true`, deletes the fragment and all descendants.
* Requires `permissions = "owned"`.

---

## 5. `move_fragment`

Move a fragment within the hierarchy.

### Input

```json
{
  "fragment_id": "string",
  "new_parent_id": "string",
  "new_name": "string | null"
}
```

### Behavior

* Updates `parent_id`
* Optionally renames the fragment
* Moves the entire subtree automatically
* Does not modify any references

### Permissions

* Requires `permissions = "owned"` or `"read-write"`

---

## 6. `search_fragments`

Search for fragments using semantic similarity and optional filters.

### Input

```json
{
  "query": "string",
  "top_k": 5,
  "tag_filter": { "key": "value" },
  "type_filter": "string | null",
  "parent_id": "string | null"
}
```

### Returns

```json
[
  {
    "id": "string",
    "name": "string",
    "snippet": "string",
    "permissions": "owned | read-write | read-only"
  }
]
```

### Notes

* `parent_id` restricts search to a subtree.
* If `parent_id = null`, search is global across accessible fragments.
* Results are ranked by semantic similarity.

---

## 7. `resolve_child`

Resolve a direct child fragment by name.

### Input

```json
{
  "parent_id": "string",
  "child_name": "string"
}
```

### Returns

```json
{
  "id": "string | null"
}
```

### Notes

* Used to resolve `fragment://#child_name` references.
* Returns `null` if no matching child exists.

---

## 8. `share_fragment`

Share a fragment with another agent.

### Input

```json
{
  "fragment_id": "string",
  "target_agent_id": "string",
  "permission": "read-only | read-write"
}
```

### Behavior

* Grants access to the target agent.
* Shared fragment retains original ownership.
* Permissions can be updated by the owner.

### Permissions

* Requires `permissions = "owned"`

---

## Permissions Model

Each fragment includes a `permissions` field in responses:

* `"owned"` → full control (read, update, delete, share)
* `"read-write"` → can read and modify, but not delete or reshare ownership
* `"read-only"` → can only read

### Enforcement Rules

| Operation | Owned | Read-Write | Read-Only |
| --------- | ----- | ---------- | --------- |
| Read      | ✅     | ✅          | ✅         |
| Update    | ✅     | ✅          | ❌         |
| Move      | ✅     | ✅          | ❌         |
| Delete    | ✅     | ❌          | ❌         |
| Share     | ✅     | ❌          | ❌         |

---

## Design Notes

* Fragment `id` is the only stable reference.
* Paths are derived and mutable.
* Tree structure is the primary organization mechanism.
* Child references (`fragment://#name`) are local and non-guaranteed.
* Agents are responsible for handling broken references and maintaining consistency.

---

## Non-Goals (v1)

* No graph-based relationships between fragments
* No global aliasing system
* No automatic pruning or summarization
* No version history

These may be introduced in future versions.
