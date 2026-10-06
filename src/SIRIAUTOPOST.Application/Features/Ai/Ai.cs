using System.Collections.Concurrent;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Ai;

// AI post drafts. The platform holds one key (Ai:ApiKey); the plan decides who may use it (Pro and above) and a daily
// count per workspace keeps the cost in check. Drafts are returned to the web app, never saved: the person edits them in
// the post editor and saves them like any post.

/// <summary>Counts drafts per workspace per UTC day, in memory (a restart gives a fresh day: it is a cost guard, not a ledger).</summary>
public sealed class AiUsage
{
    private readonly ConcurrentDictionary<(Guid Workspace, DateOnly Day), int> _used = new();

    public int Used(Guid workspaceId, DateTimeOffset now) => _used.GetValueOrDefault((workspaceId, DateOnly.FromDateTime(now.UtcDateTime)));

    public int Add(Guid workspaceId, DateTimeOffset now, int count)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        foreach (var key in _used.Keys.Where(k => k.Day < day).ToList()) _used.TryRemove(key, out _);
        return _used.AddOrUpdate((workspaceId, day), count, (_, n) => n + count);
    }
}

/// <summary>Drafts one workspace may ask for per day (Ai:DailyLimit; 0 = no limit).</summary>
public sealed record AiLimits(int DailyDrafts);

/// <summary>Whether the AI buttons of the web app work: the server has a key, and the owner's plan includes the writer.</summary>
public sealed record GetAiStatusQuery(Guid WorkspaceId) : IQuery<AiStatusDto>;

public sealed class GetAiStatusQueryHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IAiWriter writer, AiUsage usage, AiLimits limits, ICurrentUser current,
    TimeProvider clock)
    : IQueryHandler<GetAiStatusQuery, AiStatusDto>
{
    public async Task<AiStatusDto> HandleAsync(GetAiStatusQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var allowed = (await users.OwnerOfAsync(ws, ct)).HasAi;
        int? left = limits.DailyDrafts <= 0 ? null : Math.Max(0, limits.DailyDrafts - usage.Used(ws.Id, clock.GetUtcNow()));
        return new AiStatusDto(writer.Enabled, allowed, writer.Enabled ? writer.Model : "", left);
    }
}

/// <summary>Writes 1-5 drafts for a topic. Needs an editor, the key and the plan; counts against the day's allowance.</summary>
public sealed record WriteAiPostsCommand(Guid WorkspaceId, string Topic, IReadOnlyList<string>? Points, string? Tone, int Count)
    : ICommand<AiDraftsDto>;

public sealed class WriteAiPostsCommandHandler(
    IWorkspaceRepository workspaces, IUserRepository users, IAiWriter writer, AiUsage usage, AiLimits limits, ICurrentUser current,
    TimeProvider clock)
    : ICommandHandler<WriteAiPostsCommand, AiDraftsDto>
{
    public const int MaxCount = 5;
    public const int MaxPoints = 10;
    public const int MaxTopicLength = 300;
    public const int MaxPointLength = 200;
    public static readonly string[] Tones = ["friendly", "formal", "sales", "short"];

    public async Task<AiDraftsDto> HandleAsync(WriteAiPostsCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        if (!(await users.OwnerOfAsync(ws, ct)).HasAi) throw new ForbiddenException(FeatureGates.ProRequired);
        if (!writer.Enabled) throw new DomainException("ระบบ AI ยังไม่ได้ตั้งค่า (ผู้ดูแลระบบต้องใส่ Ai__ApiKey ที่เซิร์ฟเวอร์) จึงยังเขียนโพสต์ด้วย AI ไม่ได้");

        var now = clock.GetUtcNow();
        var count = Math.Clamp(c.Count, 1, MaxCount);
        if (limits.DailyDrafts > 0 && usage.Used(ws.Id, now) + count > limits.DailyDrafts)
            throw new DomainException($"วันนี้ใช้ AI ร่างโพสต์ครบ {limits.DailyDrafts} ชิ้นแล้ว ลองใหม่พรุ่งนี้");

        var tone = Tones.Contains(c.Tone ?? "") ? c.Tone! : "friendly";
        var points = (c.Points ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Take(MaxPoints).ToList();
        var drafts = await writer.DraftAsync(new AiDraftRequest(c.Topic.Trim(), points, tone, count), ct);
        var texts = drafts.Select(d => d.Trim()).Where(d => d.Length > 0).Take(count).ToList();
        if (texts.Count == 0) throw new DomainException("AI ไม่ได้ส่งข้อความกลับมา ลองใหม่อีกครั้ง");
        usage.Add(ws.Id, now, texts.Count);
        return new AiDraftsDto(texts);
    }
}
