using FluentValidation;
using SIRIAUTOPOST.Application.Features.Auth;
using SIRIAUTOPOST.Application.Features.Devices;
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
    public ChangePlanCommandValidator() => RuleFor(x => x.Plan).IsInEnum();
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
