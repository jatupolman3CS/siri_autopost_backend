using System.Globalization;
using FluentValidation;
using FluentValidation.Results;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Features.Backup;
using SIRIAUTOPOST.Application.Features.Schedules;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Application.Validators;

// Input shape checks of schedules, test posts and backups. What the numbers mean (a drip's start before its end,
// the plan's limits) is checked by the Domain.

internal static class ScheduleRules
{
    public static bool IsDate(string? text) =>
        string.IsNullOrWhiteSpace(text) ||
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>"HH:mm"; an empty value means "use the default".</summary>
    public static bool IsTimeOrEmpty(string? text) => string.IsNullOrWhiteSpace(text) || TimeOfDay.IsValid(text);

    public static bool IsTimeList(IReadOnlyList<string>? times) =>
        times is null || (times.Count <= Schedule.MaxTimes && times.All(TimeOfDay.IsValid));

    /// <summary>A start date that is a date but lies outside the range a schedule may start in (see <see cref="Schedule.StartDateRange"/>).</summary>
    public static bool IsStartDateInRange(string? text, int utcOffsetMinutes, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return true; // empty = today
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return true; // the date rule says so
        var (min, max) = Schedule.StartDateRange(now, utcOffsetMinutes);
        return date >= min && date <= max;
    }

    public static bool IsOverrideKey(string? key)
    {
        var k = (key ?? "").Trim();
        const string account = "account:";
        return Guid.TryParse(k.StartsWith(account, StringComparison.OrdinalIgnoreCase) ? k[account.Length..] : k, out _);
    }
}

public sealed class SaveScheduleRequestValidator : AbstractValidator<SaveScheduleRequest>
{
    public SaveScheduleRequestValidator(TimeProvider clock)
    {
        RuleFor(x => x.Name).MaximumLength(Schedule.MaxNameLength).WithMessage($"ชื่อตารางยาวเกิน {Schedule.MaxNameLength} ตัวอักษร");
        RuleFor(x => x.Mode).IsInEnum().WithMessage("รูปแบบตารางไม่ถูกต้อง");
        RuleFor(x => x.Order).IsInEnum().WithMessage("ลำดับโพสต์ไม่ถูกต้อง");
        RuleFor(x => x.Times).Must(ScheduleRules.IsTimeList)
            .WithMessage($"เวลาโพสต์ต้องเป็นรูปแบบ HH:mm และเลือกได้ไม่เกิน {Schedule.MaxTimes} เวลา");
        RuleFor(x => x.FirstTime).Must(ScheduleRules.IsTimeOrEmpty).WithMessage("เวลาเริ่มต้นไม่ถูกต้อง ใช้รูปแบบ HH:mm");
        RuleFor(x => x.OnceTime).Must(ScheduleRules.IsTimeOrEmpty).When(x => !x.StartNow)
            .WithMessage("เวลาโพสต์ไม่ถูกต้อง ใช้รูปแบบ HH:mm");
        RuleFor(x => x.DripFrom).Must(ScheduleRules.IsTimeOrEmpty).WithMessage("เวลาเริ่มช่วงไม่ถูกต้อง ใช้รูปแบบ HH:mm");
        RuleFor(x => x.DripTo).Must(ScheduleRules.IsTimeOrEmpty).WithMessage("เวลาสิ้นสุดช่วงไม่ถูกต้อง ใช้รูปแบบ HH:mm");
        // "Start now" ignores the date (and Once's time): the schedule starts today at this moment.
        RuleFor(x => x.StartDate).Must(ScheduleRules.IsDate).When(x => !x.StartNow)
            .WithMessage("วันที่เริ่มไม่ถูกต้อง ใช้รูปแบบ ปปปป-ดด-วว");
        RuleFor(x => x.StartDate).Must((r, d) => ScheduleRules.IsStartDateInRange(d, r.UtcOffsetMinutes, clock.GetUtcNow())).When(x => !x.StartNow)
            .WithMessage(r =>
            {
                var (min, max) = Schedule.StartDateRange(clock.GetUtcNow(), r.UtcOffsetMinutes);
                return Schedule.StartDateMessage(min, max);
            });
        RuleFor(x => x.EveryHours).InclusiveBetween(1, 24).When(x => x.Mode == ScheduleMode.Interval)
            .WithMessage("ความถี่ต้องอยู่ระหว่าง 1–24 ชั่วโมง");
        RuleFor(x => x.DripCount).InclusiveBetween(1, 12).When(x => x.Mode == ScheduleMode.Drip)
            .WithMessage("จำนวนโพสต์ต่อวันต้องอยู่ระหว่าง 1–12");
        RuleFor(x => x.BumpHours).Must(h => Schedule.BumpOptions.Contains(h)).WithMessage("ตัวเลือกดันโพสต์ไม่ถูกต้อง");
        RuleFor(x => x.AutoDeleteDays).Must(d => Schedule.AutoDeleteOptions.Contains(d)).WithMessage("ตัวเลือกลบโพสต์อัตโนมัติไม่ถูกต้อง");
        RuleFor(x => x.UtcOffsetMinutes).InclusiveBetween(-840, 840).WithMessage("เขตเวลาไม่ถูกต้อง");
        RuleFor(x => x.Overrides).Must(o => o is null || o.Count <= LinkSet.MaxLinks + LinkSet.MaxAccounts)
            .WithMessage("ตั้งเวลาเฉพาะกลุ่มได้ไม่เกินจำนวนกลุ่มในชุด");
        RuleFor(x => x.Overrides).Must(o => o is null || o.Keys.All(ScheduleRules.IsOverrideKey))
            .WithMessage("ชื่อเวลาเฉพาะกลุ่มไม่ถูกต้อง");
        RuleFor(x => x.Overrides).Must(o => o is null || o.Values.All(ScheduleRules.IsTimeList))
            .WithMessage($"เวลาเฉพาะกลุ่มต้องเป็นรูปแบบ HH:mm และเลือกได้ไม่เกิน {Schedule.MaxTimes} เวลา");
    }
}

