using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Logos.Storage
{
    // WireBytes 保存完整块的物理长度，包含头、密文和尾部；恢复核算使用这段长度，目录读取方还能取得认证后的记录。
    public sealed class DecodedBlock
    {
        public LogRecord[] Records
        {
            get;
        }
        public byte[] Epoch
        {
            get;
        }
        public ulong Sequence
        {
            get;
        }
        public long DayId
        {
            get;
        }
        public bool IsErrorClass
        {
            get;
        }
        public int WireBytes
        {
            get;
        }

        internal DecodedBlock(LogRecord[] records, byte[] wire, int wireLength)
        {
            Records = records;
            Epoch = Wire.Slice(wire, 44, 16);
            Sequence = Wire.U64(wire, 60);
            DayId = Wire.I64(wire, 28);
            IsErrorClass = wire[24] == 1;
            WireBytes = wireLength;
        }
    }
}
