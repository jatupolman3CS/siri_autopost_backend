using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// Auto-reply rules: stored only, gated by the owner's plan, scoped to "all" or one of the workspace's collections.
[Collection(ApiCollection.Name)]
public class AutoReplyEndpointsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = WorkflowTestSupport.Json;

    private static string Base(Guid ws) => $"/api/workspaces/{ws}/auto-reply";

    private static object Rule(string keywords = "ราคา, สนใจ", string reply = "ทักแชทได้เลยค่ะ", string inbox = "", string scope = "all", bool on = true, Guid? id = null) =>
        new { id = id ?? Guid.Empty, keywords, reply, inbox, scope, on };

    private static object Body(bool on = true, params object[] rules) => new { on, rules };

    private static async Task<AutoReplyDto> PutAsync(HttpClient client, Guid ws, object body) =>
        await (await client.PutAsJsonAsync(Base(ws), body, Json)).ReadAsync<AutoReplyDto>();

    private static async Task<AutoReplyDto> GetAsync(HttpClient client, Guid ws) => (await client.GetFromJsonAsync<AutoReplyDto>(Base(ws), Json))!;

    private static async Task AssertProblemAsync(HttpResponseMessage res, HttpStatusCode status, string? title = null)
    {
        Assert.Equal(status, res.StatusCode);
        if (title is not null) Assert.Equal(title, (await res.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title);
    }

    [Fact]
    public async Task A_workspace_on_any_plan_reads_no_rules()
    {
        var (free, _, ws) = await factory.SignUpAsync();

        var rules = await GetAsync(free, ws);

        Assert.False(rules.On);
        Assert.Empty(rules.Rules);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("basic")]
    public async Task Plans_below_Pro_cannot_save_rules(string? plan)
    {
        var (client, _, ws) = await factory.SignUpAsync(plan);

        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule()), Json), HttpStatusCode.Forbidden, "ต้องใช้แผน Pro ขึ้นไป");
        Assert.Empty((await GetAsync(client, ws)).Rules);
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("agency")]
    public async Task Pro_and_Agency_save_rules(string plan)
    {
        var (client, _, ws) = await factory.SignUpAsync(plan);

        var saved = await PutAsync(client, ws, Body(true, Rule()));

        Assert.True(saved.On);
        var rule = Assert.Single(saved.Rules);
        Assert.Equal(("ราคา, สนใจ", "ทักแชทได้เลยค่ะ", "", "all", true), (rule.Keywords, rule.Reply, rule.Inbox, rule.Scope, rule.On));
        Assert.NotEqual(Guid.Empty, rule.Id); // the server gives a new rule its id
        Assert.Equal(rule, Assert.Single((await GetAsync(client, ws)).Rules));
    }

    [Fact]
    public async Task Viewers_read_and_only_admins_edit()
    {
        var team = await factory.TeamAsync();

        Assert.Equal(HttpStatusCode.OK, (await team.Viewer.GetAsync(Base(team.Ws))).StatusCode);
        foreach (var client in new[] { team.Viewer, team.Editor })
            await AssertProblemAsync(await client.PutAsJsonAsync(Base(team.Ws), Body(true, Rule()), Json), HttpStatusCode.Forbidden);
        Assert.Single((await PutAsync(team.Admin, team.Ws, Body(true, Rule()))).Rules);

        var (stranger, _, _) = await factory.SignUpAsync("agency");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Base(team.Ws))).StatusCode);
    }

    [Fact]
    public async Task Saving_replaces_the_rules_and_keeps_the_ids_the_client_sends()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var keep = Guid.NewGuid();

        var first = await PutAsync(client, ws, Body(true, Rule("ราคา", id: keep), Rule("ที่อยู่", inbox: "ส่งที่อยู่ให้ทางแชท", reply: "", on: false)));
        Assert.Equal(2, first.Rules.Count);
        Assert.Equal(keep, first.Rules[0].Id);
        Assert.False(first.Rules[1].On);

        var second = await PutAsync(client, ws, Body(false, Rule("โปร", id: keep)));
        Assert.False(second.On);
        var only = Assert.Single(second.Rules);
        Assert.Equal(("โปร", keep), (only.Keywords, only.Id));

        Assert.Empty((await PutAsync(client, ws, Body(true))).Rules); // none = clear
    }

    [Fact]
    public async Task Text_is_trimmed_and_a_blank_scope_means_all()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");

        var saved = await PutAsync(client, ws, Body(true, Rule("  ราคา  ", "  ตอบ  ", "  ทัก  ", scope: "  "), Rule(scope: "ALL")));

        Assert.Equal(("ราคา", "ตอบ", "ทัก", "all"), (saved.Rules[0].Keywords, saved.Rules[0].Reply, saved.Rules[0].Inbox, saved.Rules[0].Scope));
        Assert.Equal("all", saved.Rules[1].Scope);
    }

    [Fact]
    public async Task A_scope_must_be_all_or_a_collection_of_this_workspace()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");
        var collection = await client.CreateCollectionAsync(ws);
        var (otherClient, _, otherWs) = await factory.SignUpAsync("pro");
        var foreign = await otherClient.CreateCollectionAsync(otherWs);

        var saved = await PutAsync(client, ws, Body(true, Rule(scope: collection.Id.ToString("N").ToUpperInvariant()))); // any spelling of the id
        Assert.Equal(collection.Id.ToString(), saved.Rules[0].Scope); // stored in one spelling

        const string Title = "ขอบเขตของกฎต้องเป็น \"ทั้งหมด\" หรือชุดโพสต์ที่มีอยู่ในเวิร์กสเปซนี้";
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule(scope: Guid.NewGuid().ToString())), Json), HttpStatusCode.UnprocessableEntity, Title);
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule(scope: foreign.Id.ToString())), Json), HttpStatusCode.UnprocessableEntity, Title);
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule(scope: "everything")), Json), HttpStatusCode.UnprocessableEntity, Title);
        Assert.Equal(collection.Id.ToString(), Assert.Single((await GetAsync(client, ws)).Rules).Scope); // the refused saves changed nothing

        // A scope that points at a collection stays stored when the collection is deleted later (it just matches nothing).
        (await client.DeleteAsync($"/api/workspaces/{ws}/collections/{collection.Id}")).EnsureSuccessStatusCode();
        Assert.Single((await GetAsync(client, ws)).Rules);
    }

    [Fact]
    public async Task A_rule_needs_keywords_and_at_least_one_answer()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");

        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule(keywords: "  ")), Json), HttpStatusCode.UnprocessableEntity, "กรุณาใส่คำที่ต้องการให้ตรวจจับ");
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule(reply: "", inbox: " ")), Json), HttpStatusCode.UnprocessableEntity);
        var twin = Guid.NewGuid();
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule(id: twin), Rule(id: twin)), Json), HttpStatusCode.UnprocessableEntity, "รหัสกฎซ้ำกัน");
        Assert.Empty((await GetAsync(client, ws)).Rules);
    }

    [Fact]
    public async Task The_shape_of_the_request_is_checked()
    {
        var (client, _, ws) = await factory.SignUpAsync("pro");

        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule(keywords: new string('ก', 301))), Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule(reply: new string('ก', 501))), Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Rule(inbox: new string('ก', 501))), Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), Body(true, Enumerable.Range(0, 51).Select(i => Rule($"คำ{i}")).ToArray()), Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await client.PutAsJsonAsync(Base(ws), new { on = true, rules = new object?[] { null } }, Json), HttpStatusCode.BadRequest);
        await AssertProblemAsync(await client.PutAsync(Base(ws), new StringContent("null", System.Text.Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest);
        Assert.Equal(50, (await PutAsync(client, ws, Body(true, Enumerable.Range(0, 50).Select(i => Rule($"คำ{i}")).ToArray()))).Rules.Count); // the limit itself is fine
    }
}
