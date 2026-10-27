using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Enums;
using SIRIAUTOPOST.Domain.ValueObjects;

namespace SIRIAUTOPOST.Domain.Tests;

public class LineOptionsTests
{
    private static Schedule MakeSchedule(LineOptions options, int utcOffset = 420) // Bangkok +7
    {
        var now = DateTimeOffset.UtcNow;
        var today = DateOnly.FromDateTime(now.DateTime);
        return Schedule.Create(
            Guid.NewGuid(), "Line Schedule", Guid.NewGuid(), Guid.NewGuid(), ScheduleMode.Daily,
            times: ["10:00"], everyHours: 0, firstTime: null, startDate: today,
            onceTime: null, order: PostOrder.Rotate, dripFrom: null, dripTo: null, dripCount: 0,
            bumpHours: 0, autoDeleteDays: 0, overrides: null, utcOffsetMinutes: utcOffset,
            now: now, lineOptions: options);
    }

    [Theory]
    [InlineData(8, 59, false)]
    [InlineData(9, 0, true)]
    [InlineData(14, 30, true)]
    [InlineData(20, 0, true)]
    [InlineData(20, 1, false)]
    public void Standard_window_bounds(int localHour, int localMinute, bool expected)
    {
        var options = new LineOptions
        {
            AllowedFrom = "09:00",
            AllowedTo = "20:00",
            SkipIfActiveMinutes = 30,
        };
        var schedule = MakeSchedule(options, utcOffset: 0); // UTC
        var at = new DateTimeOffset(2026, 10, 21, localHour, localMinute, 0, TimeSpan.Zero);
        Assert.Equal(expected, schedule.AllowsLineAt(at));
    }

    [Theory]
    [InlineData(21, 59, false)]
    [InlineData(22, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(2, 0, true)]
    [InlineData(6, 0, true)]
    [InlineData(6, 1, false)]
    public void Midnight_wrapping_window_bounds(int localHour, int localMinute, bool expected)
    {
        var options = new LineOptions
        {
            AllowedFrom = "22:00",
            AllowedTo = "06:00",
            SkipIfActiveMinutes = 30,
        };
        var schedule = MakeSchedule(options, utcOffset: 0);
        var at = new DateTimeOffset(2026, 10, 21, localHour, localMinute, 0, TimeSpan.Zero);
        Assert.Equal(expected, schedule.AllowsLineAt(at));
    }
}
