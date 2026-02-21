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
  private readonly Subject<PartitionCommand> _partitionCommandSubject = new();
  private readonly Subject<MultiPartitionCommand> _multiPartitionCommandSubject = new();
  private readonly Subject<SystemStatus> _systemStatusSubject = new();
  private readonly Subject<bool> _dataReadySubject = new();

  private readonly Dictionary<int, ZoneStatus> _zones = new();
  private readonly Dictionary<int, PartitionStatus> _partitions = new();
  private readonly Dictionary<string, Dictionary<int, string>> _labels = new();

  private DigiplexInfo? _panelInfo;
  private SystemStatus? _systemStatus;
  private bool _disposed;

  public ConnectionState ConnectionState => _connectionState.Value;
  public IObservable<ConnectionState> ConnectionStateChanged => _connectionState.AsObservable();

  public DigiplexInfo? PanelInfo => _panelInfo;
  public IObservable<DigiplexInfo> PanelInfoChanged => _panelInfoSubject.AsObservable();

  public IReadOnlyDictionary<int, ZoneStatus> Zones => _zones;
  public IObservable<ZoneChangedEvent> ZoneChanged => _zoneChangedSubject.AsObservable();

  public IReadOnlyDictionary<int, PartitionStatus> Partitions => _partitions;
  public IObservable<PartitionChangedEvent> PartitionChanged => _partitionChangedSubject.AsObservable();

  public IObservable<PartitionCommand> PartitionCommandRequested => _partitionCommandSubject.AsObservable();
  public IObservable<MultiPartitionCommand> MultiPartitionCommandRequested => _multiPartitionCommandSubject.AsObservable();

  public SystemStatus? SystemStatus => _systemStatus;
  public IObservable<SystemStatus> SystemStatusChanged => _systemStatusSubject
      .DistinctUntilChanged(s => (s.Vdc, s.BatteryVoltage, s.DcVoltage, s.TroubleFlags));

  public IObservable<bool> DataReady => _dataReadySubject.AsObservable();

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
    if (oldStatus != newStatus)
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
  /// Update partition statuses from raw data bytes (from RAM address 0x8195)
  /// Data layout: 4 partitions at 5-byte blocks starting at Data[0]
  /// </summary>
  public void UpdatePartitionsFromData(byte[] data, int partitionCount)
  {
    // Partition data: 5-byte blocks at offsets 0, 5, 10, 15
    for (var i = 0; i < partitionCount && i < 4; i++)
    {
      var offset = i * 5;
      if (offset + 4 >= data.Length) break;

      var armByte = data[offset];          // byte 0: arm/alarm flags
      var statusByte = data[offset + 1];   // byte 1: ready, delays
      var flagsByte = data[offset + 2];    // byte 2: additional flags

      // Byte 0 bits: armed(0), force_arm(1), stay_arm(2), no_entry(3), strobe_alarm(4), silent_alarm(5), audible_alarm(6)
      var armed = (armByte & 0x01) != 0;
      var forceArm = (armByte & 0x02) != 0;
      var armStay = (armByte & 0x04) != 0;
      var noEntry = (armByte & 0x08) != 0;
      var strobeAlarm = (armByte & 0x10) != 0;
      var silentAlarm = (armByte & 0x20) != 0;
      var audibleAlarm = (armByte & 0x40) != 0;

      // Byte 1 bits: ready(0), exit_delay(1), entry_delay(2), partition_enabled(3), alarm_in_memory(4)
      var ready = (statusByte & 0x01) != 0;
      var exitDelay = (statusByte & 0x02) != 0;
      var entryDelay = (statusByte & 0x04) != 0;
      var alarmInMemory = (statusByte & 0x10) != 0;

      var inAlarm = strobeAlarm || silentAlarm || audibleAlarm;

      var armState = ArmState.Disarmed;
      if (armed)
      {
        if (noEntry) armState = ArmState.InstantArmed;
        else if (armStay) armState = ArmState.StayArmed;
        else if (forceArm) armState = ArmState.ForceArmed;
        else armState = ArmState.Armed;
      }

      var partitionId = i + 1;
      var label = GetLabel("partition", partitionId);

      UpdatePartition(partitionId, new PartitionStatus
      {
        PartitionId = partitionId,
        Label = label,
        ArmState = armState,
        InAlarm = inAlarm,
        Ready = ready,
        ExitDelay = exitDelay,
        EntryDelay = entryDelay,
        AlarmInMemory = alarmInMemory
      });
    }
  }

  /// <summary>
  /// Update system status from raw data bytes (from RAM address 0x8144)
  /// </summary>
  public void UpdateSystemStatus(byte[] data)
  {
    _systemStatus = Models.SystemStatus.ParseFromData(data);
    _systemStatusSubject.OnNext(_systemStatus);
  }

  /// <summary>
  /// Request a partition command (arm/disarm)
  /// </summary>
  public void RequestPartitionCommand(PartitionCommand command)
  {
    _partitionCommandSubject.OnNext(command);
  }

  /// <summary>
  /// Request a multi-partition command (for macro groups)
  /// </summary>
  public void RequestMultiPartitionCommand(MultiPartitionCommand command)
  {
    _multiPartitionCommandSubject.OnNext(command);
  }

  /// <summary>
  /// Signal that initial data (labels, zones, partitions) has been loaded and is ready
  /// </summary>
  public void SignalDataReady()
  {
    _dataReadySubject.OnNext(true);
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
    _partitionCommandSubject.Dispose();
    _multiPartitionCommandSubject.Dispose();
    _systemStatusSubject.Dispose();
    _dataReadySubject.Dispose();
  }
}
