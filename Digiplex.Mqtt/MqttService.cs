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
  /// <summary>Optional HA-side alarm code (PIN). When set, HA requires this code to arm/disarm.</summary>
  public string? AlarmCode { get; set; }
  /// <summary>Interval in hours to re-publish HA discovery config. Default 24 (once per day). 0 disables.</summary>
  public int DiscoveryRepublishIntervalHours { get; set; } = 24;
}

/// <summary>
/// MQTT service for publishing Digiplex state and receiving commands
/// </summary>
public class MqttService : IMqttService
{
  private readonly ILogger<MqttService> _logger;
  private readonly MqttOptions _options;
  private readonly PartitionConfig _partitionConfig;
  private readonly DigiplexState _state;

  private IMqttClient? _client;
  private CancellationTokenSource? _cts;
  private readonly List<IDisposable> _subscriptions = [];
  private Timer? _discoveryTimer;

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
      IOptions<PartitionConfig> partitionConfig,
      DigiplexState state)
  {
    _logger = logger;
    _options = options.Value;
    _partitionConfig = partitionConfig.Value;
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

    SubscribeToStateChanges();
    await ConnectWithRetryAsync(cancellationToken);
  }

  private async Task ConnectWithRetryAsync(CancellationToken cancellationToken)
  {
    var attempt = 0;
    while (!cancellationToken.IsCancellationRequested)
    {
      try
      {
        await ConnectAsync(cancellationToken);
        return;
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        attempt++;
        var delaySeconds = Math.Min(60, (int)Math.Pow(2, Math.Min(attempt, 6)));
        _logger.LogWarning("MQTT connect attempt {Attempt} failed ({Error}); retrying in {Delay}s",
            attempt, ex.GetType().Name, delaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
      }
    }
  }

  public async Task StopAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Stopping MQTT service");

    foreach (var subscription in _subscriptions)
    {
      subscription.Dispose();
    }
    _subscriptions.Clear();

    _discoveryTimer?.Dispose();
    _discoveryTimer = null;

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

  private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
  {
    var topic = args.ApplicationMessage.Topic;
    var payloadBytes = args.ApplicationMessage.Payload;
    var payload = payloadBytes.Length > 0 ? Encoding.UTF8.GetString(payloadBytes) : string.Empty;

    _logger.LogDebug("Received message on {Topic}: {Payload}", topic, payload);

    // Handle partition commands: {prefix}/partition/{id}/set
    var partitionPrefix = $"{_options.TopicPrefix}/partition/";
    if (topic.StartsWith(partitionPrefix) && topic.EndsWith("/set"))
    {
      var idStr = topic[partitionPrefix.Length..^4]; // extract id between prefix and /set
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

    // Handle group commands: {prefix}/group/{groupId}/set
    var groupPrefix = $"{_options.TopicPrefix}/group/";
    if (topic.StartsWith(groupPrefix) && topic.EndsWith("/set"))
    {
      var groupId = topic[groupPrefix.Length..^4];
      var group = _partitionConfig.Groups.FirstOrDefault(g =>
          string.Equals(g.Id, groupId, StringComparison.OrdinalIgnoreCase));

      if (group != null)
      {
        var command = MapHaCommandToMonitoring(payload);
        if (command.HasValue)
        {
          var commands = group.PartitionIds.ToDictionary(id => id, _ => command.Value);
          _logger.LogInformation("Group {GroupId} command: {Payload} → partitions {Partitions}",
              groupId, payload, string.Join(", ", group.PartitionIds));
          _state.RequestMultiPartitionCommand(new MultiPartitionCommand(commands));
        }
        else
        {
          _logger.LogWarning("Unknown group command: {Payload}", payload);
        }
      }
      else
      {
        _logger.LogWarning("Unknown group: {GroupId}", groupId);
      }
    }

    // Handle partition beep: {prefix}/partition/{id}/beep
    if (topic.StartsWith(partitionPrefix) && topic.EndsWith("/beep"))
    {
      var idStr = topic[partitionPrefix.Length..^5]; // extract id between prefix and /beep
      if (int.TryParse(idStr, out var beepPartitionId) && beepPartitionId >= 1 && beepPartitionId <= 8)
      {
        _logger.LogInformation("Partition {Id} beep command", beepPartitionId);
        _state.RequestPartitionCommand(new PartitionCommand(beepPartitionId, MonitoringCommands.Beep));
      }
    }

    // Handle panel time request: {prefix}/panel/time/get
    if (topic == $"{_options.TopicPrefix}/panel/time/get")
    {
      var systemStatus = _state.SystemStatus;
      if (systemStatus != null)
      {
        await PublishAsync($"{_options.TopicPrefix}/panel/time", systemStatus.PanelTime.ToString("o"), false);
      }
    }
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
        .WithTopicFilter($"{_options.TopicPrefix}/+/+/get")
        .WithTopicFilter($"{_options.TopicPrefix}/+/+/beep")
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
        entry_delay = evt.NewStatus.EntryDelay,
        alarm_in_memory = evt.NewStatus.AlarmInMemory
      }, JsonOptions);

      await PublishAsync(topic, payload, true);

      // Publish HA alarm state string
      var haState = MapToHaState(evt.NewStatus);
      await PublishAsync($"{topic}/state", haState, true);

      // Update group states for any group containing this partition
      foreach (var group in _partitionConfig.Groups)
      {
        if (group.PartitionIds.Contains(evt.PartitionId))
        {
          await PublishGroupStateAsync(group);
        }
      }
    }));

    // Subscribe to system status changes
    _subscriptions.Add(_state.SystemStatusChanged.Subscribe(async status =>
    {
      var payload = JsonSerializer.Serialize(new
      {
        vdc = status.Vdc,
        battery = status.BatteryVoltage,
        dc_voltage = status.DcVoltage,
        trouble = status.TroubleFlags
      }, JsonOptions);

      await PublishAsync($"{_options.TopicPrefix}/panel/status", payload, true);

      // Publish individual sensor values for HA
      await PublishAsync($"{_options.TopicPrefix}/panel/vdc", status.Vdc.ToString("F1"), true);
      await PublishAsync($"{_options.TopicPrefix}/panel/battery", status.BatteryVoltage.ToString("F1"), true);
      await PublishAsync($"{_options.TopicPrefix}/panel/dc", status.DcVoltage.ToString("F1"), true);
      await PublishAsync($"{_options.TopicPrefix}/panel/trouble", status.TroubleFlags.ToString(), true);
    }));

    // Publish HA discovery when serial data is ready (and on reconnects)
    if (_options.EnableHomeAssistantDiscovery)
    {
      _subscriptions.Add(_state.DataReady.Subscribe(async _ =>
      {
        _logger.LogInformation("Panel data ready, publishing HA discovery");
        await PublishHomeAssistantDiscoveryAsync();
        RestartDiscoveryTimer();
      }));
    }

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

  private async Task PublishGroupStateAsync(PartitionGroup group)
  {
    var members = group.PartitionIds
        .Select(id => _state.Partitions.GetValueOrDefault(id))
        .Where(p => p != null)
        .ToList();

    if (members.Count == 0) return;

    var groupState = AggregateGroupState(members!);
    var topic = $"{_options.TopicPrefix}/group/{group.Id}";

    var payload = JsonSerializer.Serialize(new
    {
      group_id = group.Id,
      label = group.Label,
      state = groupState,
      partitions = members.Select(m => new
      {
        partition_id = m!.PartitionId,
        state = MapToHaState(m)
      })
    }, JsonOptions);

    await PublishAsync(topic, payload, true);
    await PublishAsync($"{topic}/state", groupState, true);
  }

  private static string AggregateGroupState(List<PartitionStatus> members)
  {
    if (members.Any(m => m.InAlarm)) return "triggered";
    if (members.Any(m => m.ExitDelay)) return "arming";
    if (members.Any(m => m.EntryDelay)) return "pending";

    if (members.All(m => m.ArmState == ArmState.Disarmed)) return "disarmed";

    // Any armed partition means group is armed
    if (members.All(m => m.ArmState != ArmState.Disarmed))
    {
      // All armed — use most common arm state mapping
      if (members.All(m => m.ArmState == ArmState.StayArmed)) return "armed_home";
      if (members.All(m => m.ArmState == ArmState.InstantArmed)) return "armed_night";
      if (members.All(m => m.ArmState == ArmState.ForceArmed)) return "armed_custom_bypass";
      return "armed_away";
    }

    // Mixed armed/disarmed — treat as armed for safety
    return "armed_away";
  }

  private void RestartDiscoveryTimer()
  {
    _discoveryTimer?.Dispose();
    _discoveryTimer = null;

    var hours = _options.DiscoveryRepublishIntervalHours;
    if (hours <= 0) return;

    var interval = TimeSpan.FromHours(hours);
    _discoveryTimer = new Timer(async _ =>
    {
      try
      {
        _logger.LogInformation("Periodic HA discovery re-publish");
        await PublishHomeAssistantDiscoveryAsync();
      }
      catch (Exception ex)
      {
        _logger.LogWarning(ex, "Failed to re-publish HA discovery");
      }
    }, null, interval, interval);
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
      model = _state.PanelInfo?.GetProductName() ?? "Unknown",
      sw_version = _state.PanelInfo?.GetSoftwareVersionString()
    };
  }

  private async Task PublishHomeAssistantDiscoveryAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Publishing Home Assistant discovery config");

    var device = GetDeviceConfig();
    var prefix = _options.TopicPrefix;
    var ha = _options.HomeAssistantDiscoveryPrefix;

    // Build zone → device_class lookup: zone overrides > partition default > "motion"
    var zoneDeviceClassMap = _partitionConfig.Items
        .SelectMany(p => p.ZoneIds.Select(z => (z, p.DeviceClass)))
        .ToDictionary(x => x.z, x => x.DeviceClass);
    foreach (var ov in _partitionConfig.ZoneOverrides)
      zoneDeviceClassMap[ov.Id] = ov.DeviceClass;

    // Zone binary sensors
    foreach (var (zoneId, zone) in _state.Zones)
    {
      var config = new
      {
        name = string.IsNullOrEmpty(zone.Label) ? $"Zone {zoneId}" : zone.Label,
        unique_id = $"digiplex_zone_{zoneId}",
        state_topic = $"{prefix}/zone/{zoneId}/state",
        json_attributes_topic = $"{prefix}/zone/{zoneId}",
        device_class = zoneDeviceClassMap.GetValueOrDefault(zoneId, "motion"),
        payload_on = "ON",
        payload_off = "OFF",
        availability_topic = $"{prefix}/status",
        device
      };

      await PublishAsync($"{ha}/binary_sensor/digiplex/zone_{zoneId}/config",
          JsonSerializer.Serialize(config, JsonOptions), true, cancellationToken);
    }

    // Partition alarm control panels
    foreach (var (partitionId, partition) in _state.Partitions)
    {
      await PublishAlarmPanelDiscoveryAsync(
          $"partition_{partitionId}",
          string.IsNullOrEmpty(partition.Label) ? $"Partition {partitionId}" : partition.Label,
          $"{prefix}/partition/{partitionId}",
          device, cancellationToken);

      // Beep button per partition
      var beepConfig = new
      {
        name = $"{(string.IsNullOrEmpty(partition.Label) ? $"Partition {partitionId}" : partition.Label)} Beep",
        unique_id = $"digiplex_partition_{partitionId}_beep",
        command_topic = $"{prefix}/partition/{partitionId}/beep",
        availability_topic = $"{prefix}/status",
        icon = "mdi:volume-high",
        entity_category = "config",
        device
      };

      await PublishAsync($"{ha}/button/digiplex/partition_{partitionId}_beep/config",
          JsonSerializer.Serialize(beepConfig, JsonOptions), true, cancellationToken);
    }

    // Group alarm control panels
    foreach (var group in _partitionConfig.Groups)
    {
      await PublishAlarmPanelDiscoveryAsync(
          $"group_{group.Id}",
          group.Label,
          $"{prefix}/group/{group.Id}",
          device, cancellationToken);
    }

    // Panel voltage sensor (diagnostic)
    await PublishAsync($"{ha}/sensor/digiplex/panel_vdc/config", JsonSerializer.Serialize(new
    {
      name = "Panel Voltage",
      unique_id = "digiplex_panel_vdc",
      state_topic = $"{prefix}/panel/vdc",
      device_class = "voltage",
      state_class = "measurement",
      unit_of_measurement = "V",
      entity_category = "diagnostic",
      availability_topic = $"{prefix}/status",
      device
    }, JsonOptions), true, cancellationToken);

    // Battery voltage sensor (diagnostic)
    await PublishAsync($"{ha}/sensor/digiplex/panel_battery/config", JsonSerializer.Serialize(new
    {
      name = "Battery Voltage",
      unique_id = "digiplex_panel_battery",
      state_topic = $"{prefix}/panel/battery",
      device_class = "voltage",
      state_class = "measurement",
      unit_of_measurement = "V",
      entity_category = "diagnostic",
      availability_topic = $"{prefix}/status",
      device
    }, JsonOptions), true, cancellationToken);

    // DC output voltage sensor (diagnostic)
    await PublishAsync($"{ha}/sensor/digiplex/panel_dc/config", JsonSerializer.Serialize(new
    {
      name = "DC Voltage",
      unique_id = "digiplex_panel_dc",
      state_topic = $"{prefix}/panel/dc",
      device_class = "voltage",
      state_class = "measurement",
      unit_of_measurement = "V",
      entity_category = "diagnostic",
      availability_topic = $"{prefix}/status",
      device
    }, JsonOptions), true, cancellationToken);

    // Trouble flags sensor (diagnostic)
    await PublishAsync($"{ha}/sensor/digiplex/panel_trouble/config", JsonSerializer.Serialize(new
    {
      name = "Trouble Flags",
      unique_id = "digiplex_panel_trouble",
      state_topic = $"{prefix}/panel/trouble",
      entity_category = "diagnostic",
      availability_topic = $"{prefix}/status",
      device
    }, JsonOptions), true, cancellationToken);
  }

  private async Task PublishAlarmPanelDiscoveryAsync(
      string uniqueSuffix, string name, string baseTopic,
      object device, CancellationToken cancellationToken)
  {
    var ha = _options.HomeAssistantDiscoveryPrefix;

    // Build discovery payload — include code fields only when AlarmCode is configured
    var configDict = new Dictionary<string, object?>
    {
      ["name"] = name,
      ["unique_id"] = $"digiplex_{uniqueSuffix}",
      ["state_topic"] = $"{baseTopic}/state",
      ["command_topic"] = $"{baseTopic}/set",
      ["json_attributes_topic"] = baseTopic,
      ["availability_topic"] = $"{_options.TopicPrefix}/status",
      ["supported_features"] = new[] { "arm_away", "arm_home", "arm_night", "arm_custom_bypass" },
      ["device"] = device
    };

    if (!string.IsNullOrEmpty(_options.AlarmCode))
    {
      configDict["code"] = _options.AlarmCode;
      configDict["code_arm_required"] = true;
      configDict["code_disarm_required"] = true;
      configDict["code_trigger_required"] = false;
    }

    await PublishAsync($"{ha}/alarm_control_panel/digiplex/{uniqueSuffix}/config",
        JsonSerializer.Serialize(configDict, JsonOptions), true, cancellationToken);
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    GC.SuppressFinalize(this);
  }
}
