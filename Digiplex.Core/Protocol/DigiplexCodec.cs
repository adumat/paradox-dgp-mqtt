using System.Buffers.Binary;
using Digiplex.Core.Models;
using Digiplex.Core.Models.Pdu;

namespace Digiplex.Core.Protocol;

/// <summary>
/// Encodes and decodes Digiplex protocol PDUs
/// </summary>
public class DigiplexCodec : IDigiplexCodec
{
  /// <inheritdoc />
  public byte[] Encode(PduBase pdu)
  {
    var data = pdu switch
    {
      InitPdu init => EncodeInit(init),
      SpeedRequestPdu speed => EncodeSpeedRequest(speed),
      SetPanelTimeRequestPdu time => EncodeSetPanelTime(time),
      MonitorRequestPdu monitor => EncodeMonitorRequest(monitor),
      ReadRequestPdu read => EncodeReadRequest(read),
      WriteRequestPdu write => EncodeWriteRequest(write),
      EventRequestPdu evt => EncodeEventRequest(evt),
      _ => throw new ArgumentException($"Unknown PDU type: {pdu.GetType().Name}")
    };

    return AddChecksum(data);
  }

  /// <inheritdoc />
  public PduBase Decode(byte[] data)
  {
    if (data.Length != ProtocolConstants.PacketSize)
      throw new ArgumentException($"Invalid packet size: {data.Length}, expected {ProtocolConstants.PacketSize}");

    if (!ValidateChecksum(data))
      throw new InvalidOperationException("Checksum validation failed");

    var msgType = (byte)((data[0] >> 4) & 0x0F);

    return msgType switch
    {
      CommandCodes.Init => DecodeInit(data),
      CommandCodes.Login => DecodeLoginResponse(data),
      CommandCodes.Monitor => DecodeMonitorResponse(data),
      CommandCodes.Read => DecodeReadResponse(data),
      CommandCodes.Write => DecodeWriteResponse(data),
      CommandCodes.Error => DecodeErrorResponse(data),
      CommandCodes.Event => DecodeEventResponse(data),
      _ => throw new ArgumentException($"Unknown message type: {msgType}")
    };
  }

  /// <inheritdoc />
  public bool ValidateChecksum(byte[] data)
  {
    if (data.Length != ProtocolConstants.PacketSize)
      return false;

    var sum = 0;
    for (int i = 0; i < ProtocolConstants.DataSize; i++)
      sum += data[i];

    return (byte)(sum & 0xFF) == data[ProtocolConstants.DataSize];
  }

  private static byte[] AddChecksum(byte[] data)
  {
    var padded = new byte[ProtocolConstants.PacketSize];
    Array.Copy(data, padded, Math.Min(data.Length, ProtocolConstants.DataSize));

    var sum = 0;
    for (int i = 0; i < ProtocolConstants.DataSize; i++)
      sum += padded[i];

    padded[ProtocolConstants.DataSize] = (byte)(sum & 0xFF);
    return padded;
  }

  #region Encoding

