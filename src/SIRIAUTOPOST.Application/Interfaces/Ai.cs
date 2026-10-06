namespace SIRIAUTOPOST.Application.Interfaces;

/// <summary>What the writer is asked for: a topic, optional selling points, a tone and how many different drafts.</summary>
/// <param name="Points">Things the post must mention (price, promotion, contact...), one per entry.</param>
/// <param name="Tone">friendly, formal, sales or short; anything else is treated as friendly.</param>
public sealed record AiDraftRequest(string Topic, IReadOnlyList<string> Points, string Tone, int Count);

/// <summary>
/// Writes post drafts with a language model. The key is the platform's (server configuration, never sent to the
/// browser). <see cref="Enabled"/> is false while no key is set: the web app then disables the AI buttons.
/// </summary>
public interface IAiWriter
{
    bool Enabled { get; }

    /// <summary>The model that answers, for the status line of the web app (empty while disabled).</summary>
    string Model { get; }

    /// <summary>
    /// One text per requested draft, each a complete Facebook group post in Thai that may use {a|b} spintax and the
    /// {{code}} tag. Throws <see cref="Domain.Exceptions.DomainException"/> with a Thai reason when the provider fails.
    /// </summary>
    Task<IReadOnlyList<string>> DraftAsync(AiDraftRequest request, CancellationToken ct = default);
}
