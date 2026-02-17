namespace Digiplex.Core.Models;

/// <summary>
/// Command codes for Digiplex protocol
/// </summary>
public static class CommandCodes
{
  public const byte Ack = 0x0;
  public const byte Init = 0x0;
  public const byte Login = 0x1;
  public const byte Speed = 0x2;
  public const byte Time = 0x3;
  public const byte Monitor = 0x4;
  public const byte Read = 0x5;   // upload
  public const byte Write = 0x6;  // download
  public const byte Error = 0x7;  // response only
  public const byte SaveEvent = 0x8;
  public const byte Send = 0xA;
  public const byte Broadcast = 0xB;
  public const byte Unlock = 0xC;
  public const byte ZoneChange = 0xD;
  public const byte Event = 0xE;
}

/// <summary>
/// Digiplex version identifiers
/// </summary>
public static class DigiplexVersion
{
  public const byte Version_1_30 = 0x00;
  public const byte Version_2_00 = 0x01;
  public const byte Version_NE = 0x02;
}

/// <summary>
/// Product identifiers
/// </summary>
public static class ProductIds
{
  public const byte Digiplex = 0x00;
  public const byte Spectra = 0x10;
  public const byte Contact = 0x20;
  public const byte EndUserType = 0x55;
}

/// <summary>
/// Monitoring command codes for partition control
/// </summary>
public static class MonitoringCommands
{
  public const byte Nop = 0x0;
  public const byte FullArm = 0x2;
  public const byte StayArm = 0x3;
  public const byte InstantArm = 0x4;
  public const byte ForceArm = 0x5;
  public const byte Disarm = 0x6;
  public const byte Beep = 0x8;
}

/// <summary>
/// Error codes returned by panel
/// </summary>
public static class ErrorCodes
{
  public const byte Command = 0x00;
  public const byte UserCode = 0x01;
  public const byte Partition = 0x02;
}

/// <summary>
/// Memory address utilities
/// </summary>
public static class MemoryAddress
{
  public const ushort RamFlag = 0x8000;

  public static ushort Eeprom(ushort addr) => (ushort)(addr & 0x7FFF);
  public static ushort Ram(ushort addr) => (ushort)((addr & 0x7FFF) | RamFlag);
  public static bool IsRam(ushort addr) => (addr & RamFlag) != 0;
}

/// <summary>
/// Protocol constants
/// </summary>
public static class ProtocolConstants
{
  public const int PacketSize = 37;
  public const int DataSize = 36;
  public const int DefaultBaudRate = 19200;
}
