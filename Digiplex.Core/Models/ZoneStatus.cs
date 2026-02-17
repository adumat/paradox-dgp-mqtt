namespace Digiplex.Core.Models;

/// <summary>
/// Status of a zone
/// </summary>
public enum ZoneState
{
  Ok,
  Tamper,
  Open,
  FireLoop
}

/// <summary>
/// Represents the status of a single zone
/// </summary>
public record ZoneStatus
{
  public int ZoneId { get; init; }
  public string Label { get; init; } = string.Empty;
  public ZoneState State { get; init; }

  public static ZoneState FromBits(bool zoneBit, bool tamperBit)
  {
    return (zoneBit, tamperBit) switch
    {
      (false, false) => ZoneState.Ok,
      (false, true) => ZoneState.Tamper,
      (true, false) => ZoneState.Open,
      (true, true) => ZoneState.FireLoop
    };
  }
}

/// <summary>
/// Collection of all zone statuses
/// </summary>
public class ZoneStatusCollection
{
  private readonly Dictionary<int, ZoneStatus> _zones = new();

  public IReadOnlyDictionary<int, ZoneStatus> Zones => _zones;

  public void Update(int zoneId, ZoneStatus status)
  {
    _zones[zoneId] = status;
  }

  public ZoneStatus? GetZone(int zoneId) => _zones.GetValueOrDefault(zoneId);

  public IEnumerable<ZoneStatus> GetOpenZones() =>
      _zones.Values.Where(z => z.State != ZoneState.Ok);
}
