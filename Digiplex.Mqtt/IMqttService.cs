namespace Digiplex.Mqtt;

/// <summary>
/// Interface for MQTT communication service
/// </summary>
public interface IMqttService : IAsyncDisposable
{
  /// <summary>
  /// Start the MQTT service
  /// </summary>
  Task StartAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Stop the MQTT service
  /// </summary>
  Task StopAsync(CancellationToken cancellationToken = default);

  /// <summary>
  /// Check if connected to broker
  /// </summary>
  bool IsConnected { get; }
}
