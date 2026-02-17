namespace Digiplex.Core.Models.Pdu;

/// <summary>
/// Base class for all Protocol Data Units
/// </summary>
public abstract record PduBase
{
  /// <summary>
  /// The command code for this PDU type
  /// </summary>
  public abstract byte CommandCode { get; }
}

/// <summary>
/// Base class for PDU responses that include a message center field
/// </summary>
public abstract record PduResponse : PduBase
{
  public byte MessageCenter { get; init; }
}
