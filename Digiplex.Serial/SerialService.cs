using System.IO.Ports;
using Digiplex.Core.Models;
using Digiplex.Core.Models.Pdu;
using Digiplex.Core.Protocol;
using Digiplex.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Digiplex.Serial;

public class SerialOptions
{
  public string PortName { get; set; } = "/dev/ttyUSB0";
  public int BaudRate { get; set; } = ProtocolConstants.DefaultBaudRate;
  public string Password { get; set; } = "1234";
  public int PollIntervalMs { get; set; } = 1000;
  public int ReadTimeoutMs { get; set; } = 1000;
  public int MaxRetries { get; set; } = 5;
  public int ZoneCount { get; set; } = 48;
  public int PartitionCount { get; set; } = 4;
  public bool UseMock { get; set; }
  public double MockZoneToggleProbability { get; set; } = 0.05;
}

/// <summary>
/// Serial communication service for Digiplex panel
/// </summary>
public class SerialService : ISerialService
{
  private readonly ILogger<SerialService> _logger;
  private readonly SerialOptions _options;
  private readonly IDigiplexCodec _codec;
  private readonly DigiplexState _state;

  private SerialPort? _serialPort;
  private CancellationTokenSource? _cts;
  private Task? _pollingTask;
  private IDisposable? _commandSubscription;
  private readonly SemaphoreSlim _sendLock = new(1, 1);

  private static readonly byte[] InitString =
  [
      0x5F, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x7F
  ];

  public SerialService(
      ILogger<SerialService> logger,
      IOptions<SerialOptions> options,
      IDigiplexCodec codec,
      DigiplexState state)
  {
    _logger = logger;
    _options = options.Value;
    _codec = codec;
    _state = state;
  }

  public async Task StartAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Starting serial service on {Port}", _options.PortName);
    _state.SetConnectionState(ConnectionState.Connecting);

