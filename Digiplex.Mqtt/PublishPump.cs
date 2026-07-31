using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Digiplex.Mqtt;

/// <summary>
/// Drains a channel of <see cref="PublishItem"/> and sends each one, in order, via
/// the supplied delegate. A failing send is logged and skipped so one bad message
/// never stalls the queue. Returns when the channel is completed (draining what is
/// left) or the token is cancelled.
/// </summary>
public static class PublishPump
{
    public static async Task RunAsync(
        ChannelReader<PublishItem> reader,
        Func<PublishItem, Task> send,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(ct))
            {
                try { await send(item); }
                catch (Exception ex) { logger.LogWarning(ex, "Publish to {Topic} failed", item.Topic); }
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }
}
