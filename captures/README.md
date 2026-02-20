# Serial Capture Analysis — Paradox DGP-848

Captures taken 2026-02-20 using Eltima Serial Port Monitor on Windows, with Winload 4.00 connected to a DGP-848 panel via COM4 (19200 bps). OBS recorded the screen with a millisecond clock overlay for timestamp correlation.

Each capture includes:
- `.txt` — hex dump from Serial Port Monitor
- `.spm` — raw capture file
- `.mp4` — screen recording of Winload with OBS clock overlay

Files are organized into subfolders by scenario. Note: the original MP4 filenames for captures 1 and 2 were swapped at recording time, but each MP4 is now placed in its correct scenario folder.

## Winload Polling Cycle

Winload polls 4 RAM addresses per ~1s cycle:

| Address | Purpose |
|---------|---------|
| RAM 0x0195 | Partition status (4 × 5-byte blocks) |
| RAM 0x0144 | System info (time, voltage, trouble flags) |
| RAM 0x0164 | Zone/event flags (mostly static) |
| RAM 0x0184 | Overlapping window with 0x0195 (17-byte offset, covers P5-P8 area unused on DGP-848) |

---

## Capture 1: Instant Arm + Disarm

**Folder:** [`01-instant-arm/`](01-instant-arm/)
**Serial time:** 12:07:59 – 12:16:17 | **Video time:** 12:14:33 – 12:16:10

**Scenario:** Instant arm partition 1 → wait 60s exit delay → disarm partition 1

### Commands Sent

| Time | Command PDU | Decoded |
|------|-------------|---------|
| 12:14:53 | `40 00 60 00 ...` | P1=Disarm (pre-clear by Winload) |
| 12:14:58 | `40 00 40 00 ...` | P1=InstantArm (0x4) |
| 12:16:06 | `40 00 60 00 ...` | P1=Disarm |

### Partition 1 Status (RAM 0x0195)

| Time | byte0 | byte1 | byte2 | State |
|------|-------|-------|-------|-------|
| 12:07:59 – 12:14:58 | `0x00` | `0x09` | `0x00` | Disarmed, ready |
| 12:14:59 – 12:15:58 | `0x08` (bit3=no_entry) | `0x0B` (ready+exit_delay) | `0x04` | Exit delay (instant) |
| 12:15:59 – 12:16:06 | `0x09` (bit0+bit3) | `0x09` (ready) | `0x00` | **Instant armed** |
| 12:16:07+ | `0x00` | `0x09` | `0x00` | Disarmed, ready |

### Key Frames

| Frame | Description |
|-------|-------------|
| ![Exit delay](01-instant-arm/instant_arm_exit_delay.jpg) | P1 "EXITING" countdown during exit delay |
| ![Armed](01-instant-arm/instant_arm_armed.jpg) | P1 "INSTANT ARMED" — exit delay finished |
| ![Disarmed](01-instant-arm/instant_arm_disarmed.jpg) | P1 "Ready" after disarm |

---

## Capture 2: Stay Arm + Entry Delay

**Folder:** [`02-stay-entry-delay/`](02-stay-entry-delay/)
**Serial time:** 12:19:25 – 12:21:09 | **Video time:** 12:19:09 – 12:21:09

**Scenario:** Stay arm partition 1 → wait 60s exit delay → door opens (entry delay) → disarm

### Commands Sent

| Time | Command PDU | Decoded |
|------|-------------|---------|
| 12:19:33 | `40 00 30 00 ...` | P1=StayArm (0x3) |
| 12:20:49 | `40 00 60 00 ...` | P1=Disarm |

### Partition 1 Status (RAM 0x0195)

| Time | byte0 | byte1 | byte2 | State |
|------|-------|-------|-------|-------|
| 12:19:25 – 12:19:33 | `0x00` | `0x09` | `0x00` | Disarmed, ready |
| 12:19:34 – 12:20:32 | `0x04` (bit2=stay) | `0x0B` (ready+exit_delay) | `0x04` | Exit delay (stay) |
| 12:20:33 – 12:20:39 | `0x05` (bit0+bit2) | `0x09` (ready) | `0x00` | **Stay armed** |
| 12:20:40 – 12:20:48 | `0x05` | `0x0C` (entry_delay, not ready) | `0x00` | **Entry delay** |
| 12:20:49 – 12:20:58 | `0x00` | `0x08` (not ready) | `0x00` | Disarmed (door open) |
| 12:20:59+ | `0x00` | `0x09` | `0x00` | Disarmed, ready |

### Key Frames

| Frame | Description |
|-------|-------------|
| ![Exit delay](02-stay-entry-delay/stay_arm_exit_delay.jpg) | P1 "EXITING" during stay arm exit delay |
| ![Armed](02-stay-entry-delay/stay_arm_armed.jpg) | P1 armed in stay mode |
| ![Entry delay](02-stay-entry-delay/stay_arm_entry_delay.jpg) | P1 "Entry Delay" — door opened while armed |
| ![Disarmed](02-stay-entry-delay/stay_arm_disarmed.jpg) | P1 back to "Ready" after disarm |

---

## Capture 3: Force Arm + Full Arm Sequence

**Folder:** [`03-sequence-arming/`](03-sequence-arming/)
**Serial time:** 12:22:54 – 12:25:56 | **Video time:** 12:22:38 – 12:25:56

