using FluentValidation;
using SIRIAUTOPOST.Application.Features.AutoReply;
using SIRIAUTOPOST.Application.Features.Notifications;
using SIRIAUTOPOST.Application.Features.Reports;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Application.Validators;

// Input shape checks of notifications, auto-reply and reports. Which rules are allowed (plans, scopes) lives in the handlers.

public sealed class UpdateNotificationsCommandValidator : AbstractValidator<UpdateNotificationsCommand>
{
    public UpdateNotificationsCommandValidator()
    {
        RuleFor(x => x.Settings).NotNull().WithMessage(Messages.BadValue);
        When(x => x.Settings is not null, () =>
        {
            RuleFor(x => x.Settings.Telegram).NotNull().WithMessage(Messages.BadValue);
            RuleFor(x => x.Settings.Line).NotNull().WithMessage(Messages.BadValue);
            RuleFor(x => x.Settings.Events).NotNull().WithMessage(Messages.BadValue);
            RuleFor(x => x.Settings.Telegram.Token).MaximumLength(TelegramChannel.MaxTokenLength)
                .WithMessage($"โทเคนยาวเกิน {TelegramChannel.MaxTokenLength} ตัวอักษร").When(x => x.Settings.Telegram is not null);
            RuleFor(x => x.Settings.Telegram.ChatId).MaximumLength(TelegramChannel.MaxTargetLength)
                .WithMessage($"รหัสแชทยาวเกิน {TelegramChannel.MaxTargetLength} ตัวอักษร").When(x => x.Settings.Telegram is not null);
            RuleFor(x => x.Settings.Line.Token).MaximumLength(TelegramChannel.MaxTokenLength)
                .WithMessage($"โทเคนยาวเกิน {TelegramChannel.MaxTokenLength} ตัวอักษร").When(x => x.Settings.Line is not null);
            RuleFor(x => x.Settings.Line.To).MaximumLength(TelegramChannel.MaxTargetLength)
                .WithMessage($"ผู้รับยาวเกิน {TelegramChannel.MaxTargetLength} ตัวอักษร").When(x => x.Settings.Line is not null);
            RuleFor(x => x.Settings.CommandsUsers).MaximumLength(NotificationSettings.MaxUsersLength)
                .WithMessage($"รายชื่อผู้ใช้คำสั่งยาวเกิน {NotificationSettings.MaxUsersLength} ตัวอักษร");
            RuleFor(x => x.Settings.Sets).Must(s => s is null || s.Count <= NotificationSettings.MaxSets)
                .WithMessage($"ตั้งกฎแจ้งเตือนของชุดลิงก์ได้ไม่เกิน {NotificationSettings.MaxSets} ชุด");
            RuleForEach(x => x.Settings.Sets).Cascade(CascadeMode.Stop)
                .NotNull().WithMessage(Messages.BadValue)
                .Must(s => s.Groups is null || s.Groups.Count <= NotificationSettings.MaxGroupRulesPerSet)
                .WithMessage($"ตั้งกฎแจ้งเตือนของกลุ่มได้ไม่เกิน {NotificationSettings.MaxGroupRulesPerSet} กลุ่มต่อชุด")
                .Must(s => s.Groups is null || s.Groups.Values.All(g => g is not null)).WithMessage(Messages.BadValue);
        });
    }
}

public sealed class SendTestNotificationCommandValidator : AbstractValidator<SendTestNotificationCommand>
{
    public SendTestNotificationCommandValidator() =>
        RuleFor(x => x.Channel)
            .Must(c => c is SendTestNotificationCommandHandler.Tg or SendTestNotificationCommandHandler.Line)
            .WithMessage("เลือกช่องทางทดสอบเป็น tg หรือ line");
}

public sealed class FindTelegramChatsCommandValidator : AbstractValidator<FindTelegramChatsCommand>
{
    public FindTelegramChatsCommandValidator() =>
        RuleFor(x => x.Token).MaximumLength(TelegramChannel.MaxTokenLength).WithMessage($"โทเคนยาวเกิน {TelegramChannel.MaxTokenLength} ตัวอักษร");
}

public sealed class UpdateAutoReplyCommandValidator : AbstractValidator<UpdateAutoReplyCommand>
{
    public UpdateAutoReplyCommandValidator()
    {
        RuleFor(x => x.Settings).NotNull().WithMessage(Messages.BadValue);
        When(x => x.Settings is not null, () =>
        {
            RuleFor(x => x.Settings.Rules).Must(r => r is null || r.Count <= AutoReplySettings.MaxRules)
                .WithMessage($"ตั้งกฎตอบอัตโนมัติได้ไม่เกิน {AutoReplySettings.MaxRules} กฎ");
            RuleForEach(x => x.Settings.Rules).Cascade(CascadeMode.Stop)
                .NotNull().WithMessage(Messages.BadValue)
                .Must(r => (r.Keywords ?? "").Length <= AutoReplyRule.MaxKeywordsLength).WithMessage($"คำที่ตรวจจับยาวเกิน {AutoReplyRule.MaxKeywordsLength} ตัวอักษร")
                .Must(r => (r.Reply ?? "").Length <= AutoReplyRule.MaxReplyLength).WithMessage($"ข้อความตอบคอมเมนต์ยาวเกิน {AutoReplyRule.MaxReplyLength} ตัวอักษร")
                .Must(r => (r.Inbox ?? "").Length <= AutoReplyRule.MaxInboxLength).WithMessage($"ข้อความทักแชทยาวเกิน {AutoReplyRule.MaxInboxLength} ตัวอักษร")
                .Must(r => (r.Scope ?? "").Length <= 40).WithMessage("ขอบเขตของกฎไม่ถูกต้อง");
        });
    }
}

public sealed class ShareReportCommandValidator : AbstractValidator<ShareReportCommand>
{
    public ShareReportCommandValidator()
    {
        RuleFor(x => x.Brand).Cascade(CascadeMode.Stop)
            .Must(b => !string.IsNullOrWhiteSpace(b)).WithMessage("กรุณาใส่ชื่อแบรนด์")
            .Must(b => b.Trim().Length <= ReportShare.MaxBrandLength).WithMessage($"ชื่อแบรนด์ยาวเกิน {ReportShare.MaxBrandLength} ตัวอักษร");
        RuleFor(x => x.Period).Must(p => p is "week" or "month").WithMessage("ช่วงเวลาของรายงานต้องเป็น week หรือ month");
    }
}
