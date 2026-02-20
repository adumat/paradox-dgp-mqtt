namespace Digiplex.Core.Models;

/// <summary>
/// System status from RAM address 0x8144
/// </summary>
public record SystemStatus
{
  public DateTime PanelTime { get; init; }
  public double Vdc { get; init; }
  public double BatteryVoltage { get; init; }
  public double DcVoltage { get; init; }
  public byte TroubleFlags { get; init; }

  /// <summary>
  /// Parse system status from ReadResponsePdu.Data (32 bytes from address 0x8144)
  /// </summary>
  public static SystemStatus ParseFromData(byte[] data)
  {
    // Data layout:
    // [0]: trouble flags
    // [1-4]: additional flags / unknown
    // [5]: century (0x14 = 20)
    // [6]: year (0x19 = 25)
    // [7]: month
    // [8]: day
    // [9]: hour
    // [10]: minute
    // [11]: second
    // [12]: VDC raw → 22.4 * raw / 255 (verified against Winload display on DGP-848)
    // [13]: Battery raw → 22.8 * raw / 255
    // [14]: DC output raw → 22.4 * raw / 255 (verified against Winload display on DGP-848)

    var century = data[5];
    var year = data[6];
    var month = data[7];
    var day = data[8];
    var hour = data[9];
    var minute = data[10];
    var second = data[11];

    DateTime panelTime;
    try
    {
      panelTime = new DateTime(century * 100 + year, month, day, hour, minute, second);
    }
    catch
    {
      panelTime = DateTime.MinValue;
    }

    var vdc = Math.Round(22.4 * data[12] / 255.0, 1);
    var battery = Math.Round(22.8 * data[13] / 255.0, 1);
    var dcVoltage = Math.Round(22.4 * data[14] / 255.0, 1);

    return new SystemStatus
    {
      PanelTime = panelTime,
      Vdc = vdc,
      BatteryVoltage = battery,
      DcVoltage = dcVoltage,
      TroubleFlags = data[0]
    };
  }
}
