# Paradox Digiplex Serial Protocol

This document describes the serial protocol used to communicate with Paradox Digiplex (DGP-848) alarm panels via the serial (Winload) port. The protocol details were reverse-engineered from Winload serial captures and cross-referenced with the [PAI (Paradox Alarm Interface)](https://github.com/ParadoxAlarmInterface/pai) project.

## Physical Layer

- **Interface**: RS-232 serial via panel serial port
- **Baud Rate**: 19200 (default, can be changed via Speed PDU)
- **Data Bits**: 8
- **Parity**: None
- **Stop Bits**: 1
- **Flow Control**: None

## Packet Structure

Every packet is exactly **37 bytes**: 36 bytes of data + 1 byte checksum.

```
┌──────────────────────────────────────┬──────────┐
│           Data (36 bytes)            │ Checksum │
│  Byte 0 ... Byte 35                 │ Byte 36  │
└──────────────────────────────────────┴──────────┘
```

### Checksum

The checksum is the **low byte of the sum** of all 36 data bytes:

```
checksum = (sum of bytes 0..35) & 0xFF
```

Unused data bytes are padded with `0x00`.

### Command Codes (Byte 0, high nibble)

The command type is encoded in the **upper 4 bits** of byte 0:

| Code | Hex    | Name        | Direction     | Description                       |
|------|--------|-------------|---------------|-----------------------------------|
| 0x0  | `0_`   | Init/Ack    | Both          | Handshake, login                  |
| 0x1  | `1_`   | Login       | Response only | Login response                    |
| 0x2  | `2_`   | Speed       | Request       | Change baud rate                  |
| 0x3  | `3_`   | Time        | Request       | Set panel clock                   |
| 0x4  | `4_`   | Monitor     | Both          | Partition arm/disarm commands      |
| 0x5  | `5_`   | Read        | Both          | Memory read (upload from panel)    |
| 0x6  | `6_`   | Write       | Both          | Memory write (download to panel)   |
| 0x7  | `7_`   | Error       | Response only | Error response                    |
| 0x8  | `8_`   | SaveEvent   | Request       | Save event                        |
| 0xA  | `A_`   | Send        | Request       | Send data                         |
| 0xB  | `B_`   | Broadcast   | Request       | Broadcast                         |
| 0xC  | `C_`   | Unlock      | Request       | Unlock                            |
| 0xD  | `D_`   | ZoneChange  | Response      | Zone change notification          |
| 0xE  | `E_`   | Event       | Both          | Event log retrieval               |

---

## Connection Sequence

### 1. Wake-Up

The connection begins with a 37-byte wake-up string (all `0xFF`):

```
TX: FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF
    FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF FF
    FF FF FF FF FF
```

The panel responds with `0xFF` in byte 0, rest zeros:

```
RX: FF 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
    00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
    00 00 00 00 FF
```

### 2. Init Handshake

After wake-up, the init handshake string is sent:

```
TX: 5F 20 00 00 00 00 00 00 00 00 00 00 00 00 00 00
    00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
    00 00 00 00 7F
```

- Byte 0: `0x5F` = command `0x5` (Read) with count bits — this is a special init sequence
- Byte 1: `0x20` = bus address (module address)
- Remaining: zeros + checksum `0x7F`

The panel responds with an **Init PDU** containing panel identification:

```
RX: 00 [addr] [eeprom_hi] [eeprom_lo] [product_id] [sw_ver] [sw_rev] [sw_id]
    [password_hi] [password_lo] [module_hi] [module_lo] [winload_type]
    [mem_map_ver] [event_list_ver] [fw_build_hi] [fw_build_lo]
    [serial_3] [serial_2] [serial_1] [serial_0]
    [section_3] [section_2] [section_1] [section_0]
    ... padding ... [checksum]
```

### 3. Login

Login is performed by sending an **Init PDU** back with the user password set:

```
TX: InitPdu { Password = 0000 }
    → 00 00 00 00 00 00 00 00 [pwd_hi] [pwd_lo] 00 00 55 00 00 00 00
      00 00 00 00 00 00 00 00 ... 00 [checksum]
```

- Byte 12: `0x55` = WinloadTypeId (EndUserType)
- Bytes 8-9: Password as 16-bit big-endian

The panel responds with a **Login Response PDU** (command code `0x1`):

```
RX: 1_ [answer<<4 | ...] [callback_hi] [callback_lo] ... [checksum]
```

- Byte 0 low nibble: message center
- Byte 1 high nibble: answer code (0 = success)

### 4. Load Labels

After login, zone labels are read from EEPROM. Each label is 16 bytes of ASCII text.

**Zone labels**: EEPROM addresses `0x2000` to `0x22F0`, 16 bytes each (up to 48 zones).

```
TX: ReadRequestPdu { Count=16, BusAddress=0, Address=0x2000 }
    → 58 00 20 00 00...00 [checksum]

RX: ReadResponsePdu { Data = "Front Door\0\0\0\0\0\0" }
    → 50 00 20 00 46 72 6F 6E 74 20 44 6F 6F 72 00 00
      00 00 00 00 ... [checksum]
```

### 5. Polling Loop

After initialization, the panel is polled cyclically. Winload polls 4 RAM addresses every ~1 second:

| Address   | Encoded   | Content              | Read Size |
|-----------|-----------|----------------------|-----------|
| RAM 0x184 | `81 84`   | Zone/status data     | 32 bytes  |
| RAM 0x195 | `81 95`   | Partition status     | 32 bytes  |
| RAM 0x144 | `81 44`   | System info          | 32 bytes  |
| RAM 0x164 | `81 64`   | Reserved (all zeros) | 32 bytes  |

Our implementation polls 3 addresses:

| Address   | Encoded   | Content              |
|-----------|-----------|----------------------|
| RAM 0x153 | `81 53`   | Zone status (12 bytes: 6 zone + 6 tamper) |
| RAM 0x195 | `81 95`   | Partition status     |
| RAM 0x144 | `81 44`   | System info          |

### 6. Disconnection

There is no protocol-level disconnect/close packet. Winload simply drops the serial connection. Serial captures confirm the last exchange is always a normal polling read with no termination handshake.

---

## PDU Details

### Read Request PDU (Command 0x5)

The Read PDU uses a **packed encoding** where count and bus address share bits with the command byte:

```
Byte 0: [cmd:4][count_hi:4]    cmd=0x5, count bits 4..1
Byte 1: [count_lo:1][bus:7]    count bit 0, bus address 7 bits
Byte 2: [addr_hi]              Memory address high byte
Byte 3: [addr_lo]              Memory address low byte
```

**Encoding formula:**
```
byte0 = (0x5 << 4) | (count >> 1)
byte1 = ((count & 1) << 7) | (busAddress & 0x7F)
byte2 = address >> 8
byte3 = address & 0xFF
```

**Count**: 0-31, where **0 means read 32 bytes** (full data area minus 4 header bytes).

#### Memory Addressing

- **EEPROM**: address as-is (bit 15 = 0). E.g., `0x2000` → bytes `20 00`
- **RAM**: address OR'd with `0x8000` (bit 15 = 1). E.g., RAM `0x195` → `0x8195` → bytes `81 95`

#### Examples

**Read zone status (12 bytes from RAM 0x153):**
```
Count=12 (0x0C), Bus=0, Address=Ram(0x153)=0x8153

byte0 = (5 << 4) | (12 >> 1) = 0x50 | 0x06 = 0x56
byte1 = ((12 & 1) << 7) | 0  = 0x00
byte2 = 0x81
byte3 = 0x53

TX: 56 00 81 53 00 00 ... 00 [2A]
     ││ │  └──┘
     ││ │   Address = 0x8153 (RAM 0x153)
     ││ └── Bus=0, count_lo=0
     │└──── count_hi = 6 (upper 4 bits of 12)
     └───── cmd = 5 (Read)
```

**Read partition status (32 bytes from RAM 0x195):**
```
Count=0 (=32 bytes), Bus=0, Address=Ram(0x195)=0x8195

byte0 = (5 << 4) | (0 >> 1) = 0x50
byte1 = ((0 & 1) << 7) | 0  = 0x00
byte2 = 0x81
byte3 = 0x95

TX: 50 00 81 95 00 00 ... 00 [66]
```

**Read system info (32 bytes from RAM 0x144):**
```
TX: 50 00 81 44 00 00 ... 00 [15]
```

**Read zone label (16 bytes from EEPROM 0x2000):**
```
Count=16 (0x10), Bus=0, Address=0x2000

byte0 = (5 << 4) | (16 >> 1) = 0x58
byte1 = ((16 & 1) << 7) | 0  = 0x00

TX: 58 00 20 00 00 00 ... 00 [checksum]
```

### Read Response PDU (Command 0x5)

```
Byte 0: [0x5:4][msg_center:4]
Byte 1: [bus_address]
Byte 2: [addr_hi]
Byte 3: [addr_lo]
Byte 4..35: [data, up to 32 bytes]
Byte 36: [checksum]
```

The `Data` field starts at byte 4 and extends to byte 35 (32 bytes when Count=0).

---

### Monitor Request PDU (Command 0x4) — Partition Control

Used to send arm/disarm commands to partitions. Each partition gets a **4-bit command** packed into bytes:

```
Byte 0: [0x4:4][0:4]           Command 0x4
Byte 1: [0x00]                  Reserved
Byte 2: [P1:4][P2:4]           Partitions 1 & 2
Byte 3: [P3:4][P4:4]           Partitions 3 & 4
Byte 4: [P5:4][P6:4]           Partitions 5 & 6
Byte 5: [P7:4][P8:4]           Partitions 7 & 8
```

**Partition commands (4-bit values):**

| Value | Hex  | Command           |
|-------|------|-------------------|
| 0     | 0x0  | Nop (no action)   |
| 2     | 0x2  | Full Arm (Away)   |
| 3     | 0x3  | Stay Arm (Home)   |
| 4     | 0x4  | Instant Arm       |
| 5     | 0x5  | Force Arm         |
| 6     | 0x6  | Disarm            |
| 8     | 0x8  | Beep              |

#### Example: Arm Away partition 1

```
P1=0x2 (FullArm), P2-P8=0x0 (Nop)

Byte 2 = (0x2 << 4) | 0x0 = 0x20
Byte 3 = 0x00
Byte 4 = 0x00
Byte 5 = 0x00

TX: 40 00 20 00 00 00 00 00 ... 00 [checksum]
```

#### Example: Disarm partition 3

```
P1=0, P2=0, P3=0x6 (Disarm), P4-P8=0

Byte 2 = 0x00                     (P1=0, P2=0)
Byte 3 = (0x6 << 4) | 0x0 = 0x60 (P3=6, P4=0)

TX: 40 00 00 60 00 00 00 00 ... 00 [checksum]
```

### Monitor Response PDU (Command 0x4)

Same layout as request — returns current partition command states.

---

### Error Response PDU (Command 0x7)

```
Byte 0: [0x7:4][msg_center:4]
Byte 1: [error_code]
```

| Error Code | Meaning            |
|------------|--------------------|
| 0x00       | Invalid command    |
| 0x01       | Invalid user code  |
| 0x02       | Invalid partition  |

---

### Event Request PDU (Command 0xE)

```
Byte 0: [0xE:4][0:4]
Byte 1: [0x00]
Byte 2: [event_req_hi]
Byte 3: [event_req_lo]
```

### Event Response PDU (Command 0xE)

```
Byte 0:  [0xE:4][msg_center:4]
Byte 1:  [event_request_number]
Byte 2:  [century]
Byte 3:  [year]
Byte 4:  [month]
Byte 5:  [day]
Byte 6:  [hour]
Byte 7:  [minute]
Byte 8:  [event_group]
Byte 9:  [partition1:4][partition2:4]
Byte 10: [event_number_1]
Byte 11: [event_number_2]
Byte 12..15: [serial_number, 32-bit BE]
Byte 16..35: [event_data]
```

---

### Speed Request PDU (Command 0x2)

```
Byte 0: [0x2:4][0:4]
Byte 1: [0x00]
Byte 2: [speed]
```

### Set Panel Time PDU (Command 0x3)

```
Byte 0: [0x3:4][0:4]
Byte 1: [address]
Byte 2: [0x00]
Byte 3: [0x00]
Byte 4: [century]
Byte 5: [year]
Byte 6: [month]
Byte 7: [day]
```

---

## Memory Map

### Zone Status — RAM 0x0153

Read with `ReadRequestPdu { Count=12, BusAddress=0, Address=Ram(0x153) }`.

Response `Data[0..11]` = 12 bytes:

```
Bytes 0-5:  Zone open/alarm bits (48 zones, 1 bit each)
Bytes 6-11: Zone tamper bits (48 zones, 1 bit each)
```

Each bit represents one zone (LSB first within each byte):
- Byte 0, bit 0 = Zone 1
- Byte 0, bit 7 = Zone 8
- Byte 1, bit 0 = Zone 9
- etc.

**Zone state decoding:**

| Zone bit | Tamper bit | State     |
|----------|-----------|-----------|
| 0        | 0         | OK        |
| 0        | 1         | Tamper    |
| 1        | 0         | Open      |
| 1        | 1         | Fire Loop |

### Partition Status — RAM 0x0195

Read with `ReadRequestPdu { Count=0, BusAddress=0, Address=Ram(0x195) }`.

Produces on the wire: `50 00 81 95 ... [checksum]`

Response `Data[0..31]` layout (32 bytes):

```
Data[0..4]:   Partition 1 status (5 bytes)
Data[5..9]:   Partition 2 status (5 bytes)
Data[10..14]: Partition 3 status (5 bytes)
Data[15..19]: Partition 4 status (5 bytes)
Data[20]:     Trailing byte (0x04, purpose unknown)
Data[21..31]: Analog/signal data (ignored)
```

#### Partition status block (5 bytes per partition)

Each partition uses a 5-byte block starting at `offset = partitionIndex * 5`.

**Byte 0 — Arm/Alarm flags** (verified from serial captures on DGP-848):

| Bit | Name             | Description                                                |
|-----|------------------|------------------------------------------------------------|
| 0   | armed            | Partition is armed (set after exit delay completes)        |
| 1   | force_arm        | Force arm mode (set immediately on ForceArm command)       |
| 2   | arm_stay         | Stay arm mode (set immediately on StayArm command)         |
| 3   | no_entry         | Instant/no-entry mode (set immediately on InstantArm cmd)  |
| 4   | strobe_alarm     | Strobe alarm active                                        |
| 5   | silent_alarm     | Silent alarm active                                        |
| 6   | audible_alarm    | Audible alarm active                                       |
| 7   | (unknown)        |                                                            |

**Byte 1 — Ready/Delay flags:**

| Bit | Name             | Description                        |
|-----|------------------|------------------------------------|
| 0   | ready            | Partition is ready to arm                        |
| 1   | exit_delay       | Exit delay in progress                           |
| 2   | entry_delay      | Entry delay in progress                          |
| 3   | (unknown)        | Always set on DGP-848, purpose TBD               |
| 4   | alarm_in_memory  | Alarm was triggered, persists until acknowledged |

**Byte 2..4** — Additional flags (all zeros observed during idle/disarmed state).

#### Winload capture example (idle, all disarmed + ready)

```
RX: 50 00 81 95  00 09 00 00 00  00 09 00 00 00
                 ──P1 (5 bytes)─  ──P2 (5 bytes)─
    00 09 00 00 00  00 09 00 00 00  04 76 02 B5 ...
    ──P3 (5 bytes)─  ──P4 (5 bytes)─  ──trailing──
```

Each partition block: `00 09 00 00 00`
- Byte 0 = `0x00` → arm flags all clear → **Disarmed**
- Byte 1 = `0x09 = 0b0000_1001` → bit 0 (ready) = 1, bit 3 (unknown) = 1 → **Ready**

When zone 11 opens, partition 3 changes to: `00 08 00 00 00`
- Byte 0 = `0x00` → still **Disarmed**
- Byte 1 = `0x08 = 0b0000_1000` → bit 0 (ready) = 0 → **Not Ready**

**Observed arm byte values** (from serial captures):

| Arm Type    | During Exit Delay | Fully Armed |
|-------------|-------------------|-------------|
| Full Arm    | `0x00`            | `0x01`      |
| Force Arm   | `0x02`            | `0x03`      |
| Stay Arm    | `0x04`            | `0x05`      |
| Instant Arm | `0x08`            | `0x09`      |

Note: during exit delay, the arm type bits (1-3) are set but bit 0 (armed) is NOT set.
After exit delay completes, bit 0 is added.

**Observed alarm byte value** (instant arm + audible alarm):

`0x59 = 0101_1001` → armed(0x01) + no_entry(0x08) + strobe_alarm(0x10) + audible_alarm(0x40)

After disarm, byte 1 changes from `0x09` to `0x19` (bit 4 = alarm_in_memory set).

#### Arm state decoding logic

```
if (armed) {
    if (no_entry)  → InstantArmed (armed_night)
    if (arm_stay)  → StayArmed (armed_home)
    if (force_arm) → ForceArmed (armed_custom_bypass)
    else           → Armed (armed_away)
} else {
    → Disarmed
}

if (strobe_alarm || silent_alarm || audible_alarm) → InAlarm
```

### System Info — RAM 0x0144

Read with `ReadRequestPdu { Count=0, BusAddress=0, Address=Ram(0x144) }`.

Produces on the wire: `50 00 81 44 ... [checksum]`

Response `Data[0..31]` layout (32 bytes):

```
Data[0]:     Trouble flags (bitfield)
Data[1..3]:  Additional trouble flags / unknown (zeros)
Data[4]:     Unknown (0x01)
Data[5]:     Century (0x14 = 20)
Data[6]:     Year    (0x19 = 25 → 2025)
Data[7]:     Month   (0x0B = 11)
Data[8]:     Day     (0x13 = 19)
Data[9]:     Hour    (0x15 = 21)
Data[10]:    Minute  (0x10 = 16)
Data[11]:    Second  (0x05 = 5)
Data[12]:    VDC raw
Data[13]:    Battery raw
Data[14]:    DC current raw
Data[15..31]: Zeros
```

#### Voltage conversion formulas

Verified against Winload display on DGP-848 (raw byte 0xC5=197 → Winload shows 17.3V):

```
Panel voltage (VDC)     = 22.4 × raw / 255
Battery voltage         = 22.8 × raw / 255
```

Note: PAI (Spectra/Magellan) uses `20.3` for VDC, but this gives ~15.7V for the same raw byte. The DGP-848 uses a different voltage divider ratio.

#### Winload capture example

```
RX: 50 00 81 44  08 00 00 00 01  14 19 0B 13 15 10 05
                 │               │  └─────────────────── date/time
                 │               └── unknown
                 └── trouble=0x08

    C5 94 97  00 00 ... 00  [83]
    │  │  │
    │  │  └── DC current raw = 0x97 (151)
    │  └───── Battery raw = 0x94 (148) → 22.8 × 148/255 = 13.2V
    └──────── VDC raw = 0xC5 (197) → 22.4 × 197/255 = 17.3V

Date: century=0x14(20), year=0x19(25), month=0x0B(11), day=0x13(19)
Time: hour=0x15(21), minute=0x10(16), second=0x05(5)
→ 2025-11-19 21:16:05
```

### Zone Labels — EEPROM 0x2000

Read with `ReadRequestPdu { Count=16, BusAddress=0, Address=0x2000 + (zoneIndex * 16) }`.

Each zone label is 16 bytes of ASCII, right-padded with `0x00` or spaces.

| Zone  | EEPROM Address | Range         |
|-------|----------------|---------------|
| 1     | 0x2000         | 0x2000-0x200F |
| 2     | 0x2010         | 0x2010-0x201F |
| 3     | 0x2020         | 0x2020-0x202F |
| ...   | ...            | ...           |
| 48    | 0x22F0         | 0x22F0-0x22FF |

---

## Complete Winload Polling Cycle

From the idle capture, Winload polls 4 addresses in a repeating cycle:

```
                                  ┌─────────────────────────────────────────────┐
                                  │           ~1 second polling cycle           │
                                  └─────────────────────────────────────────────┘

TX: 50 00 81 84 ...55    ─── Read RAM 0x184 (zone/status extended)
RX: 50 00 81 84 ...70    ─── Response (zones, partition hints)

TX: 50 00 81 95 ...66    ─── Read RAM 0x195 (partition status)
RX: 50 00 81 95 ...5F    ─── Response (4 partition blocks)

TX: 50 00 81 44 ...15    ─── Read RAM 0x144 (system info)
RX: 50 00 81 44 ...83    ─── Response (time, voltages, trouble)

TX: 50 00 81 64 ...35    ─── Read RAM 0x164 (reserved, all zeros)
RX: 50 00 81 64 ...35    ─── Response (empty)

          ⟳ repeat
```

---

## MQTT Topics and Home Assistant Integration

### State Topics

| Topic                           | Payload                                    | Retain |
|---------------------------------|--------------------------------------------|--------|
| `digiplex/status`               | `online` / `offline`                       | Yes    |
| `digiplex/connection`           | `connected` / `disconnected` / etc.        | Yes    |
| `digiplex/zone/{id}`            | JSON: zone_id, label, state                | Yes    |
| `digiplex/zone/{id}/state`      | `ON` / `OFF`                               | Yes    |
| `digiplex/partition/{id}`       | JSON: partition_id, label, arm_state, ...  | Yes    |
| `digiplex/partition/{id}/state` | HA state string (see below)                | Yes    |
| `digiplex/panel/info`           | JSON: product, software_version, serial    | Yes    |
| `digiplex/panel/status`         | JSON: vdc, battery, dc_current, panel_time, trouble | Yes |
| `digiplex/panel/vdc`            | Voltage string, e.g. `15.7`               | Yes    |
| `digiplex/panel/battery`        | Voltage string, e.g. `13.2`               | Yes    |
| `digiplex/panel/dc_current`     | Raw byte value                             | Yes    |
| `digiplex/panel/trouble`        | Trouble flags byte                         | Yes    |
| `digiplex/panel/time`           | ISO 8601 datetime                          | Yes    |
| `digiplex/group/{id}`            | JSON: group_id, label, state, partitions   | Yes    |
| `digiplex/group/{id}/state`     | HA state string (aggregated from members)  | Yes    |

### Command Topics

| Topic                             | Payload                                         |
|-----------------------------------|-------------------------------------------------|
| `digiplex/partition/{id}/set`     | `ARM_AWAY`, `ARM_HOME`, `ARM_NIGHT`, `ARM_CUSTOM_BYPASS`, `DISARM` |
| `digiplex/group/{id}/set`        | Same commands — applied to all partitions in the group atomically |

### HA Alarm State Mapping

| Panel State                     | HA State              |
|---------------------------------|-----------------------|
| In alarm (any alarm bit set)    | `triggered`           |
| Exit delay active               | `arming`              |
| Entry delay active              | `pending`             |
| Armed (full arm)                | `armed_away`          |
| Stay armed                      | `armed_home`          |
| Instant armed                   | `armed_night`         |
| Force armed                     | `armed_custom_bypass` |
| Disarmed                        | `disarmed`            |

### HA Discovery Entities

| Platform               | Entity                    | Discovery Topic                                              |
|------------------------|---------------------------|--------------------------------------------------------------|
| `binary_sensor`        | Zone 1..48                | `homeassistant/binary_sensor/digiplex/zone_{id}/config`      |
| `alarm_control_panel`  | Partition 1..4            | `homeassistant/alarm_control_panel/digiplex/partition_{id}/config` |
| `alarm_control_panel`  | Partition groups          | `homeassistant/alarm_control_panel/digiplex/group_{id}/config`    |
| `sensor`               | Panel Voltage (VDC)       | `homeassistant/sensor/digiplex/panel_vdc/config`             |
| `sensor`               | Battery Voltage           | `homeassistant/sensor/digiplex/panel_battery/config`         |
| `sensor`               | DC Current                | `homeassistant/sensor/digiplex/panel_dc_current/config`      |
| `sensor`               | Trouble Flags             | `homeassistant/sensor/digiplex/panel_trouble/config`         |

---

## References

- **PAI (Paradox Alarm Interface)**: https://github.com/ParadoxAlarmInterface/pai
  - Partition status adapter: `paradox/hardware/spectra_magellan/panel.py`
  - Voltage formulas: `paradox/hardware/spectra_magellan/adapters.py`
- **Winload**: Paradox's official programming software (captures taken from DGP-848 panel)
- **Panel tested**: Paradox DGP-848 (Digiplex), firmware v4.20.100
