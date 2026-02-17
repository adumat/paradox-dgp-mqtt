# Digiplex .NET

A .NET 9 service for communicating with Paradox Digiplex/EVO alarm panels via serial connection, exposing status through MQTT.

## Features

- Serial communication with Paradox DGP-848/EVO panels
- Observable state management with System.Reactive
- MQTT integration (Mosquitto) with bidirectional support
- Home Assistant auto-discovery for zones
- Runs as a Linux daemon (systemd compatible)

## Projects

| Project | Description |
|---------|-------------|
| `Digiplex.Core` | Models, protocol codec, and observable state service |
| `Digiplex.Serial` | Serial port communication and panel polling |
| `Digiplex.Mqtt` | MQTT publishing and command subscription |
| `Digiplex.Worker` | Hosted service entry point |

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

Edit `Digiplex.Worker/appsettings.json`:

```json
{
  "Serial": {
    "PortName": "/dev/ttyUSB0",
    "BaudRate": 19200,
    "Password": "0000",
    "PollIntervalMs": 1000
  },
  "Mqtt": {
    "BrokerHost": "localhost",
    "BrokerPort": 1883,
    "TopicPrefix": "digiplex",
    "EnableHomeAssistantDiscovery": true
  }
}
```

Configuration can also be set via environment variables:
```bash
Serial__PortName=/dev/ttyUSB1
Serial__Password=1234
Mqtt__BrokerHost=192.168.1.100
```

## MQTT Topics

### Published (state)

| Topic | Payload | Description |
|-------|---------|-------------|
| `digiplex/status` | `online`/`offline` | Service availability |
| `digiplex/connection` | `connected`/`disconnected`/... | Panel connection state |
| `digiplex/zone/{id}` | JSON | Zone details (id, label, state) |
| `digiplex/zone/{id}/state` | `ON`/`OFF` | Simple state for HA binary sensors |
| `digiplex/partition/{id}` | JSON | Partition status |
| `digiplex/panel/info` | JSON | Panel information |

### Subscribed (commands)

| Topic | Payload | Description |
|-------|---------|-------------|
| `digiplex/partition/{id}/arm/set` | `arm`/`stay`/`disarm` | Arm/disarm partition (future) |

## Zone States

- `ok` - Zone is closed/normal
- `open` - Zone is open/triggered
- `tamper` - Zone tamper detected
- `fire_loop` - Fire loop condition

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
Description=Digiplex Alarm Panel Service
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

## License

MIT