**Scenario:** Force arm P2 → disarm during exit delay → Full arm P2 → wait exit delay → disarm. Then view Winload trouble display.

### Commands Sent

| Time | Command PDU | Decoded |
|------|-------------|---------|
| 12:23:01 | `40 00 05 00 ...` | P2=ForceArm (0x5) |
| 12:23:08 | `40 00 06 00 ...` | P2=Disarm |
| 12:23:16 | `40 00 02 00 ...` | P2=FullArm (0x2) |
| 12:24:22 | `40 00 06 00 ...` | P2=Disarm |

### Partition 2 Status (RAM 0x0195)

| Time | byte0 | byte1 | byte2 | State |
|------|-------|-------|-------|-------|
| 12:22:54 – 12:23:01 | `0x00` | `0x09` | `0x00` | Disarmed, ready |
| 12:23:02 – 12:23:08 | `0x02` (bit1=force) | `0x0B` (ready+exit_delay) | `0x04` | Exit delay (force) |
| 12:23:09 – 12:23:16 | `0x00` | `0x09` | `0x00` | Disarmed (cancelled) |
| 12:23:17 – 12:24:16 | `0x00` (no flags!) | `0x0B` (ready+exit_delay) | `0x04` | Exit delay (full arm) |
| 12:24:17 – 12:24:22 | `0x01` (bit0 only) | `0x09` (ready) | `0x00` | **Full armed** |
| 12:24:23+ | `0x00` | `0x09` | `0x00` | Disarmed, ready |

### Winload Trouble Display (12:24:35)

After disarming, the Winload trouble display was opened, which triggered reads of:
- RAM 0x01BC — trouble status flags
- EEPROM 0x1400-0x15E8 — trouble labels

### Key Frames

| Frame | Description |
|-------|-------------|
| ![Force arm](03-sequence-arming/seq_force_arm_exit_delay.jpg) | P2 "EXITING" with force arm + "FORCE ARMED" in events |
| ![Force disarm](03-sequence-arming/seq_force_disarmed.jpg) | P2 back to ready after disarm during exit delay |
| ![Full arm exit](03-sequence-arming/seq_full_arm_exit_delay.jpg) | P2 "EXITING" with regular full arm |
| ![Full armed](03-sequence-arming/seq_full_arm_armed.jpg) | P2 "ARMED" after exit delay |
| ![Disarmed](03-sequence-arming/seq_full_arm_disarmed.jpg) | P2 back to ready after disarm |
| ![Troubles](03-sequence-arming/seq_trouble_display.jpg) | Winload trouble display (Missing Keypad, etc.) |

---

## Partition Status Byte Map (from captures)

### Byte 0 — Arm/Alarm Flags

```
bit 0 (0x01) = ARMED         — set after exit delay completes
bit 1 (0x02) = FORCE_ARM     — set immediately on ForceArm command
bit 2 (0x04) = STAY_ARM      — set immediately on StayArm command
bit 3 (0x08) = NO_ENTRY      — set immediately on InstantArm command
bit 4 (0x10) = STROBE_ALARM
bit 5 (0x20) = SILENT_ALARM
bit 6 (0x40) = AUDIBLE_ALARM
bit 7 (0x80) = unknown
```

| Arm Type | During Exit Delay | Fully Armed |
|----------|-------------------|-------------|
| Full Arm | `0x00` | `0x01` |
| Force Arm | `0x02` | `0x03` |
| Stay Arm | `0x04` | `0x05` |
| Instant Arm | `0x08` | `0x09` |

### Byte 1 — Status Flags

```
bit 0 (0x01) = READY        — all zones in partition are closed
bit 1 (0x02) = EXIT_DELAY   — exit delay countdown active
bit 2 (0x04) = ENTRY_DELAY  — entry delay countdown active
bit 3 (0x08) = always set   — likely "partition enabled/supervised"
```

### Byte 2 — Extra

Value `0x04` during exit delay only. `0x00` otherwise.

---

## Monitor Command PDU Format (0x40)

```
Byte[0]  = 0x40 (command type = Monitor)
Byte[1]  = 0x00 (bus address)
Byte[2]  = (P1_cmd << 4) | P2_cmd    ← nibble-packed
Byte[3]  = (P3_cmd << 4) | P4_cmd
Byte[4]  = (P5_cmd << 4) | P6_cmd
Byte[5]  = (P7_cmd << 4) | P8_cmd
Byte[6-35] = 0x00
Byte[36] = checksum (sum of bytes 0-35 mod 256)
```

Panel echoes the exact same PDU as acknowledgment.

### Command Codes (4-bit)

| Code | Command |
|------|---------|
| 0x0 | Nop |
| 0x2 | Full Arm |
| 0x3 | Stay Arm |
| 0x4 | Instant Arm |
| 0x5 | Force Arm |
| 0x6 | Disarm |
| 0x8 | Beep |

---

## Additional Observations

- **Winload pre-clear:** Before InstantArm, Winload sends a Disarm first (safety). Not observed before Stay/Force/Full arm.
- **Exit delay duration:** 60 seconds for all arm types.
- **Command latency:** State change reflected in next poll cycle (< 1 second).
- **P3 ready flicker:** Partition 3 (radar ground floor) loses ready bit intermittently — user was physically triggering the radar sensor during recording.
- **PC Time vs Panel Time:** Panel clock runs ~6 minutes behind PC clock. Serial monitor uses PC timestamps.
