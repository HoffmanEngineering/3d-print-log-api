using System.Buffers.Binary;
using System.Text;

namespace PrintLogApi.SpoolTags.Decoding;

public sealed record NdefRecord(byte Tnf, string Type, byte[] Payload);

public sealed record NdefMessage(IReadOnlyList<NdefRecord> Records);

/// <summary>
/// Finds and parses the NDEF message on a Type 2 or NFC-V tag. Never throws: memory that is
/// truncated, malformed or uses chunked records yields null.
/// </summary>
public static class NdefReader
{
    private const byte CapabilityMagic = 0xE1;
    private const byte CapabilityMagicTwoByteAddress = 0xE2;
    private const int Type2CapabilityOffset = 12;
    private const int Type2TlvStart = 16;
    private const int NfcVShortTlvStart = 4;
    private const int NfcVLongTlvStart = 8;

    private const byte TlvNull = 0x00;
    private const byte TlvLockControl = 0x01;
    private const byte TlvMemoryControl = 0x02;
    private const byte TlvNdef = 0x03;
    private const byte TlvProprietary = 0xFD;
    private const byte TlvTerminator = 0xFE;

    private const byte FlagMessageEnd = 0x40;
    private const byte FlagChunked = 0x20;
    private const byte FlagShortRecord = 0x10;
    private const byte FlagIdLength = 0x08;
    private const byte TnfMask = 0x07;

    /// <summary>Null when there is no NDEF message; an empty message when the TLV length is 0.</summary>
    public static NdefMessage? Find(TagMemory memory)
    {
        var data = memory.Data;
        var start = memory.Kind switch
        {
            TagMemoryKind.Type2 => data.Length > Type2CapabilityOffset && data[Type2CapabilityOffset] == CapabilityMagic
                ? Type2TlvStart
                : -1,
            TagMemoryKind.NfcV => NfcVStart(data),
            _ => -1,
        };

        return start < 0 ? null : FindTlv(data, start);
    }

    private static int NfcVStart(byte[] data)
    {
        if (data.Length < 4 || (data[0] != CapabilityMagic && data[0] != CapabilityMagicTwoByteAddress))
        {
            return -1;
        }

        return data[2] != 0 ? NfcVShortTlvStart : NfcVLongTlvStart;
    }

    private static NdefMessage? FindTlv(byte[] data, int position)
    {
        while (position < data.Length)
        {
            var tag = data[position++];
            switch (tag)
            {
                case TlvTerminator:
                    return null;
                case TlvNull:
                    continue;
                case TlvLockControl or TlvMemoryControl or TlvNdef or TlvProprietary:
                    break;
                default:
                    // An unknown tag carries no length that can be trusted.
                    return null;
            }

            if (position >= data.Length)
            {
                return null;
            }

            int length = data[position++];
            if (length == 0xFF)
            {
                if (position + 2 > data.Length)
                {
                    return null;
                }

                length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2));
                position += 2;
            }

            if (length > data.Length - position)
            {
                return null;
            }

            if (tag == TlvNdef)
            {
                return ParseMessage(data.AsSpan(position, length));
            }

            position += length;
        }

        return null;
    }

    private static NdefMessage? ParseMessage(ReadOnlySpan<byte> message)
    {
        var records = new List<NdefRecord>();
        var position = 0;
        while (position < message.Length)
        {
            var flags = message[position++];
            if ((flags & FlagChunked) != 0 || position >= message.Length)
            {
                return null;
            }

            int typeLength = message[position++];
            long payloadLength;
            if ((flags & FlagShortRecord) != 0)
            {
                if (position >= message.Length)
                {
                    return null;
                }

                payloadLength = message[position++];
            }
            else
            {
                if (position + 4 > message.Length)
                {
                    return null;
                }

                payloadLength = BinaryPrimitives.ReadUInt32BigEndian(message.Slice(position, 4));
                position += 4;
            }

            var idLength = 0;
            if ((flags & FlagIdLength) != 0)
            {
                if (position >= message.Length)
                {
                    return null;
                }

                idLength = message[position++];
            }

            if (typeLength + idLength + payloadLength > message.Length - position)
            {
                return null;
            }

            var type = Encoding.ASCII.GetString(message.Slice(position, typeLength));
            position += typeLength + idLength;
            var payload = message.Slice(position, (int)payloadLength).ToArray();
            position += (int)payloadLength;
            records.Add(new NdefRecord((byte)(flags & TnfMask), type, payload));

            if ((flags & FlagMessageEnd) != 0)
            {
                return new NdefMessage(records);
            }
        }

        // An empty message is valid; records with no end marker are truncated.
        return records.Count == 0 ? new NdefMessage(records) : null;
    }
}
