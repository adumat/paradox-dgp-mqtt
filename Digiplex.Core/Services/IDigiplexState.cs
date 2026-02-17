using Digiplex.Core.Models;

namespace Digiplex.Core.Services;

/// <summary>
/// Connection state of the Digiplex panel
/// </summary>
public enum ConnectionState
{
  Disconnected,
  Connecting,
  Initializing,
  LoggingIn,
  Connected,
  Error
}

/// <summary>
/// Event raised when zone status changes
/// </summary>
public record ZoneChangedEvent(int ZoneId, ZoneStatus OldStatus, ZoneStatus NewStatus);

/// <summary>
/// Event raised when partition status changes
/// </summary>
public record PartitionChangedEvent(int PartitionId, PartitionStatus OldStatus, PartitionStatus NewStatus);

/// <summary>
/// Interface for observing Digiplex panel state
/// </summary>
public interface IDigiplexState
{
  /// <summary>
  /// Current connection state
  /// </summary>
  ConnectionState ConnectionState { get; }

  /// <summary>
  /// Observable for connection state changes
  /// </summary>
  IObservable<ConnectionState> ConnectionStateChanged { get; }

  /// <summary>
  /// Panel information (available after connection)
  /// </summary>
  DigiplexInfo? PanelInfo { get; }

  /// <summary>
  /// Observable for panel info updates
  /// </summary>
  IObservable<DigiplexInfo> PanelInfoChanged { get; }

  /// <summary>
  /// Current zone statuses
  /// </summary>
  IReadOnlyDictionary<int, ZoneStatus> Zones { get; }

  /// <summary>
  /// Observable for zone status changes
  /// </summary>
  IObservable<ZoneChangedEvent> ZoneChanged { get; }

  /// <summary>
  /// Current partition statuses
  /// </summary>
  IReadOnlyDictionary<int, PartitionStatus> Partitions { get; }

  /// <summary>
  /// Observable for partition status changes
  /// </summary>
  IObservable<PartitionChangedEvent> PartitionChanged { get; }

  /// <summary>
  /// Labels loaded from panel
  /// </summary>
  IReadOnlyDictionary<string, Dictionary<int, string>> Labels { get; }
}
