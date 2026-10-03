using SIRIAUTOPOST.Application.Interfaces;

namespace SIRIAUTOPOST.Application.Tests;

internal sealed class FixedRandom(double value) : IRandomSource
{
    public double NextDouble() => value;
}

internal sealed class FakeUser(Guid id) : ICurrentUser
{
    public Guid UserId => id;
    public Guid? ImpersonatorId => null;
}
