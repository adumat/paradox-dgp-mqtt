using System.Text.Encodings.Web;
using System.Text.Json;
using Digiplex.Core.Models;
using Digiplex.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Packets;
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
  /// <summary>Keep-alive period in seconds sent to the broker.</summary>
  public int KeepAliveSeconds { get; set; } = 30;
  /// <summary>Delay before the managed client auto-reconnects, in seconds.</summary>
  public int ReconnectDelaySeconds { get; set; } = 5;
}

/// <summary>
/// MQTT service for publishing Digiplex state and receiving commands.
/// Uses MQTTnet's <see cref="IManagedMqttClient"/>, which owns the connection,
/// auto-reconnect (with backoff), subscription restore and an outbound queue — so a
/// broker drop can never wedge the process. (The previous manual reconnect, done by
/// calling ConnectAsync from inside the DisconnectedAsync callback, deadlocked under
/// a flapping broker and pegged a CPU core.)
/// </summary>
public class MqttService : IMqttService
{
  private readonly ILogger<MqttService> _logger;
  private readonly MqttOptions _options;
  private readonly PartitionConfig _partitionConfig;
  private readonly DigiplexState _state;

  private IManagedMqttClient? _client;
  private readonly List<IDisposable> _subscriptions = [];
  private Timer? _discoveryTimer;
  private volatile bool _stopping;

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

    var factory = new MqttFactory();
    _client = factory.CreateManagedMqttClient();

    _client.ConnectedAsync += OnConnectedAsync;
    _client.DisconnectedAsync += OnDisconnectedAsync;
    _client.ConnectingFailedAsync += OnConnectingFailedAsync;
    _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;

