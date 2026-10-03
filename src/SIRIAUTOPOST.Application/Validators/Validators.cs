using FluentValidation;
using SIRIAUTOPOST.Application.Features.Admin;
using SIRIAUTOPOST.Application.Features.Auth;
using SIRIAUTOPOST.Application.Features.Billing;
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
