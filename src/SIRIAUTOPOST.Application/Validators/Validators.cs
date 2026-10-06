using FluentValidation;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Admin;
using SIRIAUTOPOST.Application.Features.Ai;
using SIRIAUTOPOST.Application.Features.Auth;
using SIRIAUTOPOST.Application.Features.Billing;
using SIRIAUTOPOST.Application.Features.Collections;
using SIRIAUTOPOST.Application.Features.LinkSets;
using SIRIAUTOPOST.Application.Features.MasterPosts;
using SIRIAUTOPOST.Application.Features.Team;
using SIRIAUTOPOST.Application.Features.Devices;
using SIRIAUTOPOST.Application.Features.Extension;
using SIRIAUTOPOST.Application.Features.Library;
using SIRIAUTOPOST.Application.Features.Posts;
using SIRIAUTOPOST.Application.Features.Workspaces;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Application.Validators;

// Input shape checks. Business rules (future times, account health, plan gates...) live in the Domain.
// Every rule carries its own Thai message (WithMessage binds to the rule right before it): a message that
// belongs to another rule would be shown to the person for the wrong mistake.

internal static class Messages
{
    public const string BadValue = "ค่าที่ส่งมาไม่ถูกต้อง";
    public const string BadEmail = "กรุณาใส่อีเมลที่ถูกต้อง";
}

public sealed class SignUpCommandValidator : AbstractValidator<SignUpCommand>
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 100;
    public const int MaxNameLength = 120;

    public SignUpCommandValidator()
    {
        RuleFor(x => x.Email).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(Messages.BadEmail)
            .EmailAddress().WithMessage(Messages.BadEmail)
            .MaximumLength(254).WithMessage("อีเมลยาวเกิน 254 ตัวอักษร");
        RuleFor(x => x.Password).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("กรุณาใส่รหัสผ่าน")
            .MinimumLength(MinPasswordLength).WithMessage($"รหัสผ่านต้องยาวอย่างน้อย {MinPasswordLength} ตัวอักษร")
            .MaximumLength(MaxPasswordLength).WithMessage($"รหัสผ่านยาวเกิน {MaxPasswordLength} ตัวอักษร");
        RuleFor(x => x.Name).MaximumLength(MaxNameLength).WithMessage($"ชื่อยาวเกิน {MaxNameLength} ตัวอักษร");
    }
}

public sealed class GoogleLogInCommandValidator : AbstractValidator<GoogleLogInCommand>
{
    public GoogleLogInCommandValidator() =>
        RuleFor(x => x.IdToken).NotEmpty().WithMessage("ไม่พบข้อมูลจาก Google").MaximumLength(4096).WithMessage("ข้อมูลจาก Google ไม่ถูกต้อง");
}

public sealed class LogInCommandValidator : AbstractValidator<LogInCommand>
{
    public LogInCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage(Messages.BadEmail);
        RuleFor(x => x.Password).NotEmpty().WithMessage("กรุณาใส่รหัสผ่าน");
    }
}

public sealed class ChangePlanCommandValidator : AbstractValidator<ChangePlanCommand>
{
    public ChangePlanCommandValidator()
    {
        RuleFor(x => x.Plan).IsInEnum().WithMessage("แผนไม่ถูกต้อง");
        RuleFor(x => x.Cycle).IsInEnum().WithMessage("รอบบิลไม่ถูกต้อง");
        RuleFor(x => x.PromoCode).MaximumLength(30).WithMessage("โค้ดส่วนลดยาวเกิน 30 ตัวอักษร");
    }
}

public sealed class StartPaymentCommandValidator : AbstractValidator<StartPaymentCommand>
{
    public StartPaymentCommandValidator()
    {
        RuleFor(x => x.Plan).IsInEnum().WithMessage("แผนไม่ถูกต้อง");
        RuleFor(x => x.Cycle).IsInEnum().WithMessage("รอบบิลไม่ถูกต้อง");
        RuleFor(x => x.Method).IsInEnum().WithMessage("ช่องทางชำระเงินไม่ถูกต้อง");
        RuleFor(x => x.PromoCode).MaximumLength(30).WithMessage("โค้ดส่วนลดยาวเกิน 30 ตัวอักษร");
    }
}