    var clientOptionsBuilder = new MqttClientOptionsBuilder()
        .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
        .WithClientId(_options.ClientId)
        .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSeconds))
        .WithWillTopic($"{_options.TopicPrefix}/status")
        .WithWillPayload("offline"u8.ToArray())
        .WithWillRetain(true)
        .WithCleanSession(true);

    if (_options.BrokerPort == 8883)
      clientOptionsBuilder.WithTlsOptions(o => o.UseTls(true));

    if (!string.IsNullOrEmpty(_options.Username))
      clientOptionsBuilder.WithCredentials(_options.Username, _options.Password);

    var managedOptions = new ManagedMqttClientOptionsBuilder()
        .WithClientOptions(clientOptionsBuilder.Build())
        .WithAutoReconnectDelay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds))
        .WithMaxPendingMessages(1000)
        .Build();

    SubscribeToStateChanges();

    // StartAsync connects in the background and keeps the connection alive; it does
    // NOT throw when the broker is down — it retries. Subscriptions are remembered by
    // the managed client and re-applied automatically on every (re)connect.
    await _client.StartAsync(managedOptions);
    await SubscribeToCommandsAsync();
  }

  public async Task StopAsync(CancellationToken cancellationToken = default)
  {
    _logger.LogInformation("Stopping MQTT service");
    _stopping = true;

    foreach (var subscription in _subscriptions)
      subscription.Dispose();
    _subscriptions.Clear();

    _discoveryTimer?.Dispose();
    _discoveryTimer = null;

    if (_client != null)
    {
      if (_client.IsConnected)
        await EnqueueAsync($"{_options.TopicPrefix}/status", "offline", true);

      await _client.StopAsync();
      _client.Dispose();
      _client = null;
    }
  }

  private async Task OnConnectedAsync(MqttClientConnectedEventArgs args)
  {
    _logger.LogInformation("Connected to MQTT broker");
    // (Re)publish online status on every (re)connect. Command-topic subscriptions
    // are restored automatically by the managed client.
    await EnqueueAsync($"{_options.TopicPrefix}/status", "online", true);
  }

  private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
  {
    // Observe only — the managed client reconnects on its own. No manual reconnect.
    if (!_stopping)
      _logger.LogWarning("Disconnected from MQTT broker: {Reason}", args.Reason);
    return Task.CompletedTask;
  }

  private Task OnConnectingFailedAsync(ConnectingFailedEventArgs args)
  {
    if (!_stopping)
      _logger.LogWarning("MQTT connection attempt failed ({Error}); will retry",
          args.Exception?.GetType().Name ?? "unknown");
    return Task.CompletedTask;
  }

  private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
  {
    var topic = args.ApplicationMessage.Topic;
    var payload = args.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;

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
        await EnqueueAsync($"{_options.TopicPrefix}/panel/time", systemStatus.PanelTime.ToString("o"), false);
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

  private async Task SubscribeToCommandsAsync()
  {
    var filters = new List<MqttTopicFilter>
    {
      new MqttTopicFilterBuilder().WithTopic($"{_options.TopicPrefix}/+/+/set").Build(),
      new MqttTopicFilterBuilder().WithTopic($"{_options.TopicPrefix}/+/+/get").Build(),
      new MqttTopicFilterBuilder().WithTopic($"{_options.TopicPrefix}/+/+/beep").Build()
    };

    // Managed client remembers these and re-subscribes automatically after reconnects.
    await _client!.SubscribeAsync(filters);
    _logger.LogDebug("Subscribed to command topics");
  }

  private void SubscribeToStateChanges()
  {
    // Subscribe to connection state changes
    _subscriptions.Add(_state.ConnectionStateChanged.Subscribe(async state =>
    {
      await EnqueueAsync($"{_options.TopicPrefix}/connection", state.ToString().ToLower(), true);
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

      await EnqueueAsync(topic, payload, true);

      // Also publish simple state for Home Assistant binary sensors
      await EnqueueAsync($"{topic}/state", evt.NewStatus.State == ZoneState.Ok ? "OFF" : "ON", true);
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

      await EnqueueAsync(topic, payload, true);

      // Publish HA alarm state string
      var haState = MapToHaState(evt.NewStatus);
      await EnqueueAsync($"{topic}/state", haState, true);

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

      await EnqueueAsync($"{_options.TopicPrefix}/panel/status", payload, true);

      // Publish individual sensor values for HA
      await EnqueueAsync($"{_options.TopicPrefix}/panel/vdc", status.Vdc.ToString("F1"), true);
      await EnqueueAsync($"{_options.TopicPrefix}/panel/battery", status.BatteryVoltage.ToString("F1"), true);
      await EnqueueAsync($"{_options.TopicPrefix}/panel/dc", status.DcVoltage.ToString("F1"), true);
      await EnqueueAsync($"{_options.TopicPrefix}/panel/trouble", status.TroubleFlags.ToString(), true);
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

      await EnqueueAsync($"{_options.TopicPrefix}/panel/info", payload, true);
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

    await EnqueueAsync(topic, payload, true);
    await EnqueueAsync($"{topic}/state", groupState, true);
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

  /// <summary>
  /// Queue a message on the managed client. The queue drains when connected and
  /// survives short disconnects, so callers never block on — or throw from — a
  /// mid-flight broker drop. Retained topics + reconnect bring state back up.
  /// </summary>
  private async Task EnqueueAsync(string topic, string payload, bool retain)
  {
    if (_client == null) return;

    var message = new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(payload)
        .WithRetainFlag(retain)
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .Build();

    try
    {
      await _client.EnqueueAsync(message);
      _logger.LogDebug("Queued publish to {Topic}: {Payload}", topic, payload);
    }
    catch (Exception ex)
    {
      _logger.LogDebug(ex, "Failed to queue publish to {Topic}", topic);
    }
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

  private async Task PublishHomeAssistantDiscoveryAsync()
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

      await EnqueueAsync($"{ha}/binary_sensor/digiplex/zone_{zoneId}/config",
          JsonSerializer.Serialize(config, JsonOptions), true);
    }

    // Partition alarm control panels
    foreach (var (partitionId, partition) in _state.Partitions)
    {
      await PublishAlarmPanelDiscoveryAsync(
          $"partition_{partitionId}",
          string.IsNullOrEmpty(partition.Label) ? $"Partition {partitionId}" : partition.Label,
          $"{prefix}/partition/{partitionId}",
          device);

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

      await EnqueueAsync($"{ha}/button/digiplex/partition_{partitionId}_beep/config",
          JsonSerializer.Serialize(beepConfig, JsonOptions), true);
    }

    // Group alarm control panels
    foreach (var group in _partitionConfig.Groups)
    {
      await PublishAlarmPanelDiscoveryAsync(
          $"group_{group.Id}",
          group.Label,
          $"{prefix}/group/{group.Id}",
          device);
    }

    // Panel voltage sensor (diagnostic)
    await EnqueueAsync($"{ha}/sensor/digiplex/panel_vdc/config", JsonSerializer.Serialize(new
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
    }, JsonOptions), true);

    // Battery voltage sensor (diagnostic)
    await EnqueueAsync($"{ha}/sensor/digiplex/panel_battery/config", JsonSerializer.Serialize(new
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
    }, JsonOptions), true);

    // DC output voltage sensor (diagnostic)
    await EnqueueAsync($"{ha}/sensor/digiplex/panel_dc/config", JsonSerializer.Serialize(new
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
    }, JsonOptions), true);

    // Trouble flags sensor (diagnostic)
    await EnqueueAsync($"{ha}/sensor/digiplex/panel_trouble/config", JsonSerializer.Serialize(new
    {
      name = "Trouble Flags",
      unique_id = "digiplex_panel_trouble",
      state_topic = $"{prefix}/panel/trouble",
      entity_category = "diagnostic",
      availability_topic = $"{prefix}/status",
      device
    }, JsonOptions), true);
  }

  private async Task PublishAlarmPanelDiscoveryAsync(
      string uniqueSuffix, string name, string baseTopic, object device)
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

    await EnqueueAsync($"{ha}/alarm_control_panel/digiplex/{uniqueSuffix}/config",
        JsonSerializer.Serialize(configDict, JsonOptions), true);
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    GC.SuppressFinalize(this);
  }
}
