using SIRIAUTOPOST.Application.Common;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.Exceptions;

namespace SIRIAUTOPOST.Application.Tests;

// One link set, one extension; one group, one extension (2026-10-10).
public class LinkOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.NewGuid();

    private static SocialAccount Extension(string name, int order)
    {
        var account = SocialAccount.Connect(Ws, Device.Pair(Ws, name, "Chrome", "2.4", "h" + order, Now), order);
        return account;
    }

    private static SetLink Link(LinkSet set, string url, DateTimeOffset? at = null) =>
        SetLink.Create(Ws, set.Id, null, url, null, 0, at ?? Now);

    [Fact]
    public void A_set_belongs_to_the_extension_it_names_the_only_connected_one_or_nobody()
    {
        var one = Extension("Shop PC", 0);
        var two = Extension("Office PC", 1);
        var unnamed = LinkSet.Create(Ws, "a", null, Now);

        Assert.Equal(one.Id, LinkOwnership.OwnerOf(unnamed, [one]));
        Assert.Null(LinkOwnership.OwnerOf(unnamed, [one, two])); // several extensions and no choice: nobody, never "the first"
        Assert.Equal(two.Id, LinkOwnership.OwnerOf(LinkSet.Create(Ws, "b", two.Id, Now), [one, two]));

        // A named extension whose browser was unbound stays the owner (the set waits for it).
        two.Disconnect();
        Assert.Equal(two.Id, LinkOwnership.OwnerOf(LinkSet.Create(Ws, "c", two.Id, Now), [one, two]));
    }

    [Fact]
    public void A_group_in_the_sets_of_two_extensions_is_refused_naming_the_set_and_the_extension()
    {
        var shop = Extension("Shop PC", 0);
        var office = Extension("Office PC", 1);
        var shopSet = LinkSet.Create(Ws, "ชุดร้าน", shop.Id, Now);
        var officeSet = LinkSet.Create(Ws, "ชุดออฟฟิศ", office.Id, Now);
        var sameShop = LinkSet.Create(Ws, "ชุดร้าน 2", shop.Id, Now);
        var plants = Link(shopSet, "https://www.facebook.com/groups/plants");
        var map = OwnershipMap.Build([shopSet, officeSet, sameShop], [plants], [shop, office]);

        // Another set of the SAME extension may hold it too (same machine: nothing can mix)...
        map.EnsureFree("https://www.facebook.com/groups/plants", shop.Id, sameShop.Id, [shop, office]);
        // ...the other extension may not, with the address compared without regard to case.
        var refused = Assert.Throws<DomainException>(() =>
            map.EnsureFree("https://www.facebook.com/groups/PLANTS", office.Id, officeSet.Id, [shop, office]));
        Assert.Contains("ชุดร้าน", refused.Message);
        Assert.Contains("Shop PC", refused.Message);
        // A set nobody posts yet is not compared (the extension chosen later is).
        map.EnsureFree("https://www.facebook.com/groups/plants", null, officeSet.Id, [shop, office]);
        // A group nobody holds is free.
        map.EnsureFree("https://www.facebook.com/groups/other", office.Id, officeSet.Id, [shop, office]);
    }

    [Fact]
    public void Switched_off_links_and_sets_nobody_posts_hold_nothing()
    {
        var shop = Extension("Shop PC", 0);
        var office = Extension("Office PC", 1);
        var shopSet = LinkSet.Create(Ws, "ชุดร้าน", shop.Id, Now);
        var orphan = LinkSet.Create(Ws, "ไม่มีเครื่อง", null, Now);
        var off = Link(shopSet, "https://www.facebook.com/groups/off");
        off.DisableManually();
        var held = Link(orphan, "https://www.facebook.com/groups/orphan");
        var map = OwnershipMap.Build([shopSet, orphan], [off, held, Link(shopSet, "not a group")], [shop, office]);

        Assert.Null(map.WinnerOf("https://www.facebook.com/groups/off"));
        Assert.Null(map.WinnerOf("https://www.facebook.com/groups/orphan"));
        Assert.Null(map.ConflictFor("https://www.facebook.com/groups/off", office.Id, Guid.NewGuid()));
    }

    [Fact]
    public void A_group_that_two_extensions_already_hold_is_kept_by_the_one_listed_first()
    {
        var shop = Extension("Shop PC", 0);
        var office = Extension("Office PC", 1);
        var shopSet = LinkSet.Create(Ws, "ชุดร้าน", shop.Id, Now);
        var officeSet = LinkSet.Create(Ws, "ชุดออฟฟิศ", office.Id, Now);
        const string url = "https://www.facebook.com/groups/plants";
        var older = Link(officeSet, url, Now.AddDays(-3));
        var newer = Link(shopSet, url, Now);

        // The accounts' order decides, not who typed it first: the web app can show this without asking the server.
        var map = OwnershipMap.Build([shopSet, officeSet], [newer, older], [shop, office]);
        var winner = map.WinnerOf(url)!;
        Assert.Equal(shop.Id, winner.Owner);
        Assert.Equal(shopSet.Id, winner.Set.Id);

        // The same data with the extensions listed the other way round: the other one keeps it.
        Assert.Equal(office.Id, OwnershipMap.Build([shopSet, officeSet], [newer, older], [office, shop]).WinnerOf(url)!.Owner);
    }

    [Fact]
    public void Sets_that_name_nobody_are_given_to_the_extension_that_was_posting_them()
    {
        var shop = Extension("Shop PC", 0);
        var unnamed = LinkSet.Create(Ws, "a", null, Now);
        var named = LinkSet.Create(Ws, "b", Guid.NewGuid(), Now);
        unnamed.Update("a", null, [], randomOrder: false, dailyPerGroup: 2);

        var pinned = LinkOwnership.PinUnassigned([unnamed, named], shop.Id);

        Assert.Equal([unnamed], pinned);
        Assert.Equal(shop.Id, unnamed.PostAsAccountId);
        Assert.Equal((false, 2), (unnamed.RandomOrder, unnamed.DailyPerGroup)); // the rest of the set is kept
        Assert.NotEqual(shop.Id, named.PostAsAccountId);
        Assert.Equal(Platform.Fb, shop.Platform);
    }
}
