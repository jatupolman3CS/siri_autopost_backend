using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using SIRIAUTOPOST.Application.DTOs;
using static SIRIAUTOPOST.Api.IntegrationTests.EngineTestSupport;

namespace SIRIAUTOPOST.Api.IntegrationTests;

// The limits that keep one workspace's schedules from hurting the rest: where a schedule may start, how many posts may wait.
[Collection(ApiCollection.Name)]
public class ScheduleLimitsTests(ApiFactory factory)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = ApiFactory.Json;
    private const int Bangkok = 420;

    /// <summary>Fills the queue until it holds <paramref name="target"/> posts that are still to go out; returns what it holds.</summary>
    private async Task<int> FillToAsync(Shop shop, int target)
    {
        await factory.FillQueueAsync(shop.Ws, shop.Pair.AccountId, target - await factory.QueuedAsync(shop.Ws, shop.Now));
        return await factory.QueuedAsync(shop.Ws, shop.Now);
    }

    private static string Day(DateTimeOffset now, int days) => ToLocalDay(now.AddDays(days), Bangkok);

    // ---- the start date ----

    [Fact]
    public async Task A_start_date_from_yesterday_to_366_days_ahead_is_fine_and_anything_else_is_a_400_naming_the_range()
    {
        using var shop = await factory.ShopAsync(links: 1);

        async Task<HttpResponseMessage> Create(string start) => await shop.TryCreateScheduleAsync(new ScheduleSpec(Times: ["18:00"], StartDate: start, Offset: Bangkok));

        Assert.Equal(HttpStatusCode.OK, (await Create(Day(shop.Now, -1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Create(Day(shop.Now, 366))).StatusCode);
        foreach (var bad in new[] { Day(shop.Now, -2), Day(shop.Now, 367), "9999-12-31", "0001-01-01" })
        {
            var res = await Create(bad);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var problem = (await res.Content.ReadFromJsonAsync<ValidationProblemDetails>(Json))!;
            Assert.Contains("startDate", problem.Errors.Keys);
            Assert.Contains(Day(shop.Now, -1), problem.Errors["startDate"][0]);
            Assert.Contains(Day(shop.Now, 366), problem.Errors["startDate"][0]);
        }
        Assert.Equal(2, (await shop.SchedulesAsync()).Count); // the refused ones left nothing behind
    }

    [Fact]
    public async Task A_schedule_that_starts_a_year_ahead_makes_no_posts_yet_and_a_once_schedule_makes_its_day()
    {
        using var shop = await factory.ShopAsync(links: 2);

        var daily = await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["18:00"], StartDate: Day(shop.Now, 366), Offset: Bangkok));
        var once = await shop.CreateScheduleAsync(new ScheduleSpec(Mode: "once", StartDate: Day(shop.Now, 300), Offset: Bangkok));

        Assert.Equal((0, 2), (daily.Created, once.Created)); // the daily one is beyond the fortnight; the once one is made for its day
        Assert.True(daily.Schedule.Active && once.Schedule.Active);
        Assert.Null(await shop.ClaimAsync()); // and the claim is fine
    }

    // ---- the queue limit ----

    [Fact]
    public async Task Creating_a_schedule_that_would_overfill_the_queue_is_refused_saying_how_many_are_queued_and_leaves_nothing()
    {
        using var shop = await factory.ShopAsync(links: 2);
        var queued = await FillToAsync(shop, 10_000 - 10); // room for ten: a daily schedule of two groups needs 28

        var res = await shop.TryCreateScheduleAsync(new ScheduleSpec(Times: ["18:00"], Offset: Bangkok));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var title = (await res.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title!;
        Assert.Contains(queued.ToString("N0", CultureInfo.InvariantCulture), title);
        Assert.Contains("10,000", title);
        Assert.Empty(await shop.SchedulesAsync()); // the schedule itself was not kept
        Assert.Equal(queued, await factory.QueuedAsync(shop.Ws, shop.Now));
    }

    [Fact]
    public async Task Resuming_a_schedule_that_would_overfill_the_queue_is_refused_and_it_stays_paused()
    {
        using var shop = await factory.ShopAsync(links: 2);
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: ["18:00"], Offset: Bangkok));
        await shop.SetActiveAsync(created.Schedule.Id, false);
        var queued = await FillToAsync(shop, 10_000 - 10);

        var res = await shop.SetActiveAsync(created.Schedule.Id, true);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains(queued.ToString("N0", CultureInfo.InvariantCulture), (await res.Content.ReadFromJsonAsync<ProblemDetails>(Json))!.Title);
        Assert.False((await shop.SchedulesAsync()).Single().Active);
        Assert.Empty(await shop.PostsAsync(created.Schedule.Id));

        await factory.EmptyFillerAsync(shop.Ws);
        Assert.True((await (await shop.SetActiveAsync(created.Schedule.Id, true)).ReadAsync<ScheduleDto>()).Active); // with room it works
    }

    [Fact]
    public async Task A_full_queue_stops_the_top_up_without_an_error_and_it_goes_on_ten_minutes_after_there_is_room()
    {
        using var shop = await factory.ShopAsync(links: 2);
        // A slot twelve hours away: waiting three days and eleven minutes never moves "now" across it, whatever time the test runs
        // (a fixed 18:00 failed in the quarter of an hour before 18:00 Bangkok time, when the eleven minutes crossed the slot).
        var (slot, _) = EngineTestSupport.SlotAhead(shop.Now, Bangkok, TimeSpan.FromHours(12));
        var created = await shop.CreateScheduleAsync(new ScheduleSpec(Times: [slot], Offset: Bangkok));
        shop.Wait(TimeSpan.FromDays(3)); // three more days are due: 6 posts
        var before = (await shop.PostsAsync(created.Schedule.Id)).Count;
        await FillToAsync(shop, 10_000 - 3); // room for 3

        var claim = await shop.Device.PostAsync("/api/device/jobs/claim", null);

        Assert.True(claim.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK, claim.StatusCode.ToString());
        Assert.Equal(before, (await shop.PostsAsync(created.Schedule.Id)).Count); // nothing was added: it would not fit

        await factory.EmptyFillerAsync(shop.Ws);
        await shop.ClaimAsync();
        Assert.Equal(before, (await shop.PostsAsync(created.Schedule.Id)).Count); // there is room now, but the schedule is left alone for ten minutes

        shop.Wait(TimeSpan.FromMinutes(11));
        await shop.ClaimAsync();
        Assert.Equal(before + 6, (await shop.PostsAsync(created.Schedule.Id)).Count);
    }
}
