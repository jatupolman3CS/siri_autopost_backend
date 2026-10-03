using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Infrastructure.Seeding;

/// <summary>The default: a new workspace has no accounts, posts or snippets until the customer adds them.</summary>
public sealed class NoWorkspaceSeeder : IWorkspaceSeeder
{
    public Task SeedAsync(Workspace ws, CancellationToken ct = default) => Task.CompletedTask;
}