public sealed class ConfirmPaymentCommandValidator : AbstractValidator<ConfirmPaymentCommand>
{
    public ConfirmPaymentCommandValidator() =>
        RuleFor(x => x.IntentId).NotEmpty().MaximumLength(100).Must(id => id.StartsWith("pi_", StringComparison.Ordinal))
            .WithMessage("รหัสการชำระเงินไม่ถูกต้อง");
}

public sealed class ConfirmCheckoutCommandValidator : AbstractValidator<ConfirmCheckoutCommand>
{
    public ConfirmCheckoutCommandValidator() =>
        RuleFor(x => x.SessionId).NotEmpty().MaximumLength(200).Must(id => id.StartsWith("cs_", StringComparison.Ordinal))
            .WithMessage("รหัสการชำระเงินไม่ถูกต้อง");
}

public sealed class InviteMemberCommandValidator : AbstractValidator<InviteMemberCommand>
{
    public InviteMemberCommandValidator()
    {
        RuleFor(x => x.Email).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(Messages.BadEmail)
            .EmailAddress().WithMessage(Messages.BadEmail)
            .MaximumLength(254).WithMessage("อีเมลยาวเกิน 254 ตัวอักษร");
        RuleFor(x => x.Role).Cascade(CascadeMode.Stop)
            .IsInEnum().WithMessage("บทบาทไม่ถูกต้อง")
            .NotEqual(Domain.Enums.WorkspaceRole.Owner).WithMessage("บทบาทไม่ถูกต้อง");
    }
}

public sealed class ChangeMemberRoleCommandValidator : AbstractValidator<ChangeMemberRoleCommand>
{
    public ChangeMemberRoleCommandValidator() =>
        RuleFor(x => x.Role).Cascade(CascadeMode.Stop)
            .IsInEnum().WithMessage("บทบาทไม่ถูกต้อง")
            .NotEqual(Domain.Enums.WorkspaceRole.Owner).WithMessage("บทบาทไม่ถูกต้อง");
}

public sealed class SetCustomerLimitsCommandValidator : AbstractValidator<SetCustomerLimitsCommand>
{
    public SetCustomerLimitsCommandValidator()
    {
        RuleFor(x => x.Accounts).GreaterThanOrEqualTo(0).WithMessage("ขีดจำกัดต้องไม่ติดลบ");
        RuleFor(x => x.Posts).GreaterThanOrEqualTo(0).WithMessage("ขีดจำกัดต้องไม่ติดลบ");
        RuleFor(x => x.Devices).GreaterThanOrEqualTo(0).WithMessage("ขีดจำกัดต้องไม่ติดลบ");
        RuleFor(x => x.Seats).GreaterThanOrEqualTo(0).WithMessage("ขีดจำกัดต้องไม่ติดลบ");
        RuleFor(x => x.Groups).GreaterThanOrEqualTo(0).WithMessage("ขีดจำกัดต้องไม่ติดลบ");
        RuleFor(x => x.Images).GreaterThanOrEqualTo(0).WithMessage("ขีดจำกัดต้องไม่ติดลบ");
        RuleFor(x => x.LibraryPosts).GreaterThanOrEqualTo(0).WithMessage("ขีดจำกัดต้องไม่ติดลบ");
    }
}

public sealed class SetCustomerPlanCommandValidator : AbstractValidator<SetCustomerPlanCommand>
{
    public SetCustomerPlanCommandValidator() => RuleFor(x => x.Plan).IsInEnum().WithMessage("แผนไม่ถูกต้อง");
}

public sealed class SetCustomerStatusCommandValidator : AbstractValidator<SetCustomerStatusCommand>
{
    public SetCustomerStatusCommandValidator() => RuleFor(x => x.Status).IsInEnum().WithMessage("สถานะไม่ถูกต้อง");
}

public sealed class UpdatePlanCommandValidator : AbstractValidator<UpdatePlanCommand>
{
    public UpdatePlanCommandValidator()
    {
        RuleFor(x => x.Key).IsInEnum().WithMessage("แผนไม่ถูกต้อง");
        RuleFor(x => x.Price).GreaterThanOrEqualTo(0).WithMessage("ราคาไม่ถูกต้อง");
    }
}

