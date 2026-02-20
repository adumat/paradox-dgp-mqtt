namespace Digiplex.Core.Models;

/// <summary>
/// Arm state of a partition
/// </summary>
public enum ArmState
{
  Disarmed,
  Armed,
  StayArmed,
  InstantArmed,
  ForceArmed
}

/// <summary>
/// Represents the status of a single partition
/// </summary>
public record PartitionStatus
{
  public int PartitionId { get; init; }
  public string Label { get; init; } = string.Empty;
  public ArmState ArmState { get; init; }
  public bool InAlarm { get; init; }
  public bool Ready { get; init; }
  public bool ExitDelay { get; init; }
  public bool EntryDelay { get; init; }
  public bool AlarmInMemory { get; init; }
}

/// <summary>
/// Command to send to a partition (arm/disarm)
/// </summary>
public record PartitionCommand(int PartitionId, byte Command);

/// <summary>
/// Command to send to multiple partitions atomically (for macro groups)
/// </summary>
public record MultiPartitionCommand(IReadOnlyDictionary<int, byte> Commands);

/// <summary>
/// Collection of all partition statuses
/// </summary>
public class PartitionStatusCollection
{
  private readonly Dictionary<int, PartitionStatus> _partitions = new();

  public IReadOnlyDictionary<int, PartitionStatus> Partitions => _partitions;

  public void Update(int partitionId, PartitionStatus status)
  {
    _partitions[partitionId] = status;
  }

  public PartitionStatus? GetPartition(int partitionId) => _partitions.GetValueOrDefault(partitionId);
}
