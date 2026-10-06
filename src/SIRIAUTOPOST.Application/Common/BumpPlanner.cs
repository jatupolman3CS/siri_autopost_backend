using SIRIAUTOPOST.Domain.Entities;
using SIRIAUTOPOST.Domain.Services;

namespace SIRIAUTOPOST.Application.Common;

/// <summary>
/// Turns a post that went out into the bumps its schedule asks for: <see cref="BumpPlan.Rounds"/> comments, the first
/// the schedule's bump hours after the post, each next one that long after the one before (with a few minutes of random
/// jitter so they never land on the same minute). Each bump has its own text (spintax resolved) and its own images.
/// </summary>
public static class BumpPlanner
{
    /// <summary>The most minutes a bump is moved later at random.</summary>
    public const int JitterMinutes = 20;

    /// <summary>
    /// The bumps of this post; empty when the schedule does not bump, the post has no address on Facebook (the extension
    /// could not read it, or the group holds the post for approval) or it is a test.
    /// </summary>
    public static IReadOnlyList<PostBump> Plan(Post post, Schedule? schedule, DateTimeOffset now, Func<double> rnd)
    {
        if (schedule is null || schedule.BumpHours <= 0 || post.IsTest || string.IsNullOrEmpty(post.PostUrl)) return [];
        var plan = schedule.Bump;
        var published = post.PublishedAt ?? now;
        var list = new List<PostBump>();
        for (var round = 1; round <= Math.Clamp(plan.Rounds, 1, BumpPlan.MaxRounds); round++)
        {
            var due = published.AddHours(schedule.BumpHours * (double)round).AddMinutes(rnd() * JitterMinutes);
            var text = TextFor(plan, rnd);
            list.Add(PostBump.Create(post, post.PostUrl, text, PickImages(plan, rnd), round, due, now));
        }
        return list;
    }

    /// <summary>The plan's text with the spintax picked; the default when the plan has neither text nor images.</summary>
    public static string TextFor(BumpPlan plan, Func<double> rnd)
    {
        var text = string.IsNullOrWhiteSpace(plan.Text) ? (plan.MediaIds.Count > 0 && plan.ImagesEach > 0 ? "" : BumpPlan.DefaultText) : plan.Text;
        return PostComposer.Spin(text, rnd).Trim();
    }

    /// <summary>A random choice of <see cref="BumpPlan.ImagesEach"/> files of the pool, without repeats.</summary>
    public static IReadOnlyList<Guid> PickImages(BumpPlan plan, Func<double> rnd)
    {
        var pool = plan.MediaIds.ToList();
        var count = Math.Min(plan.ImagesEach, pool.Count);
        var picked = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var index = Math.Clamp((int)Math.Floor(rnd() * pool.Count), 0, pool.Count - 1);
            picked.Add(pool[index]);
            pool.RemoveAt(index);
        }
        return picked;
    }
}
