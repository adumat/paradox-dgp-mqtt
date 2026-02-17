namespace Digiplex.Core.Models;

/// <summary>
/// Information about the connected Digiplex panel
/// </summary>
public record DigiplexInfo
{
  public byte ProductId { get; init; }
  public byte SoftwareVersion { get; init; }
  public byte SoftwareRevision { get; init; }
  public byte SoftwareId { get; init; }
  public byte ModemSpeed { get; init; }
  public byte WinloadTypeId { get; init; }
  public byte MemoryMapVersion { get; init; }
  public byte EventListVersion { get; init; }
  public ushort FirmwareBuildVersion { get; init; }
  public uint ModuleSerialNumber { get; init; }

  public string GetProductName() => ProductId switch
  {
    ProductIds.Digiplex => "Digiplex",
    ProductIds.Spectra => "Spectra",
    ProductIds.Contact => "Contact",
    _ => $"Unknown ({ProductId})"
  };

  public string GetSoftwareVersionString() =>
      $"{SoftwareVersion}.{SoftwareRevision}.{FirmwareBuildVersion}";
}
