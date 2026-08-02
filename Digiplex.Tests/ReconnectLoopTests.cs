using Digiplex.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class ReconnectLoopTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Max = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Retries_failing_attempts_then_serves_until_cancelled()
    {
        var cts = new CancellationTokenSource();
        var calls = 0;

        Func<Action, CancellationToken, Task> attempt = async (onConnected, ct) =>
        {
            calls++;
            if (calls <= 2) throw new InvalidOperationException("connect failed");
            onConnected();
            await Task.Delay(Timeout.Infinite, ct); // "serving" until cancelled
        };

        var loop = ReconnectLoop.RunAsync(attempt, Initial, Max,
            (_, _) => Task.CompletedTask, NullLogger.Instance, cts.Token);

        while (Volatile.Read(ref calls) < 3) await Task.Yield();
        cts.Cancel();
        await loop;

        Assert.Equal(3, calls); // two failures, then one that reached serving
    }

    [Fact]
    public async Task Backoff_grows_on_repeated_failure_and_resets_after_connect()
    {
        var cts = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        var calls = 0;

        Func<Action, CancellationToken, Task> attempt = async (onConnected, ct) =>
        {
            calls++;
            if (calls == 4) { onConnected(); throw new InvalidOperationException("dropped after connect"); }
            if (calls >= 6) { onConnected(); await Task.Delay(Timeout.Infinite, ct); return; }
            throw new InvalidOperationException("connect failed");
        };

        Func<TimeSpan, CancellationToken, Task> delay = (d, _) => { delays.Add(d); return Task.CompletedTask; };

        var loop = ReconnectLoop.RunAsync(attempt, Initial, Max, delay, NullLogger.Instance, cts.Token);

        while (Volatile.Read(ref calls) < 6) await Task.Yield();
        cts.Cancel();
        await loop;

        // call1->1s, call2->2s, call3->4s, call4 connected-then-dropped resets ->1s, call5->2s
        Assert.Equal(new[] { 1.0, 2.0, 4.0, 1.0, 2.0 }, delays.Select(d => d.TotalSeconds).ToArray());
    }

    [Fact]
    public async Task Cancellation_during_backoff_exits_without_another_attempt()
    {
        var cts = new CancellationTokenSource();
        var calls = 0;

        Func<Action, CancellationToken, Task> attempt = (_, _) =>
        {
            calls++;
            throw new InvalidOperationException("always fails");
        };

        Func<TimeSpan, CancellationToken, Task> delay = (_, _) => { cts.Cancel(); return Task.CompletedTask; };

        await ReconnectLoop.RunAsync(attempt, Initial, Max, delay, NullLogger.Instance, cts.Token);

        Assert.Equal(1, calls); // cancelled during the first backoff -> no second attempt
    }
}
