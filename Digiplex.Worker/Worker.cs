using Digiplex.Core.Services;
using Digiplex.Mqtt;
using Digiplex.Serial;

namespace Digiplex.Worker;

public class DigiplexWorker : BackgroundService
{
  private readonly ILogger<DigiplexWorker> _logger;
  private readonly ISerialService _serialService;
  private readonly IMqttService _mqttService;
  private readonly IDigiplexState _state;

  public DigiplexWorker(
      ILogger<DigiplexWorker> logger,
      ISerialService serialService,
      IMqttService mqttService,
      IDigiplexState state)
  {
    _logger = logger;
    _serialService = serialService;
    _mqttService = mqttService;
    _state = state;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation("Digiplex worker starting");

    // Subscribe to state changes for logging
    _state.ConnectionStateChanged.Subscribe(state =>
    {
      _logger.LogInformation("Connection state changed: {State}", state);
    });

    _state.ZoneChanged.Subscribe(evt =>
    {
      if (evt.NewStatus.State != Core.Models.ZoneState.Ok)
      {
        _logger.LogInformation("Zone {Id} ({Label}): {State}",
                evt.ZoneId, evt.NewStatus.Label, evt.NewStatus.State);
      }
    });

    _state.PartitionChanged.Subscribe(evt =>
    {
      _logger.LogInformation("Partition {Id} ({Label}): {ArmState}, Alarm={Alarm}, Ready={Ready}",
              evt.PartitionId, evt.NewStatus.Label, evt.NewStatus.ArmState,
              evt.NewStatus.InAlarm, evt.NewStatus.Ready);
    });

    try
    {
      // Start MQTT service first
      await _mqttService.StartAsync(stoppingToken);
      _logger.LogInformation("MQTT service started");

      // Start serial service
      await _serialService.StartAsync(stoppingToken);
      _logger.LogInformation("Serial service started");

      // Keep running until cancellation
      await Task.Delay(Timeout.Infinite, stoppingToken);
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
    {
      _logger.LogInformation("Worker shutdown requested");
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Worker error");
      throw;
    }
    finally
    {
      _logger.LogInformation("Stopping services");

      try
      {
        await _serialService.StopAsync(CancellationToken.None);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error stopping serial service");
      }

      try
      {
        await _mqttService.StopAsync(CancellationToken.None);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error stopping MQTT service");
      }
    }
  }
}
