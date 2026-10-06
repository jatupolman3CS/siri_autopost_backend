using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>Sends nothing. The default until the infrastructure registers the real dispatcher.</summary>
public sealed class NullNotificationDispatcher : INotificationDispatcher
{
    public Task NotifyAsync(NotifyEvent ev, Guid workspaceId, Guid? linkSetId, Guid? linkId, string text, CancellationToken ct = default, byte[]? photo = null) =>
        Task.CompletedTask;
}
