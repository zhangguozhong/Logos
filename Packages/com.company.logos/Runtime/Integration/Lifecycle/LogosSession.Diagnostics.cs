using System;
namespace Logos
{
    public sealed partial class LogosSession
    {
        private string diagnosticVersionBody;
        private DiagnosticRecordMetadata sessionStartMetadata;
        private long diagnosticStartTicks = -1, nextDiagnosticTicks, lastDiagnosticTick;
        private int sessionStartAttempts;
        private bool sessionStartFinished;
        public DiagnosticControlState GetDiagnosticControlState() => pipeline.ReadControlState();
        public DiagnosticBudgetSnapshot GetDiagnosticBudgetSnapshot() => pipeline.BudgetSnapshot;
        public long GetDiagnosticFilteredCount(DiagnosticFilterDetail detail) => pipeline.GetDiagnosticFiltered(detail);
        /// <summary>只提交有限请求。返回 true 不表示 worker 已授予临时明细。</summary>
        public bool TryRequestTemporaryDiagnostics(string instanceId, long generation, long expectedContextRevision, TemporaryDiagnosticRequest request)
            => request != null && Matches(instanceId, generation) && expectedContextRevision == ContextRevision && pipeline.TryRequestDiagnostics(expectedContextRevision, request);
        public bool TryRevokeTemporaryDiagnostics(string instanceId, long generation, long expectedContextRevision)
        {
            if (!Matches(instanceId, generation) || expectedContextRevision != ContextRevision || !pipeline.TryRequestDiagnostics(expectedContextRevision, null))
                return false;
            sessionStartFinished = true;
            return true;
        }
        private static string BoundedVersion(string value)
        {
            if (value == null)
                return string.Empty;
            int length = Math.Min(64, value.Length);
            if (length < value.Length && length > 0 && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
                length--;
            return value.Substring(0, length).Replace('\r', ' ').Replace('\n', ' ');
        }
        private void CaptureDiagnosticVersion()
        {
            // 仅实例创建主线程读取一次；不采集设备标识或路径，随后仍经过 storage 脱敏。
            diagnosticVersionBody = "version=1\nscope=unity.instance\ninstance=" + UnityInstanceId + "\napp=" + BoundedVersion(Context.Device.AppVersion) + "\nunity=" + BoundedVersion(Context.Device.UnityVersion);
            sessionStartMetadata = new DiagnosticRecordMetadata("unity.instance." + UnityInstanceId);
        }
        internal void TickDiagnostics()
        {
            if (sessionStartMetadata == null || sessionStartFinished || !OnMainThread)
                return;
            // 只声明当前 Unity 实例的启动事实；使用提交时正常上下文，不推断业务会话启动。
            if (detachRequested || !pipeline.TryGetControlTimestamp(Generation, out long now))
            {
                sessionStartFinished = true;
                return;
            }
            if (diagnosticStartTicks < 0)
                diagnosticStartTicks = now;
            if (now < lastDiagnosticTick || now - diagnosticStartTicks >= 10 * TimeSpan.TicksPerSecond || sessionStartAttempts >= 10)
            {
                sessionStartFinished = true;
                return;
            }
            lastDiagnosticTick = now;
            if (now < nextDiagnosticTicks)
                return;
            nextDiagnosticTicks = now > long.MaxValue - TimeSpan.TicksPerSecond ? long.MaxValue : now + TimeSpan.TicksPerSecond;
            sessionStartAttempts++;
            var result = TryLogDiagnostic(LogLevel.Info, "session.start", sessionStartMetadata, diagnosticVersionBody);
            // 仅后端尚未就绪或正在恢复时留给后续帧重试；策略拒绝、成功或其他失败都会结束本实例的启动事件。
            if (result.Reason != AdmissionReason.NotReady && result.Reason != AdmissionReason.Recovering)
                sessionStartFinished = true;
        }
    }
}