public sealed class SetPaymentOverrideCommandValidator : AbstractValidator<SetPaymentOverrideCommand>
{
    public SetPaymentOverrideCommandValidator()
    {
        RuleFor(x => x.Emails.Count).LessThanOrEqualTo(PaymentOverride.MaxEmails)
            .WithMessage($"ระบุอีเมลได้ไม่เกิน {PaymentOverride.MaxEmails} บัญชี");
        RuleForEach(x => x.Emails).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage(Messages.BadEmail)
            .EmailAddress().WithMessage(Messages.BadEmail)
            .MaximumLength(254).WithMessage("อีเมลยาวเกิน 254 ตัวอักษร");
    }
}

public sealed class CreatePromoCommandValidator : AbstractValidator<CreatePromoCommand>
{
    public CreatePromoCommandValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("กรุณาใส่โค้ด").MaximumLength(30).WithMessage("โค้ดยาวเกิน 30 ตัวอักษร");
        RuleFor(x => x.Discount).Must(d => Promo.Discounts.Contains(d)).WithMessage("ส่วนลดไม่ถูกต้อง");
    }
}

public sealed class CreateWorkspaceCommandValidator : AbstractValidator<CreateWorkspaceCommand>
{
    public CreateWorkspaceCommandValidator() =>
        RuleFor(x => x.Name).NotEmpty().WithMessage("กรุณาใส่ชื่อเวิร์กสเปซ")
            .MaximumLength(Workspace.MaxNameLength).WithMessage($"ชื่อเวิร์กสเปซยาวเกิน {Workspace.MaxNameLength} ตัวอักษร");
}

public sealed class SchedulePostsCommandValidator : AbstractValidator<SchedulePostsCommand>
{
    public const int MaxMedia = 20;

    public SchedulePostsCommandValidator()
    {
        RuleFor(x => x.Content).NotEmpty().WithMessage("กรุณาใส่ข้อความโพสต์")
            .MaximumLength(Post.MaxContentLength).WithMessage($"ข้อความโพสต์ยาวเกิน {Post.MaxContentLength} ตัวอักษร");
        RuleFor(x => x.Targets).NotEmpty().WithMessage("กรุณาเลือกบัญชีปลายทางอย่างน้อย 1 รายการ");
        RuleFor(x => x.Repeat).Must(r => Repeat.All.Contains(r)).WithMessage("รูปแบบการทำซ้ำไม่ถูกต้อง");
        RuleFor(x => x.MediaIds).Must(m => m is null || m.Count <= MaxMedia).WithMessage($"แนบสื่อได้ไม่เกิน {MaxMedia} ไฟล์");
    }
}

public sealed class CreateSnippetCommandValidator : AbstractValidator<CreateSnippetCommand>
{
    public CreateSnippetCommandValidator()
    {
        RuleFor(x => x.Title).NotEmpty().WithMessage("กรุณาใส่ชื่อและข้อความ")
            .MaximumLength(Snippet.MaxTitleLength).WithMessage($"ชื่อยาวเกิน {Snippet.MaxTitleLength} ตัวอักษร");
        RuleFor(x => x.Text).NotEmpty().WithMessage("กรุณาใส่ชื่อและข้อความ")
            .MaximumLength(Snippet.MaxTextLength).WithMessage($"ข้อความยาวเกิน {Snippet.MaxTextLength} ตัวอักษร");
    }
}

public sealed class UploadMediaCommandValidator : AbstractValidator<UploadMediaCommand>
{
    public UploadMediaCommandValidator() =>
        RuleFor(x => x.Data).Must(d => d.LongLength <= MediaFile.MaxBytes).WithMessage("ไฟล์ใหญ่เกิน 100 MB");
}

public sealed class PairDeviceCommandValidator : AbstractValidator<PairDeviceCommand>
{
    public PairDeviceCommandValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("กรุณาใส่รหัสจับคู่");
        RuleFor(x => x.Name).MaximumLength(Device.MaxNameLength).WithMessage($"ชื่อเครื่องยาวเกิน {Device.MaxNameLength} ตัวอักษร");
    }
}

public sealed class SyncDeviceGroupsCommandValidator : AbstractValidator<SyncDeviceGroupsCommand>
{
    public const int MaxGroups = 5000;

