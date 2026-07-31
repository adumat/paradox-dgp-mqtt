using System.Threading.Channels;
using Digiplex.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class PublishPumpTests
{
    private static PublishItem P(string t) => new(t, "x", false);

    [Fact]
    public async Task Delivers_in_order()
    {
        var ch = Channel.CreateUnbounded<PublishItem>();
        var seen = new List<string>();
        var pump = PublishPump.RunAsync(ch.Reader, i => { seen.Add(i.Topic); return Task.CompletedTask; },
            NullLogger.Instance, CancellationToken.None);
        foreach (var t in new[] { "a", "b", "c" }) ch.Writer.TryWrite(P(t));
        ch.Writer.Complete();
        await pump;
        Assert.Equal(new[] { "a", "b", "c" }, seen);
    }

    [Fact]
    public async Task A_failing_send_does_not_stop_the_queue()
    {
        var ch = Channel.CreateUnbounded<PublishItem>();
        var seen = new List<string>();
        var pump = PublishPump.RunAsync(ch.Reader, i =>
        {
            if (i.Topic == "b") throw new InvalidOperationException("boom");
            seen.Add(i.Topic);
            return Task.CompletedTask;
        }, NullLogger.Instance, CancellationToken.None);
        foreach (var t in new[] { "a", "b", "c" }) ch.Writer.TryWrite(P(t));
        ch.Writer.Complete();
        await pump;
        Assert.Equal(new[] { "a", "c" }, seen); // b threw, c still delivered
    }

    [Fact]
    public async Task Drains_remaining_after_complete()
    {
        var ch = Channel.CreateUnbounded<PublishItem>();
        var count = 0;
        ch.Writer.TryWrite(P("a"));
        ch.Writer.TryWrite(P("b"));
        ch.Writer.Complete();
        await PublishPump.RunAsync(ch.Reader, _ => { count++; return Task.CompletedTask; },
            NullLogger.Instance, CancellationToken.None);
        Assert.Equal(2, count);
    }
}
