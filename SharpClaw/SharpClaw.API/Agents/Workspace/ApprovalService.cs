using System.Security.Cryptography;
using SharpClaw.API.Database.Repositories;

namespace SharpClaw.API.Agents.Workspace;

public class ApprovalService(WorkspaceRepository repository)
{
    public string GenerateApprovalToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "").Replace("=", "");
    }

    public async Task<WorkspaceApprovalEvent?> ValidateApprovalToken(string token)
    {
        var approval = await repository.GetApprovalEventByToken(token);
        if (approval is null)
            return null;
        if (approval.Status != ApprovalStatus.Pending)
            return null;
        return approval;
    }

    public async Task<bool> ResolveApproval(string token, bool approved)
    {
        var status = approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        return await repository.ResolveApprovalEvent(token, status);
    }

    public async Task ExpireOldTokens()
    {
        await repository.ExpireOldPendingApprovals();
    }

    public async Task<IReadOnlyList<WorkspaceApprovalEvent>> GetPendingApprovalsForSession(Guid sessionId)
    {
        return await repository.GetPendingApprovalsForSession(sessionId);
    }
}
