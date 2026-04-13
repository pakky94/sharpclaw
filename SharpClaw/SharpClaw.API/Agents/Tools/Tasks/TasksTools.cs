using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace SharpClaw.API.Agents.Tools.Tasks;

public static class TasksTools
{
    public static DeferredTool[] Functions((string Name, string? Description)[] agents) =>
    [
        new(TaskTool(agents)),
        new(AIFunctionFactory.Create(
            RunTasks,
            "tasks",
            TasksDescription(agents)
        )),
        new(AIFunctionFactory.Create(
            TaskOutput,
            "task_output",
            """
            Retrieve output from a background task.

            Use this to check status or get results from tasks started with run_in_background
            or moved to background mid-execution.

            Parameters:
            - task_id: The task_id returned when the task was backgrounded
            - wait: If true, block until task completes (default: false)
            - timeout: Maximum milliseconds to wait (default: 30000, max: 300000)

            Returns the task's current status and output. If the task is completed or errored,
            returns the final result. If still running and wait=false, returns current status.
            """
        )),
    ];

    public static AIFunction TaskTool((string Name, string? Description)[] agents)
        => AIFunctionFactory.Create(
            RunTask,
            "task",
            TaskDescription(agents)
        );

    /*
  description: z.string().describe("A short (3-5 words) description of the task"),
  prompt: z.string().describe("The task for the agent to perform"),
  subagent_type: z.string().describe("The type of specialized agent to use for this task"),
  delegated_scope: z
    .string()
    .describe("Required for sub-agents: the specific slice of work being delegated")
    .optional(),
  kept_work: z.string().describe("Required for sub-agents: the work you will still do yourself").optional(),
  session_id: z.string().describe("Existing Task session to continue").optional(),
  command: z.string().describe("The command that triggered this task").optional(),
  run_in_background: z
    .boolean()
    .describe("Run this task in the background and return immediately with a task_id")
    .optional(),
     */
    private static async Task<object?> RunTask(
        IServiceProvider serviceProvider,
        AIFunctionArguments args,
        [Description("A short (3-5 words) description of the task")] string description,
        [Description("The task for the agent to perform")] string prompt,
        [Description("Run this task in the background and return immediately with a task_id")] bool run_in_background = false
        )
    {
        var agentContext = serviceProvider.GetRequiredService<AgentExecutionContext>();
        agentContext.QueuedTasks.Add(new AgentClientTask
        {
            CallId = args.Context?["CallId"] as string ?? throw new InvalidOperationException("CallId is missing."),
            Type = AgentClientTask.TaskType.ChildSession,
            ChildDescription = description,
            ChildPrompt = prompt,
        });

        return null;
//         return new
//         {
//             title = description,
//             output = $"""
//                       {response}
//
//                       <task_metadata>
//                         {sessionId}
//                       </task_metadata>"
//                       """
//         };
    }

    private static async Task<object> RunTasks()
    {
        return new{};
    }

    private static async Task<object> TaskOutput()
    {
        return new{};
    }

