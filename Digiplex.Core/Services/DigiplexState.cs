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
  private readonly Subject<SystemStatus> _systemStatusSubject = new();

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

  public SystemStatus? SystemStatus => _systemStatus;
  public IObservable<SystemStatus> SystemStatusChanged => _systemStatusSubject
      .DistinctUntilChanged(s => (s.Vdc, s.BatteryVoltage, s.DcCurrent, s.TroubleFlags));

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
  /// Update partition statuses from raw data bytes (from RAM address 0x8195)
  /// Data layout: Data[0]=header, then 4 partitions at 5-byte intervals starting at Data[1]
  /// </summary>
  public void UpdatePartitionsFromData(byte[] data, int partitionCount)
  {
    // Partition data: 5-byte blocks at offsets 1, 6, 11, 16
    for (var i = 0; i < partitionCount && i < 4; i++)
    {
      var offset = 1 + (i * 5);
      if (offset >= data.Length) break;

      var statusByte0 = data[offset];
      var statusByte1 = offset + 1 < data.Length ? data[offset + 1] : (byte)0;
      var statusByte3 = offset + 3 < data.Length ? data[offset + 3] : (byte)0;

      // Byte 0 bits: arm(0), arm_sleep(1), arm_stay(2), [3], strobe_alarm(4), silent_alarm(5), audible_alarm(6), pulse_fire_alarm(7)
      var armed = (statusByte0 & 0x01) != 0;
      var armSleep = (statusByte0 & 0x02) != 0;
      var armStay = (statusByte0 & 0x04) != 0;
      var strobeAlarm = (statusByte0 & 0x10) != 0;
      var silentAlarm = (statusByte0 & 0x20) != 0;
      var audibleAlarm = (statusByte0 & 0x40) != 0;

      // Byte 1 bits: exit_delay(0), entry_delay(1), alarms_in_memory(2), zone_bypassed(3)
      var exitDelay = (statusByte1 & 0x01) != 0;
      var entryDelay = (statusByte1 & 0x02) != 0;

      // Byte 3 bits: ready_status(0), arm_force(1), stay_mode_active(2)
      var ready = (statusByte3 & 0x01) != 0;

      var inAlarm = strobeAlarm || silentAlarm || audibleAlarm;

      var armState = ArmState.Disarmed;
      if (armed)
      {
        if (armStay) armState = ArmState.StayArmed;
        else if (armSleep) armState = ArmState.InstantArmed;
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
        EntryDelay = entryDelay
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
    _systemStatusSubject.Dispose();
  }
}
