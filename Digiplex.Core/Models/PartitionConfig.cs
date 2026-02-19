namespace Digiplex.Core.Models;

/// <summary>
/// Configuration for panel partitions and macro groups
/// </summary>
public class PartitionConfig
{
  public PartitionItem[] Items { get; set; } = [];
  public PartitionGroup[] Groups { get; set; } = [];
}

/// <summary>
/// A single partition definition with id and label
/// </summary>
public class PartitionItem
{
  public int Id { get; set; }
  public string Label { get; set; } = string.Empty;
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
