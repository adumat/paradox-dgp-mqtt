namespace Digiplex.Core.Models.Pdu;

/// <summary>
/// Initialization PDU - sent for handshake and login
/// </summary>
public record InitPdu : PduBase
{
  public override byte CommandCode => CommandCodes.Init;

  public byte Address { get; init; }
  public ushort EepromAddress { get; init; }
  public byte ProductId { get; init; }
  public byte SoftwareVersion { get; init; }
  public byte SoftwareRevision { get; init; }
  public byte SoftwareId { get; init; }
  public ushort Password { get; init; }
  public ushort ModuleId { get; init; }
  public byte WinloadTypeId { get; init; } = ProductIds.EndUserType;
  public byte MemoryMapVersion { get; init; }
  public byte EventListVersion { get; init; }
  public ushort FirmwareBuildVersion { get; init; }
  public uint ModuleSerialNumber { get; init; }
  public uint SectionData { get; init; }
}

/// <summary>
/// Login response PDU
/// </summary>
public record LoginResponsePdu : PduResponse
{
  public override byte CommandCode => CommandCodes.Login;

  public byte Answer { get; init; }
  public ushort Callback { get; init; }
}
