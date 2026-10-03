using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.Interfaces;
using SIRIAUTOPOST.Infrastructure.Data;

namespace SIRIAUTOPOST.Infrastructure.Diagnostics;

public sealed class DatabaseProbe(AppDbContext db) : IDatabaseProbe
{
    public async Task<TimeSpan> PingAsync(CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
        return watch.Elapsed;
    }
}