    private static string TaskDescription((string Name, string? Description)[] agents) =>
        $$"""
          Your primary mechanism for executing non-trivial work. Launch a sub-agent to handle a complex, multi-step task autonomously.

          **Default: use this for any non-trivial work.** If a task requires more than a single read, search, or edit, delegate it to a sub-agent. Sub-agents return only their final answer — all internal tool calls stay out of your context, preserving it for orchestration.

          **For 2+ independent tasks, use the Tasks tool instead** — it runs them in parallel. For 20+ homogeneous items, consider agentic_map (needs tools) or llm_map (no tools needed) instead.

          ## Delegation Strategy

          When you anticipate a task will require multiple tool calls, spawn a sub-agent instead of executing directly:

          1. **Every tool call consumes context** — inputs, outputs, and intermediate results all accumulate
          2. **Sub-agents return only their final answer** — all their internal tool calls stay out of your context
          3. **Your context is precious** — keeping it clean lets you handle longer conversations and more complex tasks

          **Parallelize whenever possible:**

          When a task can be broken into independent parts, spawn multiple sub-agents in parallel rather than doing work sequentially. This is faster AND preserves more context.

          - **Multiple files to analyze**: Spawn one sub-agent per file (or group of related files)
          - **Multiple searches needed**: Run them in parallel sub-agents
          - **Independent subtasks**: If task A doesn't depend on task B's output, run them simultaneously
          - **Large refactors**: Spawn parallel agents for different modules/components

          To spawn parallel sub-agents, use the Tasks tool (not multiple individual Task calls).

          **Discover then parallelize:** The orchestrating sub-agent should discover work items and then spawn parallel sub-sub-agents as needed. Example: "find all controllers" first, then spawn one agent per controller.

          **Example:** If a user asks "find all the places where X is used and explain the pattern", don't grep yourself and then read 5 files. Instead, spawn a sub-agent with clear instructions to do the search and analysis, and have it return a concise summary.

          **Example:** If asked to "update the error handling in modules A, B, and C", spawn three parallel sub-agents — one for each module — rather than doing them sequentially.

          ## Full Delegation Rule (main thread only)

          When the user tells you to spawn a sub-agent for a task, spawn ONE orchestrating sub-agent with the complete task. Do not run preliminary commands yourself — discovery, searching, and coordination all happen inside the sub-agent. This keeps the main thread's context clean.

          ## Available Agents

          Available agent types and the tools they have access to:
          {{string.Join('\n', agents.Select(a => $"{a.Name}: ${a.Description ?? "This subagent should only be called manually by the user."}"))}}

          When using the Task tool, you must specify a subagent_type parameter to select which agent type to use.

          ## Parameters and Usage

          If you are a sub-agent (your prompt is wrapped in `<subtask>` tags), you MUST also include:
          - `delegated_scope`: the specific slice of work you are delegating
          - `kept_work`: the work you will still do yourself

          If you cannot clearly describe what you are keeping, do the task yourself instead of spawning a sub-task.

          Usage notes:
          1. When the agent is done, it will return a single message back to you. The result returned by the agent is not visible to the user. To show the user the result, you should send a text message back to the user with a concise summary of the result.
          2. Each agent invocation is stateless unless you provide a session_id. Your prompt should contain a highly detailed task description for the agent to perform autonomously and you should specify exactly what information the agent should return back to you in its final and only message to you.
          3. The agent's outputs should generally be trusted
          4. Clearly tell the agent whether you expect it to write code or just to do research (search, file reads, web fetches, etc.), since it is not aware of the user's intent
          5. If the agent description mentions that it should be used proactively, then you should try your best to use it without the user having to ask for it first. Use your judgement.

          When to use the Task tool:
          - When you are instructed to execute custom slash commands. Use the Task tool with the slash command invocation as the entire prompt. The slash command can take arguments. For example: Task(description="Check the file", prompt="/check-file path/to/file.py")
          - When explicitly told to "spawn a sub-agent" or "use a sub-agent" for a task

          ## Guards

          **CRITICAL: Avoid Infinite Recursion**

          If you are a sub-agent (your prompt is wrapped in `<subtask>` tags), any nested task you spawn MUST have STRICTLY SMALLER responsibility than your own task. If you spawn a task with the SAME responsibility, you create infinite recursion.

          **CRITICAL: Never spawn duplicate tasks**

          Do NOT spawn multiple tasks with identical or near-identical prompts. This wastes resources and accomplishes nothing.

          WRONG (duplicate tasks):
          1. Spawns 3 tasks all with prompt: "Extract all providers from file.json"
          2. All 3 do the exact same work redundantly

          CORRECT (discover then parallelize):
          1. First read/analyze the data yourself to discover the work items (e.g., find 5 providers: openai, anthropic, google, etc.)
          2. Then spawn tasks for each DIFFERENT item: "Analyze openai provider", "Analyze anthropic provider", etc.
          3. Each task does unique work on a different slice of the problem

          Valid reasons to spawn nested tasks:
          - Parallelize genuinely different sub-parts of your task
          - Delegate a specific portion of your work to conserve context

          WRONG (same responsibility = infinite loop):
          1. Parent asks sub-agent: "Analyze this file"
          2. Sub-agent spawns: "Analyze this file" <- same task!
          3. That sub-agent spawns: "Analyze this file"
          4. ...infinite recursion

          CORRECT (smaller responsibility):
          1. Parent asks sub-agent: "Analyze all test files in this directory"
          2. Sub-agent finds 5 test files, spawns 5 parallel tasks: "Analyze test_auth.py", "Analyze test_api.py", etc.
          3. Each sub-agent analyzes ONE file and returns results

          ALSO CORRECT (do the work yourself):
          1. Parent asks sub-agent: "Analyze this file"
          2. Sub-agent directly uses Read tool to read the file
          3. Sub-agent returns the analysis

          ## When NOT to use the Task tool

          - If you want to read a specific file path, use the Read or Glob tool instead of the Task tool, to find the match more quickly
          - If you are searching for a specific class definition like "class Foo", use the Glob tool instead, to find the match more quickly
          - If you are searching for code within a specific file or set of 2-3 files, use the Read tool instead of the Task tool, to find the match more quickly
          - Other tasks that are not related to the agent descriptions above

          Example usage (NOTE: The agents below are fictional examples for illustration only - use the actual agents listed above):

          <example_agent_descriptions>
          "code-reviewer": use this agent after you are done writing a significant piece of code
          "greeting-responder": use this agent when to respond to user greetings with a friendly joke
          </example_agent_description>

          <example>
          user: "Please write a function that checks if a number is prime"
          assistant: Sure let me write a function that checks if a number is prime
          assistant: First let me use the Write tool to write a function that checks if a number is prime
          assistant: I'm going to use the Write tool to write the following code:
          <code>
          function isPrime(n) {
            if (n <= 1) return false
            for (let i = 2; i * i <= n; i++) {
              if (n % i === 0) return false
            }
            return true
          }
          </code>
          <commentary>
          Since a significant piece of code was written and the task was completed, now use the code-reviewer agent to review the code
          </commentary>
          assistant: Now let me use the code-reviewer agent to review the code
          assistant: Uses the Task tool to launch the code-reviewer agent
          </example>

          <example>
          user: "Hello"
          <commentary>
          Since the user is greeting, use the greeting-responder agent to respond with a friendly joke
          </commentary>
          assistant: "I'm going to use the Task tool to launch the with the greeting-responder agent"
          </example>
          """;

