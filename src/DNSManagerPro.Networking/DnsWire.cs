using System.Buffers.Binary;
using System.Text;

namespace DNSManagerPro.Networking;

/// <summary>Minimal RFC 1035 wire format helpers, used for DNS-over-HTTPS (RFC 8484) where no resolver library is involved.</summary>
public static class DnsWire
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;

    public static byte[] BuildQuery(string name, ushort qtype, ushort id)
    {
        var labels = name.TrimEnd('.').Split('.');
        var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // standard query, recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // one question
        ms.Write(header);
        foreach (var l in labels)
        {
            var b = Encoding.ASCII.GetBytes(l);
            if (b.Length is 0 or > 63)
            {
                throw new ArgumentException($"Invalid DNS label '{l}'.", nameof(name));
            }

            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }

        ms.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, qtype);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1); // class IN
        ms.Write(tail);
        return ms.ToArray();
    }

    /// <summary>Returns the RCODE of a response, or null if the packet is too short or is not a response.</summary>
    public static int? ParseRcode(ReadOnlySpan<byte> response)
    {
        if (response.Length < 12)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        return (flags & 0x8000) == 0 ? null : flags & 0x000F;
    }

    public static string RcodeName(int rcode) => rcode switch
    {
        0 => "NoError",
        1 => "FormErr",
        2 => "ServFail",
        3 => "NXDomain",
        4 => "NotImp",
        5 => "Refused",
        _ => $"RCODE{rcode}",
    };
}
