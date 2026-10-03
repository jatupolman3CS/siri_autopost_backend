using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Entities;

// Someone invited into a workspace they do not own. Invited by email: an existing user joins at once,
// a new one joins when they sign up with that email.
public class WorkspaceMember : Entity
{
    public Guid WorkspaceId { get; private set; }
    public string Email { get; private set; } = "";
    public Guid? UserId { get; private set; }
    public WorkspaceRole Role { get; private set; }
    public DateTimeOffset InvitedAt { get; private set; }
    public DateTimeOffset? JoinedAt { get; private set; }

    private WorkspaceMember() { } // EF Core

    public bool IsActive => UserId is not null;

    public static WorkspaceMember Invite(Guid workspaceId, string email, WorkspaceRole role, User? existing, DateTimeOffset now)
    {
        if (role == WorkspaceRole.Owner) throw new DomainException("เชิญเป็นเจ้าของเวิร์กสเปซไม่ได้");
        var e = User.NormalizeEmail(email);
        if (e.Length == 0 || !e.Contains('@')) throw new DomainException("กรุณาใส่อีเมลที่ถูกต้อง");
        var m = new WorkspaceMember { WorkspaceId = workspaceId, Email = e, Role = role, InvitedAt = now };
        if (existing is not null) m.Join(existing.Id, now);
        return m;
    }

    public void Join(Guid userId, DateTimeOffset now)
    {
        UserId = userId;
        JoinedAt = now;
    }

    public void ChangeRole(WorkspaceRole role)
    {
        if (role == WorkspaceRole.Owner) throw new DomainException("เปลี่ยนเป็นเจ้าของเวิร์กสเปซไม่ได้");
        Role = role;
    }
}
