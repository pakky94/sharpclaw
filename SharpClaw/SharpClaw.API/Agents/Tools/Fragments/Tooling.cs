using Microsoft.Extensions.AI;
using SharpClaw.API.Database;
using SharpClaw.API.Database.Repositories;
using SharpClaw.API.Helpers;

namespace SharpClaw.API.Agents.Tools.Fragments;

public static class FragmentTools
{
    public static readonly AIFunction[] Functions =
    [
        AIFunctionFactory.Create(CreateFragment, "create_fragment", "Create a new fragment."),
        AIFunctionFactory.Create(ReadFragment, "read_fragment", "Read a fragment by id. Defaults to returning child fragments' names only."),
        AIFunctionFactory.Create(UpdateFragment, "update_fragment", "Update fragment content/type/tags."),
        AIFunctionFactory.Create(DeleteFragment, "delete_fragment", "Delete a fragment."),
        AIFunctionFactory.Create(MoveFragment, "move_fragment", "Move a fragment to a new parent and optionally rename it."),
        AIFunctionFactory.Create(SearchFragments, "search_fragments", "Search fragments by semantic-like text matching and metadata filters."),
        AIFunctionFactory.Create(ResolveChild, "resolve_child", "Resolve a direct child fragment by name."),
        AIFunctionFactory.Create(EditFragment, "edit_fragment", "Edit a fragment's content by replacing text. Works like ws_edit_file but for fragments."),
        AIFunctionFactory.Create(ShareFragment, "share_fragment", "Share a fragment with another agent."),
    ];

    public static async Task<object> CreateFragment(
        IServiceProvider serviceProvider,
        string name,
        string? parent_id,
        string content,
        string? type = null,
        Dictionary<string, string>? tags = null)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        string? parentFragmentId = null;
        if (!string.IsNullOrWhiteSpace(parent_id))
        {
            if (!FragmentIds.IsValid(parent_id))
                return new { error = $"Invalid parent_id: {parent_id}" };

            parentFragmentId = parent_id;
        }

        var effectiveParentId = parentFragmentId ?? await repository.EnsureRootFragment(ctx.AgentId);
        var existingId = await repository.ResolveChild(ctx.AgentId, effectiveParentId, name);
        if (!string.IsNullOrWhiteSpace(existingId))
        {
            return new
            {
                error =
                    $"Fragment '{name}' already exists under parent '{effectiveParentId}' (id: {existingId}). Use update_fragment on the existing fragment instead.",
            };
        }

        var id = await repository.CreateFragment(ctx.AgentId, name, effectiveParentId, content, type, tags);
        return new { id };
    }

    public static async Task<object> ReadFragment(
        IServiceProvider serviceProvider,
        string id,
        bool include_children = true,
        int max_depth = 1,
        bool child_names_only = true)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!FragmentIds.IsValid(id))
            return new { error = $"Invalid id: {id}" };

        var fragment = await repository.ReadFragment(
            ctx.AgentId,
            id,
            include_children,
            max_depth,
            child_names_only);

        return fragment is null ? new { error = $"Fragment not found: {id}" } : fragment;
    }

    public static async Task<object> UpdateFragment(
        IServiceProvider serviceProvider,
        string id,
        string? content = null,
        Dictionary<string, string>? tags = null,
        string? type = null)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!FragmentIds.IsValid(id))
            return new { error = $"Invalid id: {id}" };

        var updated = await repository.UpdateFragment(ctx.AgentId, id, content, tags, type);
        return new { updated };
    }

    public static async Task<object> DeleteFragment(
        IServiceProvider serviceProvider,
        string id,
        bool recursive = true)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!FragmentIds.IsValid(id))
            return new { error = $"Invalid id: {id}" };

        var deleted = await repository.DeleteFragment(ctx.AgentId, id, recursive);
        return new { deleted };
    }

    public static async Task<object> MoveFragment(
        IServiceProvider serviceProvider,
        string fragment_id,
        string new_parent_id,
        string? new_name = null)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!FragmentIds.IsValid(fragment_id))
            return new { error = $"Invalid fragment_id: {fragment_id}" };

        if (!FragmentIds.IsValid(new_parent_id))
            return new { error = $"Invalid new_parent_id: {new_parent_id}" };

        var moved = await repository.MoveFragment(ctx.AgentId, fragment_id, new_parent_id, new_name);
        return new { moved };
    }

    public static async Task<object> SearchFragments(
        IServiceProvider serviceProvider,
        string query,
        int top_k = 5,
        Dictionary<string, string>? tag_filter = null,
        string? type_filter = null,
        string? parent_id = null)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        string? parentFragmentId = null;
        if (!string.IsNullOrWhiteSpace(parent_id))
        {
            if (!FragmentIds.IsValid(parent_id))
                return new { error = $"Invalid parent_id: {parent_id}" };
            parentFragmentId = parent_id;
        }

        var results = await repository.SearchFragments(
            ctx.AgentId,
            query,
            top_k,
            tag_filter,
            type_filter,
            parentFragmentId);

        return results;
    }

    public static async Task<object> ResolveChild(
        IServiceProvider serviceProvider,
        string parent_id,
        string child_name)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!FragmentIds.IsValid(parent_id))
            return new { error = $"Invalid parent_id: {parent_id}" };

        var id = await repository.ResolveChild(ctx.AgentId, parent_id, child_name);
        return new { id };
    }

    public static async Task<object> EditFragment(
        IServiceProvider serviceProvider,
        string id,
        string oldString,
        string newString,
        bool replaceAll = false)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!FragmentIds.IsValid(id))
            return new { error = $"Invalid id: {id}" };

        var fragment = await repository.ReadFragment(ctx.AgentId, id, includeChildren: false);
        if (fragment == null)
            return new { error = $"Fragment {id} not found" };

        var (newContent, error) = StringReplacer.Replace(fragment.Content, oldString, newString, replaceAll);

        if (error is not null)
        {
            return new
            {
                error = error switch
                {
                    StringReplacer.Error.OldStringNotFound => "oldString not found in fragment content",
                    StringReplacer.Error.MultipleMatchesFound => "multiple matches of oldString found in fragment content, to replace all occurrences call this tool with replaceAll=true",
                    _ => "Error replacing string in fragment",
                }
            };
        }

        var updated = await repository.UpdateFragment(ctx.AgentId, id, newContent);

        return new { updated };
    }

    public static async Task<object> ShareFragment(
        IServiceProvider serviceProvider,
        string fragment_id,
        string target_agent_id,
        string permission)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!FragmentIds.IsValid(fragment_id))
            return new { error = $"Invalid fragment_id: {fragment_id}" };

        if (!long.TryParse(target_agent_id, out var targetAgentId))
            return new { error = $"Invalid target_agent_id: {target_agent_id}" };

        var shared = await repository.ShareFragment(ctx.AgentId, fragment_id, targetAgentId, permission);
        return new { shared };
    }
}
