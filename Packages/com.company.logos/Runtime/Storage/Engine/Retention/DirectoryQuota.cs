using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Logos.Storage
{
    /// <summary>所有者观察到的物理用量。分类来自认证，不能仅凭枚举判断。</summary>
    public sealed class DirectoryQuota : IJournalDirectoryAdmission, IDisposable
    {
        private struct DayUse
        {
            internal bool Valid;
            internal long Day,
                Physical,
                Normal,
                Error;
        }

        private readonly DayUse[] days = new DayUse[1024];

        /// <summary>保守数组存储：每个 bool 加四个 long 的条目按 48 字节计，包括对齐填充。</summary>
        public int FixedBufferPayloadBytes => days.Length * 48;
        private readonly IJournalFileSystem files;
        private readonly string directory;
        private IEnumerator<string> entries;
        private bool unsafeEntry;
        private long nextScanAttemptTimestamp;
        public bool IsReady
        {
            get;
            private set;
        }
        // 普通分页观察算作进展；失败扫描等待低频重试则不算。
        internal bool InventoryInProgress => !IsReady && nextScanAttemptTimestamp == 0;
        public int LastStepEntries
        {
            get;
            private set;
        }
        public long ActualBytes
        {
            get;
            private set;
        }
        public long MetadataBytes
        {
            get;
            private set;
        }
        public long NormalBytes
        {
            get;
            private set;
        }
        public long ErrorBytes
        {
            get;
            private set;
        }
        public long UnknownBytes
        {
            get;
            private set;
        }

        public DirectoryQuota(IJournalFileSystem files, string directory)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
        }

        public void RestartScan()
        {
            entries?.Dispose();
            entries = null;
            IsReady = false;
            nextScanAttemptTimestamp = 0;
            unsafeEntry = false;
            ActualBytes = MetadataBytes = NormalBytes = ErrorBytes = UnknownBytes = 0;
            for (int i = 0; i < days.Length; i++)
                days[i].Physical = 0;
        }

        public void Step(JournalStepContext context) => StepAt(context, Stopwatch.GetTimestamp());

        internal void StepAt(JournalStepContext context, long timestamp)
        {
            LastStepEntries = 0;
            if (IsReady || context.ShouldYield || timestamp < nextScanAttemptTimestamp)
                return;
            try
            {
                ScanEntries(context);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // MoveNext 可能已经推进：任何观察失败后，都不能再信任部分扫描结果。
                RestartScan();
                nextScanAttemptTimestamp =
                    timestamp > long.MaxValue - 30 * Stopwatch.Frequency
                        ? long.MaxValue
                        : timestamp + 30 * Stopwatch.Frequency;
            }
        }

        private void ScanEntries(JournalStepContext context)
        {
            if (entries == null)
                entries = files.EnumerateEntries(directory).GetEnumerator();
            while (LastStepEntries < 8 && !context.ShouldYield)
            {
                if (!entries.MoveNext())
                {
                    entries.Dispose();
                    entries = null;
                    IsReady = true;
                    return;
                }

                LastStepEntries++;
                string path = entries.Current;
                var info = files.GetEntryInfo(path);
                if (!info.IsRegularFile)
                {
                    unsafeEntry = true;
                    continue;
                }

                long length = info.Length;
                ActualBytes = checked(ActualBytes + length);
                string name = Path.GetFileName(path);
                if (name == ".journal-state" || name == ".owner.lock")
                {
                    MetadataBytes = checked(MetadataBytes + length);
                    continue;
                }

                if (TryDay(name, out long day))
                {
                    int slot = (int)(day % 1024);
                    var use = days[slot];
                    if (use.Valid && use.Day == day)
                    {
                        use.Physical = length;
                        days[slot] = use;
                        AddClassified(use, 1);
                        continue;
                    }
                }

                UnknownBytes = checked(UnknownBytes + length);
            }
        }

        internal static bool TryDay(string name, out long day)
        {
            bool valid = DateTime.TryParseExact(
                name,
                "yyyy-MM-dd'.logos'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date
            );
            day = valid ? date.Ticks / TimeSpan.TicksPerDay : 0;
            return valid;
        }

        public void ClassifyDay(long day, long normal, long error)
        {
            if (day < 0 || normal < 0 || error < 0 || normal > long.MaxValue - error)
                throw new ArgumentOutOfRangeException();
            int slot = (int)(day % 1024);
            var use = days[slot];
            if (use.Valid && use.Day != day && use.Physical != 0)
                throw new InvalidOperationException("Occupied day slot.");
            if (IsReady && use.Valid)
                AddClassified(use, -1);
            use.Valid = true;
            use.Day = day;
            use.Normal = normal;
            use.Error = error;
            days[slot] = use;
            if (IsReady)
                AddClassified(use, 1);
        }

        public void ObserveDay(long day, long physical, long normal, long error)
        {
            if (physical < 0)
                throw new ArgumentOutOfRangeException(nameof(physical));
            int slot = (int)(day % 1024);
            var old = days[slot];
            ClassifyDay(day, normal, error);
            if (IsReady)
            {
                AddClassified(days[slot], -1);
                ActualBytes = checked(ActualBytes - old.Physical + physical);
            }

            days[slot].Physical = physical;
            if (IsReady)
                AddClassified(days[slot], 1);
        }

        internal void ObserveUnknownDeleted(long bytes)
        {
            ActualBytes -= bytes;
            UnknownBytes -= bytes;
        }

        private void AddClassified(DayUse use, int direction)
        {
            // 有凭据支持的普通与错误用量合计不能超过实际文件长度。物理尾部多出的字节留在未知类别，直至恢复扫描完成。
            long normal = Math.Min(use.Normal, use.Physical);
            long error = Math.Min(use.Error, use.Physical - normal);
            NormalBytes += direction * normal;
            ErrorBytes += direction * error;
            UnknownBytes += direction * (use.Physical - normal - error);
        }

        public bool CanAppend(JournalAdmission r)
        {
            if (
                !IsReady
                || unsafeEntry
                || MetadataBytes > StorageOptions.MetadataBudgetBytes
                || r.WireBytes < 0
            )
                return false;
            long ordinary = r.HeaderBytes + (r.IsErrorClass ? 0L : r.WireBytes);
            long error = r.IsErrorClass ? r.WireBytes : 0;
            var o = r.EffectiveOptions;
            // 未知字节可能属于任一分区，检查类别额度时必须保守计入；检查目录总量时只计一次实际占用。
            return Fits(ActualBytes, r.HeaderBytes + (long)r.WireBytes, o.MaxDirectoryBytes)
                && Fits(NormalBytes, UnknownBytes, o.DirectoryNormalBytes)
                && Fits(NormalBytes + UnknownBytes, ordinary, o.DirectoryNormalBytes)
                && Fits(ErrorBytes, UnknownBytes, o.DirectoryErrorBytes)
                && Fits(ErrorBytes + UnknownBytes, error, o.DirectoryErrorBytes);
        }

        private static bool Fits(long used, long addition, long limit) =>
            used >= 0 && addition >= 0 && used <= limit && addition <= limit - used;

        public void Dispose()
        {
            entries?.Dispose();
            entries = null;
        }
    }
}
