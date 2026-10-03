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

public sealed class SignUpCommandValidator : AbstractValidator<SignUpCommand>
{
    public const int MinPasswordLength = 8;

    public SignUpCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(254).WithMessage("กรุณาใส่อีเมลที่ถูกต้อง");
        RuleFor(x => x.Password).NotEmpty().MinimumLength(MinPasswordLength).MaximumLength(100)
            .WithMessage($"รหัสผ่านต้องยาวอย่างน้อย {MinPasswordLength} ตัวอักษร");
        RuleFor(x => x.Name).MaximumLength(120);
        RuleFor(x => x.Plan).IsInEnum();
    }
}

public sealed class LogInCommandValidator : AbstractValidator<LogInCommand>
{
    public LogInCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("กรุณาใส่อีเมลที่ถูกต้อง");
        RuleFor(x => x.Password).NotEmpty().WithMessage("กรุณาใส่รหัสผ่าน");
    }
}

public sealed class ChangePlanCommandValidator : AbstractValidator<ChangePlanCommand>
{
    public ChangePlanCommandValidator()
    {
        RuleFor(x => x.Plan).IsInEnum();
        RuleFor(x => x.Cycle).IsInEnum();
        RuleFor(x => x.PromoCode).MaximumLength(30);
    }
}

public sealed class SetPaymentMethodCommandValidator : AbstractValidator<SetPaymentMethodCommand>
{
    public SetPaymentMethodCommandValidator()
    {
        RuleFor(x => x.Brand).MaximumLength(20);
        RuleFor(x => x.Last4).Matches(@"^\d{4}$").WithMessage("เลข 4 ตัวท้ายของบัตรไม่ถูกต้อง");
        RuleFor(x => x.ExpMonth).InclusiveBetween(1, 12).WithMessage("เดือนที่บัตรหมดอายุไม่ถูกต้อง");
        RuleFor(x => x.ExpYear).InclusiveBetween(2000, 2100).WithMessage("ปีที่บัตรหมดอายุไม่ถูกต้อง");
    }
}

public sealed class InviteMemberCommandValidator : AbstractValidator<InviteMemberCommand>
{
    public InviteMemberCommandValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("กรุณาใส่อีเมลที่ถูกต้อง").MaximumLength(254);
        RuleFor(x => x.Role).IsInEnum().NotEqual(Domain.Enums.WorkspaceRole.Owner).WithMessage("บทบาทไม่ถูกต้อง");
    }
}

public sealed class ChangeMemberRoleCommandValidator : AbstractValidator<ChangeMemberRoleCommand>
{
    public ChangeMemberRoleCommandValidator() =>
        RuleFor(x => x.Role).IsInEnum().NotEqual(Domain.Enums.WorkspaceRole.Owner).WithMessage("บทบาทไม่ถูกต้อง");
}

public sealed class SetCustomerLimitsCommandValidator : AbstractValidator<SetCustomerLimitsCommand>
{
    public SetCustomerLimitsCommandValidator()
    {
        RuleFor(x => x.Accounts).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Posts).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Devices).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Seats).GreaterThanOrEqualTo(0);
    }
}

public sealed class UpdatePlanCommandValidator : AbstractValidator<UpdatePlanCommand>
{
    public UpdatePlanCommandValidator() => RuleFor(x => x.Price).GreaterThanOrEqualTo(0).WithMessage("ราคาไม่ถูกต้อง");
}

public sealed class CreatePromoCommandValidator : AbstractValidator<CreatePromoCommand>
{
    public CreatePromoCommandValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("กรุณาใส่โค้ด").MaximumLength(30);
        RuleFor(x => x.Discount).Must(d => Promo.Discounts.Contains(d)).WithMessage("ส่วนลดไม่ถูกต้อง");
    }
}

public sealed class CreateWorkspaceCommandValidator : AbstractValidator<CreateWorkspaceCommand>
{
    public CreateWorkspaceCommandValidator() =>
        RuleFor(x => x.Name).NotEmpty().WithMessage("กรุณาใส่ชื่อเวิร์กสเปซ").MaximumLength(Workspace.MaxNameLength);
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
        RuleFor(x => x.Title).NotEmpty().WithMessage("กรุณาใส่ชื่อและข้อความ").MaximumLength(Snippet.MaxTitleLength);
        RuleFor(x => x.Text).NotEmpty().WithMessage("กรุณาใส่ชื่อและข้อความ").MaximumLength(Snippet.MaxTextLength);
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
    public const int MaxGroups = 1000;

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
        RuleFor(x => x.Name).MaximumLength(500);
    }
}

public sealed class PutOwnExtensionImageCommandValidator : AbstractValidator<PutOwnExtensionImageCommand>
{
    public PutOwnExtensionImageCommandValidator()
    {
        RuleFor(x => x.ImageId).Must(ExtensionImage.ValidId).WithMessage("รหัสรูปไม่ถูกต้อง");
        RuleFor(x => x.Data).NotEmpty().WithMessage("ไม่มีข้อมูลไฟล์");
        RuleFor(x => x.Name).MaximumLength(500);
    }
}

public sealed class UpdateDeviceCommandValidator : AbstractValidator<UpdateDeviceCommand>
{
    public UpdateDeviceCommandValidator() =>
        RuleFor(x => x.Name).MaximumLength(Device.MaxNameLength).WithMessage($"ชื่อเครื่องยาวเกิน {Device.MaxNameLength} ตัวอักษร");
}
