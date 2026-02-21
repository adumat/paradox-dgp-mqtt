# Paradox DGP-848 MQTT Bridge

A .NET 9 service that connects to a **Paradox Digiplex DGP-848** alarm panel through its **serial I/O port** and exposes the panel state over MQTT. Designed for integration with Home Assistant.

> **Supported hardware:** Paradox Digiplex DGP-848 only. Other Paradox panels (EVO, SP, MG) are **not** supported.
> **Connection:** Direct serial wiring to the panel's serial I/O header — no IP150 or other network module required.

## Features

- Direct serial communication with the panel (19200 baud, 8N1)
- Zone status polling (open, tamper, fire loop)
- Zone labels read from panel EEPROM
- Partition and group definitions for Home Assistant alarm panels
- MQTT publishing with Home Assistant auto-discovery
- Runs as a Linux daemon (systemd compatible)

## Wiring

Connect a USB-to-serial adapter (or UART) to the panel's serial I/O header:

| Panel Pin | Signal | Adapter Pin |
|-----------|--------|-------------|
| TX        | Panel transmit | RX        |
| RX        | Panel receive  | TX        |
| GND       | Ground         | GND       |

The panel serial port runs at **19200 baud, 8N1**. A 5 V TTL adapter should work — check your panel's documentation for the correct voltage level.

## Quick Start

```bash
# Build
dotnet build

# Run
dotnet run --project Digiplex.Worker

# Publish for Linux
dotnet publish Digiplex.Worker -c Release -r linux-x64 --self-contained
```

## Configuration

All settings live in `Digiplex.Worker/appsettings.json`. Every value can be overridden with environment variables using the `Section__Key` convention (e.g. `Serial__PortName`).

### Serial

Connection to the panel's serial I/O port.

| Key | Default | Description |
|-----|---------|-------------|
| `PortName` | `/dev/ttyUSB0` | Serial device path |
| `BaudRate` | `19200` | Baud rate (should stay 19200 for DGP-848) |
| `Password` | `"0000"` | Panel user password for login |
| `PollIntervalMs` | `1000` | Zone status polling interval in ms |
| `ReadTimeoutMs` | `1000` | Serial read timeout in ms |
| `MaxRetries` | `5` | Retries on communication errors before reconnecting |
| `ZoneCount` | `48` | Number of zones to poll (max 48 for DGP-848) |

### MQTT

Connection to the MQTT broker.

| Key | Default | Description |
|-----|---------|-------------|
| `BrokerHost` | `localhost` | MQTT broker hostname or IP |
| `BrokerPort` | `1883` | MQTT broker port |
| `Username` | `null` | Broker username (optional) |
| `Password` | `null` | Broker password (optional) |
| `ClientId` | `digiplex` | MQTT client ID |
| `TopicPrefix` | `digiplex` | Prefix for all MQTT topics |
| `EnableHomeAssistantDiscovery` | `true` | Publish HA MQTT discovery messages |
| `HomeAssistantDiscoveryPrefix` | `homeassistant` | HA discovery topic prefix |
| `AlarmCode` | `null` | Optional PIN code that HA will require for arm/disarm |

### Partitions

Define how zones are grouped into partitions and how they appear in Home Assistant.

```json
{
  "Partitions": {
    "Items": [
      {
        "Id": 1,
        "Label": "Ground Floor",
        "DeviceClass": "motion",
        "ZoneIds": [1, 2, 3, 4, 5]
      },
      {
        "Id": 2,
        "Label": "Perimeter",
        "DeviceClass": "door",
        "ZoneIds": [10, 11, 12]
      }
    ],
    "ZoneOverrides": [
      { "Id": 3, "DeviceClass": "door" }
    ],
    "Groups": [
      {
        "Id": "all",
        "Label": "Whole House",
        "PartitionIds": [1, 2]
      }
    ]
  }
}
```

- **Items** — each partition maps to an `alarm_control_panel` in HA. `DeviceClass` sets the default HA `device_class` for all `binary_sensor` zones in that partition.
- **ZoneOverrides** — override the `device_class` for individual zones (takes priority over the partition default).
- **Groups** — logical groups that arm/disarm multiple partitions together.

### Environment variable overrides

```bash
Serial__PortName=/dev/ttyUSB1
Serial__Password=1234
Mqtt__BrokerHost=192.168.1.100
Mqtt__Username=user
Mqtt__Password=secret
```

### Full example

```json
{
  "Serial": {
    "PortName": "/dev/ttyUSB0",
    "BaudRate": 19200,
    "Password": "0000",
    "PollIntervalMs": 1000,
    "ReadTimeoutMs": 1000,
    "MaxRetries": 5,
    "ZoneCount": 48
  },
  "Partitions": {
    "Items": [
      { "Id": 1, "Label": "House", "DeviceClass": "motion", "ZoneIds": [1,2,3,4,5] }
    ],
    "Groups": [],
    "ZoneOverrides": [
      { "Id": 5, "DeviceClass": "door" }
    ]
  },
  "Mqtt": {
    "BrokerHost": "192.168.1.100",
    "BrokerPort": 1883,
    "Username": null,
    "Password": null,
    "ClientId": "digiplex",
    "TopicPrefix": "digiplex",
    "EnableHomeAssistantDiscovery": true,
    "HomeAssistantDiscoveryPrefix": "homeassistant"
  }
}
```

## MQTT Topics

### Published (state)

| Topic | Payload | Description |
|-------|---------|-------------|
| `digiplex/status` | `online`/`offline` | Service availability (LWT) |
| `digiplex/connection` | `connected`/`disconnected`/... | Panel connection state |
| `digiplex/zone/{id}` | JSON | Zone details (id, label, state) |
| `digiplex/zone/{id}/state` | `ON`/`OFF` | Simple state for HA binary sensors |
| `digiplex/partition/{id}` | JSON | Partition status |
| `digiplex/panel/info` | JSON | Panel information |

### Subscribed (commands)

| Topic | Payload | Description |
|-------|---------|-------------|
| `digiplex/partition/{id}/arm/set` | `arm`/`stay`/`disarm` | Arm/disarm partition |

## Zone States

- `ok` — Zone is closed/normal
- `open` — Zone is open/triggered
- `tamper` — Zone tamper detected
- `fire_loop` — Fire loop condition

## Docker

```bash
# Build image
docker build -t digiplex .

# Run with serial device passthrough
docker run -d \
  --name digiplex \
  --device /dev/ttyUSB0:/dev/ttyUSB0 \
  -e Mqtt__BrokerHost=192.168.1.100 \
  digiplex

# Or use docker-compose (includes Mosquitto)
docker-compose up -d
```

## Systemd Service

Create `/etc/systemd/system/digiplex.service`:

```ini
[Unit]
Description=Digiplex DGP-848 MQTT Bridge
After=network.target

[Service]
Type=notify
ExecStart=/opt/digiplex/Digiplex.Worker
WorkingDirectory=/opt/digiplex
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable digiplex
sudo systemctl start digiplex
```

## Projects

| Project | Description |
|---------|-------------|
| `Digiplex.Core` | Models, protocol codec, and observable state service |
| `Digiplex.Serial` | Serial port communication and panel polling |
| `Digiplex.Mqtt` | MQTT publishing and command subscription |
| `Digiplex.Worker` | Hosted service entry point |

## License

MIT