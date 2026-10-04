using System.Text.Json;
using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Application.Interfaces.Messaging;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;
using SIRIAUTOPOST.Domain.Interfaces;

namespace SIRIAUTOPOST.Application.Features.Extension;

// The extension's own campaigns (settings, media, run state, log) shown and edited in the web app, and the
// buttons of its settings page (start, stop, test post...) sent to the device as commands. The device side
// syncs every 30 seconds; see the "cloud" section of client/background.js.

// ---------- owner side (web app, JWT) ----------

public sealed record GetExtensionConfigQuery(Guid WorkspaceId, Guid DeviceId) : IQuery<ExtensionConfigDto>;

public sealed class GetExtensionConfigQueryHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IExtensionRepository ext, ICurrentUser current)
    : IQueryHandler<GetExtensionConfigQuery, ExtensionConfigDto>
{
    public async Task<ExtensionConfigDto> HandleAsync(GetExtensionConfigQuery q, CancellationToken ct = default)
    {
        var (ws, role) = await workspaces.RequireRoleAsync(q.WorkspaceId, current, ct);
        var device = await devices.GetAsync(ws.Id, q.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", q.DeviceId);
        // The Telegram bot token is a secret of the workspace admins: everyone else sees it empty.
        return ExtensionConfigs.Dto(device.Id, await ext.GetConfigAsync(device.Id, ct), hideToken: role < WorkspaceRole.Admin);
    }
}

/// <param name="BaseRevision">The revision the page edited; null overwrites (a 409 when it moved on).</param>
public sealed record SaveExtensionConfigCommand(Guid WorkspaceId, Guid DeviceId, JsonElement Settings, int? BaseRevision)
    : ICommand<ConfigSavedDto>;

public sealed class SaveExtensionConfigCommandHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IExtensionRepository ext, IDeviceEventRepository events,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SaveExtensionConfigCommand, ConfigSavedDto>
{
    public async Task<ConfigSavedDto> HandleAsync(SaveExtensionConfigCommand c, CancellationToken ct = default)
    {
        var (ws, role) = await workspaces.RequireRoleAsync(c.WorkspaceId, current, ct);
        if (role < WorkspaceRole.Editor) throw new ForbiddenException("สิทธิ์ของคุณในเวิร์กสเปซนี้ทำรายการนี้ไม่ได้");
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        return await ExtensionConfigs.SaveAsync(
            ext, events, uow, ws.Id, device.Id, c.Settings, c.BaseRevision, false, clock.GetUtcNow(), ct, keepToken: role < WorkspaceRole.Admin);
    }
}

public sealed record GetExtensionImageQuery(Guid WorkspaceId, string ImageId) : IQuery<MediaContent>;

public sealed class GetExtensionImageQueryHandler(IWorkspaceRepository workspaces, IExtensionRepository ext, ICurrentUser current)
    : IQueryHandler<GetExtensionImageQuery, MediaContent>
{
    public async Task<MediaContent> HandleAsync(GetExtensionImageQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var img = await ext.GetImageAsync(ws.Id, q.ImageId, ct) ?? throw new NotFoundException("รูป", q.ImageId);
        return new MediaContent(img.Name, img.ContentType, img.Data);
    }
}

/// <summary>A photo or video added in the web app, before the settings that use it are saved.</summary>
public sealed record PutExtensionImageCommand(Guid WorkspaceId, string ImageId, string Name, string Type, string Data) : ICommand<Unit>;

public sealed class PutExtensionImageCommandHandler(
    IWorkspaceRepository workspaces, IExtensionRepository ext, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<PutExtensionImageCommand, Unit>
{
    public async Task<Unit> HandleAsync(PutExtensionImageCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        await ExtensionConfigs.PutImageAsync(ext, uow, ws.Id, c.ImageId, c.Name, c.Data, clock.GetUtcNow(), ct);
        return Unit.Value;
    }
}

/// <summary>Presence, the last state the extension reported and the newest lines of its log.</summary>
public sealed record GetDeviceLiveQuery(Guid WorkspaceId, Guid DeviceId) : IQuery<DeviceLiveDto>;

public sealed class GetDeviceLiveQueryHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IExtensionRepository ext, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetDeviceLiveQuery, DeviceLiveDto>
{
    public const int Logs = 200;

    public async Task<DeviceLiveDto> HandleAsync(GetDeviceLiveQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var device = await devices.GetAsync(ws.Id, q.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", q.DeviceId);
        var state = await ext.GetStateAsync(device.Id, ct);
        var head = await ext.GetConfigHeadAsync(device.Id, ct);
        var logs = await ext.ListLogsAsync(device.Id, Logs, ct);
        return new DeviceLiveDto(device.Id, device.IsOnline(clock.GetUtcNow()), device.LastSeenAt, device.Version,
            ExtensionSettings.Element(state?.Json), state?.UpdatedAt, head?.Revision ?? 0,
            logs.Select(DeviceLogDto.From).ToList());
    }
}

/// <summary>A button of the extension's settings page, run by the device on its next sync (within ~30 s).</summary>
public sealed record SendDeviceCommandCommand(Guid WorkspaceId, Guid DeviceId, string Cmd, JsonElement? Args) : ICommand<DeviceCommandDto>;

public sealed class SendDeviceCommandCommandHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IExtensionRepository ext, IDeviceEventRepository events,
    IUserRepository users, INotificationDispatcher notifier, ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SendDeviceCommandCommand, DeviceCommandDto>
{
    public async Task<DeviceCommandDto> HandleAsync(SendDeviceCommandCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        // A suspended, banned or paused customer's devices get no jobs, and no button that starts posting either.
        if (DeviceCommand.StartsPosting(c.Cmd) && !(await users.OwnerOfAsync(ws, ct)).CanPost)
            throw new DomainException("บัญชีเจ้าของเวิร์กสเปซถูกระงับหรือหยุดการโพสต์โดยผู้ดูแลแพลตฟอร์ม จึงสั่งให้เครื่องโพสต์ไม่ได้");
        var args = c.Args is { ValueKind: JsonValueKind.Object } a ? a.GetRawText() : "{}";
        var now = clock.GetUtcNow();
        var cmd = DeviceCommand.Create(ws.Id, device.Id, c.Cmd, args, now);
        ext.Add(cmd);
        events.Add(CommandEvents.Of(cmd, now)); // wakes a device waiting in a long sync
        await uow.SaveChangesAsync(ct);
        // Starting and stopping posting from the web is a "start / stop" notification (best effort, never fails the command).
        if (c.Cmd is "start" or "stop")
            await notifier.NotifyAsync(
                NotifyEvent.StartStop, ws.Id, null, null,
                c.Cmd == "start" ? $"สั่งเริ่มโพสต์อัตโนมัติจากเว็บ: เครื่อง {device.Name}" : $"สั่งหยุดโพสต์อัตโนมัติจากเว็บ: เครื่อง {device.Name}", ct);
        return DeviceCommandDto.From(cmd);
    }
}

internal static class CommandEvents
{
    /// <summary>A "device.command" event for the command's current status.</summary>
    public static DeviceEvent Of(DeviceCommand cmd, DateTimeOffset now) =>
        DeviceEvents.Make(cmd.WorkspaceId, cmd.DeviceId, DeviceEventType.Command,
            new { id = cmd.Id, cmd = cmd.Cmd, status = cmd.Status, result = ExtensionSettings.Element(cmd.Result) }, now);
}

public sealed record GetDeviceCommandQuery(Guid WorkspaceId, Guid DeviceId, Guid CommandId) : IQuery<DeviceCommandDto>;

public sealed class GetDeviceCommandQueryHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IExtensionRepository ext, ICurrentUser current, TimeProvider clock)
    : IQueryHandler<GetDeviceCommandQuery, DeviceCommandDto>
{
    public async Task<DeviceCommandDto> HandleAsync(GetDeviceCommandQuery q, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(q.WorkspaceId, current, WorkspaceRole.Viewer, ct);
        var device = await devices.GetAsync(ws.Id, q.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", q.DeviceId);
        var cmd = await ext.GetCommandAsync(device.Id, q.CommandId, ct) ?? throw new NotFoundException("คำสั่ง", q.CommandId);
        var dto = DeviceCommandDto.From(cmd);
        // Not saved: the device's next sync expires it for real; the page only needs to stop waiting.
        return cmd.Stale(clock.GetUtcNow()) ? dto with { Status = CommandStatus.Expired } : dto;
    }
}

/// <summary>Empties the log on the server and asks the device to empty its own.</summary>
public sealed record ClearDeviceLogsCommand(Guid WorkspaceId, Guid DeviceId) : ICommand<Unit>;

public sealed class ClearDeviceLogsCommandHandler(
    IWorkspaceRepository workspaces, IDeviceRepository devices, IExtensionRepository ext, IDeviceEventRepository events,
    ICurrentUser current, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ClearDeviceLogsCommand, Unit>
{
    public async Task<Unit> HandleAsync(ClearDeviceLogsCommand c, CancellationToken ct = default)
    {
        var ws = await workspaces.RequireAsync(c.WorkspaceId, current, WorkspaceRole.Editor, ct);
        var device = await devices.GetAsync(ws.Id, c.DeviceId, ct) ?? throw new NotFoundException("อุปกรณ์", c.DeviceId);
        var now = clock.GetUtcNow();
        await ext.ClearLogsAsync(device.Id, ct);
        var cmd = DeviceCommand.Create(ws.Id, device.Id, "clearLogs", "{}", now);
        ext.Add(cmd);
        events.Add(DeviceEvents.Make(ws.Id, device.Id, DeviceEventType.LogCleared, new { }, now));
        events.Add(CommandEvents.Of(cmd, now));
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

// ---------- device side (extension, X-Device-Key) ----------

/// <param name="T">Unix milliseconds.</param>
public sealed record DeviceLogEntry(long T, string? Level, string? Msg);

/// <summary>
/// Every 30 seconds: the device reports its state (when it changed) and new log lines, and learns the
/// server's settings revision and the commands waiting for it. With <paramref name="Wait"/> and no command
/// waiting, the call stays open (up to <see cref="DeviceSyncCommandHandler.MaxWait"/>) until the web app sends
/// one, so a button on the web reaches the extension at once instead of on its next 30-second sync.
/// </summary>
public sealed record DeviceSyncCommand(string? Version, JsonElement? State, IReadOnlyList<DeviceLogEntry>? Logs, bool TakeCommands, bool Wait = false)
    : ICommand<DeviceSyncDto>;

public sealed class DeviceSyncCommandHandler(
    ICurrentDevice current, IDeviceRepository devices, IExtensionRepository ext, IDeviceEventRepository events,
    IDeviceEventBus bus, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<DeviceSyncCommand, DeviceSyncDto>
{
    /// <summary>Lines taken from one sync (the extension keeps 400).</summary>
    public const int MaxLogsPerSync = 400;
    /// <summary>A waiting sync answers by then even when nothing happened (under proxy and browser timeouts).</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);

    public async Task<DeviceSyncDto> HandleAsync(DeviceSyncCommand c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var device = await devices.GetByIdAsync(current.DeviceId, ct) ?? throw new AuthenticationException("อุปกรณ์นี้ถูกยกเลิกการผูกแล้ว");
        if (device.Seen(c.Version, now))
            events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Online, new { version = device.Version }, now));

        if (c.State is { ValueKind: JsonValueKind.Object } st)
        {
            var state = await ext.GetStateAsync(device.Id, ct);
            if (state is null) ext.Add(state = DeviceState.Create(device.Id));
            var json = st.GetRawText();
            state.Set(json, now);
            // A very big state (hundreds of groups) would not fit an event: the event only says it changed and the
            // client fetches it, instead of the sync failing for good with the same state sent again.
            events.Add(DeviceEvents.MakeRaw(device.WorkspaceId, device.Id, DeviceEventType.State,
                json.Length <= DeviceEvents.MaxStateChars
                    ? $"{{\"state\":{json},\"at\":{JsonSerializer.Serialize(now)}}}"
                    : $"{{\"truncated\":true,\"at\":{JsonSerializer.Serialize(now)}}}", now));
        }

        var newLogs = false;
        if (c.Logs is { Count: > 0 } logs)
        {
            var last = await ext.LastLogTAsync(device.Id, ct);
            var fresh = logs.Where(l => l.T > last).OrderBy(l => l.T).TakeLast(MaxLogsPerSync)
                .Select(l => DeviceLog.Create(device.Id, l.T, l.Level, l.Msg)).ToList();
            ext.AddRange(fresh);
            newLogs = fresh.Count > 0;
            if (newLogs)
            {
                var truncated = fresh.Count > DeviceEvents.MaxLogLines;
                var lines = truncated ? [] : fresh.Select(DeviceLogDto.From).ToList();
                events.Add(DeviceEvents.Make(device.WorkspaceId, device.Id, DeviceEventType.Log, new { lines, truncated }, now));
            }
        }

        // A held call (wait) only waits for new commands; the ones handed out earlier without a result come again
        // with the next plain sync (the 30-second alarm, an edit, a button), so a held call never spins on them.
        var commands = await TakeAsync(c.TakeCommands, again: !c.Wait, now, ct);
        var head = await ext.GetConfigHeadAsync(device.Id, ct);
        await uow.SaveChangesAsync(ct);
        if (newLogs) await ext.PruneLogsAsync(device.Id, DeviceLog.KeepPerDevice, ct);
        // Events grow with every state change and log line: keep the newest ones after any sync that wrote some.
        if (newLogs || c.State is not null) await events.PruneAsync(device.Id, DeviceEvent.KeepPerDevice, ct);

        // Nothing to run: hold the call until the web app sends a command (or the wait is over), then take it.
        if (c.Wait && c.TakeCommands && commands.Count == 0
            && await bus.WaitForAsync(device.Id, DeviceEventType.Command, MaxWait, ct))
        {
            commands = await TakeAsync(true, again: false, clock.GetUtcNow(), ct);
            await uow.SaveChangesAsync(ct);
        }
        return new DeviceSyncDto(head?.Revision ?? 0, head?.HasContent ?? false, commands);
    }

    /// <summary>
    /// Expires stale commands and, when asked, returns the open ones: new commands are marked sent, and with
    /// <paramref name="again"/> the ones handed out earlier without a result come again (the call that carried
    /// them may have been cut short; the device runs each id once and re-reports a result it already has).
    /// </summary>
    private async Task<List<DeviceCommandItemDto>> TakeAsync(bool take, bool again, DateTimeOffset now, CancellationToken ct)
    {
        var commands = new List<DeviceCommandItemDto>();
        foreach (var cmd in await ext.ListOpenCommandsAsync(current.DeviceId, ct))
        {
            if (cmd.Stale(now))
            {
                cmd.Expire();
                events.Add(CommandEvents.Of(cmd, now));
                continue;
            }
            if (!take) continue;
            if (cmd.Send(now)) events.Add(CommandEvents.Of(cmd, now));
            else if (!again) continue; // handed out before: only a plain sync gives it again
            using var args = JsonDocument.Parse(cmd.Args);
            commands.Add(new DeviceCommandItemDto(cmd.Id, cmd.Cmd, args.RootElement.Clone()));
        }
        return commands;
    }
}

public sealed record GetOwnExtensionConfigQuery : IQuery<ExtensionConfigDto>;

public sealed class GetOwnExtensionConfigQueryHandler(ICurrentDevice current, IExtensionRepository ext)
    : IQueryHandler<GetOwnExtensionConfigQuery, ExtensionConfigDto>
{
    public async Task<ExtensionConfigDto> HandleAsync(GetOwnExtensionConfigQuery q, CancellationToken ct = default) =>
        ExtensionConfigs.Dto(current.DeviceId, await ext.GetConfigAsync(current.DeviceId, ct));
}

/// <summary>The device uploads its settings (its own edits, or everything on the first sync).</summary>
public sealed record SaveOwnExtensionConfigCommand(JsonElement Settings, int? BaseRevision) : ICommand<ConfigSavedDto>;

public sealed class SaveOwnExtensionConfigCommandHandler(
    ICurrentDevice current, IExtensionRepository ext, IDeviceEventRepository events, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<SaveOwnExtensionConfigCommand, ConfigSavedDto>
{
    public Task<ConfigSavedDto> HandleAsync(SaveOwnExtensionConfigCommand c, CancellationToken ct = default) =>
        ExtensionConfigs.SaveAsync(ext, events, uow, current.WorkspaceId, current.DeviceId, c.Settings, c.BaseRevision, true, clock.GetUtcNow(), ct);
}

/// <summary>The ids the server does not have yet, so the device uploads only those.</summary>
public sealed record GetMissingImagesQuery(IReadOnlyList<string> Ids) : IQuery<MissingImagesDto>;

public sealed class GetMissingImagesQueryHandler(ICurrentDevice current, IExtensionRepository ext)
    : IQueryHandler<GetMissingImagesQuery, MissingImagesDto>
{
    public async Task<MissingImagesDto> HandleAsync(GetMissingImagesQuery q, CancellationToken ct = default)
    {
        var ids = (q.Ids ?? []).Where(ExtensionImage.ValidId).Distinct().ToList();
        var have = await ext.ExistingImageIdsAsync(current.WorkspaceId, ids, ct);
        return new MissingImagesDto(ids.Where(id => !have.Contains(id)).ToList());
    }
}

public sealed record GetOwnExtensionImageQuery(string ImageId) : IQuery<ExtensionImageDto>;

public sealed class GetOwnExtensionImageQueryHandler(ICurrentDevice current, IExtensionRepository ext)
    : IQueryHandler<GetOwnExtensionImageQuery, ExtensionImageDto>
{
    public async Task<ExtensionImageDto> HandleAsync(GetOwnExtensionImageQuery q, CancellationToken ct = default)
    {
        var img = await ext.GetImageAsync(current.WorkspaceId, q.ImageId, ct) ?? throw new NotFoundException("รูป", q.ImageId);
        return new ExtensionImageDto(img.Name, img.ContentType, ExtensionSettings.ToDataUrl(img.ContentType, img.Data));
    }
}

public sealed record PutOwnExtensionImageCommand(string ImageId, string Name, string Type, string Data) : ICommand<Unit>;

public sealed class PutOwnExtensionImageCommandHandler(
    ICurrentDevice current, IExtensionRepository ext, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<PutOwnExtensionImageCommand, Unit>
{
    public async Task<Unit> HandleAsync(PutOwnExtensionImageCommand c, CancellationToken ct = default)
    {
        await ExtensionConfigs.PutImageAsync(ext, uow, current.WorkspaceId, c.ImageId, c.Name, c.Data, clock.GetUtcNow(), ct);
        return Unit.Value;
    }
}

public sealed record ReportCommandResultCommand(Guid CommandId, JsonElement? Result) : ICommand<Unit>;

public sealed class ReportCommandResultCommandHandler(
    ICurrentDevice current, IExtensionRepository ext, IDeviceEventRepository events, IUnitOfWork uow, TimeProvider clock)
    : ICommandHandler<ReportCommandResultCommand, Unit>
{
    public async Task<Unit> HandleAsync(ReportCommandResultCommand c, CancellationToken ct = default)
    {
        var cmd = await ext.GetCommandAsync(current.DeviceId, c.CommandId, ct) ?? throw new NotFoundException("คำสั่ง", c.CommandId);
        var result = c.Result is { ValueKind: JsonValueKind.Object } r ? r.GetRawText() : "{\"ok\":true}";
        var now = clock.GetUtcNow();
        cmd.Complete(result, now);
        events.Add(CommandEvents.Of(cmd, now)); // the web page waiting for this answer gets it now
        await uow.SaveChangesAsync(ct);
        return Unit.Value;
    }
}

internal static class ExtensionConfigs
{
    /// <summary>Unused images stay this long: the web app uploads images before the settings that use them.</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(1);

    /// <param name="hideToken">Blank out the Telegram bot token (a secret of the workspace admins).</param>
    public static ExtensionConfigDto Dto(Guid deviceId, ExtensionConfig? c, bool hideToken = false)
    {
        var settings = c?.Settings;
        if (hideToken && !string.IsNullOrEmpty(settings)) settings = ExtensionSettings.WithBotToken(settings, "");
        return new(deviceId, c?.Revision ?? 0, ExtensionSettings.Element(settings), c?.UpdatedAt, c?.UpdatedByDevice ?? false,
            c?.HasContent ?? false);
    }

    /// <param name="keepToken">The caller may not change the Telegram bot token: the stored one stays whatever the settings say.</param>
    public static async Task<ConfigSavedDto> SaveAsync(
        IExtensionRepository ext, IDeviceEventRepository events, IUnitOfWork uow, Guid workspaceId, Guid deviceId, JsonElement settings,
        int? baseRevision, bool byDevice, DateTimeOffset now, CancellationToken ct, bool keepToken = false)
    {
        if (!ExtensionSettings.IsValid(settings)) throw new DomainException("รูปแบบการตั้งค่าไม่ถูกต้อง (ต้องมี campaigns)");
        var config = await ext.GetConfigAsync(deviceId, ct);
        if (config is null) ext.Add(config = ExtensionConfig.Create(workspaceId, deviceId));
        var json = settings.GetRawText();
        if (keepToken) json = ExtensionSettings.WithBotToken(json, ExtensionSettings.BotToken(config.Settings));
        var changed = config.Save(json, ExtensionSettings.HasContent(json), baseRevision, byDevice, now);
        if (changed)
            events.Add(DeviceEvents.Make(workspaceId, deviceId, DeviceEventType.Config, new { revision = config.Revision, byDevice }, now));
        await uow.SaveChangesAsync(ct);
        if (changed) await CleanupImagesAsync(ext, workspaceId, now, ct);
        return new ConfigSavedDto(config.Revision, config.UpdatedAt);
    }

    /// <summary>Deletes images no device's settings use any more (older than the grace period).</summary>
    public static async Task CleanupImagesAsync(IExtensionRepository ext, Guid workspaceId, DateTimeOffset now, CancellationToken ct)
    {
        var keep = new HashSet<string>();
        foreach (var s in await ext.ListSettingsAsync(workspaceId, ct)) keep.UnionWith(ExtensionSettings.ImageIds(s));
        await ext.DeleteImagesExceptAsync(workspaceId, keep, now - OrphanGrace, ct);
    }

    /// <summary>Stores a media file once: an id never changes content, so an existing one is kept as it is.</summary>
    public static async Task PutImageAsync(
        IExtensionRepository ext, IUnitOfWork uow, Guid workspaceId, string imageId, string name, string data,
        DateTimeOffset now, CancellationToken ct)
    {
        var parsed = ExtensionSettings.ParseDataUrl(data) ?? throw new DomainException("ไฟล์ต้องเป็นรูปภาพหรือวิดีโอ (data URL)");
        if (await ext.GetImageAsync(workspaceId, imageId, ct) is not null) return;
        ext.Add(ExtensionImage.Create(workspaceId, imageId, name, parsed.Type, parsed.Bytes, now));
        await uow.SaveChangesAsync(ct);
    }
}
