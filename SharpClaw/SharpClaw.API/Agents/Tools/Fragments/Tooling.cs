using Microsoft.Extensions.AI;
using SharpClaw.API.Database;

namespace SharpClaw.API.Agents.Tools.Fragments;

public static class FragmentTools
{
    public static readonly AIFunction[] Functions =
    [
        AIFunctionFactory.Create(CreateFragment, "create_fragment", "Create a new fragment."),
        AIFunctionFactory.Create(ReadFragment, "read_fragment", "Read a fragment by id."),
        AIFunctionFactory.Create(UpdateFragment, "update_fragment", "Update fragment content/type/tags."),
        AIFunctionFactory.Create(DeleteFragment, "delete_fragment", "Delete a fragment."),
        AIFunctionFactory.Create(MoveFragment, "move_fragment", "Move a fragment to a new parent and optionally rename it."),
        AIFunctionFactory.Create(SearchFragments, "search_fragments", "Search fragments by semantic-like text matching and metadata filters."),
        AIFunctionFactory.Create(ResolveChild, "resolve_child", "Resolve a direct child fragment by name."),
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

        Guid? parentGuid = null;
        if (!string.IsNullOrWhiteSpace(parent_id))
        {
            if (!Guid.TryParse(parent_id, out var parsedParent))
                return new { error = $"Invalid parent_id: {parent_id}" };

            parentGuid = parsedParent;
        }

        var id = await repository.CreateFragment(ctx.AgentId, name, parentGuid, content, type, tags);
        return new { id = id.ToString() };
    }

    public static async Task<object> ReadFragment(
        IServiceProvider serviceProvider,
        string id,
        bool include_children = false,
        int max_depth = 1,
        bool child_names_only = false)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!Guid.TryParse(id, out var fragmentId))
            return new { error = $"Invalid id: {id}" };

        var fragment = await repository.ReadFragment(
            ctx.AgentId,
            fragmentId,
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

        if (!Guid.TryParse(id, out var fragmentId))
            return new { error = $"Invalid id: {id}" };

        var updated = await repository.UpdateFragment(ctx.AgentId, fragmentId, content, tags, type);
        return new { updated };
    }

    public static async Task<object> DeleteFragment(
        IServiceProvider serviceProvider,
        string id,
        bool recursive = true)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!Guid.TryParse(id, out var fragmentId))
            return new { error = $"Invalid id: {id}" };

        var deleted = await repository.DeleteFragment(ctx.AgentId, fragmentId, recursive);
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

        if (!Guid.TryParse(fragment_id, out var fragmentId))
            return new { error = $"Invalid fragment_id: {fragment_id}" };

        if (!Guid.TryParse(new_parent_id, out var newParentId))
            return new { error = $"Invalid new_parent_id: {new_parent_id}" };

        var moved = await repository.MoveFragment(ctx.AgentId, fragmentId, newParentId, new_name);
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

        Guid? parentGuid = null;
        if (!string.IsNullOrWhiteSpace(parent_id))
        {
            if (!Guid.TryParse(parent_id, out var parsed))
                return new { error = $"Invalid parent_id: {parent_id}" };
            parentGuid = parsed;
        }

        var results = await repository.SearchFragments(
            ctx.AgentId,
            query,
            top_k,
            tag_filter,
            type_filter,
            parentGuid);

        return results;
    }

    public static async Task<object> ResolveChild(
        IServiceProvider serviceProvider,
        string parent_id,
        string child_name)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!Guid.TryParse(parent_id, out var parentId))
            return new { error = $"Invalid parent_id: {parent_id}" };

        var id = await repository.ResolveChild(ctx.AgentId, parentId, child_name);
        return new { id = id?.ToString() };
    }

    public static async Task<object> ShareFragment(
        IServiceProvider serviceProvider,
        string fragment_id,
        string target_agent_id,
        string permission)
    {
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        var repository = serviceProvider.GetRequiredService<FragmentsRepository>();

        if (!Guid.TryParse(fragment_id, out var fragmentId))
            return new { error = $"Invalid fragment_id: {fragment_id}" };

        if (!long.TryParse(target_agent_id, out var targetAgentId))
            return new { error = $"Invalid target_agent_id: {target_agent_id}" };

        var shared = await repository.ShareFragment(ctx.AgentId, fragmentId, targetAgentId, permission);
        return new { shared };
    }
}
