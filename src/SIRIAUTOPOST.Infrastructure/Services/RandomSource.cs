using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Infrastructure.Services;

public sealed class RandomSource : IRandomSource
{
    public double NextDouble() => Random.Shared.NextDouble();
}
