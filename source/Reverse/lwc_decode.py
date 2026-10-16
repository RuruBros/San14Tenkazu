"""LWC decoder, transcribed from SAN14CE_KO FUN 0x9330A0 (literal/offset code) and 0x933AF0 (length code)."""
import struct, sys

class Bits:
    def __init__(self, data, pos):
        self.data = data; self.pos = pos; self.cur = 0; self.left = 0
    def bit(self):
        if self.left == 0:
            self.cur = self.data[self.pos]; self.pos += 1; self.left = 8
        self.left -= 1
        b = self.cur >> 7
        self.cur = (self.cur << 1) & 0xFF
        return b
    def gamma(self):
        # unary prefix of ones ended by a zero, value accumulates the prefix bits, then as many more bits
        prefix = 0; count = 0
        while True:
            b = self.bit(); count += 1; prefix = prefix * 2 + b
            if b == 0: break
        value = 0
        for _ in range(count):
            value = value * 2 + self.bit()
        return value + prefix

def decode(data, offset, length_reader="gamma"):
    magic, unc, comp = struct.unpack_from("<4sII", data, offset)
    assert magic == b"LWC\x1a", magic
    assert comp + 0x10C == len(data) - offset, (comp, len(data) - offset)
    table = data[offset + 0xC: offset + 0x10C]
    bits = Bits(data, offset + 0x10C)
    out = bytearray()
    while len(out) < unc:
        code = bits.gamma()
        if code < 0x100:
            out.append(table[code])
        else:
            distance = code - 0x100
            if length_reader == "gamma":
                length = bits.gamma() + 3
            else:
                length = length_reader(bits) + 3
            start = len(out) - distance
            if start < 0: raise ValueError(f"bad distance {distance} at {len(out)}")
            for i in range(length):
                out.append(out[start + i])
    assert len(out) == unc, (len(out), unc)
    return bytes(out)

if __name__ == "__main__":
    path = sys.argv[1]
    d = open(path, "rb").read()
    o = d.find(b"LWC\x1a")
    out = decode(d, o)
    open(sys.argv[2], "wb").write(out)
    print("decoded", len(out), "first", out[:32].hex(" "))
