namespace Digiplex.Core.Models.Pdu;

/// <summary>
/// Set panel time request PDU
/// </summary>
public record SetPanelTimeRequestPdu : PduBase
{
  public override byte CommandCode => CommandCodes.Time;

  public byte Address { get; init; }
  public byte Century { get; init; }
  public byte Year { get; init; }
  public byte Month { get; init; }
  public byte Day { get; init; }
}
