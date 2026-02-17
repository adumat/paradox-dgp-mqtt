namespace Digiplex.Core.Models.Pdu;

/// <summary>
/// Monitor request PDU - for partition control commands
/// </summary>
public record MonitorRequestPdu : PduBase
{
  public override byte CommandCode => CommandCodes.Monitor;

  public byte Partition1 { get; init; }
  public byte Partition2 { get; init; }
  public byte Partition3 { get; init; }
  public byte Partition4 { get; init; }
  public byte Partition5 { get; init; }
  public byte Partition6 { get; init; }
  public byte Partition7 { get; init; }
  public byte Partition8 { get; init; }
}

/// <summary>
/// Monitor response PDU - partition status
/// </summary>
public record MonitorResponsePdu : PduResponse
{
  public override byte CommandCode => CommandCodes.Monitor;

  public byte Partition1 { get; init; }
  public byte Partition2 { get; init; }
  public byte Partition3 { get; init; }
  public byte Partition4 { get; init; }
  public byte Partition5 { get; init; }
  public byte Partition6 { get; init; }
  public byte Partition7 { get; init; }
  public byte Partition8 { get; init; }
}