    private static string TasksDescription((string Name, string? Description)[] agents) =>
        $$"""
          Run multiple independent tasks in parallel using sub-agents.

          **USE THIS TOOL** instead of multiple Task calls when you have 2 or more independent tasks. Parallel execution is the default for independent work — always prefer this over sequential Task calls.

          For 20+ homogeneous items (same task applied to each), consider agentic_map (needs tools per item) or llm_map (no tools needed) instead — they handle JSONL I/O, retries, concurrency, and keep results out of your context.

          Available agent types:
          {{string.Join('\n', agents.Select(a => $"{a.Name}: ${a.Description ?? "This subagent should only be called manually by the user."}"))}}

          See the Task tool for recursion and duplication rules.

          ## When to use Tasks (this tool) vs Task

          | Scenario                        | Tool to use           |
          | ------------------------------- | --------------------- |
          | Single task                     | Task                  |
          | 2+ independent tasks            | **Tasks** (this tool) |
          | Tasks that depend on each other | Task (sequential)     |

          ## Parameters

          - `tasks`: Array of task objects, each with:
            - `description`: Short description (3-5 words)
            - `prompt`: The task for the agent to perform
            - `subagent_type`: Which agent type to use

          ## Example

          To search for authentication code AND find API endpoints simultaneously:

          ```json
          {
            "tasks": [
              {
                "description": "Find auth code",
                "prompt": "Search for authentication-related code and implementations",
                "subagent_type": "Explore"
              },
              {
                "description": "Find API endpoints",
                "prompt": "Search for API endpoint definitions and routes",
                "subagent_type": "Explore"
              }
            ]
          }
          ```

          ## Important notes

          1. All tasks run simultaneously - don't use for tasks that depend on each other
          2. Each task creates its own sub-agent session
          3. Results are aggregated and returned together when all tasks complete
          4. The agent's outputs should generally be trusted
          5. Clearly tell each agent whether you expect it to write code or just do research
          """;
}