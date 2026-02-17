# -*- coding: utf-8 -*-
# Digiplex encode/decode logic (converted from Erlang)
# Author: Tony Rogvall <tony@rogvall.se>
# Copyright (C) 2016, Tony Rogvall
#
# Python conversion using models.py

import struct
from models import *

def encode_pdu(msg):
	if isinstance(msg, DigiplexInit):
		# <<DIGIPLEX_INIT:4,0:4, AA, EEADDR:16, II, SS, RR, CC, WW:16, MM:16, ID, E2, EV, FW:16, NN:32, DD:24>>
		return struct.pack(
			'>B B H B B B B H H B B B H I I',
			(DIGIPLEX_INIT << 4),
			msg.address,
			msg.eeaddr,
			msg.product_id,
			msg.software_version,
			msg.software_revision,
			msg.software_id,
			msg.password,
			msg.module_id,
			msg.winload_type_id,
			msg.memory_map_version or 0,
			msg.event_list_version or 0,
			msg.firware_build_version or 0,
			msg.module_serial_number or 0,
			msg.section_data or 0
		)
	elif isinstance(msg, DigiplexSpeedReq):
		# <<DIGIPLEX_SPEED:4, 0:4, 0, Speed, Speed>>
		return struct.pack('>B B B', (DIGIPLEX_SPEED << 4), 0, msg.speed)
	elif isinstance(msg, DigiplexSetPanelTimeReq):
		# <<DIGIPLEX_TIME:4, 0:4, Address, 0, 0, Century, Year, Month, Day>>
		return struct.pack(
			'>B B B B B B B B',
			(DIGIPLEX_TIME << 4),
			msg.address,
			0,
			0,
			msg.century or 0,
			msg.year or 0,
			msg.month or 0,
			msg.day or 0
		)
	elif isinstance(msg, DigiplexMonitorReq):
		# <<DIGIPLEX_MONITOR:4, 0:4, 0, P1:4, P2:4, P3:4, P4:4, P5:4, P6:4, P7:4, P8:4>>
		p = [msg.partition1 or 0, msg.partition2 or 0, msg.partition3 or 0, msg.partition4 or 0,
			 msg.partition5 or 0, msg.partition6 or 0, msg.partition7 or 0, msg.partition8 or 0]
		# Pack 8 4-bit values into 4 bytes
		packed = ((p[0] & 0xF) << 4 | (p[1] & 0xF)), ((p[2] & 0xF) << 4 | (p[3] & 0xF)), ((p[4] & 0xF) << 4 | (p[5] & 0xF)), ((p[6] & 0xF) << 4 | (p[7] & 0xF))
		return struct.pack('>B B 4B', (DIGIPLEX_MONITOR << 4), 0, *packed)
	elif isinstance(msg, DigiplexReadReq):
		# <<DIGIPLEX_READ:4, Count:5, BusAddress:7, Address:16>>
		count = msg.count or 0
		bus_address = msg.bus_address & 0x7F
		address = msg.address or 0
		# First byte: 4 bits type, 5 bits count, 7 bits bus_address
		b0 = (DIGIPLEX_READ << 4) | ((count & 0x1F) >> 1)
		b1 = ((count & 0x1) << 7) | (bus_address & 0x7F)
		return struct.pack('>B B H', b0, b1, address)
	elif isinstance(msg, DigiplexWriteReq):
		# <<DIGIPLEX_WRITE:4, Count:5, BusAddress:7, Address:16, Data/binary>>
		count = msg.count or (len(msg.data) if msg.data else 0)
		bus_address = msg.bus_address & 0x7F
		address = msg.address or 0
		data = msg.data or b''
		b0 = (DIGIPLEX_WRITE << 4) | ((count & 0x1F) >> 1)
		b1 = ((count & 0x1) << 7) | (bus_address & 0x7F)
		return struct.pack('>B B H', b0, b1, address) + data
	elif isinstance(msg, DigiplexEventReq):
		# <<DIGIPLEX_EVENT:4,0:4, 0:8, EventRequestNumber:16>>
		return struct.pack('>B B H', (DIGIPLEX_EVENT << 4), 0, msg.event_request_number or 0)
	else:
		raise ValueError('Unknown message type for encoding')

