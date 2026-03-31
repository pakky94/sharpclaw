namespace SharpClaw.API.Agents;

public static class Environment
{
    // TODO: when adding multiprovider support update the prompt
    // TODO: when workspace support update the prompt
    public static string EnvPrompt(string modelId, DateTimeOffset now) =>
        $"""
         You are powered by the model named {modelId}.
         Here is some useful information about the environment you are running in:
         <env>`,
           Local date: ${now.ToLocalTime():dddd dd MM YYYY}`,
         </env>`,
         """;
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