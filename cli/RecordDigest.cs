using System.Security.Cryptography;
using Logos;
namespace Logos.Cli;
internal static class RecordDigest
{
    internal static byte[] Compute(LogRecord r)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, BoundedWire.Utf8, true);
        BoundedWire.Text(w, "Logos.Receiver/Record/v1", 64);
        w.Write((byte)r.Level);
        w.Write(r.MessageTruncated);
        w.Write(r.StackTruncated);
        w.Write(r.Sequence);
        w.Write(r.UtcTimestamp.Ticks);
        w.Write(r.MonotonicTimestamp);
        BoundedWire.Text(w, r.RecordId, 384);
        BoundedWire.Text(w, r.SourceId, 384);
        BoundedWire.Text(w, r.EventTypeId, 384);
        BoundedWire.Text(w, r.Message, 32768);
        BoundedWire.Text(w, r.Stack, 65536);
        BoundedWire.Text(w, r.Context.AppSessionId, 384);
        BoundedWire.Text(w, r.Context.UnityInstanceId, 384);
        BoundedWire.Text(w, r.Context.UnityVisitId, 384);
        w.Write(r.Context.IdentityContextVersion);
        DeviceAndUser(w, r.Context);
        // 保持历史 null metadata 摘要逐字节兼容；非空元数据追加独立域及完整有界编码。
        if (r.DiagnosticMetadata != null)
            Metadata(w, r.DiagnosticMetadata);
        w.Flush();
        return SHA256.HashData(stream.ToArray());
    }
    // 旧记录保持原摘要；新块中的用户和设备参与幂等冲突检查。
    private static void DeviceAndUser(BinaryWriter w, LogContext context)
    {
        if (context.Device == null && context.DiagnosticUserId == null)
            return;
        BoundedWire.Text(w, "Logos.Receiver/Context/v1", 64);
        BoundedWire.Text(w, context.DiagnosticUserId, 384, true);
        var device = context.Device;
        w.Write(device != null);
        if (device == null)
            return;
        foreach (var field in new[] { device.Platform, device.Model, device.OperatingSystem, device.AppVersion, device.UnityVersion })
            BoundedWire.Text(w, field, 512, true);
    }
    private static void Metadata(BinaryWriter w, DiagnosticRecordMetadata m)
    {
        BoundedWire.Text(w, "Logos.Receiver/Diagnostic/v1", 64);
        w.Write((ushort)m.Kind);
        BoundedWire.Text(w, m.OperationId, 128);
        BoundedWire.Text(w, m.ResultCode, 128);
        w.Write(m.DurationTicks);
        if (m.Members.Count > DiagnosticRecordMetadata.MaxMembers)
            throw new FormatException();
        w.Write(m.Members.Count);
        foreach (var member in m.Members)
            Reference(w, member);
        Reference(w, m.FirstCommitted);
        Reference(w, m.LatestReference);
        BoundedWire.Text(w, m.LatestSanitizedSample, 1024);
    }
    // 故障上下文的目标身份也参与摘要；同一 recordId 改指另一条记录时应判为内容冲突。
    private static void Reference(BinaryWriter w, DiagnosticRecordReference r)
    {
        w.Write(r != null);
        if (r == null)
            return;
        BoundedWire.Text(w, r.RecordId, 384);
        w.Write(r.Sequence);
        w.Write(r.UtcTimestamp.Ticks);
        w.Write((byte)r.Level);
        w.Write((ushort)r.Outcome);
        BoundedWire.Text(w, r.TargetRecordId, 384);
        w.Write(r.TargetSequence);
        w.Write(r.TargetUtcTimestamp.Ticks);
        BoundedWire.Text(w, r.Context.AppSessionId, 384);
        BoundedWire.Text(w, r.Context.UnityInstanceId, 384);
        BoundedWire.Text(w, r.Context.UnityVisitId, 384);
        w.Write(r.Context.IdentityContextVersion);
        DeviceAndUser(w, r.Context);
    }
}
