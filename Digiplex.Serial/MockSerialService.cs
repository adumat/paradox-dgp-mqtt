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
  private IDisposable? _commandSubscription;
  private readonly byte[] _zoneData = new byte[12];
  private readonly byte[] _partitionData = new byte[32];
  private readonly byte[] _mockPartitionStates = new byte[4]; // arm state per partition

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

    var zoneCount = Math.Clamp(_options.ZoneCount, 1, 48);
    for (var i = 0; i < zoneCount; i++)
    {
      _state.SetLabel("zone", i + 1, ZoneNames[i % ZoneNames.Length]);
    }
    _logger.LogInformation("Loaded {Count} mock zone labels", zoneCount);

    _state.UpdateZonesFromData(_zoneData);

    // Set partition labels and initial state (all ready/disarmed = 0x09)
    string[] partitionNames = ["Home", "Garage", "Office", "Shed"];
    var partitionCount = Math.Clamp(_options.PartitionCount, 1, 4);
    for (var i = 0; i < partitionCount; i++)
    {
      _state.SetLabel("partition", i + 1, partitionNames[i % partitionNames.Length]);
      // 0x09 = ready(bit3 of byte3=0x01) + disarmed — match Winload idle capture
      var offset = 1 + (i * 5);
      _partitionData[offset] = 0x00;     // byte 0: not armed
      _partitionData[offset + 3] = 0x01; // byte 3: ready
    }
    _state.UpdatePartitionsFromData(_partitionData, partitionCount);
    _logger.LogInformation("Loaded {Count} mock partition labels", partitionCount);

    // Subscribe to partition commands
    _commandSubscription = _state.PartitionCommandRequested.Subscribe(cmd =>
    {
      HandleMockPartitionCommand(cmd);
    });

    _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    _pollingTask = Task.Run(() => MockPollingLoopAsync(_cts.Token), _cts.Token);
  }

  public async Task StopAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Stopping mock serial service");

    _commandSubscription?.Dispose();
    _commandSubscription = null;

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
        _options.PollIntervalMs, _options.ZoneCount, _options.MockZoneToggleProbability);

    var zoneCount = Math.Clamp(_options.ZoneCount, 1, 48);

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

        // Update partition status
        _state.UpdatePartitionsFromData(_partitionData, Math.Clamp(_options.PartitionCount, 1, 4));

        // Generate mock system status (current time, fixed voltages)
        var now = DateTime.Now;
        var systemData = new byte[32];
        systemData[0] = 0x00; // no trouble
        systemData[5] = (byte)(now.Year / 100);
        systemData[6] = (byte)(now.Year % 100);
        systemData[7] = (byte)now.Month;
        systemData[8] = (byte)now.Day;
        systemData[9] = (byte)now.Hour;
        systemData[10] = (byte)now.Minute;
        systemData[11] = (byte)now.Second;
        systemData[12] = 0xC5; // ~15.7V panel
        systemData[13] = 0x94; // ~13.2V battery
        systemData[14] = 0x97; // DC current
        _state.UpdateSystemStatus(systemData);

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

  private void HandleMockPartitionCommand(PartitionCommand cmd)
  {
    if (cmd.PartitionId < 1 || cmd.PartitionId > 4) return;

    var idx = cmd.PartitionId - 1;
    var offset = 1 + (idx * 5);

    _logger.LogInformation("Mock: partition {Id} command {Cmd}", cmd.PartitionId, cmd.Command);

    switch (cmd.Command)
    {
      case MonitoringCommands.FullArm:
        _partitionData[offset] = 0x01; // armed
        _partitionData[offset + 3] = 0x00; // not ready (armed)
        break;
      case MonitoringCommands.StayArm:
        _partitionData[offset] = 0x05; // armed + stay
        _partitionData[offset + 3] = 0x00;
        break;
      case MonitoringCommands.InstantArm:
        _partitionData[offset] = 0x03; // armed + sleep/instant
        _partitionData[offset + 3] = 0x00;
        break;
      case MonitoringCommands.ForceArm:
        _partitionData[offset] = 0x01; // armed
        _partitionData[offset + 3] = 0x00;
        break;
      case MonitoringCommands.Disarm:
        _partitionData[offset] = 0x00; // disarmed
        _partitionData[offset + 3] = 0x01; // ready
        break;
    }

    // Immediately update state so the next poll reflects the change
    _state.UpdatePartitionsFromData(_partitionData, Math.Clamp(_options.PartitionCount, 1, 4));
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    GC.SuppressFinalize(this);
  }
}
