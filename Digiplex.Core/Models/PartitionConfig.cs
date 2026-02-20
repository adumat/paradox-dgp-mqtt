namespace Digiplex.Core.Models;

/// <summary>
/// Configuration for panel partitions and macro groups
/// </summary>
public class PartitionConfig
{
  public PartitionItem[] Items { get; set; } = [];
  public PartitionGroup[] Groups { get; set; } = [];
  public ZoneOverride[] ZoneOverrides { get; set; } = [];
}

/// <summary>
/// A single partition definition with id, label, and zone assignments
/// </summary>
public class PartitionItem
{
  public int Id { get; set; }
  public string Label { get; set; } = string.Empty;
  /// <summary>HA binary_sensor device_class for zones in this partition (default: "motion")</summary>
  public string DeviceClass { get; set; } = "motion";
  /// <summary>Zone IDs belonging to this partition</summary>
  public int[] ZoneIds { get; set; } = [];
}

/// <summary>
/// Per-zone device_class override (takes precedence over partition default)
/// </summary>
public class ZoneOverride
{
  public int Id { get; set; }
  public string DeviceClass { get; set; } = "motion";
}

/// <summary>
/// A macro group that controls multiple partitions together
/// </summary>
public class PartitionGroup
{
  public string Id { get; set; } = string.Empty;
  public string Label { get; set; } = string.Empty;
  public int[] PartitionIds { get; set; } = [];
}