  private static byte[] EncodeInit(InitPdu init)
  {
    var data = new byte[25];
    data[0] = (byte)(CommandCodes.Init << 4);
    data[1] = init.Address;
    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), init.EepromAddress);
    data[4] = init.ProductId;
    data[5] = init.SoftwareVersion;
    data[6] = init.SoftwareRevision;
    data[7] = init.SoftwareId;
    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), init.Password);
    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10), init.ModuleId);
    data[12] = init.WinloadTypeId;
    data[13] = init.MemoryMapVersion;
    data[14] = init.EventListVersion;
    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(15), init.FirmwareBuildVersion);
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(17), init.ModuleSerialNumber);
    BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(21), init.SectionData);
    return data;
  }

  private static byte[] EncodeSpeedRequest(SpeedRequestPdu speed)
  {
    return [(byte)(CommandCodes.Speed << 4), 0, speed.Speed];
  }

  private static byte[] EncodeSetPanelTime(SetPanelTimeRequestPdu time)
  {
    return [
        (byte)(CommandCodes.Time << 4),
            time.Address,
            0,
            0,
            time.Century,
            time.Year,
            time.Month,
            time.Day
    ];
  }

  private static byte[] EncodeMonitorRequest(MonitorRequestPdu monitor)
  {
    var packed = new byte[]
    {
            (byte)(((monitor.Partition1 & 0xF) << 4) | (monitor.Partition2 & 0xF)),
            (byte)(((monitor.Partition3 & 0xF) << 4) | (monitor.Partition4 & 0xF)),
            (byte)(((monitor.Partition5 & 0xF) << 4) | (monitor.Partition6 & 0xF)),
            (byte)(((monitor.Partition7 & 0xF) << 4) | (monitor.Partition8 & 0xF))
    };

    return [(byte)(CommandCodes.Monitor << 4), 0, packed[0], packed[1], packed[2], packed[3]];
  }

  private static byte[] EncodeReadRequest(ReadRequestPdu read)
  {
    var count = (byte)(read.Count & 0x1F);
    var busAddress = (byte)(read.BusAddress & 0x7F);

    var b0 = (byte)((CommandCodes.Read << 4) | ((count & 0x1F) >> 1));
    var b1 = (byte)(((count & 0x1) << 7) | busAddress);

    var data = new byte[4];
    data[0] = b0;
    data[1] = b1;
    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), read.Address);
    return data;
  }

  private static byte[] EncodeWriteRequest(WriteRequestPdu write)
  {
    var count = (byte)((write.Count > 0 ? write.Count : write.Data.Length) & 0x1F);
    var busAddress = (byte)(write.BusAddress & 0x7F);

    var b0 = (byte)((CommandCodes.Write << 4) | ((count & 0x1F) >> 1));
    var b1 = (byte)(((count & 0x1) << 7) | busAddress);

    var data = new byte[4 + write.Data.Length];
    data[0] = b0;
    data[1] = b1;
    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), write.Address);
    Array.Copy(write.Data, 0, data, 4, write.Data.Length);
    return data;
  }

  private static byte[] EncodeEventRequest(EventRequestPdu evt)
  {
    var data = new byte[4];
    data[0] = (byte)(CommandCodes.Event << 4);
    data[1] = 0;
    BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), evt.EventRequestNumber);
    return data;
  }

  #endregion

  #region Decoding

  private static InitPdu DecodeInit(byte[] data)
  {
    return new InitPdu
    {
      Address = data[1],
      EepromAddress = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)),
      ProductId = data[4],
      SoftwareVersion = data[5],
      SoftwareRevision = data[6],
      SoftwareId = data[7],
      Password = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8)),
      ModuleId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10)),
      WinloadTypeId = data[12],
      MemoryMapVersion = data[13],
      EventListVersion = data[14],
      FirmwareBuildVersion = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(15)),
      ModuleSerialNumber = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(17)),
      SectionData = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(21))
    };
  }

  private static LoginResponsePdu DecodeLoginResponse(byte[] data)
  {
    return new LoginResponsePdu
    {
      MessageCenter = (byte)(data[0] & 0x0F),
      Answer = (byte)((data[1] >> 4) & 0x0F),
      Callback = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2))
    };
  }

  private static MonitorResponsePdu DecodeMonitorResponse(byte[] data)
  {
    return new MonitorResponsePdu
    {
      MessageCenter = (byte)(data[0] & 0x0F),
      Partition1 = (byte)((data[2] >> 4) & 0x0F),
      Partition2 = (byte)(data[2] & 0x0F),
      Partition3 = (byte)((data[3] >> 4) & 0x0F),
      Partition4 = (byte)(data[3] & 0x0F),
      Partition5 = (byte)((data[4] >> 4) & 0x0F),
      Partition6 = (byte)(data[4] & 0x0F),
      Partition7 = (byte)((data[5] >> 4) & 0x0F),
      Partition8 = (byte)(data[5] & 0x0F)
    };
  }

  private static ReadResponsePdu DecodeReadResponse(byte[] data)
  {
    var responseData = new byte[ProtocolConstants.DataSize - 4];
    Array.Copy(data, 4, responseData, 0, responseData.Length);

    return new ReadResponsePdu
    {
      MessageCenter = (byte)(data[0] & 0x0F),
      BusAddress = data[1],
      Address = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)),
      Data = responseData
    };
  }

  private static WriteResponsePdu DecodeWriteResponse(byte[] data)
  {
    return new WriteResponsePdu
    {
      MessageCenter = (byte)(data[0] & 0x0F),
      BusAddress = data[1],
      Address = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2))
    };
  }

  private static ErrorResponsePdu DecodeErrorResponse(byte[] data)
  {
    return new ErrorResponsePdu
    {
      MessageCenter = (byte)(data[0] & 0x0F),
      ErrorMessage = data[1]
    };
  }

  private static EventResponsePdu DecodeEventResponse(byte[] data)
  {
    var century = data[2];
    var year = data[3];
    var month = data[4];
    var day = data[5];
    var hour = data[6];
    var minute = data[7];

    var timestamp = new DateTime(century * 100 + year, month, day, hour, minute, 0);

    var eventData = new byte[ProtocolConstants.DataSize - 16];
    Array.Copy(data, 16, eventData, 0, eventData.Length);

    return new EventResponsePdu
    {
      MessageCenter = (byte)(data[0] & 0x0F),
      EventRequestNumber = data[1],
      Timestamp = timestamp,
      EventGroup = data[8],
      Partition1 = (byte)((data[9] >> 4) & 0x0F),
      Partition2 = (byte)(data[9] & 0x0F),
      EventNumber1 = data[10],
      EventNumber2 = data[11],
      Serial = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12)),
      EventData = eventData
    };
  }

  #endregion
}
