using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Api.IntegrationTests;

[Collection(ApiCollection.Name)]
public class BillingAndTeamTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;

    [Fact]
    public async Task Plans_are_public_and_a_new_account_starts_on_free()
    {
        var anon = factory.CreateClient();
        var plans = (await anon.GetFromJsonAsync<List<PlanDto>>("/api/plans", Json))!;
        Assert.Equal([PlanKey.Free, PlanKey.Basic, PlanKey.Pro, PlanKey.Agency], plans.Select(p => p.Key));
        Assert.Equal((790, (int?)3), (plans[2].Price, plans[2].Devices));

        // A paid plan is bought at Stripe Checkout: signing up never hands one out.
        var res = await anon.PostAsJsonAsync("/api/auth/signup", new { email = $"u{Guid.NewGuid():N}@shop.co", password = "password1", plan = "agency" }, Json);
        var auth = (await res.Content.ReadFromJsonAsync<AuthResultDto>(Json))!;
        Assert.Equal((PlanKey.Free, CustomerStatus.Active), (auth.User.Plan, auth.User.Status));
    }

    [Fact]
    public async Task Promo_codes_discount_the_first_invoice_and_count_their_uses_once_the_checkout_is_paid()
    {
        var admin = await factory.AdminAsync();
        var code = $"T{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        (await admin.PostAsJsonAsync("/api/admin/promos", new { code, discount = "d20" }, Json)).EnsureSuccessStatusCode();

        var (client, auth, _) = await factory.SignUpAsync();
        var bad = await client.PutAsJsonAsync("/api/billing/plan", new { plan = "basic", promoCode = "NOPE123" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var onFree = await client.PutAsJsonAsync("/api/billing/plan", new { plan = "free", promoCode = code }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, onFree.StatusCode);

        (await client.PutAsJsonAsync("/api/billing/plan", new { plan = "basic", cycle = "month", promoCode = code.ToLowerInvariant() }, Json))
            .EnsureSuccessStatusCode();
        var request = factory.Payments.Session(factory.Payments.LastSessionOf(auth.User.Id));
        Assert.Equal((58, code), (request.FirstDiscount, request.PromoCode)); // 20% of 290
        var unpaid = (await admin.GetFromJsonAsync<List<PromoDto>>("/api/admin/promos", Json))!.Single(p => p.Code == code);
        Assert.Equal(0, unpaid.Uses); // opening Checkout is not using the code

        await factory.SubscribeAsync(client, auth, PlanKey.Basic, promoCode: code);
        var charge = (await client.GetFromJsonAsync<List<TransactionDto>>("/api/billing/invoices", Json))!.Single();
        Assert.Equal((232m, code), (charge.Amount, charge.PromoCode));
        Assert.Equal(1, (await admin.GetFromJsonAsync<List<PromoDto>>("/api/admin/promos", Json))!.Single(p => p.Code == code).Uses);

        (await admin.PutAsJsonAsync($"/api/admin/promos/{code}/active", new { active = false }, Json)).EnsureSuccessStatusCode();
        var (other, _, _) = await factory.SignUpAsync();
        var closed = await other.PutAsJsonAsync("/api/billing/plan", new { plan = "pro", promoCode = code }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, closed.StatusCode);
    }

    [Fact]
    public async Task Inviting_needs_seats_from_the_plan()
    {
        var (owner, _, ws) = await factory.SignUpAsync("pro"); // 1 seat: the owner
        var res = await owner.PostAsJsonAsync($"/api/workspaces/{ws}/members", new { email = "x@shop.co", role = "editor" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Members_get_what_their_role_allows()
    {
        var (owner, _, ws) = await factory.SignUpAsync("agency");
        var (editor, editorAuth, _) = await factory.SignUpAsync();
        var viewerEmail = $"v{Guid.NewGuid():N}@shop.co";

        var inv = await owner.PostAsJsonAsync($"/api/workspaces/{ws}/members", new { email = editorAuth.User.Email, role = "editor" }, Json);
        var editorRow = (await inv.Content.ReadFromJsonAsync<MemberDto>(Json))!;
        Assert.True(editorRow.Active); // existing account: joined at once
        var pending = (await (await owner.PostAsJsonAsync($"/api/workspaces/{ws}/members", new { email = viewerEmail, role = "viewer" }, Json))
            .Content.ReadFromJsonAsync<MemberDto>(Json))!;
        Assert.False(pending.Active);
        Assert.Equal(HttpStatusCode.Conflict,
            (await owner.PostAsJsonAsync($"/api/workspaces/{ws}/members", new { email = viewerEmail, role = "viewer" }, Json)).StatusCode);

        // The viewer signs up later and finds the workspace.
        var (viewer, _, _) = await factory.SignUpAsync(email: viewerEmail);
        var shared = (await viewer.GetFromJsonAsync<List<WorkspaceDto>>("/api/workspaces", Json))!.Single(w => w.Id == ws);
        Assert.Equal((WorkspaceRole.Viewer, 3), (shared.Role, shared.Members));

        var members = (await viewer.GetFromJsonAsync<List<MemberDto>>($"/api/workspaces/{ws}/members", Json))!;
        Assert.Equal([WorkspaceRole.Owner, WorkspaceRole.Editor, WorkspaceRole.Viewer], members.Select(m => m.Role));
        Assert.True(members.Single(m => m.Role == WorkspaceRole.Viewer).You);

        var accounts = (await viewer.GetFromJsonAsync<List<AccountDto>>($"/api/workspaces/{ws}/accounts", Json))!;
        var schedule = new
        {
            content = "จากทีม", startAt = DateTimeOffset.UtcNow.AddHours(3), useDelay = false, repeat = "none",
            targets = new[] { new { accountId = accounts.First(a => a.Platform == Platform.Ig).Id } },
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", schedule, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.PostAsJsonAsync($"/api/workspaces/{ws}/posts/schedule", schedule, Json)).StatusCode);

        var engine = (await editor.GetFromJsonAsync<EngineSettingsDto>($"/api/workspaces/{ws}/engine", Json))!;
        Assert.Equal(HttpStatusCode.Forbidden,
            (await editor.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban", engine.AntiBan, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PostAsync($"/api/workspaces/{ws}/devices/pairing", null)).StatusCode);

        // Promoted to admin, the editor may change settings; removed, they lose the workspace.
        (await owner.PutAsJsonAsync($"/api/workspaces/{ws}/members/{editorRow.Id}", new { role = "admin" }, Json)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await editor.PutAsJsonAsync($"/api/workspaces/{ws}/engine/anti-ban", engine.AntiBan, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/workspaces/{ws}/members/{editorRow.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetAsync($"/api/workspaces/{ws}/posts?from=2026-01-01&to=2026-02-01")).StatusCode);

        // A member may leave on their own.
        var viewerRow = members.Single(m => m.You);
        Assert.Equal(HttpStatusCode.NoContent, (await viewer.DeleteAsync($"/api/workspaces/{ws}/members/{viewerRow.Id}")).StatusCode);
    }
}
