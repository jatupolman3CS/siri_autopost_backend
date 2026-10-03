using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Accounts;
using SIRIAUTOPOST.Application.Features.Admin;
using SIRIAUTOPOST.Application.Features.Auth;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Features.Team;
using SIRIAUTOPOST.Application.Features.Devices;
using SIRIAUTOPOST.Application.Features.Engine;
using SIRIAUTOPOST.Application.Features.Library;
using SIRIAUTOPOST.Application.Features.Posts;
using SIRIAUTOPOST.Application.Features.Workspaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Application.Validators;

namespace SIRIAUTOPOST.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        // Auth
        services.AddCommand<SignUpCommand, AuthResultDto, SignUpCommandHandler>();
        services.AddCommand<LogInCommand, AuthResultDto, LogInCommandHandler>();
        services.AddQuery<GetMeQuery, UserDto, GetMeQueryHandler>();

        // Plans and billing
        services.AddQuery<GetPlansQuery, IReadOnlyList<PlanDto>, GetPlansQueryHandler>();
        services.AddQuery<GetInvoicesQuery, IReadOnlyList<TransactionDto>, GetInvoicesQueryHandler>();
        services.AddCommand<ChangePlanCommand, UserDto, ChangePlanCommandHandler>();

        // Team
        services.AddQuery<GetMembersQuery, IReadOnlyList<MemberDto>, GetMembersQueryHandler>();
        services.AddCommand<InviteMemberCommand, MemberDto, InviteMemberCommandHandler>();
        services.AddCommand<ChangeMemberRoleCommand, Unit, ChangeMemberRoleCommandHandler>();
        services.AddCommand<RemoveMemberCommand, Unit, RemoveMemberCommandHandler>();

        // Workspaces and accounts
        services.AddQuery<GetWorkspacesQuery, IReadOnlyList<WorkspaceDto>, GetWorkspacesQueryHandler>();
        services.AddCommand<CreateWorkspaceCommand, WorkspaceDto, CreateWorkspaceCommandHandler>();
        services.AddQuery<GetAccountsQuery, IReadOnlyList<AccountDto>, GetAccountsQueryHandler>();
        services.AddCommand<ReconnectAccountCommand, AccountDto, ReconnectAccountCommandHandler>();

        // Posts
        services.AddQuery<GetPostsQuery, IReadOnlyList<PostDto>, GetPostsQueryHandler>();
        services.AddQuery<GetErrorsQuery, IReadOnlyList<PostDto>, GetErrorsQueryHandler>();
        services.AddCommand<SchedulePostsCommand, ScheduleResultDto, SchedulePostsCommandHandler>();
        services.AddCommand<DeletePostCommand, Unit, DeletePostCommandHandler>();
        services.AddCommand<RetryPostCommand, PostDto, RetryPostCommandHandler>();
        services.AddCommand<DismissPostErrorCommand, PostDto, DismissPostErrorCommandHandler>();

        // Library
        services.AddQuery<GetMediaQuery, IReadOnlyList<MediaDto>, GetMediaQueryHandler>();
        services.AddQuery<GetMediaContentQuery, MediaContent, GetMediaContentQueryHandler>();
        services.AddCommand<UploadMediaCommand, MediaDto, UploadMediaCommandHandler>();
        services.AddQuery<GetSnippetsQuery, IReadOnlyList<SnippetDto>, GetSnippetsQueryHandler>();
        services.AddCommand<CreateSnippetCommand, SnippetDto, CreateSnippetCommandHandler>();

        // Posting engine
        services.AddQuery<GetEngineSettingsQuery, EngineSettingsDto, GetEngineSettingsQueryHandler>();
        services.AddCommand<UpdateAntiBanCommand, EngineSettingsDto, UpdateAntiBanCommandHandler>();
        services.AddCommand<UpdateOfflineCommand, EngineSettingsDto, UpdateOfflineCommandHandler>();
        services.AddCommand<SetExtensionOnlineCommand, ExtensionStateDto, SetExtensionOnlineCommandHandler>();
        services.AddCommand<SkipWaitingPostsCommand, ExtensionStateDto, SkipWaitingPostsCommandHandler>();

        // Devices: the owner's side, then the extension's side
        services.AddQuery<GetDevicesQuery, IReadOnlyList<DeviceDto>, GetDevicesQueryHandler>();
        services.AddCommand<CreatePairingCodeCommand, PairingCodeDto, CreatePairingCodeCommandHandler>();
        services.AddCommand<RevokeDeviceCommand, Unit, RevokeDeviceCommandHandler>();
        services.AddCommand<PairDeviceCommand, PairResultDto, PairDeviceCommandHandler>();
        services.AddCommand<DeviceHeartbeatCommand, DeviceStatusDto, DeviceHeartbeatCommandHandler>();
        services.AddCommand<SyncDeviceGroupsCommand, int, SyncDeviceGroupsCommandHandler>();
        services.AddCommand<ClaimJobCommand, JobDto?, ClaimJobCommandHandler>();
        services.AddCommand<ReportJobResultCommand, PostDto, ReportJobResultCommandHandler>();
        services.AddQuery<GetDeviceMediaQuery, MediaContent, GetDeviceMediaQueryHandler>();

        // Platform admin
        services.AddScoped<AdminCustomers>();
        services.AddScoped<AdminAudit>();
        services.AddQuery<GetPlatformHealthQuery, PlatformHealthDto, GetPlatformHealthQueryHandler>();
        services.AddCommand<ImpersonateCommand, AuthResultDto, ImpersonateCommandHandler>();
        services.AddQuery<GetAuditQuery, IReadOnlyList<AuditEntryDto>, GetAuditQueryHandler>();
        services.AddQuery<GetCustomersQuery, IReadOnlyList<CustomerDto>, GetCustomersQueryHandler>();
        services.AddQuery<GetAdminSummaryQuery, AdminSummaryDto, GetAdminSummaryQueryHandler>();
        services.AddQuery<GetAdminJobsQuery, IReadOnlyList<AdminJobDto>, GetAdminJobsQueryHandler>();
        services.AddCommand<SetCustomerStatusCommand, CustomerDto, SetCustomerStatusCommandHandler>();
        services.AddCommand<SetCustomerPausedCommand, CustomerDto, SetCustomerPausedCommandHandler>();
        services.AddCommand<SetCustomerPlanCommand, CustomerDto, SetCustomerPlanCommandHandler>();
        services.AddCommand<SetCustomerLimitsCommand, CustomerDto, SetCustomerLimitsCommandHandler>();
        services.AddCommand<SetCustomerNoteCommand, CustomerDto, SetCustomerNoteCommandHandler>();
        services.AddCommand<AdminRevokeDeviceCommand, CustomerDto, AdminRevokeDeviceCommandHandler>();
        services.AddCommand<RetryCustomerFailedCommand, int, RetryCustomerFailedCommandHandler>();
        services.AddQuery<GetTransactionsQuery, IReadOnlyList<TransactionDto>, GetTransactionsQueryHandler>();
        services.AddCommand<RefundCommand, TransactionDto, RefundCommandHandler>();
        services.AddCommand<RecordPaymentCommand, TransactionDto, RecordPaymentCommandHandler>();
        services.AddCommand<UpdatePlanCommand, PlanDto, UpdatePlanCommandHandler>();
        services.AddQuery<GetPromosQuery, IReadOnlyList<PromoDto>, GetPromosQueryHandler>();
        services.AddCommand<CreatePromoCommand, PromoDto, CreatePromoCommandHandler>();
        services.AddCommand<SetPromoActiveCommand, PromoDto, SetPromoActiveCommandHandler>();

        return services;
    }

    // The handler is wrapped so its validators always run first.
    private static void AddCommand<TCommand, TResult, THandler>(this IServiceCollection services)
        where TCommand : ICommand<TResult>
        where THandler : class, ICommandHandler<TCommand, TResult>
    {
        services.AddScoped<THandler>();
        services.AddScoped<ICommandHandler<TCommand, TResult>>(sp =>
            new ValidationCommandHandlerDecorator<TCommand, TResult>(
                sp.GetRequiredService<THandler>(), sp.GetServices<IValidator<TCommand>>()));
    }

    private static void AddQuery<TQuery, TResult, THandler>(this IServiceCollection services)
        where TQuery : IQuery<TResult>
        where THandler : class, IQueryHandler<TQuery, TResult> =>
        services.AddScoped<IQueryHandler<TQuery, TResult>, THandler>();
}
