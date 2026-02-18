using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Digiplex.Core.Models;
using Digiplex.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace Digiplex.Mqtt;

public class MqttOptions
{
  public string BrokerHost { get; set; } = "localhost";
  public int BrokerPort { get; set; } = 1883;
  public string? Username { get; set; }
  public string? Password { get; set; }
  public string ClientId { get; set; } = "digiplex";
  public string TopicPrefix { get; set; } = "digiplex";
  public bool EnableHomeAssistantDiscovery { get; set; } = true;
  public string HomeAssistantDiscoveryPrefix { get; set; } = "homeassistant";
}

/// <summary>
/// MQTT service for publishing Digiplex state and receiving commands
/// </summary>
public class MqttService : IMqttService
{
  private readonly ILogger<MqttService> _logger;
  private readonly MqttOptions _options;
  private readonly DigiplexState _state;

  private IMqttClient? _client;
  private CancellationTokenSource? _cts;
  private readonly List<IDisposable> _subscriptions = [];

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = false,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  public bool IsConnected => _client?.IsConnected ?? false;

  public MqttService(
      ILogger<MqttService> logger,
      IOptions<MqttOptions> options,
      DigiplexState state)
  {
    _logger = logger;
    _options = options.Value;
    _state = state;
  }

  public async Task StartAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Starting MQTT service, connecting to {Host}:{Port}",
        _options.BrokerHost, _options.BrokerPort);

    _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

    var factory = new MqttClientFactory();
    _client = factory.CreateMqttClient();

    _client.DisconnectedAsync += OnDisconnectedAsync;
    _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

    await ConnectAsync(cancellationToken);
    SubscribeToStateChanges();
  }

  public async Task StopAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Stopping MQTT service");

    foreach (var subscription in _subscriptions)
    {
      subscription.Dispose();
    }
    _subscriptions.Clear();

    if (_client?.IsConnected == true)
    {
      // Publish offline status
      await PublishAsync($"{_options.TopicPrefix}/status", "offline", true, cancellationToken);

      var disconnectOptions = new MqttClientDisconnectOptionsBuilder()
          .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
          .Build();

      await _client.DisconnectAsync(disconnectOptions, cancellationToken);
    }

    _client?.Dispose();
    _client = null;

    _cts?.Cancel();
    _cts?.Dispose();
    _cts = null;
  }

  private async Task ConnectAsync(CancellationToken cancellationToken)
  {
    var optionsBuilder = new MqttClientOptionsBuilder()
        .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
        .WithTlsOptions(new MqttClientTlsOptions
        {
          UseTls = _options.BrokerPort == 8883, // Assume TLS if using standard secure MQTT port
          AllowUntrustedCertificates = false, // Adjust as needed for production
        })
        .WithClientId(_options.ClientId)
        .WithWillTopic($"{_options.TopicPrefix}/status")
        .WithWillPayload("offline"u8.ToArray())
        .WithWillRetain(true)
        .WithCleanSession(true);

    if (!string.IsNullOrEmpty(_options.Username))
    {
      optionsBuilder.WithCredentials(_options.Username, _options.Password);
    }

    var options = optionsBuilder.Build();

    try
    {
      await _client!.ConnectAsync(options, cancellationToken);
      _logger.LogInformation("Connected to MQTT broker");

      // Publish online status
      await PublishAsync($"{_options.TopicPrefix}/status", "online", true, cancellationToken);

      // Subscribe to command topics
      await SubscribeToCommandsAsync(cancellationToken);

      // Publish Home Assistant discovery if enabled
      if (_options.EnableHomeAssistantDiscovery)
      {
        await PublishHomeAssistantDiscoveryAsync(cancellationToken);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to connect to MQTT broker");
      throw;
    }
  }

  private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
  {
    if (_cts?.IsCancellationRequested == true)
      return;

    _logger.LogWarning("Disconnected from MQTT broker: {Reason}", args.Reason);

    // Attempt reconnection
    await Task.Delay(5000);

    try
    {
      if (_cts?.IsCancellationRequested == false)
      {
        await ConnectAsync(_cts.Token);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Reconnection failed");
    }
  }

  private Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
  {
    var topic = args.ApplicationMessage.Topic;
    var payloadBytes = args.ApplicationMessage.Payload;
    var payload = payloadBytes.Length > 0 ? Encoding.UTF8.GetString(payloadBytes) : string.Empty;

    _logger.LogDebug("Received message on {Topic}: {Payload}", topic, payload);

    // Handle partition commands: {prefix}/partition/{id}/set
    var prefix = $"{_options.TopicPrefix}/partition/";
    if (topic.StartsWith(prefix) && topic.EndsWith("/set"))
    {
      var idStr = topic[prefix.Length..^4]; // extract id between prefix and /set
      if (int.TryParse(idStr, out var partitionId) && partitionId >= 1 && partitionId <= 8)
      {
        var command = MapHaCommandToMonitoring(payload);
        if (command.HasValue)
        {
          _logger.LogInformation("Partition {Id} command: {Payload} → 0x{Cmd:X2}",
              partitionId, payload, command.Value);
          _state.RequestPartitionCommand(new PartitionCommand(partitionId, command.Value));
        }
        else
        {
          _logger.LogWarning("Unknown partition command: {Payload}", payload);
        }
      }
    }

    return Task.CompletedTask;
  }

  private static byte? MapHaCommandToMonitoring(string haCommand)
  {
    return haCommand.ToUpperInvariant() switch
    {
      "ARM_AWAY" => MonitoringCommands.FullArm,
      "ARM_HOME" => MonitoringCommands.StayArm,
      "ARM_NIGHT" => MonitoringCommands.InstantArm,
      "ARM_CUSTOM_BYPASS" => MonitoringCommands.ForceArm,
      "DISARM" => MonitoringCommands.Disarm,
      _ => null
    };
  }

  private static string MapToHaState(PartitionStatus status)
  {
    if (status.InAlarm) return "triggered";
    if (status.ExitDelay) return "arming";
    if (status.EntryDelay) return "pending";

    return status.ArmState switch
    {
      ArmState.Armed => "armed_away",
      ArmState.StayArmed => "armed_home",
      ArmState.InstantArmed => "armed_night",
      ArmState.ForceArmed => "armed_custom_bypass",
      _ => "disarmed"
    };
  }

  private async Task SubscribeToCommandsAsync(CancellationToken cancellationToken)
  {
    var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
        .WithTopicFilter($"{_options.TopicPrefix}/+/+/set")
        .Build();

    await _client!.SubscribeAsync(subscribeOptions, cancellationToken);
    _logger.LogDebug("Subscribed to command topics");
  }

  private void SubscribeToStateChanges()
  {
    // Subscribe to connection state changes
    _subscriptions.Add(_state.ConnectionStateChanged.Subscribe(async state =>
    {
      await PublishAsync($"{_options.TopicPrefix}/connection", state.ToString().ToLower(), true);
    }));

    // Subscribe to zone changes
    _subscriptions.Add(_state.ZoneChanged.Subscribe(async evt =>
    {
      var topic = $"{_options.TopicPrefix}/zone/{evt.ZoneId}";
      var payload = JsonSerializer.Serialize(new
      {
        zone_id = evt.ZoneId,
        label = evt.NewStatus.Label,
        state = evt.NewStatus.State.ToString().ToLower()
      }, JsonOptions);

      await PublishAsync(topic, payload, true);

      // Also publish simple state for Home Assistant binary sensors
      await PublishAsync($"{topic}/state", evt.NewStatus.State == ZoneState.Ok ? "OFF" : "ON", true);
    }));

    // Subscribe to partition changes
    _subscriptions.Add(_state.PartitionChanged.Subscribe(async evt =>
    {
      var topic = $"{_options.TopicPrefix}/partition/{evt.PartitionId}";
      var payload = JsonSerializer.Serialize(new
      {
        partition_id = evt.PartitionId,
        label = evt.NewStatus.Label,
        arm_state = evt.NewStatus.ArmState.ToString().ToLower(),
        in_alarm = evt.NewStatus.InAlarm,
        ready = evt.NewStatus.Ready,
        exit_delay = evt.NewStatus.ExitDelay,
        entry_delay = evt.NewStatus.EntryDelay
      }, JsonOptions);

      await PublishAsync(topic, payload, true);

      // Publish HA alarm state string
      var haState = MapToHaState(evt.NewStatus);
      await PublishAsync($"{topic}/state", haState, true);
    }));

    // Subscribe to system status changes
    _subscriptions.Add(_state.SystemStatusChanged.Subscribe(async status =>
    {
      var payload = JsonSerializer.Serialize(new
      {
        vdc = status.Vdc,
        battery = status.BatteryVoltage,
        dc_current = status.DcCurrent,
        panel_time = status.PanelTime.ToString("o"),
        trouble = status.TroubleFlags
      }, JsonOptions);

      await PublishAsync($"{_options.TopicPrefix}/panel/status", payload, true);

      // Publish individual sensor values for HA
      await PublishAsync($"{_options.TopicPrefix}/panel/vdc", status.Vdc.ToString("F1"), true);
      await PublishAsync($"{_options.TopicPrefix}/panel/battery", status.BatteryVoltage.ToString("F1"), true);
      await PublishAsync($"{_options.TopicPrefix}/panel/dc_current", status.DcCurrent.ToString(), true);
      await PublishAsync($"{_options.TopicPrefix}/panel/trouble", status.TroubleFlags.ToString(), true);
      await PublishAsync($"{_options.TopicPrefix}/panel/time", status.PanelTime.ToString("o"), true);
    }));

    // Subscribe to panel info changes
    _subscriptions.Add(_state.PanelInfoChanged.Subscribe(async info =>
    {
      var payload = JsonSerializer.Serialize(new
      {
        product = info.GetProductName(),
        software_version = info.GetSoftwareVersionString(),
        serial_number = info.ModuleSerialNumber
      }, JsonOptions);

      await PublishAsync($"{_options.TopicPrefix}/panel/info", payload, true);
    }));
  }

  private async Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default)
  {
    if (_client?.IsConnected != true)
      return;

    var message = new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(payload)
        .WithRetainFlag(retain)
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .Build();

    await _client.PublishAsync(message, cancellationToken);
    _logger.LogDebug("Published to {Topic}: {Payload}", topic, payload);
  }

  private object GetDeviceConfig()
  {
    return new
    {
      identifiers = new[] { $"digiplex_{_state.PanelInfo?.ModuleSerialNumber}" },
      name = "Digiplex Panel",
      manufacturer = "Paradox",
      model = _state.PanelInfo?.GetProductName() ?? "Unknown"
    };
  }

  private async Task PublishHomeAssistantDiscoveryAsync(CancellationToken cancellationToken)
  {
    _logger.LogInformation("Publishing Home Assistant discovery config");

    var device = GetDeviceConfig();

    // Publish discovery for each known zone
    foreach (var (zoneId, zone) in _state.Zones)
    {
      var config = new
      {
        name = string.IsNullOrEmpty(zone.Label) ? $"Zone {zoneId}" : zone.Label,
        unique_id = $"digiplex_zone_{zoneId}",
        state_topic = $"{_options.TopicPrefix}/zone/{zoneId}/state",
        json_attributes_topic = $"{_options.TopicPrefix}/zone/{zoneId}",
        device_class = "motion",
        payload_on = "ON",
        payload_off = "OFF",
        availability_topic = $"{_options.TopicPrefix}/status",
        device
      };

      var topic = $"{_options.HomeAssistantDiscoveryPrefix}/binary_sensor/digiplex/zone_{zoneId}/config";
      await PublishAsync(topic, JsonSerializer.Serialize(config, JsonOptions), true, cancellationToken);
    }

    // Publish discovery for each known partition (alarm_control_panel)
    foreach (var (partitionId, partition) in _state.Partitions)
    {
      var config = new
      {
        name = string.IsNullOrEmpty(partition.Label) ? $"Partition {partitionId}" : partition.Label,
        unique_id = $"digiplex_partition_{partitionId}",
        state_topic = $"{_options.TopicPrefix}/partition/{partitionId}/state",
        command_topic = $"{_options.TopicPrefix}/partition/{partitionId}/set",
        json_attributes_topic = $"{_options.TopicPrefix}/partition/{partitionId}",
        availability_topic = $"{_options.TopicPrefix}/status",
        supported_features = new[] { "arm_away", "arm_home", "arm_night", "arm_custom_bypass" },
        device
      };

      var topic = $"{_options.HomeAssistantDiscoveryPrefix}/alarm_control_panel/digiplex/partition_{partitionId}/config";
      await PublishAsync(topic, JsonSerializer.Serialize(config, JsonOptions), true, cancellationToken);
    }

    // Publish discovery for panel voltage sensor
    {
      var config = new
      {
        name = "Panel Voltage",
        unique_id = "digiplex_panel_vdc",
        state_topic = $"{_options.TopicPrefix}/panel/vdc",
        device_class = "voltage",
        unit_of_measurement = "V",
        availability_topic = $"{_options.TopicPrefix}/status",
        device
      };

      var topic = $"{_options.HomeAssistantDiscoveryPrefix}/sensor/digiplex/panel_vdc/config";
      await PublishAsync(topic, JsonSerializer.Serialize(config, JsonOptions), true, cancellationToken);
    }

    // Publish discovery for battery voltage sensor
    {
      var config = new
      {
        name = "Battery Voltage",
        unique_id = "digiplex_panel_battery",
        state_topic = $"{_options.TopicPrefix}/panel/battery",
        device_class = "voltage",
        unit_of_measurement = "V",
        availability_topic = $"{_options.TopicPrefix}/status",
        device
      };

      var topic = $"{_options.HomeAssistantDiscoveryPrefix}/sensor/digiplex/panel_battery/config";
      await PublishAsync(topic, JsonSerializer.Serialize(config, JsonOptions), true, cancellationToken);
    }

    // Publish discovery for DC current sensor
    {
      var config = new
      {
        name = "DC Current",
        unique_id = "digiplex_panel_dc_current",
        state_topic = $"{_options.TopicPrefix}/panel/dc_current",
        device_class = "current",
        availability_topic = $"{_options.TopicPrefix}/status",
        device
      };

      var topic = $"{_options.HomeAssistantDiscoveryPrefix}/sensor/digiplex/panel_dc_current/config";
      await PublishAsync(topic, JsonSerializer.Serialize(config, JsonOptions), true, cancellationToken);
    }

    // Publish discovery for trouble flags sensor
    {
      var config = new
      {
        name = "Trouble Flags",
        unique_id = "digiplex_panel_trouble",
        state_topic = $"{_options.TopicPrefix}/panel/trouble",
        availability_topic = $"{_options.TopicPrefix}/status",
        device
      };

      var topic = $"{_options.HomeAssistantDiscoveryPrefix}/sensor/digiplex/panel_trouble/config";
      await PublishAsync(topic, JsonSerializer.Serialize(config, JsonOptions), true, cancellationToken);
    }
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    GC.SuppressFinalize(this);
  }
}
