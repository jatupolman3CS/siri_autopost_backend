using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Domain.Tests;

public class UserAndMediaTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Email_is_normalized_and_name_defaults_to_its_local_part()
    {
        var u = User.Create("  Nattaya@BaanDee.co ", "", UserRole.User, PlanKey.Free, Now);
        Assert.Equal("nattaya@baandee.co", u.Email);
        Assert.Equal("nattaya", u.Name);
        Assert.False(u.HasAdvancedAntiBan);
        u.ChangePlan(PlanKey.Pro);
        Assert.True(u.HasAdvancedAntiBan);
    }

    [Fact]
    public void Rejects_an_invalid_email() =>
        Assert.Throws<DomainException>(() => User.Create("no-at-sign", "", UserRole.User, PlanKey.Free, Now));

    [Theory]
    [InlineData("image/jpeg", MediaKind.Image)]
    [InlineData("video/mp4", MediaKind.Video)]
    public void Media_kind_follows_the_content_type(string type, MediaKind kind) =>
        Assert.Equal(kind, MediaFile.Create(Guid.NewGuid(), "poster.jpg", type, [1, 2, 3], Now).Kind);

    [Fact]
    public void Media_must_be_a_non_empty_image_or_video()
    {
        Assert.Throws<DomainException>(() => MediaFile.Create(Guid.NewGuid(), "a.pdf", "application/pdf", [1], Now));
        Assert.Throws<DomainException>(() => MediaFile.Create(Guid.NewGuid(), "a.jpg", "image/jpeg", [], Now));
    }
}
