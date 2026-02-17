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
  public string Password { get; set; } = "0000";
  public int PollIntervalMs { get; set; } = 1000;
  public int ReadTimeoutMs { get; set; } = 1000;
  public int MaxRetries { get; set; } = 5;
  public bool UseMock { get; set; }
  public int MockZoneCount { get; set; } = 16;
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
        var readPdu = new ReadRequestPdu
        {
          Count = 12,
          BusAddress = 0,
          Address = MemoryAddress.Ram(0x153)
        };

        var response = await SendAndReceiveAsync(readPdu, cancellationToken);

        if (response is ReadResponsePdu readResponse)
        {
          _state.UpdateZonesFromData(readResponse.Data);
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

  private async Task LoadLabelsAsync(CancellationToken cancellationToken)
  {
    _logger.LogInformation("Loading zone labels");

    // Zone labels are at EEPROM 0x2000-0x22F0, 16 bytes each
    const int labelSize = 16;
    const int startAddress = 0x2000;
    const int endAddress = 0x22F0;

    var zoneIndex = 0;
    for (var addr = startAddress; addr < endAddress; addr += labelSize)
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

    _logger.LogInformation("Loaded {Count} zone labels", zoneIndex);
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    _sendLock.Dispose();
    GC.SuppressFinalize(this);
  }
}
