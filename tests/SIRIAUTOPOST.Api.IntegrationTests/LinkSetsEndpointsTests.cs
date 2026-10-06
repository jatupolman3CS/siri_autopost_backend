using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class LinkSetsEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = WorkflowTestSupport.Json;
    private const string Plants = "https://www.facebook.com/groups/plants.lovers";

    private static string Sets(Guid ws) => $"/api/workspaces/{ws}/link-sets";

    [Fact]
    public async Task A_new_workspace_has_no_link_sets_and_a_set_starts_empty()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        Assert.Empty(await client.LinkSetsAsync(ws));

        var set = await client.CreateLinkSetAsync(ws, "  กลุ่มขายของ ");

        Assert.Equal("กลุ่มขายของ", set.Name);
        Assert.Null(set.PostAsAccountId);
        Assert.Empty(set.AccountIds);
        Assert.Empty(set.Links);
        Assert.Equal(0, set.ScheduleCount);
        Assert.Single(await client.LinkSetsAsync(ws));
    }

    [Fact]
    public async Task Links_are_added_normalised_and_named_from_their_address()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);

        var link = await client.AddLinkAsync(ws, set.Id, "m.facebook.com/groups/plants.lovers/?ref=share", " ราคาพิเศษ ", dailyMax: 3);

        Assert.Equal(Plants, link.Url);
        Assert.Equal("plants lovers", link.Name);
        Assert.Equal(("ราคาพิเศษ", 3, true), (link.Code, link.DailyMax, link.Enabled));
        Assert.Equal((LinkHealth.Ok, 0, true, false), (link.Health, link.FailStreak, link.Valid, link.Duplicate));

        var named = await client.AddLinkAsync(ws, set.Id, "fb.com/groups/other", name: "ตลาดนัด");
        Assert.Equal(("ตลาดนัด", "https://www.facebook.com/groups/other"), (named.Name, named.Url));
        Assert.Equal([link.Id, named.Id], (await client.LinkSetsAsync(ws)).Single().Links.Select(l => l.Id)); // in their order
    }

    [Fact]
    public async Task A_blank_or_invalid_address_makes_a_row_that_is_shown_red_and_never_used()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);

        var blank = await client.AddLinkAsync(ws, set.Id, null);
        var bad = await client.AddLinkAsync(ws, set.Id, "https://example.com/not-a-group");

        Assert.False(blank.Valid);
        Assert.False(bad.Valid);
        Assert.Equal("https://example.com/not-a-group", bad.Url);
        Assert.Equal(2, (await client.LinkSetsAsync(ws)).Single().Links.Count(l => !l.Valid));

        // Fixing the row makes it valid.
        var fixedUp = await (await client.PutAsJsonAsync($"{Sets(ws)}/{set.Id}/links/{blank.Id}",
            new { name = "", url = Plants, code = "", dailyMax = 0, enabled = true }, Json)).ReadAsync<SetLinkDto>();
        Assert.True(fixedUp.Valid);
        Assert.Equal("plants lovers", fixedUp.Name);
    }

    [Fact]
    public async Task The_same_group_twice_is_flagged_on_the_later_link()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);

        var first = await client.AddLinkAsync(ws, set.Id, Plants);
        var second = await client.AddLinkAsync(ws, set.Id, "facebook.com/groups/plants.lovers");

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        var links = (await client.LinkSetsAsync(ws)).Single().Links;
        Assert.Equal([false, true], links.Select(l => l.Duplicate));
    }

    [Fact]
    public async Task Addresses_that_differ_only_in_case_are_one_group_whichever_way_they_come_in()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var (device, pair) = await factory.PairDeviceAsync(client, ws);
        (await device.PutAsJsonAsync("/api/device/groups", new { groups = new[] { new { name = "ตลาด", url = "https://www.facebook.com/groups/MARKET" } } })).EnsureSuccessStatusCode();
        var set = await client.CreateLinkSetAsync(ws);

        // One at a time: the later one is flagged, and both keep the case they were typed in.
        var first = await client.AddLinkAsync(ws, set.Id, "facebook.com/groups/ABC", "A");
        var second = await client.AddLinkAsync(ws, set.Id, "facebook.com/groups/abc", "B");
        Assert.Equal(("https://www.facebook.com/groups/ABC", false), (first.Url, first.Duplicate));
        Assert.Equal(("https://www.facebook.com/groups/abc", true), (second.Url, second.Duplicate));
        Assert.Equal([false, true], (await client.LinkSetsAsync(ws)).Single().Links.Select(l => l.Duplicate));

        // Editing a link into a copy of another one in another case flags it too.
        var third = await client.AddLinkAsync(ws, set.Id, "facebook.com/groups/third");
        var edited = await (await client.PutAsJsonAsync($"{Sets(ws)}/{set.Id}/links/{third.Id}",
            new { name = "สาม", url = "facebook.com/groups/aBc", code = "", dailyMax = 0, enabled = true }, Json)).ReadAsync<SetLinkDto>();
        Assert.True(edited.Duplicate);

        // Pasted: a known address in another case is a duplicate that recodes it; so is a repeat inside the paste.
        var bulk = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/bulk",
            new { text = "facebook.com/groups/aBC | NEW\nfacebook.com/groups/Zed | Z1\nfacebook.com/groups/zED | Z2" }, Json)).ReadAsync<BulkLinksResultDto>();
        Assert.Equal((1, 2, 2, 0), (bulk.Added, bulk.Duplicates, bulk.Recoded, bulk.Invalid));
        Assert.Equal("NEW", bulk.Set.Links[0].Code); // the first of the two spellings of abc
        Assert.Equal(("https://www.facebook.com/groups/Zed", "Z2"), (bulk.Set.Links[^1].Url, bulk.Set.Links[^1].Code));

        // CSV rows are skipped the same way, in the set and inside the file.
        var csv = await (await client.PostAsJsonAsync($"{Sets(ws)}/import-csv", new
        {
            rows = new[]
            {
                new { set = "ชุดใหม่", name = "", url = "facebook.com/groups/Row", code = "" },
                new { set = "ชุดใหม่", name = "", url = "facebook.com/groups/ROW", code = "" },
            },
        }, Json)).ReadAsync<CsvImportResultDto>();
        Assert.Equal((1, 1, 0), (csv.Links, csv.Sets, csv.Invalid));

        // Importing the browser's groups: a group the set has in another case is not added again, and one it lacks is found in any case.
        var inOtherCase = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/import", new { accountId = pair.AccountId, urls = new[] { "https://www.facebook.com/groups/market" } }, Json))
            .ReadAsync<LinkSetDto>();
        Assert.Equal("https://www.facebook.com/groups/market", inOtherCase.Links[^1].Url); // synced as MARKET, picked as market: found
        var again = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/import", new { accountId = pair.AccountId, urls = new[] { "https://www.facebook.com/groups/MARKET" } }, Json))
            .ReadAsync<LinkSetDto>();
        Assert.Equal(inOtherCase.Links.Count, again.Links.Count); // already there, in the other case
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("-")]
    [InlineData("_.-")]
    public async Task A_slug_with_no_letter_or_digit_is_not_a_group(string slug)
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);

        var link = await client.AddLinkAsync(ws, set.Id, $"https://www.facebook.com/groups/{slug}");

        Assert.False(link.Valid);
        Assert.Equal($"https://www.facebook.com/groups/{slug}", link.Url); // kept as typed, shown red, never used
        var bulk = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/bulk", new { text = $"facebook.com/groups/{slug} | X" }, Json)).ReadAsync<BulkLinksResultDto>();
        Assert.Equal((0, 1), (bulk.Added, bulk.Invalid));
        var csv = await (await client.PostAsJsonAsync($"{Sets(ws)}/import-csv", new { rows = new[] { new { set = "ใหม่", name = "", url = $"facebook.com/groups/{slug}", code = "" } } }, Json))
            .ReadAsync<CsvImportResultDto>();
        Assert.Equal((0, 1), (csv.Links, csv.Invalid));
    }

    [Fact]
    public async Task A_link_is_edited_switched_off_and_enabled_again()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);
        var link = await client.AddLinkAsync(ws, set.Id, Plants);
        var url = $"{Sets(ws)}/{set.Id}/links/{link.Id}";

        var off = await (await client.PutAsJsonAsync(url, new { name = "ใหม่", url = "fb.com/groups/new", code = "A1", dailyMax = 5, enabled = false }, Json))
            .ReadAsync<SetLinkDto>();
        Assert.Equal(("ใหม่", "https://www.facebook.com/groups/new", "A1", 5, false, LinkHealth.Off), (off.Name, off.Url, off.Code, off.DailyMax, off.Enabled, off.Health));

        var on = await (await client.PostAsync($"{url}/enable", null)).ReadAsync<SetLinkDto>();
        Assert.Equal((true, LinkHealth.Ok, 0), (on.Enabled, on.Health, on.FailStreak));

        // An engine switch-off (failures in a row) is undone the same way.
        await factory.WithDbAsync(async db =>
        {
            var row = await db.SetLinks.SingleAsync(l => l.Id == link.Id);
            row.RecordFailure();
            row.AutoDisable(3);
            await db.SaveChangesAsync();
        });
        var autoOff = (await client.LinkSetsAsync(ws)).Single().Links.Single();
        Assert.Equal((false, LinkHealth.Off, 3), (autoOff.Enabled, autoOff.Health, autoOff.FailStreak));
        var again = await (await client.PostAsync($"{url}/enable", null)).ReadAsync<SetLinkDto>();
        Assert.Equal((true, LinkHealth.Ok, 0), (again.Enabled, again.Health, again.FailStreak));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(url)).StatusCode);
        Assert.Empty((await client.LinkSetsAsync(ws)).Single().Links);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{url}/enable", null)).StatusCode);
    }

    [Fact]
    public async Task A_link_is_only_reachable_through_its_own_set()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var a = await client.CreateLinkSetAsync(ws, "A");
        var b = await client.CreateLinkSetAsync(ws, "B");
        var link = await client.AddLinkAsync(ws, a.Id, Plants);

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Sets(ws)}/{b.Id}/links/{link.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"{Sets(ws)}/{b.Id}/links/{link.Id}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Sets(ws)}/{b.Id}/links/{link.Id}", new { name = "x", url = Plants, code = "", dailyMax = 0, enabled = true }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Sets(ws)}/{Guid.NewGuid()}/links", new { url = Plants }, Json)).StatusCode);
    }

    [Fact]
    public async Task Bad_input_is_reported_field_by_field()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);
        var link = await client.AddLinkAsync(ws, set.Id, Plants);

        var noName = await client.PostAsJsonAsync(Sets(ws), new { name = "" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Contains("name", (await noName.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Sets(ws), new { name = new string('x', 121) }, Json)).StatusCode);

        string Links = $"{Sets(ws)}/{set.Id}/links";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Links, new { url = Plants, dailyMax = 51 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Links, new { url = Plants, dailyMax = -1 }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Links, new { url = Plants, code = new string('x', 101) }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Links, new { url = Plants, name = new string('x', 201) }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Links, new { url = "https://www.facebook.com/groups/" + new string('x', 300) }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync($"{Links}/{link.Id}", new { name = "x", url = Plants, code = "", dailyMax = 99, enabled = true }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Links}/bulk", new { text = "  " }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Links}/import", new { accountId = Guid.NewGuid(), urls = Array.Empty<string>() }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"{Sets(ws)}/import-csv", new { rows = Array.Empty<object>() }, Json)).StatusCode);
    }

    [Fact]
    public async Task Pasted_lines_are_added_counted_and_recoded()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);
        await client.AddLinkAsync(ws, set.Id, "facebook.com/groups/old", "OLD");
        await client.AddLinkAsync(ws, set.Id, "facebook.com/groups/same", "KEEP");

        var text = string.Join("\r\n",
            "https://facebook.com/groups/aaa | CODE1",
            "m.facebook.com/groups/bbb",
            "",
            "not a link | X",
            "  fb.com/groups/old  |  NEW  ",  // known address, another code: recoded
            "facebook.com/groups/same | KEEP", // known address, same code: only a duplicate
            "facebook.com/groups/same",        // no code: only a duplicate
            "https://www.facebook.com/groups/aaa | CODE2", // repeated inside the paste: a duplicate that recodes the first one
            "facebook.com/groups/Ccc.dd_ee");
        var result = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/bulk", new { text }, Json)).ReadAsync<BulkLinksResultDto>();

        Assert.Equal((3, 4, 2, 1), (result.Added, result.Duplicates, result.Recoded, result.Invalid));
        var links = result.Set.Links;
        Assert.Equal(5, links.Count);
        Assert.Equal("NEW", links.Single(l => l.Url.EndsWith("/old")).Code);
        Assert.Equal("KEEP", links.Single(l => l.Url.EndsWith("/same")).Code);
        var aaa = links.Single(l => l.Url.EndsWith("/aaa"));
        Assert.Equal(("CODE2", "aaa"), (aaa.Code, aaa.Name));
        Assert.Equal(("", "Ccc dd ee"), (links.Single(l => l.Url.EndsWith("/bbb")).Code, links.Single(l => l.Url.EndsWith("/Ccc.dd_ee")).Name));
        Assert.All(links, l => Assert.True(l.Valid && l.Enabled && !l.Duplicate));
    }

    [Fact]
    public async Task Rows_that_do_not_fit_are_counted_as_invalid_instead_of_failing_the_import()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);
        var longSlug = new string('a', 280);

        var text = string.Join("\n", "facebook.com/groups/ok | CODE", "facebook.com/groups/long-code | " + new string('x', 101), $"facebook.com/groups/{longSlug}");
        var bulk = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/bulk", new { text }, Json)).ReadAsync<BulkLinksResultDto>();
        Assert.Equal((1, 2), (bulk.Added, bulk.Invalid));

        var rows = new[]
        {
            new { set = "ใหม่", name = "ดี", url = "facebook.com/groups/fine", code = "A" },
            new { set = "ใหม่", name = new string('x', 201), url = "facebook.com/groups/long-name", code = "" },
            new { set = "ใหม่", name = "x", url = "facebook.com/groups/long-code", code = new string('x', 101) },
            new { set = "ใหม่", name = "x", url = $"facebook.com/groups/{longSlug}", code = "" },
        };
        var csv = await (await client.PostAsJsonAsync($"{Sets(ws)}/import-csv", new { rows }, Json)).ReadAsync<CsvImportResultDto>();
        Assert.Equal((1, 1, 3), (csv.Links, csv.Sets, csv.Invalid));
    }

    [Fact]
    public async Task A_set_holds_at_most_200_links()
    {
        var (client, _, ws) = await factory.SignUpAsync("agency");
        var set = await client.CreateLinkSetAsync(ws);
        var lines = (int from, int count) => string.Join("\n", Enumerable.Range(from, count).Select(i => $"facebook.com/groups/g{i}"));

        var ok = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/bulk", new { text = lines(0, 150) }, Json)).ReadAsync<BulkLinksResultDto>();
        Assert.Equal(150, ok.Added);

        var tooMany = await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/bulk", new { text = lines(150, 51) }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Equal(150, (await client.LinkSetsAsync(ws)).Single().Links.Count); // all or none

        var fits = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/bulk", new { text = lines(150, 50) }, Json)).ReadAsync<BulkLinksResultDto>();
        Assert.Equal(50, fits.Added);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links", new { url = "facebook.com/groups/one-more" }, Json)).StatusCode);
    }

    [Fact]
    public async Task A_workspace_holds_at_most_100_link_sets()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        await factory.WithDbAsync(async db =>
        {
            for (var i = 0; i < LinkSet.MaxPerWorkspace; i++) db.LinkSets.Add(LinkSet.Create(ws, $"ชุด {i}", null, DateTimeOffset.UtcNow, i));
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Sets(ws), new { name = "ชุดที่ 101" }, Json)).StatusCode);
        // The CSV import would create a 101st set: nothing is imported.
        var csv = await client.PostAsJsonAsync($"{Sets(ws)}/import-csv", new { rows = new[] { new { set = "ใหม่", name = "x", url = Plants, code = "" } } }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, csv.StatusCode);
        Assert.Equal(LinkSet.MaxPerWorkspace, (await client.LinkSetsAsync(ws)).Count);
        // ...but rows for a set that exists are fine.
        var existing = await client.PostAsJsonAsync($"{Sets(ws)}/import-csv", new { rows = new[] { new { set = "ชุด 3", name = "x", url = Plants, code = "" } } }, Json);
        Assert.Equal(1, (await existing.ReadAsync<CsvImportResultDto>()).Links);
    }

    [Fact]
    public async Task The_csv_import_creates_sets_by_name_and_skips_duplicates_and_invalid_rows()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var existing = await client.CreateLinkSetAsync(ws, "มีอยู่แล้ว");
        await client.AddLinkAsync(ws, existing.Id, "facebook.com/groups/known");

        var rows = new[]
        {
            new { set = "มีอยู่แล้ว", name = "ใหม่ในชุดเดิม", url = "facebook.com/groups/fresh", code = "C1" },
            new { set = "มีอยู่แล้ว", name = "ซ้ำ", url = "https://www.facebook.com/groups/known", code = "" },
            new { set = "ชุดใหม่", name = "", url = "m.facebook.com/groups/one", code = "N1" },
            new { set = "ชุดใหม่", name = "สอง", url = "fb.com/groups/two", code = "" },
            new { set = "ชุดใหม่", name = "ซ้ำในไฟล์", url = "facebook.com/groups/one", code = "N9" },
            new { set = "ชุดใหม่", name = "เสีย", url = "https://example.com/x", code = "" },
            new { set = "", name = "ไม่มีชุด", url = "facebook.com/groups/three", code = "" },
        };
        var result = await (await client.PostAsJsonAsync($"{Sets(ws)}/import-csv", new { rows }, Json)).ReadAsync<CsvImportResultDto>();

        Assert.Equal((3, 1, 2), (result.Links, result.Sets, result.Invalid));
        var sets = await client.LinkSetsAsync(ws);
        Assert.Equal(["มีอยู่แล้ว", "ชุดใหม่"], sets.Select(s => s.Name));
        Assert.Equal(["known", "fresh"], sets[0].Links.Select(l => l.Url.Split('/')[^1]));
        Assert.Equal("C1", sets[0].Links[1].Code);
        Assert.Equal(["one", "สอง"], sets[1].Links.Select(l => l.Name)); // a blank name comes from the address
        Assert.Equal("N1", sets[1].Links[0].Code);

        // Doing it again adds nothing.
        var again = await (await client.PostAsJsonAsync($"{Sets(ws)}/import-csv", new { rows }, Json)).ReadAsync<CsvImportResultDto>();
        Assert.Equal((0, 0, 2), (again.Links, again.Sets, again.Invalid));
    }

    [Fact]
    public async Task Groups_of_a_connected_account_are_listed_and_imported()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var (device, pair) = await factory.PairDeviceAsync(client, ws);
        (await device.PutAsJsonAsync("/api/device/groups", new
        {
            groups = new[]
            {
                new { name = "ต้นไม้", url = "https://www.facebook.com/groups/plants" },
                new { name = "ตลาดนัด", url = "https://m.facebook.com/groups/market/" },
                new { name = "https://www.facebook.com/groups/noname", url = "https://www.facebook.com/groups/noname" },
                new { name = "ไม่ใช่กลุ่ม", url = "https://example.com/other" },
            },
        })).EnsureSuccessStatusCode();

        var groups = (await client.GetFromJsonAsync<List<GroupLinkDto>>($"/api/workspaces/{ws}/accounts/{pair.AccountId}/groups", Json))!;
        Assert.Equal(["ต้นไม้", "ตลาดนัด", "https://www.facebook.com/groups/noname", "ไม่ใช่กลุ่ม"], groups.Select(g => g.Name));

        var set = await client.CreateLinkSetAsync(ws);
        await client.AddLinkAsync(ws, set.Id, "facebook.com/groups/market", "KEEP");
        var urls = new[]
        {
            "https://www.facebook.com/groups/plants", "https://www.facebook.com/groups/market", "https://www.facebook.com/groups/noname",
            "https://example.com/other", "https://www.facebook.com/groups/not-synced",
        };
        var imported = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/import", new { accountId = pair.AccountId, urls }, Json)).ReadAsync<LinkSetDto>();

        // Only synced groups that are not in the set yet are added; a group without a name is named after its address.
        Assert.Equal(["market", "plants", "noname"], imported.Links.Select(l => l.Url.Split('/')[^1]));
        Assert.Equal(["market", "ต้นไม้", "noname"], imported.Links.Select(l => l.Name));
        Assert.Equal("KEEP", imported.Links[0].Code);
        Assert.All(imported.Links, l => Assert.True(l.Valid && l.Enabled && l.DailyMax == 0));

        var again = await (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/import", new { accountId = pair.AccountId, urls }, Json)).ReadAsync<LinkSetDto>();
        Assert.Equal(3, again.Links.Count);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/workspaces/{ws}/accounts/{Guid.NewGuid()}/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"{Sets(ws)}/{set.Id}/links/import", new { accountId = Guid.NewGuid(), urls }, Json)).StatusCode);
    }

    [Fact]
    public async Task A_set_names_the_account_that_posts_it_and_other_accounts()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var page = await factory.SeedAccountAsync(ws, "เพจ", "เพจ");
        var ig = await factory.SeedAccountAsync(ws);
        var x = await factory.SeedAccountAsync(ws, "@shop_x", "ไทม์ไลน์");

        var set = await client.CreateLinkSetAsync(ws, "ชุดเพจ", page.Id);
        Assert.Equal(page.Id, set.PostAsAccountId);

        var updated = await (await client.PutAsJsonAsync($"{Sets(ws)}/{set.Id}",
            new { name = "ชุดเพจ 2", postAsAccountId = page.Id, accountIds = new[] { ig.Id, x.Id, ig.Id, page.Id } }, Json)).ReadAsync<LinkSetDto>();
        Assert.Equal("ชุดเพจ 2", updated.Name);
        Assert.Equal([ig.Id, x.Id], updated.AccountIds); // distinct, and not the posting account itself

        var cleared = await (await client.PutAsJsonAsync($"{Sets(ws)}/{set.Id}", new { name = "x", postAsAccountId = (Guid?)null, accountIds = Array.Empty<Guid>() }, Json))
            .ReadAsync<LinkSetDto>();
        Assert.Equal((null, 0), (cleared.PostAsAccountId, cleared.AccountIds.Count));

        // The posting account must be an account of this workspace; the others must belong to it too.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsJsonAsync(Sets(ws), new { name = "x", postAsAccountId = Guid.NewGuid() }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PutAsJsonAsync($"{Sets(ws)}/{set.Id}", new { name = "x", accountIds = new[] { Guid.NewGuid() } }, Json)).StatusCode);
        var (stranger, _, _) = await factory.SignUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync(Sets(ws), new { name = "x" }, Json)).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_set_takes_its_links_and_is_refused_while_a_schedule_uses_it()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var set = await client.CreateLinkSetAsync(ws);
        var link = await client.AddLinkAsync(ws, set.Id, Plants);
        var collection = await client.CreateCollectionAsync(ws);
        var start = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        var schedule = Schedule.Create(ws, "ตารางเย็น", collection.Id, set.Id, ScheduleMode.Weekend, ["18:00"], 6, "09:00", start, "14:00",
            PostOrder.Rotate, "09:00", "21:00", 3, 0, 0, new Dictionary<string, IReadOnlyList<string>> { [Schedule.LinkKey(link.Id)] = ["07:00", "19:30"] }, 420,
            DateTimeOffset.UtcNow);
        await factory.WithDbAsync(async db =>
        {
            db.Schedules.Add(schedule);
            await db.SaveChangesAsync();
        });

        var refused = await client.DeleteAsync($"{Sets(ws)}/{set.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("ตารางเย็น", (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!.Title);

        // The schedule's own settings survive a round trip through the database.
        var stored = await factory.WithDbAsync(db => db.Schedules.AsNoTracking().SingleAsync(s => s.Id == schedule.Id));
        Assert.Equal((ScheduleMode.Weekend, PostOrder.Rotate, start, 420), (stored.Mode, stored.Order, stored.StartDate, stored.UtcOffsetMinutes));
        Assert.Equal(["07:00", "19:30"], stored.Overrides[Schedule.LinkKey(link.Id)]);
        Assert.Equal(["18:00"], stored.Times);

        await factory.WithDbAsync(async db =>
        {
            db.Schedules.Remove(await db.Schedules.SingleAsync(s => s.Id == schedule.Id));
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Sets(ws)}/{set.Id}")).StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.SetLinks.CountAsync(l => l.Id == link.Id)));
        Assert.Empty(await client.LinkSetsAsync(ws));
    }

    [Fact]
    public async Task Viewers_read_editors_write()
    {
        var t = await factory.TeamAsync();
        var set = await t.Owner.CreateLinkSetAsync(t.Ws);
        var link = await t.Owner.AddLinkAsync(t.Ws, set.Id, Plants);
        string Links = $"{Sets(t.Ws)}/{set.Id}/links";

        Assert.Single(await t.Viewer.LinkSetsAsync(t.Ws));
        Assert.Equal(HttpStatusCode.NotFound, (await t.Viewer.GetAsync($"/api/workspaces/{t.Ws}/accounts/{Guid.NewGuid()}/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync(Sets(t.Ws), new { name = "x" }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PutAsJsonAsync($"{Sets(t.Ws)}/{set.Id}", new { name = "x", accountIds = Array.Empty<Guid>() }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.DeleteAsync($"{Sets(t.Ws)}/{set.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync(Links, new { url = Plants }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t.Viewer.PutAsJsonAsync($"{Links}/{link.Id}", new { name = "x", url = Plants, code = "", dailyMax = 0, enabled = true }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.DeleteAsync($"{Links}/{link.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsync($"{Links}/{link.Id}/enable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync($"{Links}/bulk", new { text = Plants }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Viewer.PostAsJsonAsync($"{Links}/import", new { accountId = Guid.NewGuid(), urls = new[] { Plants } }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t.Viewer.PostAsJsonAsync($"{Sets(t.Ws)}/import-csv", new { rows = new[] { new { set = "x", name = "", url = Plants, code = "" } } }, Json)).StatusCode);

        var byEditor = await t.Editor.CreateLinkSetAsync(t.Ws, "ของบรรณาธิการ");
        await t.Editor.AddLinkAsync(t.Ws, byEditor.Id, Plants);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Editor.DeleteAsync($"{Links}/{link.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t.Editor.DeleteAsync($"{Sets(t.Ws)}/{set.Id}")).StatusCode);
    }

    [Fact]
    public async Task Switching_a_link_off_or_on_tells_the_workspace_when_a_browser_posts_the_set()
    {
        var (client, _, ws) = await factory.SignUpAsync();
        var (_, pair) = await factory.PairDeviceAsync(client, ws);
        var set = await client.CreateLinkSetAsync(ws, "ชุดเพจ", pair.AccountId);
        var link = await client.AddLinkAsync(ws, set.Id, Plants);
        var head = (await client.GetFromJsonAsync<DeviceEventsPageDto>($"/api/workspaces/{ws}/events?after=0", Json))!.Head;
        var url = $"{Sets(ws)}/{set.Id}/links/{link.Id}";

        await client.PutAsJsonAsync(url, new { name = "x", url = Plants, code = "", dailyMax = 0, enabled = false }, Json);
        await client.PutAsJsonAsync(url, new { name = "y", url = Plants, code = "", dailyMax = 0, enabled = false }, Json); // no change in health: no event
        await client.PostAsync($"{url}/enable", null);

        var events = (await client.GetFromJsonAsync<DeviceEventsPageDto>($"/api/workspaces/{ws}/events?after={head}", Json))!.Events
            .Where(e => e.Type == DeviceEventType.Links).ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(pair.DeviceId, events[0].DeviceId);
        Assert.Equal((set.Id, link.Id, "off"),
            (events[0].Payload.GetProperty("linkSetId").GetGuid(), events[0].Payload.GetProperty("linkId").GetGuid(), events[0].Payload.GetProperty("health").GetString()));
        Assert.Equal("ok", events[1].Payload.GetProperty("health").GetString());
    }

    [Fact]
    public async Task A_set_without_a_browser_has_nobody_to_tell()
    {
        var (client, _, ws) = await factory.SignUpAsync(); // demo accounts only: none is connected
        var set = await client.CreateLinkSetAsync(ws);
        var link = await client.AddLinkAsync(ws, set.Id, Plants);

        var res = await client.PutAsJsonAsync($"{Sets(ws)}/{set.Id}/links/{link.Id}", new { name = "x", url = Plants, code = "", dailyMax = 0, enabled = false }, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<DeviceEventsPageDto>($"/api/workspaces/{ws}/events?after=0", Json))!.Events);
    }
}
