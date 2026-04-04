using SharpClaw.API.Agents.Workspace;
using SharpClaw.API.Database;

namespace SharpClaw.API.Agents;

public static class Environment
{
    public static string EnvPrompt(string modelId, DateTimeOffset now,
        string rootFragment,
        IReadOnlyList<FragmentReadItem>? fragmentsChildren,
        ResolvedWorkspace? workspace = null)
    {
        var resolvedRoot = workspace?.RootPath;
        var policyMode = workspace?.PolicyMode.ToString();

        var workspaceSection = resolvedRoot is not null
            ? $"""
               <workspace>
                 Root: {resolvedRoot}
                 Policy: {policyMode}
                 Approval: {GetApprovalDescription(workspace?.PolicyMode ?? WorkspacePolicyMode.ConfirmWritesAndExec)}
               </workspace>
               """
            : "";

        return $"""
         You are powered by the model named {modelId}.
         Here is some useful information about the environment you are running in:
         <env>
           Local date: {now.ToLocalTime():dddd dd MM yyyy}
         </env>
         {(string.IsNullOrEmpty(workspaceSection) ? "" : workspaceSection + "\n")}These are your root fragments:
         <rootFragment id=\"{rootFragment}\">
           <fragments>
         {string.Join("\n", fragmentsChildren?.Select(f => $"    <fragment name=\"{f.Name}\" id=\"{f.Id}\" />") ?? [])}
           </fragments>
         </rootFragment>
         """;
    }

    private static string GetApprovalDescription(WorkspacePolicyMode mode) => mode switch
    {
        WorkspacePolicyMode.Disabled => "All operations blocked.",
        WorkspacePolicyMode.ReadOnly => "Read-only. No writes or commands.",
        WorkspacePolicyMode.ConfirmExecOnly => "Reads and writes allowed; commands require approval.",
        WorkspacePolicyMode.ConfirmWritesAndExec => "Reads allowed; writes and commands require approval.",
        WorkspacePolicyMode.Unrestricted => "All allowed except destructive commands (require approval).",
        WorkspacePolicyMode.TrueUnrestricted => "All allowed without restriction.",
        _ => "Reads allowed; writes and commands require approval.",
    };
    /*
        `You are powered by the model named ${model.api.id}. The exact model ID is ${model.providerID}/${model.api.id}`,
        `Here is some useful information about the environment you are running in:`,
        `<env>`,
        `  Working directory: ${Instance.directory}`,
        `  Is directory a git repo: ${project.vcs === "git" ? "yes" : "no"}`,
        `  Platform: ${process.platform}`,
        `  Local date: ${new Date().toDateString()}`,
        `  Timezone: ${Intl.DateTimeFormat().resolvedOptions().timeZone}`,
        `</env>`,
        `<directories>`,
        `  ${
          project.vcs === "git" && false
            ? await Ripgrep.tree({
                cwd: Instance.directory,
                limit: 50,
              })
            : ""
        }`,
        `</directories>`,
     */
}
