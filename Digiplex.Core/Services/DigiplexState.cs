using System.Reactive.Linq;
using System.Reactive.Subjects;
using Digiplex.Core.Models;

namespace Digiplex.Core.Services;

/// <summary>
/// Observable state container for Digiplex panel
/// </summary>
public class DigiplexState : IDigiplexState, IDisposable
{
  private readonly BehaviorSubject<ConnectionState> _connectionState = new(ConnectionState.Disconnected);
  private readonly Subject<DigiplexInfo> _panelInfoSubject = new();
  private readonly Subject<ZoneChangedEvent> _zoneChangedSubject = new();
  private readonly Subject<PartitionChangedEvent> _partitionChangedSubject = new();

  private readonly Dictionary<int, ZoneStatus> _zones = new();
  private readonly Dictionary<int, PartitionStatus> _partitions = new();
  private readonly Dictionary<string, Dictionary<int, string>> _labels = new();

  private DigiplexInfo? _panelInfo;
  private bool _disposed;

  public ConnectionState ConnectionState => _connectionState.Value;
  public IObservable<ConnectionState> ConnectionStateChanged => _connectionState.AsObservable();

  public DigiplexInfo? PanelInfo => _panelInfo;
  public IObservable<DigiplexInfo> PanelInfoChanged => _panelInfoSubject.AsObservable();

  public IReadOnlyDictionary<int, ZoneStatus> Zones => _zones;
  public IObservable<ZoneChangedEvent> ZoneChanged => _zoneChangedSubject.AsObservable();

  public IReadOnlyDictionary<int, PartitionStatus> Partitions => _partitions;
  public IObservable<PartitionChangedEvent> PartitionChanged => _partitionChangedSubject.AsObservable();

  public IReadOnlyDictionary<string, Dictionary<int, string>> Labels => _labels;

  /// <summary>
  /// Update the connection state
  /// </summary>
  public void SetConnectionState(ConnectionState state)
  {
    if (_connectionState.Value != state)
    {
      _connectionState.OnNext(state);
    }
  }

  /// <summary>
  /// Update the panel info
  /// </summary>
  public void SetPanelInfo(DigiplexInfo info)
  {
    _panelInfo = info;
    _panelInfoSubject.OnNext(info);
  }

  /// <summary>
  /// Update a zone status
  /// </summary>
  public void UpdateZone(int zoneId, ZoneState state)
  {
    var label = GetLabel("zone", zoneId);
    var newStatus = new ZoneStatus
    {
      ZoneId = zoneId,
      Label = label,
      State = state
    };

    var oldStatus = _zones.GetValueOrDefault(zoneId);
    if (oldStatus?.State != newStatus.State)
    {
      _zones[zoneId] = newStatus;
      _zoneChangedSubject.OnNext(new ZoneChangedEvent(
          zoneId,
          oldStatus ?? new ZoneStatus { ZoneId = zoneId },
          newStatus));
    }
  }

  /// <summary>
  /// Update zone status from raw data bytes
  /// </summary>
  public void UpdateZonesFromData(byte[] data)
  {
    if (data.Length < 12) return;

    var zoneBits = ExtractBits(data[..6]);
    var tamperBits = ExtractBits(data[6..12]);

    for (int i = 0; i < Math.Min(zoneBits.Length, tamperBits.Length); i++)
    {
      var state = ZoneStatus.FromBits(zoneBits[i], tamperBits[i]);
      UpdateZone(i + 1, state);
    }
  }

  /// <summary>
  /// Update a partition status
  /// </summary>
  public void UpdatePartition(int partitionId, PartitionStatus status)
  {
    var oldStatus = _partitions.GetValueOrDefault(partitionId);
    if (oldStatus != status)
    {
      _partitions[partitionId] = status;
      _partitionChangedSubject.OnNext(new PartitionChangedEvent(
          partitionId,
          oldStatus ?? new PartitionStatus { PartitionId = partitionId },
          status));
    }
  }

  /// <summary>
  /// Set a label for an entity
  /// </summary>
  public void SetLabel(string type, int id, string label)
  {
    if (!_labels.ContainsKey(type))
      _labels[type] = new Dictionary<int, string>();

    _labels[type][id] = label;

    // Update zone with new label if applicable
    if (type == "zone" && _zones.TryGetValue(id, out var zone))
    {
      _zones[id] = zone with { Label = label };
    }
  }

  /// <summary>
  /// Get a label for an entity
  /// </summary>
  public string GetLabel(string type, int id)
  {
    if (_labels.TryGetValue(type, out var typeLabels) &&
        typeLabels.TryGetValue(id, out var label))
    {
      return label;
    }
    return $"{type} {id}";
  }

  private static bool[] ExtractBits(byte[] data)
  {
    var bits = new bool[data.Length * 8];
    for (int byteIdx = 0; byteIdx < data.Length; byteIdx++)
    {
      for (int bitIdx = 0; bitIdx < 8; bitIdx++)
      {
        bits[byteIdx * 8 + bitIdx] = ((data[byteIdx] >> bitIdx) & 1) == 1;
      }
    }
    return bits;
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;

    _connectionState.Dispose();
    _panelInfoSubject.Dispose();
    _zoneChangedSubject.Dispose();
    _partitionChangedSubject.Dispose();
  }
}
