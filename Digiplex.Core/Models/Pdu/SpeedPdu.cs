namespace Digiplex.Core.Models.Pdu;

/// <summary>
/// Speed change request PDU
/// </summary>
public record SpeedRequestPdu : PduBase
{
  public override byte CommandCode => CommandCodes.Speed;

  public byte Speed { get; init; }
}

/// <summary>
/// Speed change response PDU
/// </summary>
public record SpeedResponsePdu : PduResponse
{
  public override byte CommandCode => CommandCodes.Speed;

  public byte Speed { get; init; }
}
