using System;

namespace Logos.Storage
{
    /// <summary>运行时类别门禁。只有实际恢复才会构造一对新门禁。</summary>
    public sealed class DailyQuotaState
    {
        private readonly long normalLimit;
        private readonly long errorLimit;
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
        public bool NormalClosed
        {
            get;
            private set;
        }
        public bool ErrorClosed
        {
            get;
            private set;
        }

        public DailyQuotaState(long fileBytes, long errorBytes, long normalUsed, long errorUsed)
        {
            if (fileBytes <= errorBytes || errorBytes < 0 || normalUsed < 0 || errorUsed < 0)
                throw new ArgumentOutOfRangeException();
            normalLimit = fileBytes - errorBytes;
            errorLimit = errorBytes;
            NormalBytes = normalUsed;
            ErrorBytes = errorUsed;
        }

        public bool TryReserve(bool errorClass, int wireBytes, bool needsHeader)
        {
            if (!CanFit(errorClass, wireBytes, needsHeader))
                return false;
            Charge(errorClass, wireBytes, needsHeader);
            return true;
        }

        internal bool CanFit(bool errorClass, int wireBytes, bool needsHeader)
        {
            if (wireBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(wireBytes));
            if (errorClass ? ErrorClosed : NormalClosed)
                return false;
            // 即使首块属于错误类别，文件头仍占普通分区；因此首块准入需要同时检查普通分区的头部空间和本类别块空间。
            long header = needsHeader ? StorageRuntimeLimits.FileHeaderBytes : 0;
            bool fits =
                NormalBytes <= normalLimit - header
                && (
                    errorClass
                        ? ErrorBytes <= errorLimit - wireBytes
                        : NormalBytes <= normalLimit - header - wireBytes
                );
            // 第一次超额关闭本次尝试所属类别。即使后续块更小，同一运行期也不会借另一个分区或反复试写。
            if (!fits)
            {
                if (errorClass)
                    ErrorClosed = true;
                else
                    NormalClosed = true;
            }

            return fits;
        }

        internal void CloseBoth()
        {
            NormalClosed = ErrorClosed = true;
        }

        internal void SetRecoveredUsage(long normal, long error)
        {
            NormalBytes = normal;
            ErrorBytes = error;
        }

        internal void Charge(bool errorClass, int wireBytes, bool needsHeader)
        {
            NormalBytes += needsHeader ? StorageRuntimeLimits.FileHeaderBytes : 0;
            if (errorClass)
                ErrorBytes += wireBytes;
            else
                NormalBytes += wireBytes;
        }
    }
}
