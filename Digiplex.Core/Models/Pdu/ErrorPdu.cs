namespace Digiplex.Core.Models.Pdu;

/// <summary>
/// Error response PDU
/// </summary>
public record ErrorResponsePdu : PduResponse
{
  public override byte CommandCode => CommandCodes.Error;

  public byte ErrorMessage { get; init; }

  public string GetErrorDescription() => ErrorMessage switch
  {
    ErrorCodes.Command => "Invalid command",
    ErrorCodes.UserCode => "Invalid user code",
    ErrorCodes.Partition => "Invalid partition",
    _ => $"Unknown error: {ErrorMessage}"
  };
}
