# -*- coding: utf-8 -*-
# Paradox Digiplex protocol server (Python port)
# Author: Tony Rogvall <tony@rogvall.se> (Erlang original)
# Python conversion using models.py e codec.py

import sys, threading, time, asyncio, os
from itertools import chain
try:
    import serial  # pip install pyserial
except ImportError:
    print("Missing dependency. Run: pip install pyserial", file=sys.stderr); raise
from models import *
from codec import *

MEMMAP_SIZE = 512
MEMMAP_BEGIN = 0x0
MEMMAP_END = MEMMAP_SIZE - 32

# mem_map = {
#   "status_base1": 0x8000,
#   "status_base2": 0x1FE0,
#   "definitions": {
#     "zone": {"addresses": [range(0x730, 0x7A0, 0x03)]},
#     "pgm": {"addresses": [range(0x7A0, 0x800, 0x06)]}},
#   "labels": {
#     "zone": {"label_offset": 0, "addresses": [range(0x010, 0x210, 0x10)]},
#     "pgm": {
#       "label_offset": 0,
#       "addresses": [range(0x210, 0x310, 0x10)],
#       "template": {"on": False, "pulse": False},
#     },
#     "partition": {"label_offset": 0, "addresses": [range(0x310, 0x330, 0x10)]},
#     "user": {"label_offset": 0, "addresses": [range(0x330, 0x530, 0x10)]},
#     "module": {"label_offset": 0, "addresses": [range(0x530, 0x620, 0x10)]},
#     "repeater": {"label_offset": 0, "addresses": [range(0x620, 0x640, 0x10)]},
#     "keypad": {"label_offset": 0, "addresses": [range(0x640, 0x6C0, 0x10)]},
#     "site": {"label_offset": 0, "addresses": [range(0x6C0, 0x6D0, 0x10)]},
#     "siren": {"label_offset": 0, "addresses": [range(0x6D0, 0x700, 0x10)]},
#   },
# }

mem_map = {
  "labels": {
    "zone": {
      "label_offset": 0,
      "addresses": [range(0x2000, 0x22F0, 0x10)],
    },
  },
  "definitions": {
    "zone": {"addresses": [range(0x01F0, 0x024E + 2, 2)]},  # EVO48
    "partition": {
      "bit_encoded": True,
      "addresses": [[0x39D8]],  # All 8 partitions as bits
    },
    "user": {"addresses": [range(0x0BF0, 0x0FA6 + 10, 10),]},  # 96 users
  },
}

class DigiplexInfo:
  def __init__(self, **kwargs):
    self.product_id = kwargs.get('product_id', 0)
    self.software_version = kwargs.get('software_version', 0)
    self.software_revision = kwargs.get('software_revision', 0)
    self.software_id = kwargs.get('software_id', 0)
    self.modem_speed = kwargs.get('modem_speed', 0)
    self.winload_type_id = kwargs.get('winload_type_id', 0)
    self.memory_map_version = kwargs.get('memory_map_version', 0)
    self.event_list_version = kwargs.get('event_list_version', 0)
    self.firware_build_version = kwargs.get('firware_build_version', 0)
    self.module_serial_number = kwargs.get('module_serial_number', 0)

class DigiplexState:
  def __init__(self, device, baud=19200, password="", reopen_ival=None, debug=False):
    self.state = 'init'
    self.device = device
    self.baud = baud
    self.password = password
    self.attempt = 0
    self.info = DigiplexInfo()
    self.reopen_ival = reopen_ival
    self.memory_map = bytearray(MEMMAP_SIZE)
    self.read_addr = None
    self.sent_pdu = None
    self.last_pdu = None
    self.wait_pdu = None
    self.buf = bytearray()
    self.debug = debug
    self.labels = {
      "zone": {},
      "pgm": {},
      "partition": {},
      "user": {},
      "module": {},
      "repeater": {},
      "keypad": {},
      "site": {},
      "siren": {}
    }
    self.zones_status = {}
    # Add other fields as needed

  def set_info(self, pdu):
    self.info = DigiplexInfo(
      product_id=pdu.product_id,
      software_version=pdu.software_version,
      software_revision=pdu.software_revision,
      software_id=pdu.software_id,
      modem_speed=pdu.modem_speed,
      winload_type_id=pdu.winload_type_id,
      memory_map_version=pdu.memory_map_version,
      event_list_version=pdu.event_list_version,
      firware_build_version=pdu.firware_build_version,
      module_serial_number=pdu.module_serial_number
    )

  def set_memory_map(self, pdu):
    if pdu.address & 0x8000 == 0x8000:
      addr = pdu.address & 0x7fff
      data = pdu.data
      size = len(data)
      if addr < (MEMMAP_SIZE - 32):
        self.memory_map[addr:addr+size] = data