def decode_pdu(data):
	# Get type from first 4 bits
	msg_type = (data[0] >> 4) & 0xF
	if msg_type == DIGIPLEX_INIT:
		# <<DIGIPLEX_INIT:4,O:4, AA, EEADDR:16, II, SS, RR, CC, WW:16, MM:16, ID, E2, EV, FW:16, NN:32, DD:24, _Data:12/binary>>
		if len(data[:25]) != 25:
			raise ValueError(f"DigiplexInit decode error: expected 25 bytes, got {len(data[:25])}")
		unpacked = struct.unpack('>B B H B B B B H H B B B H I I', data[:25])
		return DigiplexInit(
			address=unpacked[1],
			eeaddr=unpacked[2],
			product_id=unpacked[3],
			software_version=unpacked[4],
			software_revision=unpacked[5],
			software_id=unpacked[6],
			password=unpacked[7],
			module_id=unpacked[8],
			winload_type_id=unpacked[9],
			memory_map_version=unpacked[10],
			event_list_version=unpacked[11],
			firware_build_version=unpacked[12],
			module_serial_number=unpacked[13],
			section_data=unpacked[14]
		)
	elif msg_type == DIGIPLEX_LOGIN:
		# <<DIGIPLEX_LOGIN:4,O:4, K:4, _:4, Callback:16,_Data/binary>>
		o = data[0] & 0xF
		k = (data[1] >> 4) & 0xF
		callback = struct.unpack('>H', data[2:4])[0]
		return DigiplexLoginResp(message_center=o, answer=k, callback=callback)
	elif msg_type == DIGIPLEX_MONITOR:
		# <<DIGIPLEX_MONITOR:4, O:4, _:8, P1:4, P2:4, P3:4, P4:4, P5:4, P6:4, P7:4, P8:4, _/binary>>
		o = data[0] & 0xF
		p = [ (data[2] >> 4) & 0xF, data[2] & 0xF,
			  (data[3] >> 4) & 0xF, data[3] & 0xF,
			  (data[4] >> 4) & 0xF, data[4] & 0xF,
			  (data[5] >> 4) & 0xF, data[5] & 0xF ]
		return DigiplexMonitorResp(
			message_center=o,
			partition1=p[0], partition2=p[1], partition3=p[2], partition4=p[3],
			partition5=p[4], partition6=p[5], partition7=p[6], partition8=p[7]
		)
	elif msg_type == DIGIPLEX_READ:
		# <<DIGIPLEX_READ:4,O:4, BusAddress:8, Address:16, Data/binary>>
		o = data[0] & 0xF
		bus_address = data[1]
		address = struct.unpack('>H', data[2:4])[0]
		d = data[4:]
		return DigiplexReadResp(message_center=o, bus_address=bus_address, address=address, data=d)
	elif msg_type == DIGIPLEX_WRITE:
		# <<DIGIPLEX_WRITE:4,O:4, BusAddress:8, Address:16, _/binary>>
		o = data[0] & 0xF
		bus_address = data[1]
		address = struct.unpack('>H', data[2:4])[0]
		return DigiplexWriteResp(message_center=o, bus_address=bus_address, address=address)
	elif msg_type == DIGIPLEX_ERROR:
		# <<DIGIPLEX_ERROR:4,O:4, Message:8, _/binary>>
		o = data[0] & 0xF
		message = data[1]
		return DigiplexErrorResp(message_center=o, message=message)
	elif msg_type == DIGIPLEX_EVENT:
		# <<DIGIPLEX_EVENT:4,O:4, EventRequestNumber : 8, Century:8, Year:8, Month:8, Day:8, Hour:8, Minute:8, EventGroup: 8, Partition1:4, Partition2:4, EventNumber1:8, EventNumber2:8, Serial:32, EventData/binary>>
		o = data[0] & 0xF
		event_request_number = data[1]
		century = data[2]
		year = data[3]
		month = data[4]
		day = data[5]
		hour = data[6]
		minute = data[7]
		event_group = data[8]
		partition1 = (data[9] >> 4) & 0xF
		partition2 = data[9] & 0xF
		event_number1 = data[10]
		event_number2 = data[11]
		serial = struct.unpack('>I', data[12:16])[0]
		event_data = data[16:]
		timestamp = ((century * 100 + year, month, day), (hour, minute, 0))
		return DigiplexEventResp(
			message_center=o,
			event_request_number=event_request_number,
			timestamp=timestamp,
			event_group=event_group,
			partition1=partition1,
			partition2=partition2,
			event_number1=event_number1,
			event_number2=event_number2,
			serial=serial,
			event_data=event_data
		)
	else:
		raise ValueError('Unknown message type for decoding')

# Utility: add_checksum (stub, needs digiplex_crc implementation)
def add_checksum(bin_data):
	size = len(bin_data)
	pad = 36 - size
	# CRC: sum of all bytes modulo 256
	crc = sum(bin_data) & 0xff
	return bin_data + b'\x00' * pad + struct.pack('B', crc)
