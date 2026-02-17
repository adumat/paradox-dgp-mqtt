using Digiplex.Core.Models.Pdu;

namespace Digiplex.Core.Protocol;

/// <summary>
/// Interface for encoding and decoding Digiplex protocol PDUs
/// </summary>
public interface IDigiplexCodec
{
  /// <summary>
  /// Encode a PDU to binary data with checksum
  /// </summary>
  byte[] Encode(PduBase pdu);

  /// <summary>
  /// Decode binary data to a PDU (validates checksum)
  /// </summary>
  PduBase Decode(byte[] data);

  /// <summary>
  /// Validate the checksum of received data
  /// </summary>
  bool ValidateChecksum(byte[] data);
}
