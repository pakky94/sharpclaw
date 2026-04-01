# Fragments

Fragments are text documents that the LLM agent uses to store and retrieve pieces of information, such as text, code, or other data, in a structured and organized manner.

Fragments are stored in a folder-like structure, each fragment has a name and a parent fragment, forming a hierarchical tree-like structure, starting from a root fragment.

Each fragment also has a vector representation, which is used for similarity search and retrieval.

Fragments can also have tags: metadata that can be used for filtering, searching, and organizing fragments.

The current database backed implementation of files is going to be renamed and converted to fragments.

Fragments have a simple string name (eg. `parent_1/parent_2/fragment_name`) which allows a fragment's content to reference other fragments.

The agent should be able, and encouraged to, manage its fragments freely and effectively. 
For example an agent might store the instructions for a task in a fragment named `task_instructions` and it could store the code needed to execute the task in a fragment named `task_instruction/task_code` so the code can be executed without loading the fragment into the agent's context.

## Tools

The agent is given tools to interact with fragments, such as creating, reading, updating, and deleting fragments, as well as searching and retrieving fragments based on their content, tags, and vector representations.

## Scoping

Each agent has its own set of fragments, which can be accessed and manipulated by the agent.

The agent can also share fragments with other agents, either as read-only or read-write access, allowing for collaboration and knowledge sharing.

Only the fragments' owner can decide who can access and manipulate their fragments.