    public SyncDeviceGroupsCommandValidator()
    {
        RuleFor(x => x.Groups).NotNull().Must(g => g.Count <= MaxGroups).WithMessage($"ส่งกลุ่มได้ไม่เกิน {MaxGroups} กลุ่ม");
        RuleForEach(x => x.Groups).ChildRules(g =>
        {
            g.RuleFor(x => x.Name).MaximumLength(Post.MaxTargetLength).WithMessage($"ชื่อกลุ่มยาวเกิน {Post.MaxTargetLength} ตัวอักษร");
            g.RuleFor(x => x.Url)
                .Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                .WithMessage("ลิงก์กลุ่มต้องขึ้นต้นด้วย http:// หรือ https://");
        });
    }
}

public sealed class SendDeviceCommandCommandValidator : AbstractValidator<SendDeviceCommandCommand>
{
    public SendDeviceCommandCommandValidator() =>
        RuleFor(x => x.Cmd).Must(c => DeviceCommand.Allowed.Contains(c ?? "")).WithMessage("ไม่รู้จักคำสั่งนี้");
}

public sealed class PutExtensionImageCommandValidator : AbstractValidator<PutExtensionImageCommand>
{
    public PutExtensionImageCommandValidator()
    {
        RuleFor(x => x.ImageId).Must(ExtensionImage.ValidId).WithMessage("รหัสรูปไม่ถูกต้อง");
        RuleFor(x => x.Data).NotEmpty().WithMessage("ไม่มีข้อมูลไฟล์");
        RuleFor(x => x.Name).MaximumLength(500).WithMessage("ชื่อไฟล์ยาวเกิน 500 ตัวอักษร");
    }
}

public sealed class PutOwnExtensionImageCommandValidator : AbstractValidator<PutOwnExtensionImageCommand>
{
    public PutOwnExtensionImageCommandValidator()
    {
        RuleFor(x => x.ImageId).Must(ExtensionImage.ValidId).WithMessage("รหัสรูปไม่ถูกต้อง");
        RuleFor(x => x.Data).NotEmpty().WithMessage("ไม่มีข้อมูลไฟล์");
        RuleFor(x => x.Name).MaximumLength(500).WithMessage("ชื่อไฟล์ยาวเกิน 500 ตัวอักษร");
    }
}

public sealed class UpdateDeviceCommandValidator : AbstractValidator<UpdateDeviceCommand>
{
    public UpdateDeviceCommandValidator() =>
        RuleFor(x => x.Name).MaximumLength(Device.MaxNameLength).WithMessage($"ชื่อเครื่องยาวเกิน {Device.MaxNameLength} ตัวอักษร");
}

// ---------- collections ----------

public sealed class CreateCollectionCommandValidator : AbstractValidator<CreateCollectionCommand>
{
    public CreateCollectionCommandValidator()
    {
        RuleFor(x => x.Name).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("กรุณาใส่ชื่อชุดโพสต์")
            .MaximumLength(PostCollection.MaxNameLength).WithMessage($"ชื่อชุดโพสต์ยาวเกิน {PostCollection.MaxNameLength} ตัวอักษร");
        RuleFor(x => x.Description).MaximumLength(PostCollection.MaxDescriptionLength)
            .WithMessage($"คำอธิบายยาวเกิน {PostCollection.MaxDescriptionLength} ตัวอักษร");
    }
}

public sealed class WriteAiPostsCommandValidator : AbstractValidator<WriteAiPostsCommand>
{
    public WriteAiPostsCommandValidator()
    {
        RuleFor(x => x.Topic).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("กรุณาบอก AI ว่าจะให้เขียนโพสต์เรื่องอะไร")
            .MaximumLength(WriteAiPostsCommandHandler.MaxTopicLength).WithMessage($"หัวข้อยาวเกิน {WriteAiPostsCommandHandler.MaxTopicLength} ตัวอักษร");
        RuleFor(x => x.Points!.Count).LessThanOrEqualTo(WriteAiPostsCommandHandler.MaxPoints)
            .When(x => x.Points is not null).WithMessage($"ใส่จุดขายได้ไม่เกิน {WriteAiPostsCommandHandler.MaxPoints} ข้อ");
        RuleForEach(x => x.Points).MaximumLength(WriteAiPostsCommandHandler.MaxPointLength)
            .WithMessage($"จุดขายแต่ละข้อยาวเกิน {WriteAiPostsCommandHandler.MaxPointLength} ตัวอักษร");
        RuleFor(x => x.Count).InclusiveBetween(1, WriteAiPostsCommandHandler.MaxCount)
            .WithMessage($"จำนวนที่ให้ AI เขียนต้องอยู่ระหว่าง 1–{WriteAiPostsCommandHandler.MaxCount}");
    }
}

