namespace SharpClaw.API.Agents.Workspace;

public interface IWorkspaceExecutionRouter
{
    Task<object> ListFiles(ResolvedWorkspace workspace, string path, bool recursive = false, bool includeHidden = false);
    Task<object> ReadFile(ResolvedWorkspace workspace, string path, int? offset = null, int? length = null);
    Task<object> WriteFile(ResolvedWorkspace workspace, string path, string content, string mode = "overwrite");
    Task<object> EditFile(ResolvedWorkspace workspace, string path, string oldString, string newString, bool replaceAll = false);
    Task<object> DeleteFile(ResolvedWorkspace workspace, string path, bool recursive = false);
    Task<object> MoveFile(ResolvedWorkspace workspace, string source, string destination);
    Task<object> MakeDirectory(ResolvedWorkspace workspace, string path);
    Task<object> RunCommand(ResolvedWorkspace workspace, string command, int? timeoutMs = null, int? maxOutputBytes = null, Dictionary<string, string>? env = null);
}
