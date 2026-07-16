using System.IO.Hashing;
using WalStore.Wal.Contracts;

namespace WalStore.Wal.Checksum;

public sealed class Crc32ChecksumProvider : IChecksumProvider
{
    public uint Compute(ReadOnlySpan<byte> data, ulong lsn)
    {
        var crc = new Crc32();
        crc.Append(data);
        crc.Append(BitConverter.GetBytes(lsn));
        return crc.GetCurrentHashAsUInt32();
    }
}
