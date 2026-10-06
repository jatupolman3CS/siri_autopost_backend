using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.ValueObjects;

/// <summary>Which of the events are sent. The defaults are the design's.</summary>
public class NotifyEventSet
{
    public bool Success { get; set; }
    public bool Fail { get; set; } = true;
    /// <summary>A screenshot of the posting window goes with the success, failure and block messages (Telegram only).</summary>
    public bool Shot { get; set; } = true;
    public bool Round { get; set; } = true;
    public bool StartStop { get; set; } = true;
    public bool Block { get; set; } = true;
    /// <summary>A device that is offline while posts are due for it, and when it is back.</summary>
    public bool Offline { get; set; } = true;
    public bool Quota { get; set; }
    /// <summary>Every job a device takes (one message per post: busy, so off unless asked for).</summary>
    public bool Job { get; set; }

    public bool Has(NotifyEvent ev) => ev switch
    {
        NotifyEvent.Success => Success,
        NotifyEvent.Fail => Fail,
        NotifyEvent.Shot => Shot,
        NotifyEvent.Round => Round,
        NotifyEvent.StartStop => StartStop,
        NotifyEvent.Block => Block,
        NotifyEvent.Offline => Offline,
        NotifyEvent.Quota => Quota,
        NotifyEvent.Job => Job,
        _ => false,
    };

    public NotifyEventSet Clone() => (NotifyEventSet)MemberwiseClone();
}

public class TelegramChannel
{
    public const int MaxTokenLength = 200;
    public const int MaxTargetLength = 100;

    public bool On { get; set; }
    /// <summary>Private: the API never returns it.</summary>
    public string Token { get; set; } = "";
    public string ChatId { get; set; } = "";

    /// <summary>A channel is used only when it is on and both its token and its target are filled.</summary>
    public bool IsReady => On && Token.Length > 0 && ChatId.Length > 0;
}

public class LineChannel
{
    public bool On { get; set; }
    /// <summary>Private: the API never returns it.</summary>
    public string Token { get; set; } = "";
    public string To { get; set; } = "";

    public bool IsReady => On && Token.Length > 0 && To.Length > 0;
}

/// <summary>A group's own rule; Default channel and null events follow the set's rule.</summary>
public class GroupNotifyRule
{
    public NotifyChannel Channel { get; set; } = NotifyChannel.Default;
    public NotifyEventSet? Events { get; set; }
}

/// <summary>A link set's own rule; Default channel and null events follow the workspace. Groups are keyed by link id.</summary>
public class SetNotifyRule
{
    public NotifyChannel Channel { get; set; } = NotifyChannel.Default;
    public NotifyEventSet? Events { get; set; }
    public Dictionary<Guid, GroupNotifyRule> Groups { get; set; } = new();
}

/// <summary>Where a notification goes after the rules are applied.</summary>
public readonly record struct NotifyRoute(bool Telegram, bool Line)
{
    public bool Any => Telegram || Line;
}

/// <summary>
/// The workspace's notification settings (a Pro feature), kept as one jsonb document. Telegram and LINE tokens are
/// private: the API only says whether one is stored.
/// </summary>
public class NotificationSettings
{
    public const int MaxSets = 100;
    public const int MaxGroupRulesPerSet = 200;
    public const int MaxUsersLength = 300;

    public TelegramChannel Telegram { get; set; } = new();
    public LineChannel Line { get; set; } = new();
    /// <summary>Where the workspace's events go unless a set or group says otherwise.</summary>
    public NotifyChannel Channel { get; set; } = NotifyChannel.Tg;
    public NotifyEventSet Events { get; set; } = new();
    public Dictionary<Guid, SetNotifyRule> Sets { get; set; } = new();
    /// <summary>Stored only: nothing reads Telegram or LINE commands yet.</summary>
    public bool CommandsOn { get; set; }
    public string CommandsUsers { get; set; } = "";

    public void Validate()
    {
        Telegram.Token = (Telegram.Token ?? "").Trim();
        Telegram.ChatId = (Telegram.ChatId ?? "").Trim();
        Line.Token = (Line.Token ?? "").Trim();
        Line.To = (Line.To ?? "").Trim();
        CommandsUsers = (CommandsUsers ?? "").Trim();
        if (Telegram.Token.Length > TelegramChannel.MaxTokenLength || Line.Token.Length > TelegramChannel.MaxTokenLength)
            throw new DomainException($"โทเคนยาวเกิน {TelegramChannel.MaxTokenLength} ตัวอักษร");
        if (Telegram.ChatId.Length > TelegramChannel.MaxTargetLength || Line.To.Length > TelegramChannel.MaxTargetLength)
            throw new DomainException($"รหัสแชทหรือผู้รับยาวเกิน {TelegramChannel.MaxTargetLength} ตัวอักษร");
        if (CommandsUsers.Length > MaxUsersLength) throw new DomainException($"รายชื่อผู้ใช้คำสั่งยาวเกิน {MaxUsersLength} ตัวอักษร");
        if (!Enum.IsDefined(Channel)) throw new DomainException("ช่องทางแจ้งเตือนไม่ถูกต้อง");
        if (Sets.Count > MaxSets) throw new DomainException($"ตั้งกฎแจ้งเตือนของชุดลิงก์ได้ไม่เกิน {MaxSets} ชุด");
        foreach (var set in Sets.Values)
        {
            if (!Enum.IsDefined(set.Channel) || set.Groups.Values.Any(g => !Enum.IsDefined(g.Channel)))
                throw new DomainException("ช่องทางแจ้งเตือนไม่ถูกต้อง");
            if (set.Groups.Count > MaxGroupRulesPerSet)
                throw new DomainException($"ตั้งกฎแจ้งเตือนของกลุ่มได้ไม่เกิน {MaxGroupRulesPerSet} กลุ่มต่อชุด");
        }
    }

    /// <summary>
    /// Applies the rules: a group's channel and events, else its set's, else the workspace's. A channel is used only
    /// when it is on and its token and target are filled.
    /// </summary>
    public NotifyRoute Resolve(NotifyEvent ev, Guid? linkSetId, Guid? linkId)
    {
        SetNotifyRule? set = null;
        GroupNotifyRule? group = null;
        if (linkSetId is { } sid && Sets.TryGetValue(sid, out set) && linkId is { } lid) set.Groups.TryGetValue(lid, out group);

        var events = group?.Events ?? set?.Events ?? Events;
        if (!events.Has(ev)) return default;

        var channel = group is { Channel: not NotifyChannel.Default } ? group.Channel
            : set is { Channel: not NotifyChannel.Default } ? set.Channel
            : Channel;
        var tg = channel is NotifyChannel.Tg or NotifyChannel.Both && Telegram.IsReady;
        var line = channel is NotifyChannel.Line or NotifyChannel.Both && Line.IsReady;
        return new NotifyRoute(tg, line);
    }
}
