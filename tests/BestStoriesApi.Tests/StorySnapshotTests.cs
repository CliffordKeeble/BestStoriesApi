using System.Text;
using System.Text.Json;
using BestStoriesApi.Stories;

namespace BestStoriesApi.Tests;

/// <summary>The pre-built answers must be exactly what serialising each request used to produce.</summary>
public class StorySnapshotTests
{
    private static readonly DateTimeOffset Example = DateTimeOffset.FromUnixTimeSeconds(TestData.ExampleUnixTime);

    /// <summary>Awkward on purpose: no url, non-ASCII, quotes, HTML characters, a backslash, a big score.</summary>
    private static readonly Story[] Awkward =
    [
        new("Plain story", "https://example.com/a", "alice", Example, 1716, 3),
        new("Ask HN: no link here?", null, "bob", Example.AddMinutes(-5), 900, 0),
        new("Café — naïve “quotes” and emoji 🎯", "https://example.com/é?q=1&r=<2>", "zoë", Example.AddHours(-1), 450, 12),
        new("He said \"hi\" \\ then left", "https://example.com/b", "carol", Example.AddDays(-1), 450, 7),
        new("<script>alert('x')</script> & co", "https://example.com/c+d", "dave", Example.AddDays(-2), int.MaxValue, int.MaxValue),
    ];

    /// <summary>What the controller of 4ee5907 wrote: System.Text.Json with the web defaults, per request.</summary>
    private static string AsTheControllerWroteIt(IEnumerable<Story> stories) =>
        JsonSerializer.Serialize(stories, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static string Answer(StorySnapshot snapshot, int n) =>
        Encoding.UTF8.GetString(snapshot.Head(n).Span) + Encoding.UTF8.GetString(StorySnapshot.Tail.Span);

    [Test]
    public void Every_answer_is_byte_for_byte_what_serialising_per_request_produced()
    {
        var snapshot = StorySnapshot.Create(Awkward);

        for (var n = 1; n <= Awkward.Length + 3; n++)
        {
            var expected = AsTheControllerWroteIt(Awkward.Take(n));
            Assert.That(Answer(snapshot, n), Is.EqualTo(expected), $"n = {n}");
            Assert.That(snapshot.Length(n), Is.EqualTo(Encoding.UTF8.GetByteCount(expected)), $"Content-Length for n = {n}");
        }
    }

    [Test]
    public void The_answer_for_n_is_valid_JSON_holding_n_stories_and_more_than_there_are_gives_all()
    {
        var snapshot = StorySnapshot.Create(Awkward);

        Assert.That(JsonDocument.Parse(Answer(snapshot, 1)).RootElement.GetArrayLength(), Is.EqualTo(1));
        Assert.That(JsonDocument.Parse(Answer(snapshot, 3)).RootElement.GetArrayLength(), Is.EqualTo(3));
        Assert.That(JsonDocument.Parse(Answer(snapshot, 500)).RootElement.GetArrayLength(), Is.EqualTo(Awkward.Length));
    }

    [Test]
    public void The_tag_depends_only_on_the_content_so_an_unchanged_answer_keeps_its_tag_across_refreshes()
    {
        var first = StorySnapshot.Create(Awkward);
        var second = StorySnapshot.Create(Awkward.Select(s => s with { }).ToArray());

        for (var n = 1; n <= Awkward.Length; n++)
            Assert.That(second.ETag(n), Is.EqualTo(first.ETag(n)), $"n = {n}");
    }

    [Test]
    public void A_change_to_story_k_changes_the_tag_for_every_n_that_includes_it_and_no_other()
    {
        var before = StorySnapshot.Create(Awkward);
        var changed = Awkward.ToArray();
        changed[2] = changed[2] with { CommentCount = changed[2].CommentCount + 1 };
        var after = StorySnapshot.Create(changed);

        Assert.That(after.ETag(1), Is.EqualTo(before.ETag(1)));
        Assert.That(after.ETag(2), Is.EqualTo(before.ETag(2)));
        for (var n = 3; n <= Awkward.Length; n++)
            Assert.That(after.ETag(n), Is.Not.EqualTo(before.ETag(n)), $"n = {n}");
    }

    [Test]
    public void Tags_differ_between_n_and_are_quoted_as_HTTP_requires()
    {
        var snapshot = StorySnapshot.Create(Awkward);
        var tags = Enumerable.Range(1, Awkward.Length).Select(snapshot.ETag).ToList();

        Assert.That(tags, Is.Unique);
        Assert.That(tags, Is.All.StartWith("\"").And.EndWith("\""));
    }
}
