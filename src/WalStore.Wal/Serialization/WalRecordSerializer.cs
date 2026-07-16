using System.Buffers.Binary;
using Google.Protobuf;
using WalStore.Wal.Contracts;

namespace WalStore.Wal.Serialization;

public sealed class WalRecordSerializer : IWalRecordSerializer
{
    /*
        +----4 bytes----+------Data bytes-----+
        |      ????     |      empty          |
        +---------------+---------------------+

    First 4 bytes are the size of the protobuf data, followed by the actual protobuf data.
    This helps us to know how many bytes to read for each record when reading from the WAL file
     */
    public byte[] SerializeWithSizePrefix(WalRecord record)
    {
        var protobuf = record.ToByteArray();
        var result = new byte[sizeof(int) + protobuf.Length];

        // Write the size of the protobuf data as a 4-byte integer prefix
        BinaryPrimitives.WriteInt32LittleEndian(result, protobuf.Length);

        // Copy the protobuf data into the result array after the size prefix
        protobuf.CopyTo(result.AsMemory(sizeof(int)));
        return result;
    }

    public int ReadSize(ReadOnlySpan<byte> header) =>
        BinaryPrimitives.ReadInt32LittleEndian(header);

    public WalRecord Deserialize(ReadOnlyMemory<byte> data) =>
        WalRecord.Parser.ParseFrom(data.Span);
}
