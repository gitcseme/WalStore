namespace WalStore.Wal.Contracts;

public interface IChecksumProvider
{
    uint Compute(ReadOnlySpan<byte> data, ulong lsn);
}
