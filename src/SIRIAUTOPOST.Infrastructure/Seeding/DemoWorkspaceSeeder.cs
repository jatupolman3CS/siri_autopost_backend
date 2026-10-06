using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Domain.Entities;

namespace SIRIAUTOPOST.Infrastructure.Seeding;

// A new workspace starts empty. It used to be filled with the design's sample accounts (Facebook, Instagram, X,
// TikTok, LINE, Threads) and snippets, but those were mock data: no browser posted for them, and Facebook groups and
// pages are the only thing the system posts to for now. A workspace now holds only what the person and their paired
// browsers add. The seam stays (IWorkspaceSeeder) for the callers and the tests that replace it.
public sealed class DemoWorkspaceSeeder : IWorkspaceSeeder
{
    public Task SeedAsync(Workspace ws, CancellationToken ct = default) => Task.CompletedTask;
}
