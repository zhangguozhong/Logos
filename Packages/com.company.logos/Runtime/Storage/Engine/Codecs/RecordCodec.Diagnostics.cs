using System;
namespace Logos.Storage
{
    // 诊断记录把成员引用及摘要目标编码在附加字段中，普通记录无需为这些可选字段付出逐条开销。
    public sealed partial class RecordCodec
    {
        // 不可变模型在构造时已验证并缓存精确 wire 长度，编码器不维护第二份计数公式。
        private static int DiagnosticSize(DiagnosticRecordMetadata value) => value?.WireBytes ?? 0;
        private static void WriteReference(DiagnosticRecordReference value, byte[] destination, ref int at)
        {
            Wire.Put64(destination, at, value.Sequence);
            at += 8;
            Wire.Put64(destination, at, value.UtcTimestamp.Ticks);
            at += 8;
            Wire.Put64(destination, at, value.Context.IdentityContextVersion);
            at += 8;
            Wire.Put16(destination, at, (ushort)value.Level);
            at += 2;
            Wire.Put16(destination, at, (ushort)value.Outcome);
            at += 2;
            Wire.Put64(destination, at, value.IsResolved ? value.TargetSequence : -1);
            at += 8;
            Wire.Put64(destination, at, value.IsResolved ? value.TargetUtcTimestamp.Ticks : -1);
            at += 8;
            WriteText(value.TargetRecordId, destination, ref at);
            WriteText(value.RecordId, destination, ref at);
            WriteText(value.Context.AppSessionId, destination, ref at);
            WriteText(value.Context.UnityInstanceId, destination, ref at);
            WriteText(value.Context.UnityVisitId, destination, ref at);
        }
        private static void WriteDiagnostic(DiagnosticRecordMetadata value, byte[] destination, ref int at)
        {
            if (value == null)
                return;
            int size = DiagnosticSize(value);
            Wire.Put16(destination, at, 1);
            Wire.Put16(destination, at + 2, (ushort)value.Kind);
            Wire.Put32(destination, at + 4, (uint)(size - 8));
            at += 8;
            WriteText(value.OperationId, destination, ref at);
            WriteText(value.ResultCode, destination, ref at);
            Wire.Put64(destination, at, value.DurationTicks);
            at += 8;
            if (value.Kind == DiagnosticRecordKind.Event)
                return;
            Wire.Put16(destination, at, (ushort)value.Members.Count);
            at += 2;
            for (int i = 0; i < value.Members.Count; i++)
                WriteReference(value.Members[i], destination, ref at);
            if (value.Kind == DiagnosticRecordKind.RepeatSummary)
            {
                WriteReference(value.FirstCommitted, destination, ref at);
                WriteReference(value.LatestReference, destination, ref at);
                WriteText(value.LatestSanitizedSample, destination, ref at);
            }
        }
        private static DiagnosticRecordReference ReadReference(byte[] body, ref int at, int end, string user, LogDeviceSnapshot device)
        {
            if (end - at < 44)
                throw new StorageFormatException();
            long sequence = Wire.I64(body, at), ticks = Wire.I64(body, at + 8), version = Wire.I64(body, at + 16);
            var level = (LogLevel)Wire.U16(body, at + 24);
            var outcome = (DiagnosticReferenceOutcome)Wire.U16(body, at + 26);
            at += 28;
            long targetSequence = Wire.I64(body, at), targetTicks = Wire.I64(body, at + 8);
            at += 16;
            string targetId = ReadText(body, ref at, 128, end);
            bool resolved = outcome == DiagnosticReferenceOutcome.Committed || outcome == DiagnosticReferenceOutcome.Represented;
            if (!resolved && (targetSequence != -1 || targetTicks != -1 || targetId.Length != 0))
                throw new StorageFormatException();
            string id = ReadText(body, ref at, 128, end), app = ReadText(body, ref at, 128, end), instance = ReadText(body, ref at, 128, end), visit = ReadText(body, ref at, 128, end);
            return new DiagnosticRecordReference(id, sequence, new DateTime(ticks, DateTimeKind.Utc), new LogContext(app, instance, visit, version, user, device), level, outcome, resolved ? targetId : null, resolved ? (long?)targetSequence : null, resolved ? (DateTime?)new DateTime(targetTicks, DateTimeKind.Utc) : null);
        }
        private static DiagnosticRecordMetadata ReadDiagnostic(byte[] body, ref int at, int end, string user, LogDeviceSnapshot device)
        {
            if (end - at < 8 || Wire.U16(body, at) != 1)
                throw new StorageFormatException();
            var kind = (DiagnosticRecordKind)Wire.U16(body, at + 2);
            uint length = Wire.U32(body, at + 4);
            at += 8;
            if (kind < DiagnosticRecordKind.Event || kind > DiagnosticRecordKind.IncidentReference || length > DiagnosticRecordMetadata.MaxPayloadBytes || length != end - at)
                throw new StorageFormatException();
            string operation = ReadText(body, ref at, 128, end), result = ReadText(body, ref at, 128, end);
            if (end - at < 8)
                throw new StorageFormatException();
            long duration = Wire.I64(body, at);
            at += 8;
            DiagnosticRecordReference[] members = null;
            DiagnosticRecordReference first = null, latest = null;
            string sample = null;
            if (kind != DiagnosticRecordKind.Event)
            {
                if (end - at < 2)
                    throw new StorageFormatException();
                int count = Wire.U16(body, at);
                at += 2;
                if (count < 1 || count > DiagnosticRecordMetadata.MaxMembers || count > (end - at) / 64)
                    throw new StorageFormatException();
                members = new DiagnosticRecordReference[count];
                for (int i = 0; i < count; i++)
                    members[i] = ReadReference(body, ref at, end, user, device);
                if (kind == DiagnosticRecordKind.RepeatSummary)
                {
                    first = ReadReference(body, ref at, end, user, device);
                    latest = ReadReference(body, ref at, end, user, device);
                    sample = ReadText(body, ref at, DiagnosticRecordMetadata.MaxSampleChars, end);
                }
            }
            if (at != end)
                throw new StorageFormatException();
            return new DiagnosticRecordMetadata(kind, operation, result, duration, members, first, latest, sample);
        }
    }
}
