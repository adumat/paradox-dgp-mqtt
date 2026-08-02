using Microsoft.Extensions.Logging;

namespace Digiplex.Core.Services;

/// <summary>
/// Supervises a long-lived connection: repeatedly runs <paramref name="attempt"/> until
/// <paramref name="ct"/> is cancelled. Each attempt should open, initialise and then serve
/// the connection, invoking the supplied <c>onConnected</c> callback once it is fully up so
/// the backoff resets. When an attempt throws, the loop logs it, waits (starting at
/// <paramref name="initialDelay"/>, doubling up to <paramref name="maxDelay"/>), then retries.
/// The loop returns only when cancellation is requested.
/// </summary>
public static class ReconnectLoop
{
    public static async Task RunAsync(
        Func<Action, CancellationToken, Task> attempt,
        TimeSpan initialDelay,
        TimeSpan maxDelay,
        Func<TimeSpan, CancellationToken, Task> delay,
        ILogger logger,
        CancellationToken ct)
    {
        var current = initialDelay;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await attempt(() => current = initialDelay, ct);
                return; // attempt returned without throwing => cancellation requested
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Connection attempt failed; retrying in {Delay}", current);

                try
                {
                    await delay(current, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }

                current = TimeSpan.FromMilliseconds(
                    Math.Min(current.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
            }
        }
    }
}