public sealed class UpdateCollectionCommandValidator : AbstractValidator<UpdateCollectionCommand>
{
    public UpdateCollectionCommandValidator()
    {
        RuleFor(x => x.Name).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("กรุณาใส่ชื่อชุดโพสต์")
            .MaximumLength(PostCollection.MaxNameLength).WithMessage($"ชื่อชุดโพสต์ยาวเกิน {PostCollection.MaxNameLength} ตัวอักษร");
        RuleFor(x => x.Description).MaximumLength(PostCollection.MaxDescriptionLength)
            .WithMessage($"คำอธิบายยาวเกิน {PostCollection.MaxDescriptionLength} ตัวอักษร");
        RuleFor(x => x.Icon).MaximumLength(PostCollection.MaxIconLength).WithMessage($"ไอคอนยาวเกิน {PostCollection.MaxIconLength} ตัวอักษร");
        RuleFor(x => x.Settings).Cascade(CascadeMode.Stop).NotNull().WithMessage(Messages.BadValue)
            .SetValidator(new CollectionSettingsValidator());
    }
}

public sealed class CollectionSettingsValidator : AbstractValidator<CollectionSettingsDto>
{
    public CollectionSettingsValidator()
    {
        RuleFor(x => x.Hashtags).MaximumLength(CollectionSettings.MaxHashtagsLength)
            .WithMessage($"แฮชแท็กยาวเกิน {CollectionSettings.MaxHashtagsLength} ตัวอักษร");
        RuleFor(x => x.PageTags).MaximumLength(CollectionSettings.MaxPageTagsLength)
            .WithMessage($"รายการแท็กเพจยาวเกิน {CollectionSettings.MaxPageTagsLength} ตัวอักษร");
        RuleFor(x => x.Footer).MaximumLength(CollectionSettings.MaxFooterLength)
            .WithMessage($"ข้อความส่วนท้ายยาวเกิน {CollectionSettings.MaxFooterLength} ตัวอักษร");
        RuleFor(x => x.FooterPos).IsInEnum().WithMessage(Messages.BadValue);
        RuleFor(x => x.WatermarkPos).IsInEnum().WithMessage(Messages.BadValue);
    }
}

internal static class CollectionPostRules
{
    public static IRuleBuilderOptions<T, string> Text<T>(this IRuleBuilderInitial<T, string> rule) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("กรุณาใส่ข้อความโพสต์")
            .MaximumLength(CollectionPost.MaxTextLength).WithMessage($"ข้อความโพสต์ยาวเกิน {CollectionPost.MaxTextLength} ตัวอักษร");

    public static IRuleBuilderOptions<T, IReadOnlyList<Guid>?> Media<T>(this IRuleBuilder<T, IReadOnlyList<Guid>?> rule) =>
        rule.Must(m => m is null || m.Count <= CollectionPost.MaxMedia).WithMessage($"แนบสื่อได้ไม่เกิน {CollectionPost.MaxMedia} ไฟล์");
}

public sealed class AddCollectionPostCommandValidator : AbstractValidator<AddCollectionPostCommand>
{
    public AddCollectionPostCommandValidator()
    {
        RuleFor(x => x.Text).Text();
        RuleFor(x => x.MediaIds).Media();
    }
}

public sealed class AddCollectionPostsBatchCommandValidator : AbstractValidator<AddCollectionPostsBatchCommand>
{
    public AddCollectionPostsBatchCommandValidator()
    {
        RuleFor(x => x.Items).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("ไม่มีโพสต์ให้เพิ่ม")
            .Must(i => i.Count <= AddCollectionPostsBatchCommandHandler.MaxItems)
            .WithMessage($"เพิ่มโพสต์ได้ครั้งละไม่เกิน {AddCollectionPostsBatchCommandHandler.MaxItems} โพสต์");
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.Text).Text();
            i.RuleFor(x => x.MediaIds).Media();
        });
    }
}

public sealed class UpdateCollectionPostCommandValidator : AbstractValidator<UpdateCollectionPostCommand>
{
    public UpdateCollectionPostCommandValidator()
    {
        RuleFor(x => x.Text).Text();
        RuleFor(x => x.MediaIds).Media();
    }
}

