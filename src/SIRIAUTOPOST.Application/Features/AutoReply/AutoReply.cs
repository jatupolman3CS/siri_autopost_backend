using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.AutoReply;

// Auto-reply rules of a workspace (a Pro feature). They are only stored: nothing reads Facebook comments yet, so
// nothing executes them. Viewers read, admins edit.

public sealed record GetAutoReplyQuery(Guid WorkspaceId) : IQuery<AutoReplyDto>;

/// <summary>Works on every plan: below Pro a workspace simply has no rules.</summary>
public sealed class GetAutoReplyQueryHandler(IWorkspaceRepository workspaces, ICurrentUser current) : IQueryHandler<GetAutoReplyQuery, AutoReplyDto>
{
    public async Task<AutoReplyDto> HandleAsync(GetAutoReplyQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        return AutoReplyDto.From(ws.AutoReply);
    }
}

public sealed record UpdateAutoReplyCommand(Guid WorkspaceId, AutoReplyDto Settings) : ICommand<AutoReplyDto>;

/// <summary>Replaces the rules. A rule's scope is "all" or the id of a collection of this workspace.</summary>
public sealed class UpdateAutoReplyCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, ICollectionRepository collections, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<UpdateAutoReplyCommand, AutoReplyDto>
{
    public async Task<AutoReplyDto> HandleAsync(UpdateAutoReplyCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Admin, ct);
        await users.RequireAutoReplyAsync(ws, ct);

        var settings = c.Settings.ToSettings();
        settings.Validate();
        foreach (var rule in settings.Rules.Where(r => r.Scope.Equals("all", StringComparison.OrdinalIgnoreCase))) rule.Scope = "all";
        var scoped = settings.Rules.Where(r => r.Scope != "all").ToList();
        if (scoped.Count > 0)
        {
            var known = (await collections.ListAsync(ws.Id, ct)).Select(x => x.Id).ToHashSet();
            foreach (var rule in scoped)
            {
                if (!Guid.TryParse(rule.Scope, out var id) || !known.Contains(id))
                    throw new DomainException("ขอบเขตของกฎต้องเป็น \"ทั้งหมด\" หรือชุดโพสต์ที่มีอยู่ในเวิร์กสเปซนี้");
                rule.Scope = id.ToString(); // one spelling of an id
            }
        }

        ws.UpdateAutoReply(settings);
        await uow.SaveChangesAsync(ct);
        return AutoReplyDto.From(ws.AutoReply);
    }
}
