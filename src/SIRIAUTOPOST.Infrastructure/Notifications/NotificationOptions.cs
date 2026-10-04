namespace SIRIAUTOPOST.Infrastructure.Notifications;

/// <summary>The "Notifications" configuration section (env: Notifications__TelegramBaseUrl ...).</summary>
public sealed class NotificationOptions
{
    public const string Section = "Notifications";

    /// <summary>Where Telegram's Bot API lives. Tests point it at a fake.</summary>
    public string TelegramBaseUrl { get; set; } = "https://api.telegram.org";

    /// <summary>Where LINE's Messaging API lives. Tests point it at a fake.</summary>
    public string LineBaseUrl { get; set; } = "https://api.line.me";

    /// <summary>How long one call to Telegram or LINE may take before it counts as failed.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Messages waiting to be sent. When it is full the oldest one is dropped (and logged).</summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>How long a stopping host may spend sending what is still queued before the rest is dropped.</summary>
    public TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delivers each message inside the call that raised it instead of in the background (integration tests, so what
    /// would be sent is known the moment the request returns). Never on in production: a slow Telegram would slow the request.
    /// </summary>
    public bool Inline { get; set; }
}
