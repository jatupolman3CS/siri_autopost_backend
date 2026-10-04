using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Accounts;
using SIRIAUTOPOST.Application.Features.Admin;
using SIRIAUTOPOST.Application.Features.Auth;
using SIRIAUTOPOST.Application.Features.Backup;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Features.AutoReply;
using SIRIAUTOPOST.Application.Features.Collections;
using SIRIAUTOPOST.Application.Features.LinkSets;
using SIRIAUTOPOST.Application.Features.Notifications;
using SIRIAUTOPOST.Application.Features.Reports;
using SIRIAUTOPOST.Application.Features.Team;
using SIRIAUTOPOST.Application.Features.Devices;
using SIRIAUTOPOST.Application.Features.Engine;
using SIRIAUTOPOST.Application.Features.Events;
using SIRIAUTOPOST.Application.Features.Extension;
using SIRIAUTOPOST.Application.Features.Library;
using SIRIAUTOPOST.Application.Features.Posts;
using SIRIAUTOPOST.Application.Features.Schedules;
using SIRIAUTOPOST.Application.Features.Workspaces;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.Interfaces;
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
        services.AddCommand<GoogleLogInCommand, AuthResultDto, GoogleLogInCommandHandler>();
        services.AddQuery<GetMeQuery, UserDto, GetMeQueryHandler>();

        // Plans and billing
        services.AddQuery<GetPlansQuery, IReadOnlyList<PlanDto>, GetPlansQueryHandler>();
        services.AddQuery<GetInvoicesQuery, IReadOnlyList<TransactionDto>, GetInvoicesQueryHandler>();
        services.AddScoped<PaymentSync>();
        services.AddQuery<GetBillingQuery, BillingDto, GetBillingQueryHandler>();
        services.AddCommand<ChangePlanCommand, PlanChangeDto, ChangePlanCommandHandler>();
        services.AddCommand<ConfirmCheckoutCommand, UserDto, ConfirmCheckoutCommandHandler>();
        services.AddCommand<CreatePortalSessionCommand, UrlDto, CreatePortalSessionCommandHandler>();
        services.AddCommand<StripeWebhookCommand, Unit, StripeWebhookCommandHandler>();

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

        // Notifications (Telegram / LINE), auto-reply rules and reports
        services.AddQuery<GetNotificationsQuery, NotificationSettingsDto, GetNotificationsQueryHandler>();
        services.AddCommand<UpdateNotificationsCommand, NotificationSettingsDto, UpdateNotificationsCommandHandler>();
        services.AddCommand<SendTestNotificationCommand, NotifyTestResultDto, SendTestNotificationCommandHandler>();
        services.AddCommand<FindTelegramChatsCommand, TelegramChatsDto, FindTelegramChatsCommandHandler>();
        services.AddQuery<GetAutoReplyQuery, AutoReplyDto, GetAutoReplyQueryHandler>();
        services.AddCommand<UpdateAutoReplyCommand, AutoReplyDto, UpdateAutoReplyCommandHandler>();
        services.AddScoped<ReportComposer>();
        services.AddQuery<GetReportQuery, ReportDto, GetReportQueryHandler>();
        services.AddCommand<ShareReportCommand, ReportShareDto, ShareReportCommandHandler>();
        services.AddQuery<GetSharedReportQuery, SharedReportDto, GetSharedReportQueryHandler>();

        // Collections ("ชุดโพสต์")
        services.AddQuery<GetCollectionsQuery, IReadOnlyList<CollectionDto>, GetCollectionsQueryHandler>();
        services.AddCommand<CreateCollectionCommand, CollectionDto, CreateCollectionCommandHandler>();
        services.AddCommand<UpdateCollectionCommand, CollectionDto, UpdateCollectionCommandHandler>();
        services.AddCommand<DeleteCollectionCommand, Unit, DeleteCollectionCommandHandler>();
        services.AddCommand<AddCollectionPostCommand, CollectionPostDto, AddCollectionPostCommandHandler>();
        services.AddCommand<AddCollectionPostsBatchCommand, IReadOnlyList<CollectionPostDto>, AddCollectionPostsBatchCommandHandler>();
        services.AddCommand<UpdateCollectionPostCommand, CollectionPostDto, UpdateCollectionPostCommandHandler>();
        services.AddCommand<DeleteCollectionPostCommand, Unit, DeleteCollectionPostCommandHandler>();
        services.AddCommand<CollectionPostApprovalCommand, CollectionPostDto, CollectionPostApprovalCommandHandler>();

        // Link sets ("ชุดลิงก์กลุ่ม")
        services.AddQuery<GetLinkSetsQuery, IReadOnlyList<LinkSetDto>, GetLinkSetsQueryHandler>();
        services.AddCommand<CreateLinkSetCommand, LinkSetDto, CreateLinkSetCommandHandler>();
        services.AddCommand<UpdateLinkSetCommand, LinkSetDto, UpdateLinkSetCommandHandler>();
        services.AddCommand<DeleteLinkSetCommand, Unit, DeleteLinkSetCommandHandler>();
        services.AddCommand<AddLinkCommand, SetLinkDto, AddLinkCommandHandler>();
        services.AddCommand<UpdateLinkCommand, SetLinkDto, UpdateLinkCommandHandler>();
        services.AddCommand<DeleteLinkCommand, Unit, DeleteLinkCommandHandler>();
        services.AddCommand<EnableLinkCommand, SetLinkDto, EnableLinkCommandHandler>();
        services.AddCommand<BulkAddLinksCommand, BulkLinksResultDto, BulkAddLinksCommandHandler>();
        services.AddCommand<ImportAccountGroupsCommand, LinkSetDto, ImportAccountGroupsCommandHandler>();
        services.AddCommand<ImportLinksCsvCommand, CsvImportResultDto, ImportLinksCsvCommandHandler>();
        services.AddQuery<GetAccountGroupsQuery, IReadOnlyList<GroupLinkDto>, GetAccountGroupsQueryHandler>();

        // Schedule engine: schedules, the materializer that queues their posts, test posts, backup and restore
        services.AddScoped<ScheduleMaterializer>();
        services.AddSingleton<TopUpThrottle>();
        services.AddScoped<ScheduleTopUp>();
        services.AddScoped<ScheduleViews>();
        services.AddQuery<GetSchedulesQuery, IReadOnlyList<ScheduleDto>, GetSchedulesQueryHandler>();
        services.AddCommand<CreateScheduleCommand, ScheduleCreatedDto, CreateScheduleCommandHandler>();
        services.AddCommand<SetScheduleActiveCommand, ScheduleDto, SetScheduleActiveCommandHandler>();
        services.AddCommand<DeleteScheduleCommand, Unit, DeleteScheduleCommandHandler>();
        services.AddQuery<GetBestTimesQuery, IReadOnlyList<string>, GetBestTimesQueryHandler>();
        services.AddCommand<CreateTestPostCommand, PostDto, CreateTestPostCommandHandler>();
        services.AddQuery<GetBackupQuery, BackupDto, GetBackupQueryHandler>();
        services.AddCommand<RestoreBackupCommand, RestoreResultDto, RestoreBackupCommandHandler>();

        // Notifications: sends nothing until the infrastructure registers the real dispatcher
        services.TryAddSingleton<INotificationDispatcher, NullNotificationDispatcher>();

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
        services.AddCommand<UpdateDeviceCommand, DeviceDto, UpdateDeviceCommandHandler>();
        services.AddCommand<PairDeviceCommand, PairResultDto, PairDeviceCommandHandler>();
        services.AddCommand<DeviceHeartbeatCommand, DeviceStatusDto, DeviceHeartbeatCommandHandler>();
        services.AddCommand<SyncDeviceGroupsCommand, int, SyncDeviceGroupsCommandHandler>();
        services.AddCommand<ClaimJobCommand, JobDto?, ClaimJobCommandHandler>();
        services.AddCommand<ReportJobResultCommand, PostDto, ReportJobResultCommandHandler>();
        services.AddQuery<GetDeviceMediaQuery, MediaContent, GetDeviceMediaQueryHandler>();

        // The extension's own campaigns: edited in the web app, synced by the device
        services.AddQuery<GetExtensionConfigQuery, ExtensionConfigDto, GetExtensionConfigQueryHandler>();
        services.AddCommand<SaveExtensionConfigCommand, ConfigSavedDto, SaveExtensionConfigCommandHandler>();
        services.AddQuery<GetExtensionImageQuery, MediaContent, GetExtensionImageQueryHandler>();
        services.AddCommand<PutExtensionImageCommand, Unit, PutExtensionImageCommandHandler>();
        services.AddQuery<GetDeviceLiveQuery, DeviceLiveDto, GetDeviceLiveQueryHandler>();
        services.AddCommand<SendDeviceCommandCommand, DeviceCommandDto, SendDeviceCommandCommandHandler>();
        services.AddQuery<GetDeviceCommandQuery, DeviceCommandDto, GetDeviceCommandQueryHandler>();
        services.AddCommand<ClearDeviceLogsCommand, Unit, ClearDeviceLogsCommandHandler>();
        services.AddCommand<DeviceSyncCommand, DeviceSyncDto, DeviceSyncCommandHandler>();
        services.AddQuery<GetWorkspaceEventsQuery, DeviceEventsPageDto, GetWorkspaceEventsQueryHandler>();
        services.AddQuery<GetOwnExtensionConfigQuery, ExtensionConfigDto, GetOwnExtensionConfigQueryHandler>();
        services.AddCommand<SaveOwnExtensionConfigCommand, ConfigSavedDto, SaveOwnExtensionConfigCommandHandler>();
        services.AddQuery<GetMissingImagesQuery, MissingImagesDto, GetMissingImagesQueryHandler>();
        services.AddQuery<GetOwnExtensionImageQuery, ExtensionImageDto, GetOwnExtensionImageQueryHandler>();
        services.AddCommand<PutOwnExtensionImageCommand, Unit, PutOwnExtensionImageCommandHandler>();
        services.AddCommand<ReportCommandResultCommand, Unit, ReportCommandResultCommandHandler>();

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
        services.AddCommand<RetryPaymentCommand, TransactionDto, RetryPaymentCommandHandler>();
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
