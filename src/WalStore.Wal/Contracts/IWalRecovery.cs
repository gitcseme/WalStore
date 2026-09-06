using WalStore.Wal.Recovery;

namespace WalStore.Wal.Contracts;

public interface IWalRecovery
{
    /// <summary>
    /// Repairs the WAL in <paramref name="directory"/> so that it holds an unbroken,
    /// checksum-verified prefix of records. Must be called with no segment file open.
    /// </summary>
    Task<WalRecoveryReport> RecoverDirectoryAsync(string directory, CancellationToken ct = default);
}