/// <summary>Checks the request; its errors carry the form's own field names ("times", "startDate"), not "request.times".</summary>
public sealed class CreateScheduleCommandValidator(TimeProvider clock) : AbstractValidator<CreateScheduleCommand>
{
    public override Task<ValidationResult> ValidateAsync(ValidationContext<CreateScheduleCommand> context, CancellationToken cancellation = default) =>
        context.InstanceToValidate.Request is { } request
            ? new SaveScheduleRequestValidator(clock).ValidateAsync(request, cancellation)
            : Task.FromResult(new ValidationResult([new ValidationFailure("request", Messages.BadValue)]));
}

public sealed class CreateTestPostCommandValidator : AbstractValidator<CreateTestPostCommand>
{
    public CreateTestPostCommandValidator() =>
        RuleFor(x => x.Text).MaximumLength(Post.MaxContentLength).WithMessage($"ข้อความโพสต์ยาวเกิน {Post.MaxContentLength} ตัวอักษร");
}

public sealed class RestoreBackupCommandValidator : AbstractValidator<RestoreBackupCommand>
{
    public RestoreBackupCommandValidator() =>
        RuleFor(x => x.Backup).Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("ไม่พบข้อมูลไฟล์สำรอง")
            .Must(b => b.Collections is not null && b.LinkSets is not null && b.Schedules is not null)
            .WithMessage("ไฟล์สำรองไม่สมบูรณ์ ต้องมีชุดโพสต์ ชุดลิงก์ และตารางโพสต์ (ใส่รายการว่างได้)")
            .Must(HasNoHoles)
            .WithMessage("ไฟล์สำรองไม่สมบูรณ์ มีรายการที่เป็นค่าว่าง (null) อยู่ในชุดโพสต์ โพสต์ ชุดลิงก์ ลิงก์ ตารางโพสต์ หรือกฎต่าง ๆ");

    /// <summary>A hand-edited file may hold a null where a whole entry should be; the handler assumes every entry is there.</summary>
    private static bool HasNoHoles(BackupDto b) =>
        b.Collections.All(c => c is { Posts: not null, Settings: not null } && c.Posts.All(p => p is not null))
        && b.LinkSets.All(s => s is { Links: not null } && s.Links.All(l => l is not null))
        && b.Schedules.All(s => s is { Times: not null } && s.Times.All(t => t is not null)
            && (s.Overrides is null || s.Overrides.Values.All(v => v is not null && v.All(t => t is not null))))
        && (b.NotificationRules is null
            || (b.NotificationRules.Events is not null && b.NotificationRules.Sets is not null
                && b.NotificationRules.Sets.All(r => r is not null && (r.Groups is null || r.Groups.All(g => g is not null)))))
        && (b.AutoReply is null || (b.AutoReply.Rules is not null && b.AutoReply.Rules.All(r => r is not null)));
}
