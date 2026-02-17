namespace Digiplex.Core.Models.Pdu;

/// <summary>
/// Memory read request PDU
/// </summary>
public record ReadRequestPdu : PduBase
{
  public override byte CommandCode => CommandCodes.Read;

  /// <summary>
  /// Number of bytes to read (0-31, where 0 = 32 bytes)
  /// </summary>
  public byte Count { get; init; }

  /// <summary>
  /// Bus address (7 bits)
  /// </summary>
  public byte BusAddress { get; init; }

  /// <summary>
  /// Memory address. 0x8000 + addr = RAM, 0x0000 + addr = EEPROM
  /// </summary>
  public ushort Address { get; init; }
}

/// <summary>
/// Memory read response PDU
/// </summary>
public record ReadResponsePdu : PduResponse
{
  public override byte CommandCode => CommandCodes.Read;

  public byte BusAddress { get; init; }
  public ushort Address { get; init; }
  public byte[] Data { get; init; } = [];
}

/// <summary>
/// Memory write request PDU
/// </summary>
public record WriteRequestPdu : PduBase
{
  public override byte CommandCode => CommandCodes.Write;

  /// <summary>
  /// Number of bytes to write (0-31, where 0 = 32 bytes)
  /// </summary>
  public byte Count { get; init; }

  /// <summary>
  /// Bus address (7 bits)
  /// </summary>
  public byte BusAddress { get; init; }

  /// <summary>
  /// Memory address
  /// </summary>
  public ushort Address { get; init; }

  /// <summary>
  /// Data to write (1-32 bytes)
  /// </summary>
  public byte[] Data { get; init; } = [];
}

/// <summary>
/// Memory write response PDU
/// </summary>
public record WriteResponsePdu : PduResponse
{
  public override byte CommandCode => CommandCodes.Write;

  public byte BusAddress { get; init; }
  public ushort Address { get; init; }
}