public sealed class CollectionPostApprovalCommandValidator : AbstractValidator<CollectionPostApprovalCommand>
{
    public CollectionPostApprovalCommandValidator() => RuleFor(x => x.Action).IsInEnum().WithMessage("คำสั่งอนุมัติไม่ถูกต้อง");
}

public sealed class AddPostsToCollectionCommandValidator : AbstractValidator<AddPostsToCollectionCommand>
{
    public AddPostsToCollectionCommandValidator() =>
        RuleFor(x => x.PostIds).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("ยังไม่ได้เลือกโพสต์")
            .Must(i => i.Count <= AddPostsToCollectionCommandHandler.MaxItems)
            .WithMessage($"เลือกโพสต์ได้ครั้งละไม่เกิน {AddPostsToCollectionCommandHandler.MaxItems} โพสต์");
}

internal static class MasterPostRules
{
    public static IRuleBuilderOptions<T, IReadOnlyList<Guid>?> Collections<T>(this IRuleBuilder<T, IReadOnlyList<Guid>?> rule) =>
        rule.Must(c => c is null || c.Count <= CollectionPost.MaxCollections)
            .WithMessage($"โพสต์หนึ่งอยู่ได้ไม่เกิน {CollectionPost.MaxCollections} ชุดโพสต์");

    public static void PostSettings<T>(this IRuleBuilder<T, CollectionPostSettingsDto?> rule)
    {
        rule.Must(s => s?.FooterPos is not { } pos || Enum.IsDefined(pos)).WithMessage(Messages.BadValue);
        rule.Must(s => s?.Weekdays is null || s.Weekdays.Count <= 7).WithMessage(Messages.BadValue);
        rule.Must(s => s is null || s.MaxPerDay is >= 0 and <= CollectionPostSettings.MaxPerDayLimit)
            .WithMessage($"จำนวนครั้งต่อวันของโพสต์ต้องอยู่ระหว่าง 0–{CollectionPostSettings.MaxPerDayLimit}");
    }
}

public sealed class CreateMasterPostCommandValidator : AbstractValidator<CreateMasterPostCommand>
{
    public CreateMasterPostCommandValidator()
    {
        RuleFor(x => x.Text).Text();
        RuleFor(x => x.MediaIds).Media();
        RuleFor(x => x.CollectionIds).Collections();
        RuleFor(x => x.Settings).PostSettings();
    }
}

public sealed class UpdateMasterPostCommandValidator : AbstractValidator<UpdateMasterPostCommand>
{
    public UpdateMasterPostCommandValidator()
    {
        RuleFor(x => x.Text).Text();
        RuleFor(x => x.MediaIds).Media();
        RuleFor(x => x.CollectionIds).Collections();
        RuleFor(x => x.Settings).PostSettings();
    }
}

public sealed class MasterPostApprovalCommandValidator : AbstractValidator<MasterPostApprovalCommand>
{
    public MasterPostApprovalCommandValidator() => RuleFor(x => x.Action).IsInEnum().WithMessage("คำสั่งอนุมัติไม่ถูกต้อง");
}

public sealed class BulkMasterPostsCommandValidator : AbstractValidator<BulkMasterPostsCommand>
{
    public BulkMasterPostsCommandValidator()
    {
        RuleFor(x => x.Action).IsInEnum().WithMessage(Messages.BadValue);
        RuleFor(x => x.PostIds).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("ยังไม่ได้เลือกโพสต์")
            .Must(i => i.Count <= BulkMasterPostsCommandHandler.MaxItems)
            .WithMessage($"เลือกโพสต์ได้ครั้งละไม่เกิน {BulkMasterPostsCommandHandler.MaxItems} โพสต์");
        RuleFor(x => x.CollectionId).NotNull()
            .When(x => x.Action is BulkPostAction.AddToCollection or BulkPostAction.RemoveFromCollection)
            .WithMessage("ยังไม่ได้เลือกชุดโพสต์");
    }
}

// ---------- link sets ----------

public sealed class CreateLinkSetCommandValidator : AbstractValidator<CreateLinkSetCommand>
{
    public CreateLinkSetCommandValidator() =>
        RuleFor(x => x.Name).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("กรุณาใส่ชื่อชุดลิงก์")
            .MaximumLength(LinkSet.MaxNameLength).WithMessage($"ชื่อชุดลิงก์ยาวเกิน {LinkSet.MaxNameLength} ตัวอักษร");
}

