using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Accounts;

public sealed record GetAccountsQuery(Guid WorkspaceId) : IQuery<IReadOnlyList<AccountDto>>;

public sealed class GetAccountsQueryHandler(IWorkspaceRepository workspaces, IAccountRepository accounts, ICurrentUser current)
    : IQueryHandler<GetAccountsQuery, IReadOnlyList<AccountDto>>
{
    public async Task<IReadOnlyList<AccountDto>> HandleAsync(GetAccountsQuery q, CancellationToken ct = default)
    {
        await workspaces.RequireOwnedAsync(q.WorkspaceId, current, ct);
        return (await accounts.ListAsync(q.WorkspaceId, ct)).Select(AccountDto.From).ToList();
    }
}

/// <summary>"Sign in again" after a session expired. Until the extension reports sessions, this marks the account healthy.</summary>
public sealed record ReconnectAccountCommand(Guid WorkspaceId, Guid AccountId) : ICommand<AccountDto>;

public sealed class ReconnectAccountCommandHandler(
    IWorkspaceRepository workspaces, IAccountRepository accounts, ICurrentUser current, IUnitOfWork uow)
    : ICommandHandler<ReconnectAccountCommand, AccountDto>
{
    public async Task<AccountDto> HandleAsync(ReconnectAccountCommand c, CancellationToken ct = default)
    {
        await workspaces.RequireOwnedAsync(c.WorkspaceId, current, ct);
        var account = await accounts.GetAsync(c.WorkspaceId, c.AccountId, ct) ?? throw new NotFoundException("บัญชี", c.AccountId);
        account.MarkHealthy();
        await uow.SaveChangesAsync(ct);
        return AccountDto.From(account);
    }
}
