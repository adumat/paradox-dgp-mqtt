using Digiplex.Core.Models.Pdu;

namespace Digiplex.Serial;

/// <summary>
/// Interface for serial communication service
/// </summary>
public interface ISerialService : IAsyncDisposable
{
  /// <summary>
  /// Start the serial service
  /// </summary>
  Task StartAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Stop the serial service
  /// </summary>
  Task StopAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Send a PDU to the panel
  /// </summary>
  Task SendAsync(PduBase pdu, CancellationToken cancellationToken = default);

  /// <summary>
  /// Send a PDU and wait for response
  /// </summary>
  Task<PduBase> SendAndReceiveAsync(PduBase pdu, CancellationToken cancellationToken = default);
}