public sealed class UpdateLinkSetCommandValidator : AbstractValidator<UpdateLinkSetCommand>
{
    public UpdateLinkSetCommandValidator()
    {
        RuleFor(x => x.Name).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("กรุณาใส่ชื่อชุดลิงก์")
            .MaximumLength(LinkSet.MaxNameLength).WithMessage($"ชื่อชุดลิงก์ยาวเกิน {LinkSet.MaxNameLength} ตัวอักษร");
        RuleFor(x => x.AccountIds).Must(a => a is null || a.Count <= LinkSet.MaxAccounts)
            .WithMessage($"เพิ่มบัญชีอื่นได้ไม่เกิน {LinkSet.MaxAccounts} บัญชีต่อชุด");
    }
}

internal static class LinkRules
{
    public static IRuleBuilderOptions<T, string?> LinkName<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(SetLink.MaxNameLength).WithMessage($"ชื่อกลุ่มยาวเกิน {SetLink.MaxNameLength} ตัวอักษร");

    public static IRuleBuilderOptions<T, string?> LinkUrl<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(SetLink.MaxUrlLength).WithMessage($"ลิงก์ยาวเกิน {SetLink.MaxUrlLength} ตัวอักษร");

    public static IRuleBuilderOptions<T, string?> LinkCode<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(SetLink.MaxCodeLength).WithMessage($"รหัสกลุ่มยาวเกิน {SetLink.MaxCodeLength} ตัวอักษร");
}

public sealed class AddLinkCommandValidator : AbstractValidator<AddLinkCommand>
{
    public AddLinkCommandValidator()
    {
        RuleFor(x => x.Name).LinkName();
        RuleFor(x => x.Url).LinkUrl();
        RuleFor(x => x.Code).LinkCode();
        RuleFor(x => x.DailyMax).InclusiveBetween(0, SetLink.MaxDailyMax)
            .WithMessage($"เพดานต่อวันของกลุ่มต้องอยู่ระหว่าง 0–{SetLink.MaxDailyMax}");
    }
}

public sealed class UpdateLinkCommandValidator : AbstractValidator<UpdateLinkCommand>
{
    public UpdateLinkCommandValidator()
    {
        RuleFor(x => x.Name).LinkName();
        RuleFor(x => x.Url).LinkUrl();
        RuleFor(x => x.Code).LinkCode();
        RuleFor(x => x.DailyMax).InclusiveBetween(0, SetLink.MaxDailyMax)
            .WithMessage($"เพดานต่อวันของกลุ่มต้องอยู่ระหว่าง 0–{SetLink.MaxDailyMax}");
    }
}

public sealed class BulkAddLinksCommandValidator : AbstractValidator<BulkAddLinksCommand>
{
    public BulkAddLinksCommandValidator() =>
        RuleFor(x => x.Text).Cascade(CascadeMode.Stop)
            .Must(t => !string.IsNullOrWhiteSpace(t)).WithMessage("วางลิงก์กลุ่มอย่างน้อย 1 บรรทัด")
            .MaximumLength(200_000).WithMessage("ข้อความยาวเกินไป");
}

public sealed class ImportAccountGroupsCommandValidator : AbstractValidator<ImportAccountGroupsCommand>
{
    public ImportAccountGroupsCommandValidator() =>
        RuleFor(x => x.Urls).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("เลือกกลุ่มอย่างน้อย 1 กลุ่ม")
            .Must(u => u.Count <= SyncDeviceGroupsCommandValidator.MaxGroups).WithMessage($"เลือกกลุ่มได้ไม่เกิน {SyncDeviceGroupsCommandValidator.MaxGroups} กลุ่ม");
}

public sealed class ImportLinksCsvCommandValidator : AbstractValidator<ImportLinksCsvCommand>
{
    public ImportLinksCsvCommandValidator()
    {
        RuleFor(x => x.Rows).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("ไม่มีแถวข้อมูลให้นำเข้า")
            .Must(r => r.Count <= ImportLinksCsvCommandHandler.MaxRows).WithMessage($"นำเข้าได้ครั้งละไม่เกิน {ImportLinksCsvCommandHandler.MaxRows:N0} แถว");
        RuleForEach(x => x.Rows).NotNull().WithMessage(Messages.BadValue);
    }
}