    try
    {
      _serialPort = new SerialPort(
          _options.PortName,
          _options.BaudRate,
          Parity.None,
          8,
          StopBits.One)
      {
        ReadTimeout = _options.ReadTimeoutMs,
        WriteTimeout = _options.ReadTimeoutMs
      };

      _serialPort.Open();
      _serialPort.DiscardInBuffer();
      _serialPort.DiscardOutBuffer();

      _logger.LogInformation("Serial port opened successfully");

      // Initialize connection
      await InitializeConnectionAsync(cancellationToken);

      // Subscribe to partition commands
      _commandSubscription = _state.PartitionCommandRequested.Subscribe(async cmd =>
      {
        try
        {
          await SendPartitionCommandAsync(cmd);
        }
        catch (Exception ex)
        {
          _logger.LogError(ex, "Error sending partition command {Command} to partition {Partition}",
              cmd.Command, cmd.PartitionId);
        }
      });

      // Start polling loop
      _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      _pollingTask = Task.Run(() => PollingLoopAsync(_cts.Token), _cts.Token);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to start serial service");
      _state.SetConnectionState(ConnectionState.Error);
      throw;
    }
  }

  public async Task StopAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Stopping serial service");

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
        catch (OperationCanceledException)
        {
          // Expected
        }
      }
      _cts.Dispose();
      _cts = null;
    }

    if (_serialPort?.IsOpen == true)
    {
      _serialPort.Close();
    }
    _serialPort?.Dispose();
    _serialPort = null;

    _state.SetConnectionState(ConnectionState.Disconnected);
  }

  public async Task SendAsync(PduBase pdu, CancellationToken cancellationToken = default)
  {
    await _sendLock.WaitAsync(cancellationToken);
    try
    {
      var data = _codec.Encode(pdu);
      _logger.LogDebug("TX: {Data}", BitConverter.ToString(data));

      if (_serialPort?.IsOpen != true)
        throw new InvalidOperationException("Serial port not open");

      _serialPort.Write(data, 0, data.Length);
    }
    finally
    {
      _sendLock.Release();
    }
  }

  public async Task<PduBase> SendAndReceiveAsync(PduBase pdu, CancellationToken cancellationToken = default)
  {
    await _sendLock.WaitAsync(cancellationToken);
    try
    {
      var data = _codec.Encode(pdu);
      _logger.LogDebug("TX: {Data}", BitConverter.ToString(data));

      if (_serialPort?.IsOpen != true)
        throw new InvalidOperationException("Serial port not open");

      _serialPort.Write(data, 0, data.Length);

      return await ReceiveAsync(cancellationToken);
    }
    finally
    {
      _sendLock.Release();
    }
  }

  private async Task<PduBase> ReceiveAsync(CancellationToken cancellationToken)
  {
    var buffer = new byte[ProtocolConstants.PacketSize];
    var bytesRead = 0;
    var attempts = 0;

    while (bytesRead < ProtocolConstants.PacketSize && attempts < _options.MaxRetries)
    {
      cancellationToken.ThrowIfCancellationRequested();

      try
      {
        var read = _serialPort!.Read(buffer, bytesRead, ProtocolConstants.PacketSize - bytesRead);
        bytesRead += read;
      }
      catch (TimeoutException)
      {
        attempts++;
        _logger.LogDebug("Read timeout, attempt {Attempt}/{Max}", attempts, _options.MaxRetries);
        await Task.Delay(100, cancellationToken);
      }
    }

    if (bytesRead != ProtocolConstants.PacketSize)
    {
      throw new TimeoutException($"Failed to receive complete packet after {_options.MaxRetries} attempts");
    }

    _logger.LogDebug("RX: {Data}", BitConverter.ToString(buffer));
    return _codec.Decode(buffer);
  }

  private async Task InitializeConnectionAsync(CancellationToken cancellationToken)
  {
    _state.SetConnectionState(ConnectionState.Initializing);

    // Send init string
    _logger.LogDebug("Sending init string");
    _serialPort!.Write(InitString, 0, InitString.Length);

    // Wait for init response
    var initResponse = await ReceiveAsync(cancellationToken);
    if (initResponse is not InitPdu initPdu)
    {
      throw new InvalidOperationException($"Unexpected response type: {initResponse.GetType().Name}");
    }

    _logger.LogInformation("Received init response from panel");

    // Login
    _state.SetConnectionState(ConnectionState.LoggingIn);
    var loginAttempts = 0;

    while (loginAttempts < _options.MaxRetries)
    {
      var loginPdu = new InitPdu
      {
        Password = ushort.Parse(_options.Password)
      };

      _logger.LogDebug("Sending login request, attempt {Attempt}", loginAttempts + 1);
      var response = await SendAndReceiveAsync(loginPdu, cancellationToken);

      if (response is LoginResponsePdu)
      {
        _logger.LogInformation("Login successful");

        // Store panel info
        _state.SetPanelInfo(new DigiplexInfo
        {
          ProductId = initPdu.ProductId,
          SoftwareVersion = initPdu.SoftwareVersion,
          SoftwareRevision = initPdu.SoftwareRevision,
          SoftwareId = initPdu.SoftwareId,
          WinloadTypeId = initPdu.WinloadTypeId,
          MemoryMapVersion = initPdu.MemoryMapVersion,
          EventListVersion = initPdu.EventListVersion,
          FirmwareBuildVersion = initPdu.FirmwareBuildVersion,
          ModuleSerialNumber = initPdu.ModuleSerialNumber
        });

        _state.SetConnectionState(ConnectionState.Connected);
        return;
      }

      loginAttempts++;
      await Task.Delay(500, cancellationToken);
    }

    throw new InvalidOperationException("Login failed after max attempts");
  }

  private async Task PollingLoopAsync(CancellationToken cancellationToken)
  {
    _logger.LogInformation("Starting polling loop");

    // First load labels
    await LoadLabelsAsync(cancellationToken);

    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        // Poll zone status at RAM address 0x153
        var zonePdu = new ReadRequestPdu
        {
          Count = 12,
          BusAddress = 0,
          Address = MemoryAddress.Ram(0x153)
        };

        var zoneResponse = await SendAndReceiveAsync(zonePdu, cancellationToken);
        if (zoneResponse is ReadResponsePdu zoneData)
        {
          _state.UpdateZonesFromData(zoneData.Data);
        }

        // Poll partition status at RAM address 0x195
        var partitionPdu = new ReadRequestPdu
        {
          Count = 0,
          BusAddress = 0,
          Address = MemoryAddress.Ram(0x195)
        };

        var partitionResponse = await SendAndReceiveAsync(partitionPdu, cancellationToken);
        if (partitionResponse is ReadResponsePdu partitionData)
        {
          _logger.LogDebug("Partition raw: {Data}", BitConverter.ToString(partitionData.Data[..21]));
          _state.UpdatePartitionsFromData(partitionData.Data, _options.PartitionCount);
        }

        // Poll system info at RAM address 0x144
        var systemPdu = new ReadRequestPdu
        {
          Count = 0,
          BusAddress = 0,
          Address = MemoryAddress.Ram(0x144)
        };

        var systemResponse = await SendAndReceiveAsync(systemPdu, cancellationToken);
        if (systemResponse is ReadResponsePdu systemData)
        {
          _state.UpdateSystemStatus(systemData.Data);
        }

        await Task.Delay(_options.PollIntervalMs, cancellationToken);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error in polling loop");
        _state.SetConnectionState(ConnectionState.Error);
        await Task.Delay(5000, cancellationToken);
      }
    }

    _logger.LogInformation("Polling loop stopped");
  }

  private async Task SendPartitionCommandAsync(PartitionCommand command)
  {
    _logger.LogInformation("Sending partition command {Command} to partition {Partition}",
        command.Command, command.PartitionId);

    // Build MonitorRequestPdu with command in the correct partition slot
    var pdu = new MonitorRequestPdu
    {
      Partition1 = command.PartitionId == 1 ? command.Command : MonitoringCommands.Nop,
      Partition2 = command.PartitionId == 2 ? command.Command : MonitoringCommands.Nop,
      Partition3 = command.PartitionId == 3 ? command.Command : MonitoringCommands.Nop,
      Partition4 = command.PartitionId == 4 ? command.Command : MonitoringCommands.Nop,
    };

    var response = await SendAndReceiveAsync(pdu);

    if (response is MonitorResponsePdu monitorResponse)
    {
      _logger.LogDebug("Partition command response: P1={P1} P2={P2} P3={P3} P4={P4}",
          monitorResponse.Partition1, monitorResponse.Partition2,
          monitorResponse.Partition3, monitorResponse.Partition4);
    }
    else if (response is ErrorResponsePdu errorResponse)
    {
      _logger.LogWarning("Partition command error: {Error}", errorResponse.ErrorMessage);
    }
  }

  private async Task LoadLabelsAsync(CancellationToken cancellationToken)
  {
    _logger.LogInformation("Loading zone labels");

    // Zone labels are at EEPROM 0x2000, 16 bytes each
    const int labelSize = 16;
    const int startAddress = 0x2000;
    var zoneCount = Math.Clamp(_options.ZoneCount, 1, 48);

    var zoneIndex = 1;
    for (var addr = startAddress; zoneIndex <= zoneCount; addr += labelSize)
    {
      var readPdu = new ReadRequestPdu
      {
        Count = (byte)labelSize,
        BusAddress = 0,
        Address = (ushort)addr
      };

      var response = await SendAndReceiveAsync(readPdu, cancellationToken);

      if (response is ReadResponsePdu readResponse)
      {
        var label = System.Text.Encoding.ASCII
            .GetString(readResponse.Data[..labelSize])
            .TrimEnd('\0', ' ');

        if (!string.IsNullOrWhiteSpace(label))
        {
          _state.SetLabel("zone", zoneIndex, label);
          _logger.LogDebug("Zone {Index}: {Label}", zoneIndex, label);
        }
      }

      zoneIndex++;
    }

    _logger.LogInformation("Loaded {Count} zone labels", zoneIndex - 1);

    // Set default partition labels (EEPROM address unknown for DGP-848)
    for (var i = 1; i <= _options.PartitionCount; i++)
    {
      _state.SetLabel("partition", i, $"Partition {i}");
    }
    _logger.LogInformation("Set {Count} default partition labels", _options.PartitionCount);
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    _sendLock.Dispose();
    GC.SuppressFinalize(this);
  }
}
