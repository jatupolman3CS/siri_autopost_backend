using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Accounts;
using SIRIAUTOPOST.Application.Features.Workspaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;

namespace SIRIAUTOPOST.Api.Controllers;

[ApiController]
[Route("api/workspaces")]
[Produces("application/json")]
public sealed class WorkspacesController : ControllerBase
{
    public sealed record CreateWorkspaceRequest(string Name);

    [HttpGet]
    public Task<IReadOnlyList<WorkspaceDto>> List(
        [FromServices] IQueryHandler<GetWorkspacesQuery, IReadOnlyList<WorkspaceDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetWorkspacesQuery(), ct);

    [HttpPost]
    public Task<WorkspaceDto> Create(
        CreateWorkspaceRequest request, [FromServices] ICommandHandler<CreateWorkspaceCommand, WorkspaceDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new CreateWorkspaceCommand(request.Name), ct);

    [HttpGet("{wsId:guid}/accounts")]
    public Task<IReadOnlyList<AccountDto>> Accounts(
        Guid wsId, [FromServices] IQueryHandler<GetAccountsQuery, IReadOnlyList<AccountDto>> handler, CancellationToken ct) =>
        handler.HandleAsync(new GetAccountsQuery(wsId), ct);

    [HttpPost("{wsId:guid}/accounts/{accountId:guid}/reconnect")]
    public Task<AccountDto> Reconnect(
        Guid wsId, Guid accountId, [FromServices] ICommandHandler<ReconnectAccountCommand, AccountDto> handler, CancellationToken ct) =>
        handler.HandleAsync(new ReconnectAccountCommand(wsId, accountId), ct);
}
