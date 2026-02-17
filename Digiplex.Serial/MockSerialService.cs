using Digiplex.Core.Models;
using Digiplex.Core.Models.Pdu;
using Digiplex.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Digiplex.Serial;

public class MockSerialService : ISerialService
{
  private readonly ILogger<MockSerialService> _logger;
  private readonly SerialOptions _options;
  private readonly DigiplexState _state;
  private readonly Random _random = new();

  private CancellationTokenSource? _cts;
  private Task? _pollingTask;
  private readonly byte[] _zoneData = new byte[12];

  private static readonly string[] ZoneNames =
  [
      "Front Door", "Back Door", "Garage Door", "Kitchen Window",
        "Living Room", "Master Bedroom", "Hallway", "Bathroom",
        "Study", "Laundry", "Patio Door", "Side Gate",
        "Basement", "Attic Access", "Office", "Guest Room",
        "Dining Room", "Stairwell", "Storage Room", "Balcony",
        "Pantry", "Utility Room", "Shed", "Driveway",
        "Front Yard PIR", "Back Yard PIR", "Pool Area", "Garden Shed",
        "Wine Cellar", "Server Room", "Nursery", "Walk-in Closet",
        "Mud Room", "Sunroom", "Conservatory", "Landing",
        "Ensuite", "Guest Bath", "Foyer", "Den",
        "Workshop", "Gym", "Media Room", "Playroom",
        "Sauna", "Terrace", "Veranda", "Loft"
  ];

  public MockSerialService(
      ILogger<MockSerialService> logger,
      IOptions<SerialOptions> options,
      DigiplexState state)
  {
    _logger = logger;
    _options = options.Value;
    _state = state;
  }

  public async Task StartAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Starting MOCK serial service (simulated panel)");

    _state.SetConnectionState(ConnectionState.Connecting);
    await Task.Delay(200, cancellationToken);

    _state.SetConnectionState(ConnectionState.Initializing);
    await Task.Delay(300, cancellationToken);

    _state.SetConnectionState(ConnectionState.LoggingIn);
    await Task.Delay(200, cancellationToken);

    _state.SetPanelInfo(new DigiplexInfo
    {
      ProductId = ProductIds.Digiplex,
      SoftwareVersion = 4,
      SoftwareRevision = 20,
      SoftwareId = 0,
      WinloadTypeId = ProductIds.EndUserType,
      MemoryMapVersion = 1,
      EventListVersion = 1,
      FirmwareBuildVersion = 100,
      ModuleSerialNumber = 999999
    });

    _state.SetConnectionState(ConnectionState.Connected);
    _logger.LogInformation("Mock panel connected (simulating Digiplex v4.20.100)");

    var zoneCount = Math.Clamp(_options.MockZoneCount, 1, 48);
    for (var i = 0; i < zoneCount; i++)
    {
      _state.SetLabel("zone", i + 1, ZoneNames[i % ZoneNames.Length]);
    }
    _logger.LogInformation("Loaded {Count} mock zone labels", zoneCount);

    _state.UpdateZonesFromData(_zoneData);

    _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    _pollingTask = Task.Run(() => MockPollingLoopAsync(_cts.Token), _cts.Token);
  }

  public async Task StopAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Stopping mock serial service");

    if (_cts != null)
    {
      await _cts.CancelAsync();
      if (_pollingTask != null)
      {
        try
        {
          await _pollingTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (OperationCanceledException) { }
      }
      _cts.Dispose();
      _cts = null;
    }

    try { _state.SetConnectionState(ConnectionState.Disconnected); }
    catch (ObjectDisposedException) { }
  }

  public Task SendAsync(PduBase pdu, CancellationToken cancellationToken = default)
  {
    _logger.LogDebug("Mock TX: {PduType} (ignored)", pdu.GetType().Name);
    return Task.CompletedTask;
  }

  public Task<PduBase> SendAndReceiveAsync(PduBase pdu, CancellationToken cancellationToken = default)
  {
    _logger.LogDebug("Mock TX/RX: {PduType} (returning empty read response)", pdu.GetType().Name);

    PduBase response = new ReadResponsePdu
    {
      BusAddress = 0,
      Address = 0,
      Data = new byte[32]
    };

    return Task.FromResult(response);
  }

  private async Task MockPollingLoopAsync(CancellationToken cancellationToken)
  {
    _logger.LogInformation("Starting mock polling loop (interval: {Interval}ms, zones: {Zones}, toggle probability: {Prob})",
        _options.PollIntervalMs, _options.MockZoneCount, _options.MockZoneToggleProbability);

    var zoneCount = Math.Clamp(_options.MockZoneCount, 1, 48);

    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        for (var i = 0; i < zoneCount; i++)
        {
          if (_random.NextDouble() < _options.MockZoneToggleProbability)
          {
            var byteIndex = i / 8;
            var bitIndex = i % 8;
            _zoneData[byteIndex] ^= (byte)(1 << bitIndex);
          }
        }

        _state.UpdateZonesFromData(_zoneData);

        await Task.Delay(_options.PollIntervalMs, cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error in mock polling loop");
        await Task.Delay(5000, cancellationToken);
      }
    }

    _logger.LogInformation("Mock polling loop stopped");
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    GC.SuppressFinalize(this);
  }
}
