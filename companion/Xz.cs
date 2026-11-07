namespace FlexScreen.Companion;

/// <summary>
/// Unpacks .xz data (one block, LZMA2), which is how Vial keyboards keep their definition
/// (vial.json through Python's lzma.compress). .NET has no LZMA, and the definition is a few
/// KB, so this is the plain decoder from the LZMA spec over a growing buffer. The xz check
/// (CRC32 or CRC64 of the output) is verified, so a decoding mistake can't pass unnoticed.
/// </summary>
static class Xz
{
    public static byte[] Decode(byte[] data)
    {
        ReadOnlySpan<byte> magic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];
        if (data.Length < 24 || !data.AsSpan(0, 6).SequenceEqual(magic)) throw new InvalidDataException("not xz data");
        var check = data[7] & 0x0F;

        // The block header: its size, flags, optional sizes, then one filter, LZMA2 (0x21) with one property byte.
        var pos = 12;
        var headerSize = (data[pos] + 1) * 4;
        var flags = data[pos + 1];
        var p = pos + 2;
        if ((flags & 0x40) != 0) Vli(data, ref p);
        if ((flags & 0x80) != 0) Vli(data, ref p);
        if ((flags & 0x03) != 0) throw new InvalidDataException("xz filter chain");
        if (Vli(data, ref p) != 0x21 || Vli(data, ref p) != 1) throw new InvalidDataException("not LZMA2");
        pos += headerSize;

        var output = Lzma2(data, ref pos);

