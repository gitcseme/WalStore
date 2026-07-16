namespace WalStore.Wal.Contracts;

public interface IWalRecordSerializer
{
    byte[] SerializeWithSizePrefix(WalRecord record);
    int ReadSize(ReadOnlySpan<byte> header);
    WalRecord Deserialize(ReadOnlyMemory<byte> data);
}