class DigiplexServer:
  def __init__(self, device, baud=19200, password="", debug=False):
    self.state = DigiplexState(device, baud, password, None, debug)
    self.ping_interval = 1
    self.serial_port = None

  def close(self):
    """
    Chiude la porta seriale e resetta lo stato.
    """
    if self.serial_port:
      try:
        self.serial_port.close()
        print("Serial port closed.")
      except Exception as e:
        print(f"Error closing serial port: {e}")
      self.serial_port = None
    self.state.state = 'closed'

  def open_uart(self):
    try:
      self.serial_port = serial.Serial(
        self.state.device,
        self.state.baud,
        bytesize=serial.EIGHTBITS,
        parity=serial.PARITY_NONE,
        stopbits=serial.STOPBITS_ONE,
        timeout=1
      )
      self.flush_uart()
      self.send_init_string()
      self.state.state = 'init'
      self.state.attempt = 0
      self.receive_and_handle_pdu()
      return True
    except Exception as e:
      print(f"UART open error: {e}")
      if self.state.reopen_ival is not None:
        print(f"Retrying in {self.state.reopen_ival} ms...")
        time.sleep(self.state.reopen_ival / 1000)
        return self.open_uart()
      return False

  def flush_uart(self):
    if self.serial_port:
      self.serial_port.reset_input_buffer()
      self.serial_port.reset_output_buffer()

  def send_init_string(self):
    INIT_STRING = bytes([
      0x5F, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x00, 0x00, 0x00, 0x00, 0x7F
    ])
    if self.serial_port:
      if (self.state.debug):
        print(f"[TRACE] [TX]: {INIT_STRING.hex()}")
      self.serial_port.write(INIT_STRING)
      print("Init string sent.")
      # # ack read
      # packet = self.serial_port.read(37)
      # if self.state.debug:
      #   print(f"[TRACE] [TX ACK]: {packet.hex()}")

  def init(self):
    success = self.open_uart()
    if not success:
      print("Failed to initialize Digiplex server.")
    else:
      print("Digiplex server initialized and waiting for response.")

  def login(self):
    """
    Invia il messaggio di login al pannello dopo l'init.
    """
    if not self.serial_port:
      print("Serial port not initialized. Call init() first.")
      return
    # Crea messaggio DigiplexInit con password
    pdu = DigiplexInit(password=int(self.state.password))
    self.send_pdu(pdu)
    print("Login message sent.")

  def handle_pdu(self, pdu):
    """
    Gestisce la transizione di stato in base al tipo di PDU ricevuto.
    """
    # Stato INIT o LOGIN: ricezione DigiplexInit
    if isinstance(pdu, DigiplexInit) and self.state.state in ['init', 'login']:
      print(f"init pdu = {pdu}")
      if self.state.attempt > 4:
        print("Too many attempts, closing UART and retrying...")
        if self.serial_port:
          self.serial_port.close()
        self.init()
      else:
        print("digiplex: logging in")
        self.state.attempt += 1
        self.login()
        self.state.state = 'login'
    # Stato LOGIN: ricezione DigiplexLoginResp
    elif isinstance(pdu, DigiplexLoginResp) and self.state.state == 'login':
      print(f"login pdu = {pdu}")
      self.state.state = 'up'
    # Stato READ: ricezione DigiplexReadResp
    elif isinstance(pdu, DigiplexReadResp) and self.state.state == 'read':
      self.state.state = 'up'

  def do_ram_dump(self):
    """
    Inizia la lettura della RAM inviando la prima richiesta.
    """
    try:
      os.remove("ram_dump.bin")
    except FileNotFoundError:
      pass
    if not self.serial_port:
      print("Serial port not initialized. Call init() first.")
      return

    self.state.state = 'load_ram'
    addresses = enumerate(range(0x0, 0x8000, 0x20))
    for i, addr in addresses:
      print(f"digiplex: read RAM addr={addr}")
      pdu = DigiplexReadReq(count=32, bus_address=0, address=addr | 0x8000)
      self.send_pdu(pdu)
      self.receive_and_handle_pdu()
      # Salva tutti i blocchi RAM in un unico file
      try:
        with open("ram_dump.bin", "ab") as f:
          f.write(self.state.last_pdu.data)
        print(f"RAM block appended to ram_dump.bin")
      except Exception as e:
        print(f"Error saving RAM block: {e}")

  def do_eeprom_dump(self):
    """
    Inizia la lettura della EEPROM inviando la prima richiesta.
    """
    try:
      os.remove("eeprom_dump.bin")
    except FileNotFoundError:
      pass
    if not self.serial_port:
      print("Serial port not initialized. Call init() first.")
      return
    self.state.state = 'load_eeprom'
    addresses = enumerate(range(0x0, 0x8000, 0x20))
    for i, addr in addresses:
      print(f"digiplex: read EEPROM addr={addr}")
      pdu = DigiplexReadReq(count=32, bus_address=0, address=addr)
      self.send_pdu(pdu)
      self.receive_and_handle_pdu()
      # Salva tutti i blocchi RAM in un unico file
      try:
        with open("eeprom_dump.bin", "ab") as f:
          f.write(self.state.last_pdu.data)
        print(f"EEPROM block appended to eeprom_dump.bin")
      except Exception as e:
        print(f"Error saving EEPROM block: {e}")

  def load_all_definitions(self):
    """
    Legge tutte le label (zone, partizioni, ecc.) secondo i range definiti.
    Salva le label in un dizionario e le stampa.
    """
    self.state.state = 'load_definitions'
    self.state.labels = {}
    for label_type, info in mem_map['labels'].items():
      self.state.labels[label_type] = {}
      addresses = enumerate(chain.from_iterable(info["addresses"]), start=0)

      for i, addr in addresses:
        addr = info['label_offset'] + addr
        pdu = DigiplexReadReq(count=16, bus_address=0, address=addr)
        self.send_pdu(pdu)
        self.receive_and_handle_pdu()
        # Accedi al pdu ricevuto tramite self.state.last_pdu
        last_pdu = self.state.last_pdu
        if hasattr(last_pdu, 'data'):
          label_bytes = last_pdu.data[:16]
          try:
            label = label_bytes.decode('ascii', errors='ignore').strip('\0 ')
          except Exception:
            label = '<decode error>'
          self.state.labels[label_type][i] = label
          print(f"{label_type} {i+1}: '{label}' (addr: {hex(addr)})")
        else:
          print(f"{label_type} {i+1}: <no data> (addr: {hex(addr)})")

  def send_pdu(self, pdu):
    """
    Codifica e invia un PDU sulla seriale, aggiorna lo stato sent_pdu.
    """
    if not self.serial_port:
      print("Serial port not initialized. Call init() first.")
      return
    data = encode_pdu(pdu)
    data = add_checksum(data)
    if (self.state.debug):
      print(f"[TRACE] [TX]: {data.hex()}")
    self.serial_port.write(data)
    self.state.sent_pdu = pdu
    # # ack read
    # packet = self.serial_port.read(37)
    # if self.state.debug:
    #   print(f"[TRACE] [TX ACK]: {packet.hex()}")

  def receive_and_handle_pdu(self):
    """
    Riceve un pacchetto dalla seriale, verifica il checksum e chiama handle_pdu.
    """
    if not self.serial_port:
      print("Serial port not initialized. Call init() first.")
      return
    # Leggi 36 byte di dati + 1 byte CRC
    max_attempts = 5
    attempts = 0
    packet = b''
    while attempts < max_attempts:
      packet = self.serial_port.read(37)
      if self.state.debug:
        print(f"[TRACE] [RX]: {packet.hex()}")
      if len(packet) == 37:
        break
      attempts += 1
      print(f"Incomplete packet received (attempt {attempts}/{max_attempts})")
      time.sleep(0.1)
    if len(packet) != 37:
      print(f"Error: failed to receive complete packet after {max_attempts} attempts.")
      return
    data, crc = packet[:36], packet[36]
    # Verifica checksum
    calc_crc = sum(data) & 0xff
    if calc_crc != crc:
      print(f"CRC error: received={crc}, calculated={calc_crc}")
      return
    try:
      pdu = decode_pdu(data)
      self.state.last_pdu = pdu
      self.handle_pdu(pdu)
    except Exception as e:
      print(f"Decode error: {e}")

  def main_loop(self):
    """
    Flusso principale: init, login, lettura EEPROM, lettura RAM, polling.
    """

    self.init()
    self.login()
    # Attendi la risposta di login e gestisci PDU finché non si passa a 'up'
    while self.state.state != 'up':
      self.receive_and_handle_pdu()

    # Leggi le varie labels (sequenza di DigiplexReadResp finché non si passa a 'up')
    self.load_all_definitions()

    # Leggi la RAM (sequenza di DigiplexReadResp finché non si passa a 'up')
    self.do_ram_dump()

    # # Leggi la RAM (sequenza di DigiplexReadResp finché non si passa a 'up')
    self.do_eeprom_dump()

    self.state.state = 'up'

    print("Panel is UP. Entering polling loop...")
    while self.state.state == 'up':
      # Ogni secondo invia una lettura RAM all'indirizzo 153
      pdu_read = DigiplexReadReq(count=12, bus_address=0, address=0x8000 | 0x153)
      self.send_pdu(pdu_read)
      self.receive_and_handle_pdu()
      self.update_zone_status(self.state.last_pdu.data)
      self.show_not_ok_zones()
      time.sleep(self.ping_interval)

  def update_zone_status(self, data):
    zone_status = bit_info(data[:6])
    tamper_status = bit_info(data[7:12])
    for i, (z, t) in enumerate(zip(zone_status, tamper_status), 1):
      val = z*2 + t
      if val == 0:
        info = 'ok'
      elif val == 1:
        info = 'tamper'
      elif val == 2:
        info = 'open'
      elif val == 3:
        info = 'fire_loop'
      self.state.zones_status[i] = info

  def show_not_ok_zones(self):
    """
    Stampa tutte le zone non ok, mostrando la label e lo stato.
    """
    for zone_id, status in self.state.zones_status.items():
      if status != 'ok':
        label = self.state.labels.get('zone', {}).get(zone_id - 1, f"Zona {zone_id}")
        print(f"Zona {zone_id}: '{label}' - Stato: {status}")

  async def get_memory_map(self):
    return self.state.memory_map

  async def get_info(self):
    return vars(self.state.info)

def bit_info(bin_data):
  bits = []
  for byte in bin_data:
    bits.extend([(byte >> i) & 1 for i in range(8)])
  return bits