        while (pos % 4 != 0) pos++;  // block padding (the stream header and block header are whole words)
        var ok = check switch
        {
            0x00 => true,
            0x01 => BitConverter.ToUInt32(data, pos) == Crc32(output),
            0x04 => BitConverter.ToUInt64(data, pos) == Crc64(output),
            _ => true,  // SHA-256: not checked
        };
        if (!ok) throw new InvalidDataException("xz check failed");
        return output;
    }

    static ulong Vli(byte[] data, ref int pos)
    {
        ulong value = 0;
        for (var shift = 0; shift < 63; shift += 7)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("xz number");
    }

    static byte[] Lzma2(byte[] data, ref int pos)
    {
        var lzma = new Lzma(data);
        var haveProps = false;
        while (true)
        {
            int control = data[pos++];
            if (control == 0x00) return lzma.Output.ToArray();
            if (control is 0x01 or 0x02)  // stored: 0x01 also resets the dictionary
            {
                if (control == 0x01) lzma.ResetDictionary();
                var size = ((data[pos] << 8) | data[pos + 1]) + 1;
                pos += 2;
                lzma.Output.AddRange(data.AsSpan(pos, size));
                pos += size;
                continue;
            }
            if (control < 0x80) throw new InvalidDataException("LZMA2 control byte");
            var unpacked = ((control & 0x1F) << 16) + (data[pos] << 8) + data[pos + 1] + 1;
            var packed = ((data[pos + 2] << 8) | data[pos + 3]) + 1;
            pos += 4;
            var reset = (control >> 5) & 3;  // 1 state, 2 state and properties, 3 those and the dictionary
            if (reset == 3) lzma.ResetDictionary();
            if (reset >= 2)
            {
                lzma.SetProperties(data[pos++]);
                haveProps = true;
            }
            if (!haveProps) throw new InvalidDataException("LZMA2 without properties");
            if (reset >= 1) lzma.ResetState();
            lzma.Chunk(pos, unpacked);
            pos += packed;
        }
    }

    sealed class Lzma(byte[] input)
    {
        const int States = 12;
        const ushort Half = 1024;  // probabilities are 11-bit, starting at one half

        public readonly List<byte> Output = [];
        int _dictStart;
        int _pos;
        uint _range, _code;
        int _lc, _lp, _pb;
        int _state;
        uint _rep0, _rep1, _rep2, _rep3;

        ushort[] _literal = [];
        readonly ushort[] _isMatch = new ushort[States << 4];
        readonly ushort[] _isRep = new ushort[States];
        readonly ushort[] _isRepG0 = new ushort[States];
        readonly ushort[] _isRepG1 = new ushort[States];
        readonly ushort[] _isRepG2 = new ushort[States];
        readonly ushort[] _isRep0Long = new ushort[States << 4];
        readonly ushort[] _posSlot = new ushort[4 << 6];
        readonly ushort[] _posSpecial = new ushort[1 + 128 - 14];
        readonly ushort[] _align = new ushort[16];
        readonly Length _length = new(), _repLength = new();

        sealed class Length
        {
            public readonly ushort[] Choice = new ushort[2];
            public readonly ushort[] Low = new ushort[16 << 3];
            public readonly ushort[] Mid = new ushort[16 << 3];
            public readonly ushort[] High = new ushort[256];
        }

        public void ResetDictionary() => _dictStart = Output.Count;

        public void SetProperties(byte b)
        {
            _lc = b % 9;
            b /= 9;
            _lp = b % 5;
            _pb = b / 5;
            if (_lc + _lp > 4 || _pb > 4) throw new InvalidDataException("LZMA properties");
            _literal = new ushort[0x300 << (_lc + _lp)];
        }

        public void ResetState()
        {
            foreach (var probs in new[] { _literal, _isMatch, _isRep, _isRepG0, _isRepG1, _isRepG2, _isRep0Long, _posSlot, _posSpecial, _align,
                         _length.Choice, _length.Low, _length.Mid, _length.High, _repLength.Choice, _repLength.Low, _repLength.Mid, _repLength.High })
            {
                Array.Fill(probs, Half);
            }
            _state = 0;
            _rep0 = _rep1 = _rep2 = _rep3 = 0;
        }

        // Each LZMA2 chunk starts the range decoder afresh; state and dictionary carry over.
        public void Chunk(int start, int unpacked)
        {
            _pos = start + 1;  // the first byte is always 0
            _range = 0xFFFFFFFF;
            _code = 0;
            for (var i = 0; i < 4; i++) _code = (_code << 8) | input[_pos++];

            var end = Output.Count + unpacked;
            while (Output.Count < end)
            {
                var posState = (Output.Count - _dictStart) & ((1 << _pb) - 1);
                if (Bit(_isMatch, (_state << 4) + posState) == 0)
                {
                    Literal();
                    _state = _state < 4 ? 0 : _state < 10 ? _state - 3 : _state - 6;
                    continue;
                }
                int len;
                if (Bit(_isRep, _state) != 0)
                {
                    if (Bit(_isRepG0, _state) == 0)
                    {
                        if (Bit(_isRep0Long, (_state << 4) + posState) == 0)  // one byte from rep0
                        {
                            _state = _state < 7 ? 9 : 11;
                            Output.Add(Back(_rep0));
                            continue;
                        }
                    }
                    else
                    {
                        uint dist;
                        if (Bit(_isRepG1, _state) == 0)
                        {
                            dist = _rep1;
                        }
                        else
                        {
                            if (Bit(_isRepG2, _state) == 0)
                            {
                                dist = _rep2;
                            }
                            else
                            {
                                dist = _rep3;
                                _rep3 = _rep2;
                            }
                            _rep2 = _rep1;
                        }
                        _rep1 = _rep0;
                        _rep0 = dist;
                    }
                    len = DecodeLength(_repLength, posState);
                    _state = _state < 7 ? 8 : 11;
                }
                else
                {
                    _rep3 = _rep2;
                    _rep2 = _rep1;
                    _rep1 = _rep0;
                    len = DecodeLength(_length, posState);
                    _state = _state < 7 ? 7 : 10;
                    _rep0 = Distance(len);
                }
                len += 2;
                if (Output.Count + len > end) throw new InvalidDataException("LZMA match past its chunk");
                for (var i = 0; i < len; i++) Output.Add(Back(_rep0));
            }
        }

        byte Back(uint distance)
        {
            if (distance >= Output.Count - _dictStart) throw new InvalidDataException("LZMA distance");
            return Output[Output.Count - (int)distance - 1];
        }

        void Literal()
        {
            var at = Output.Count - _dictStart;
            int previous = at > 0 ? Output[^1] : 0;
            var bank = 0x300 * (((at & ((1 << _lp) - 1)) << _lc) + (previous >> (8 - _lc)));
            var symbol = 1;
            if (_state >= 7)  // after a match: the byte at rep0 guides the first bits
            {
                int match = Back(_rep0);
                do
                {
                    var matchBit = (match >> 7) & 1;
                    match <<= 1;
                    var bit = Bit(_literal, bank + ((1 + matchBit) << 8) + symbol);
                    symbol = (symbol << 1) | bit;
                    if (matchBit != bit) break;
                }
                while (symbol < 0x100);
            }
            while (symbol < 0x100) symbol = (symbol << 1) | Bit(_literal, bank + symbol);
            Output.Add((byte)(symbol - 0x100));
        }

        int DecodeLength(Length l, int posState)
        {
            if (Bit(l.Choice, 0) == 0) return Tree(l.Low, posState << 3, 3);
            if (Bit(l.Choice, 1) == 0) return 8 + Tree(l.Mid, posState << 3, 3);
            return 16 + Tree(l.High, 0, 8);
        }

        uint Distance(int len)
        {
            var slot = Tree(_posSlot, Math.Min(len, 3) << 6, 6);
            if (slot < 4) return (uint)slot;
            var direct = (slot >> 1) - 1;
            var dist = (uint)((2 | (slot & 1)) << direct);
            if (slot < 14) return dist + (uint)ReverseTree(_posSpecial, (int)dist - slot, direct);
            dist += DirectBits(direct - 4) << 4;
            return dist + (uint)ReverseTree(_align, 0, 4);
        }

        int Tree(ushort[] probs, int offset, int bits)
        {
            var m = 1;
            for (var i = 0; i < bits; i++) m = (m << 1) + Bit(probs, offset + m);
            return m - (1 << bits);
        }

        int ReverseTree(ushort[] probs, int offset, int bits)
        {
            int m = 1, symbol = 0;
            for (var i = 0; i < bits; i++)
            {
                var bit = Bit(probs, offset + m);
                m = (m << 1) + bit;
                symbol |= bit << i;
            }
            return symbol;
        }

        int Bit(ushort[] probs, int i)
        {
            uint p = probs[i];
            var bound = (_range >> 11) * p;
            int bit;
            if (_code < bound)
            {
                _range = bound;
                probs[i] = (ushort)(p + ((2048 - p) >> 5));
                bit = 0;
            }
            else
            {
                _range -= bound;
                _code -= bound;
                probs[i] = (ushort)(p - (p >> 5));
                bit = 1;
            }
            Normalize();
            return bit;
        }

        uint DirectBits(int count)
        {
            uint result = 0;
            for (; count > 0; count--)
            {
                _range >>= 1;
                _code -= _range;
                var t = 0 - (_code >> 31);  // all ones when the subtraction went below zero (a 0 bit)
                _code += _range & t;
                Normalize();
                result = (result << 1) + (t + 1);
            }
            return result;
        }

        void Normalize()
        {
            if (_range >= 1u << 24) return;
            _range <<= 8;
            _code = (_code << 8) | input[_pos++];
        }
    }

    static uint Crc32(IReadOnlyList<byte> data)
    {
        var crc = ~0u;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    static ulong Crc64(IReadOnlyList<byte> data)
    {
        var crc = ~0UL;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xC96C5795D7870F42UL : crc >> 1;
        }
        return ~crc;
    }
}
