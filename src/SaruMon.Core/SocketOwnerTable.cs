using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SaruMon.Core;

internal static class SocketOwnerTable
{
    internal delegate uint NativeRead(byte[]? buffer, ref int length, bool sorted, int family, int tableClass, uint reserved);

    internal static ReadOnlyMemory<byte> Read(AddressFamily family, int tableClass, NativeRead read)
    {
        const uint bufferTooSmall = 122;
        var length = 0;
        var status = read(null, ref length, false, (int)family, tableClass, 0);
        for (var attempt = 0; status == bufferTooSmall && attempt < 3; attempt++)
        {
            if (length is < 4 or > 32 * 1024 * 1024) throw new InvalidDataException("ソケット表のサイズが不正です。");
            var buffer = new byte[length];
            status = read(buffer, ref length, false, (int)family, tableClass, 0);
            if (status == 0)
            {
                if (length < 4 || length > buffer.Length) throw new InvalidDataException("ソケット表のサイズが不正です。");
                return buffer.AsMemory(0, length);
            }
        }
        if (status != 0) throw new NetworkInformationException((int)status);
        return ReadOnlyMemory<byte>.Empty;
    }

    internal static int ReadCount(ReadOnlySpan<byte> bytes, int rowSize)
    {
        if (bytes.IsEmpty) return 0;
        if (bytes.Length < 4) throw new InvalidDataException("ソケット表が不完全です。");
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count < 0 || count > (bytes.Length - 4) / rowSize) throw new InvalidDataException("ソケット表の行数が不正です。");
        return count;
    }

    internal static IPAddress NormalizeAddress(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
