import binascii

# Leggi il dump RAM dal file
with open("ram_dump.bin", "rb") as f:
    ram = f.read()

# Zone status: offset 0x153, 6 byte
zone_status = ram[0x153:0x153+6]
# Tamper status: offset 0x159, 6 byte
tamper_status = ram[0x159:0x159+6]

def bit_info(bin_data):
    bits = []
    for byte in bin_data:
        bits.extend([(byte >> i) & 1 for i in reversed(range(8))])
    return bits

zones = bit_info(zone_status)
tampers = bit_info(tamper_status)

for i, (z, t) in enumerate(zip(zones, tampers), 1):
    val = z*2 + t
    if val == 0:
        info = 'ok'
    elif val == 1:
        info = 'tamper'
    elif val == 2:
        info = 'open'
    elif val == 3:
        info = 'fire_loop'
    print(f"Zone {i}: {info}")
