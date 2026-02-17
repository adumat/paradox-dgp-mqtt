namespace Digiplex.Core.Models.Pdu;

/// <summary>
/// Event request PDU
/// </summary>
public record EventRequestPdu : PduBase
{
  public override byte CommandCode => CommandCodes.Event;

  public ushort EventRequestNumber { get; init; }
}

/// <summary>
/// Event response PDU
/// </summary>
public record EventResponsePdu : PduResponse
{
  public override byte CommandCode => CommandCodes.Event;

  public byte EventRequestNumber { get; init; }
  public DateTime Timestamp { get; init; }
  public byte EventGroup { get; init; }
  public byte Partition1 { get; init; }
  public byte Partition2 { get; init; }
  public byte EventNumber1 { get; init; }
  public byte EventNumber2 { get; init; }
  public uint Serial { get; init; }
  public byte[] EventData { get; init; } = [];
}
