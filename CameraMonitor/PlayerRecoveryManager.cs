using System;
using System.Collections.Generic;

namespace CameraMonitor
{
    /// <summary>
    /// 管理VLC单路恢复的冷却时间和频繁重连计数。
    /// 不直接操作MediaPlayer，真正的Stop/Play仍由MainForm.PlayCamera完成。
    /// </summary>
    internal sealed class PlayerRecoveryManager
    {
        private sealed class SlotRecoveryState
        {
            public DateTime LastRestartUtc =
                DateTime.MinValue;

            public readonly Queue<DateTime> RestartHistory =
                new Queue<DateTime>();
        }

        private readonly object _syncRoot =
            new object();

        private readonly SlotRecoveryState[] _slots;
        private readonly int _cooldownSeconds;
        private readonly int _historyWindowSeconds;
        private readonly int _frequentRestartThreshold;

        // 全局错峰间隔：任意一路刚重开，其他路在该间隔内的重开请求先拒绝。
        // 看门狗周期性采样，被拒的下一轮自然重试，形成串行错峰。
        private readonly int _staggerSeconds;

        // 全局最近一次重开时间（跨slot）。
        private DateTime _lastAnyRestartUtc =
            DateTime.MinValue;

        public PlayerRecoveryManager(
            int slotCount,
            int cooldownSeconds,
            int historyWindowSeconds,
            int frequentRestartThreshold,
            int staggerSeconds)
        {
            if (slotCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "slotCount");
            }

            _cooldownSeconds =
                Math.Max(1, cooldownSeconds);

            _historyWindowSeconds =
                Math.Max(30, historyWindowSeconds);

            _frequentRestartThreshold =
                Math.Max(2, frequentRestartThreshold);

            _staggerSeconds =
                Math.Max(0, staggerSeconds);

            _slots =
                new SlotRecoveryState[slotCount];

            for (int i = 0;
                 i < slotCount;
                 i++)
            {
                _slots[i] =
                    new SlotRecoveryState();
            }
        }

        public bool TryBeginRestart(
            int slot,
            DateTime nowUtc,
            out int recentRestartCount,
            out bool frequentRestart)
        {
            recentRestartCount =
                0;

            frequentRestart =
                false;

            if (!IsValidSlot(slot))
            {
                return false;
            }

            lock (_syncRoot)
            {
                /*
                 * 全局错峰：
                 * 雪崩场景三路同时触发重开，会瞬时叠加
                 * RTSP协商+硬解初始化压力（实测互拖致死）。
                 * 这里强制任意两次重开至少间隔 _staggerSeconds，
                 * 被拒的由看门狗下一轮采样自然重试。
                 */
                if (_staggerSeconds > 0 &&
                    _lastAnyRestartUtc !=
                        DateTime.MinValue &&
                    (nowUtc -
                     _lastAnyRestartUtc)
                        .TotalSeconds <
                    _staggerSeconds)
                {
                    return false;
                }

                SlotRecoveryState state =
                    _slots[slot];

                /*
                 * 指数退避：
                 * 5分钟窗口内重连次数越多，冷却越长
                 * （15s → 30s → 45s → 60s 封顶），
                 * 避免连续失败时形成重连风暴。
                 */
                int backoffMultiplier =
                    Math.Min(
                        state.RestartHistory.Count,
                        4);

                int effectiveCooldown =
                    _cooldownSeconds *
                    Math.Max(1, backoffMultiplier);

                if (state.LastRestartUtc !=
                        DateTime.MinValue &&
                    (nowUtc -
                     state.LastRestartUtc)
                        .TotalSeconds <
                    effectiveCooldown)
                {
                    return false;
                }

                DateTime minTime =
                    nowUtc.AddSeconds(
                        -_historyWindowSeconds);

                while (state.RestartHistory.Count > 0 &&
                       state.RestartHistory.Peek() < minTime)
                {
                    state.RestartHistory.Dequeue();
                }

                state.LastRestartUtc =
                    nowUtc;

                state.RestartHistory.Enqueue(
                    nowUtc);

                _lastAnyRestartUtc =
                    nowUtc;

                recentRestartCount =
                    state.RestartHistory.Count;

                frequentRestart =
                    recentRestartCount >=
                    _frequentRestartThreshold;

                return true;
            }
        }

        /// <summary>
        /// ZLM确认恢复属于公共链路恢复，需要允许立即重开播放器，
        /// 因此只清除单路冷却，不清除历史计数。
        /// </summary>
        public void ResetCooldown(
            int slot)
        {
            if (!IsValidSlot(slot))
            {
                return;
            }

            lock (_syncRoot)
            {
                _slots[slot]
                    .LastRestartUtc =
                    DateTime.MinValue;
            }
        }

        private bool IsValidSlot(
            int slot)
        {
            return slot >= 0 &&
                   slot < _slots.Length;
        }
    }
}
