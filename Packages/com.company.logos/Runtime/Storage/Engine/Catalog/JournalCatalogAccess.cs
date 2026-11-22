using System;
using System.IO;

namespace Logos.Storage
{
    public sealed partial class JournalSession
    {
        private void CloseAllCatalogs()
        {
            Exception failure = null;
            foreach (var cursor in catalogs)
                DisposeOne(cursor, ref failure);
            if (failure != null)
                throw failure;
        }

        // 打开快照时固定可见范围。写入器后续追加的数据要通过新游标才能看到。
        public CommittedBlockCatalog OpenCatalog(long day) => OpenCatalogSnapshot(day, null);

        internal CommittedBlockCatalog OpenCatalogSnapshot(long day, long? capturedEnd, IJournalSourceReadAuthorization authorization = null)
        {
            CheckThread();
            if (Status != JournalStatus.Ready)
                throw new InvalidOperationException("Catalog is not ready.");
            VerifyOwner();
            int cursorSlot = Array.FindIndex(catalogs, c => c == null);
            if (cursorSlot < 0 || Clear.IsActive)
                throw new InvalidOperationException("The bounded read leases are unavailable.");
            if (day < 0)
                throw new ArgumentOutOfRangeException(nameof(day));
            var summary = summaries[day % 1024];
            if (
                !summary.Valid
                || summary.Tombstone
                || summary.DayId != day
                || summary.ConfirmedEnd < 512
            )
                throw new InvalidOperationException("No confirmed data for this day.");
            if (capturedEnd.HasValue && (capturedEnd.Value < 512 || capturedEnd.Value > summary.ConfirmedEnd))
                throw new StorageFormatException();
            Stream stream = null;
            KeyMaterial key = null;
            try
            {
                if (authorization != null && !authorization.TryConsume(512))
                    throw new JournalSourceCreditException();
                stream = files.Open(DayPath(day), FileMode.Open, FileAccess.Read);
                var bytes = new byte[512];
                JournalIo.ReadExactly(stream, bytes, 0, 512);
                key = JournalIo.Acquire(keys, JournalIo.ReadKeyId(bytes, 64, 65));
                var header = FileHeaderCodec.Verify(bytes, key);
                if (header.DayId != day || !Wire.Equal(header.DirectoryId, global.DirectoryId))
                    throw new StorageFormatException();
                long cleanupGeneration = CleanupGeneration;
                ReadLeaseRegistry.ReadLease readLease = null;
                var catalog = new CommittedBlockCatalog(
                    stream,
                    key,
                    header,
                    codec,
                    scratch,
                    capturedEnd ?? summary.ConfirmedEnd,
                    summary.ConfirmedEnd,
                    cleanupGeneration,
                    () =>
                    {
                        CheckThread();
                        VerifyOwner();
                        readers.RequestExpired(System.Diagnostics.Stopwatch.GetTimestamp());
                        if (
                            Status != JournalStatus.Ready
                            || cleanupGeneration != CleanupGeneration
                            || readLease.IsCancellationRequested
                        )
                            throw new InvalidOperationException("Catalog generation changed.");
                    },
                    CheckThread,
                    () =>
                    {
                        catalogs[cursorSlot] = null;
                        readLease.ReleaseAfterClosed();
                    }
                );
                catalog.ReadAuthorization = authorization;
                readLease = readers.Register(
                    catalog,
                    cleanupGeneration,
                    System.Diagnostics.Stopwatch.GetTimestamp()
                );
                catalogs[cursorSlot] = catalog;
                stream = null;
                key = null;
                return catalog;
            }
            finally
            {
                try
                {
                    stream?.Dispose();
                }
                finally
                {
                    key?.Dispose();
                }
            }
        }
    }
}
