# Digiplex protocol constants and structures (converted from Erlang)

# Command codes
DIGIPLEX_ACK          = 0x0
DIGIPLEX_INIT         = 0x0
DIGIPLEX_LOGIN        = 0x1
DIGIPLEX_SPEED        = 0x2
DIGIPLEX_TIME         = 0x3
DIGIPLEX_MONITOR      = 0x4
DIGIPLEX_READ         = 0x5  # upload
DIGIPLEX_WRITE        = 0x6  # download
DIGIPLEX_ERROR        = 0x7  # response only
DIGIPLEX_SAVE_EVENT   = 0x8
DIGIPLEX_SEND         = 0xA
DIGIPLEX_BROADCAST    = 0xB
DIGIPLEX_UNLOCK       = 0xC
DIGIPLEX_ZONE_CHANGE  = 0xD
DIGIPLEX_EVENT        = 0xE

# Memory address macros
def EEPROM(addr):
	return addr & 0x7fff

def RAM(addr):
	return (addr & 0x7fff) | 0x8000

# Digiplex versions
DIGIPLEX_VERSION_1_30 = 0x00
DIGIPLEX_VERSION_2_00 = 0x01
DIGIPLEX_VERSION_NE   = 0x02

DIGIPLEX_ENDUSER_TYPE = 0x55

# Product IDs
DIGIPLEX_PRODUCT_ID_DIGIPLEX = 0x00
DIGIPLEX_PRODUCT_ID_SPECTRA  = 0x10
DIGIPLEX_PRODUCT_ID_CONTACT  = 0x20

# Monitoring command codes
DIGIPLEX_NOP         = 0x0
DIGIPLEX_FULL_ARM    = 0x2
DIGIPLEX_STAY_ARM    = 0x3
DIGIPLEX_INSTANT_ARM = 0x4
DIGIPLEX_FORCE_ARM   = 0x5
DIGIPLEX_DISARM      = 0x6
DIGIPLEX_BEEP        = 0x8

# Error codes
ERROR_COMMAND    = 0x00
ERROR_USER_CODE  = 0x01
ERROR_PARTITION  = 0x02

# Data structures (Erlang records -> Python dataclasses)
from dataclasses import dataclass, field
from typing import Optional, Any

@dataclass
class DigiplexInit:
	address: int = 0
	eeaddr: int = 0
	message_center: Optional[Any] = None
	product_id: int = 0
	software_version: int = 0
	software_revision: int = 0
	software_id: int = 0
	password: int = 0
	module_id: int = 0
	modem_speed: int = 0
	winload_type_id: int = DIGIPLEX_ENDUSER_TYPE
	memory_map_version: Optional[Any] = None
	event_list_version: Optional[Any] = None
	firware_build_version: Optional[Any] = None
	module_serial_number: Optional[Any] = None
	section_data: Optional[Any] = None

@dataclass
class DigiplexLoginResp:
	message_center: Optional[Any] = None
	answer: Optional[Any] = None
	callback: Optional[Any] = None

@dataclass
class DigiplexSpeedReq:
	speed: int = 0

@dataclass
class DigiplexSpeedResp:
	message_center: Optional[Any] = None
	speed: int = 0

@dataclass
class DigiplexSetPanelTimeReq:
	address: int = 0
	century: Optional[Any] = None
	year: Optional[Any] = None
	month: Optional[Any] = None
	day: Optional[Any] = None

@dataclass
class DigiplexMonitorReq:
	partition1: Optional[Any] = None
	partition2: Optional[Any] = None
	partition3: Optional[Any] = None
	partition4: Optional[Any] = None
	partition5: Optional[Any] = None
	partition6: Optional[Any] = None
	partition7: Optional[Any] = None
	partition8: Optional[Any] = None

@dataclass
class DigiplexMonitorResp:
	message_center: Optional[Any] = None
	partition1: Optional[Any] = None
	partition2: Optional[Any] = None
	partition3: Optional[Any] = None
	partition4: Optional[Any] = None
	partition5: Optional[Any] = None
	partition6: Optional[Any] = None
	partition7: Optional[Any] = None
	partition8: Optional[Any] = None

@dataclass
class DigiplexReadReq:
	count: Optional[Any] = None  # :5  0..31  0=32 bytes
	bus_address: int = 0  # :7 bits
	address: Optional[Any] = None  # :16 16#8000 + addr = RAM, 16#0000 +addr = EEPROM

@dataclass
class DigiplexReadResp:
	message_center: Optional[Any] = None
	bus_address: Optional[Any] = None
	address: Optional[Any] = None
	data: Optional[Any] = None

@dataclass
class DigiplexWriteReq:
	count: Optional[Any] = None  # 0..31, 0=32 bytes
	bus_address: int = 0  # 7 bits
	address: Optional[Any] = None  # 16 bits
	data: Optional[bytes] = None  # 1..32 bytes

@dataclass
class DigiplexWriteResp:
	message_center: Optional[Any] = None
	bus_address: Optional[Any] = None
	address: Optional[Any] = None

@dataclass
class DigiplexErrorResp:
	message_center: Optional[Any] = None
	message: Optional[Any] = None

@dataclass
class DigiplexEventReq:
	event_request_number: Optional[Any] = None

@dataclass
class DigiplexEventResp:
	message_center: Optional[Any] = None
	event_request_number: Optional[Any] = None
	timestamp: Optional[Any] = None
	event_group: Optional[Any] = None
	partition1: Optional[Any] = None
	partition2: Optional[Any] = None
	event_number1: Optional[Any] = None
	event_number2: Optional[Any] = None
	serial: Optional[Any] = None
	event_data: Optional[bytes] = None
