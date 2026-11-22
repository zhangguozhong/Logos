using System;
using System.IO;

namespace Logos.Storage
{
    // 轮换要让全局、按日及统计副本改用新控制密钥；已有数据块仍引用创建时的数据密钥。
    public enum ControlKeyRotationStatus
    {
        None,
        Pending,
        Completed,
        Failed,
    }

    public sealed partial class JournalSession
    {
        private KeyMaterial rotationKey;
        private int rotationPhase,
            rotationSlot;
        private bool rotationNeedsResync;
        public ControlKeyRotationStatus ControlKeyRotationStatus
        {
            get;
            private set;
        }

        /// <summary>仅控制引用已退役。这绝不授权删除活跃文件的数据密钥。</summary>
        public bool OldControlReferencesRetired =>
            ControlKeyRotationStatus == ControlKeyRotationStatus.Completed;

        public bool RequestControlKeyRotation()
        {
            CheckThread();
            if (
                Status != JournalStatus.Ready
                || Clear.IsActive
                || expiringRow != null
                || ControlKeyRotationStatus == ControlKeyRotationStatus.Pending
            )
                return false;
            ControlKeyRotationStatus = ControlKeyRotationStatus.Pending;
            rotationPhase = rotationSlot = 0;
            rotationNeedsResync = false;
            return true;
        }

        public void StepControlKeyRotation(JournalStepContext context)
        {
            CheckThread();
            if (context.ShouldYield || ControlKeyRotationStatus != ControlKeyRotationStatus.Pending)
                return;
            try
            {
                VerifyOwner();
                if (rotationNeedsResync)
                {
                    global = store.ReadGlobal();
                    if (global.ClearPhase == 1)
                        throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                    rotationPhase = 1;
                    rotationSlot = 0;
                    rotationNeedsResync = false;
                    return;
                }

                if (rotationPhase == 0)
                {
                    rotationKey = JournalIo.Acquire(keys, null);
                    rotationPhase = 1;
                    return;
                }

                if (rotationPhase == 1)
                {
                    global = store.ReadGlobal();
                    store.WriteGlobalBoth(global, rotationKey);
                    rotationPhase = 2;
                    rotationSlot = 0;
                    return;
                }

                if (rotationPhase == 2)
                {
                    if (rotationSlot == 1024)
                    {
                        rotationPhase = 3;
                        return;
                    }

                    var row = store.ReadDay(rotationSlot++, out bool evidence);
                    if (row == null)
                    {
                        if (evidence)
                            throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                        return;
                    }

                    if (row.CleanupGeneration > global.CleanupGeneration)
                        throw new JournalBlockedException(JournalBlockReason.StateInvalid);
                    store.WriteDay(row, rotationKey, true);
                    if (row.DayId == global.HighestDay)
                        current = row;
                    return;
                }

                if (rotationPhase == 3)
                {
                    DropStatistics.RotateBoth(store, keys, rotationKey);
                    rotationPhase = 4;
                    rotationSlot = 0;
                    return;
                }

                // 逐槽读回并检查两份副本；全局、日期和统计副本都验证成功，才用新材料替换会话控制密钥并宣布完成。
                if (rotationPhase == 4)
                {
                    if (rotationSlot < 1024)
                    {
                        if (!store.VerifyDayControlKeyBoth(rotationSlot++, rotationKey.KeyId))
                            throw new IOException("Control rotation verification failed.");
                        return;
                    }

                    if (
                        !store.VerifyGlobalControlKeyBoth(rotationKey.KeyId)
                        || !DropStatistics.VerifyControlKeyBoth(store, keys, rotationKey.KeyId)
                    )
                        throw new IOException("Control rotation verification failed.");
                    controlKey.Dispose();
                    controlKey = rotationKey;
                    rotationKey = null;
                    ControlKeyRotationStatus = ControlKeyRotationStatus.Completed;
                }
            }
            catch (JournalBlockedException ex)
            {
                Block(ex.Reason);
                ControlKeyRotationStatus = ControlKeyRotationStatus.Failed;
                rotationKey?.Dispose();
                rotationKey = null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                rotationNeedsResync = true;
            }
        }
    }
}
