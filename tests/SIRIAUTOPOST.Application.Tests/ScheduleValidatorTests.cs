using SIRIAUTOPOST.Application.DTOs;
using SIRIAUTOPOST.Application.Validators;
using SIRIAUTOPOST.Domain.Enums;

namespace SIRIAUTOPOST.Application.Tests;

public class ScheduleValidatorTests
{
    // Saturday 3 Oct 2026, 10:30 in Bangkok.
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 30, 0, TimeSpan.FromHours(7));

    private static SaveScheduleRequest Request(string? start, int offset = 420) =>
        new(null, Guid.NewGuid(), Guid.NewGuid(), ScheduleMode.Daily, ["09:00"], 6, null, start, null, PostOrder.Rotate, null, null, 3, 0, 0, null, offset);

    private static string[] Errors(SaveScheduleRequest r) =>
        new SaveScheduleRequestValidator(new FixedClock(Now)).Validate(r).Errors.Where(e => e.PropertyName == nameof(SaveScheduleRequest.StartDate))
            .Select(e => e.ErrorMessage).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-02")] // yesterday
    [InlineData("2026-10-03")]
    [InlineData("2027-10-04")] // 366 days ahead
    public void A_start_date_from_yesterday_to_a_year_ahead_passes(string? start) => Assert.Empty(Errors(Request(start)));

    [Theory]
    [InlineData("9999-12-31")]
    [InlineData("0001-01-01")]
    [InlineData("2026-10-01")]
    [InlineData("2027-10-05")]
    public void A_start_date_outside_that_range_is_a_400_that_says_what_is_allowed(string start)
    {
        var error = Assert.Single(Errors(Request(start)));
        Assert.Contains("2026-10-02", error);
        Assert.Contains("2027-10-04", error);
    }

    [Fact]
    public void The_range_is_the_one_of_the_schedules_own_calendar()
    {
        // 03:30 UTC on the 3rd is 22:30 on the 2nd five hours behind: yesterday there is the 1st.
        Assert.Empty(Errors(Request("2026-10-01", offset: -300)));
        Assert.Single(Errors(Request("2026-09-30", offset: -300)));
    }

    [Fact]
    public void A_text_that_is_not_a_date_is_left_to_the_date_rule()
    {
        var errors = Errors(Request("03/10/2026"));
        Assert.Single(errors);
        Assert.Contains("ปปปป-ดด-วว", errors[0]);
    }
}
