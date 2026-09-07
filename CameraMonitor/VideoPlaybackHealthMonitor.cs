using System;

namespace CameraMonitor
{
    /// <summary>
    /// VLC真实画面健康检测。
    ///
    /// 只判断一个有可靠证据的故障：
    /// ZLM持续有新帧、VLC仍为Playing，但当前可见画面的输出统计连续一段时间没有推进。
    ///
    /// 不再使用MediaPlayer.Time与系统墙钟估算“实时延迟”。
    /// RTSP直播在PCR/时间戳重建、clock-synchro关闭等情况下，MediaPlayer.Time并不是可靠的
    /// 实时时钟，拿它做2.5秒阈值会把正常播放器误判成落后并主动重连。
    ///
    /// 该类只判断，不直接操作MediaPlayer。
    /// MainForm收到Stalled后，只重开当前客户端Media，不碰ZLM代理。
    /// </summary>
    internal sealed class VideoPlaybackHealthMonitor
    {
        private sealed class SlotState
        {
            public int LastDecodedVideo;
            public int LastDisplayedPictures;

            public bool BaselineReady;
            public bool HasEverSeenProgress;
            public bool PreferDisplayedPictures;

            public DateTime LastProgressUtc;
            public DateTime GraceUntilUtc;
        }

        private readonly object _syncRoot =
            new object();

        private readonly SlotState[] _slots;
        private readonly int _stallSeconds;

        public VideoPlaybackHealthMonitor(
            int slotCount,
            int stallSeconds)
        {
            if (slotCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "slotCount");
            }

            if (stallSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "stallSeconds");
            }

            _stallSeconds =
                stallSeconds;

            _slots =
                new SlotState[slotCount];

            for (int i = 0;
                 i < slotCount;
                 i++)
            {
                _slots[i] =
                    new SlotState();
            }
        }

        public void Reset(
            int slot,
            DateTime nowUtc,
            int graceSeconds)
        {
            if (!IsValidSlot(slot))
            {
                return;
            }

            lock (_syncRoot)
            {
                ResetState(
                    _slots[slot],
                    nowUtc,
                    graceSeconds);
            }
        }

        public PlaybackHealthResult Sample(
            int slot,
            DateTime nowUtc,
            bool isVisible,
            bool playerIsPlaying,
            bool zlmFramesGrowing,
            int decodedVideo,
            int displayedPictures,
            long playerTimeMs)
        {
            if (!IsValidSlot(slot))
            {
                return PlaybackHealthResult.Ignored();
            }

            lock (_syncRoot)
            {
                SlotState state =
                    _slots[slot];

                if (!isVisible ||
                    !playerIsPlaying ||
                    !zlmFramesGrowing)
                {
                    // 当前不是“ZLM健康 + 当前可见画面正在播放”的场景。
                    // 不做VLC冻结判断，下次重新建立统计基线。
                    state.BaselineReady =
                        false;

                    state.LastProgressUtc =
                        nowUtc;

                    return PlaybackHealthResult.Ignored();
                }

                if (!state.BaselineReady)
                {
                    state.LastDecodedVideo =
                        decodedVideo;

                    state.LastDisplayedPictures =
                        displayedPictures;

                    state.PreferDisplayedPictures =
                        displayedPictures > 0;

                    state.HasEverSeenProgress =
                        decodedVideo > 0 ||
                        displayedPictures > 0;

                    state.LastProgressUtc =
                        nowUtc;

                    state.BaselineReady =
                        true;

                    return PlaybackHealthResult.Healthy(
                        decodedVideo,
                        displayedPictures,
                        playerTimeMs);
                }

                // Media/Vout重建后统计可能回零。
                // 这表示新的播放会话，不是冻结。
                if (decodedVideo <
                        state.LastDecodedVideo ||
                    displayedPictures <
                        state.LastDisplayedPictures)
                {
                    state.LastDecodedVideo =
                        decodedVideo;

                    state.LastDisplayedPictures =
                        displayedPictures;

                    state.PreferDisplayedPictures =
                        displayedPictures > 0;

                    state.HasEverSeenProgress =
                        state.HasEverSeenProgress ||
                        decodedVideo > 0 ||
                        displayedPictures > 0;

                    state.LastProgressUtc =
                        nowUtc;

                    return PlaybackHealthResult.Healthy(
                        decodedVideo,
                        displayedPictures,
                        playerTimeMs);
                }

                bool decodedProgressed =
                    decodedVideo >
                    state.LastDecodedVideo;

                bool displayedProgressed =
                    displayedPictures >
                    state.LastDisplayedPictures;

                if (displayedPictures > 0)
                {
                    state.PreferDisplayedPictures =
                        true;
                }

                if (decodedProgressed ||
                    displayedProgressed)
                {
                    state.HasEverSeenProgress =
                        true;
                }

                // 一旦DisplayedPictures可用，就优先用“真正送到Vout的画面”判断；
                // 只有该统计始终不可用时才退回DecodedVideo。
                bool effectiveProgress =
                    state.PreferDisplayedPictures
                        ? displayedProgressed
                        : decodedProgressed;

                state.LastDecodedVideo =
                    decodedVideo;

                state.LastDisplayedPictures =
                    displayedPictures;

                // 启动/刚重连后的宽限期内，允许VLC建立Vout和硬解上下文。
                bool inGrace =
                    nowUtc <
                    state.GraceUntilUtc;

                if (effectiveProgress)
                {
                    state.LastProgressUtc =
                        nowUtc;

                    return PlaybackHealthResult.Healthy(
                        decodedVideo,
                        displayedPictures,
                        playerTimeMs);
                }

                // 某些VLC构建可能不给任何统计值。
                // 从未见过统计进度时不自动重启，避免无证据误判。
                if (!state.HasEverSeenProgress)
                {
                    state.LastProgressUtc =
                        nowUtc;

                    return PlaybackHealthResult.Unsupported(
                        decodedVideo,
                        displayedPictures,
                        playerTimeMs);
                }

                if (inGrace)
                {
                    return PlaybackHealthResult.Healthy(
                        decodedVideo,
                        displayedPictures,
                        playerTimeMs);
                }

                double noProgressSeconds =
                    (nowUtc -
                     state.LastProgressUtc)
                        .TotalSeconds;

                if (noProgressSeconds >=
                    _stallSeconds)
                {
                    // 触发一次后重新建立基线；真正的重连由MainForm统一节流。
                    state.BaselineReady =
                        false;

                    state.LastProgressUtc =
                        nowUtc;

                    return PlaybackHealthResult.Stalled(
                        decodedVideo,
                        displayedPictures,
                        playerTimeMs,
                        noProgressSeconds);
                }

                return PlaybackHealthResult.Suspect(
                    decodedVideo,
                    displayedPictures,
                    playerTimeMs,
                    noProgressSeconds);
            }
        }

        private static void ResetState(
            SlotState state,
            DateTime nowUtc,
            int graceSeconds)
        {
            state.LastDecodedVideo =
                0;

            state.LastDisplayedPictures =
                0;

            state.BaselineReady =
                false;

            state.HasEverSeenProgress =
                false;

            state.PreferDisplayedPictures =
                false;

            state.LastProgressUtc =
                nowUtc;

            state.GraceUntilUtc =
                nowUtc.AddSeconds(
                    Math.Max(0, graceSeconds));
        }

        private bool IsValidSlot(
            int slot)
        {
            return slot >= 0 &&
                   slot < _slots.Length;
        }
    }


    internal sealed class PlaybackHealthResult
    {
        public bool IsStalled;
        public bool IsUnsupported;

        public int DecodedVideo;
        public int DisplayedPictures;
        public long PlayerTimeMs;

        public double NoProgressSeconds;

        public static PlaybackHealthResult Ignored()
        {
            return new PlaybackHealthResult();
        }

        public static PlaybackHealthResult Healthy(
            int decodedVideo,
            int displayedPictures,
            long playerTimeMs)
        {
            return new PlaybackHealthResult
            {
                DecodedVideo =
                    decodedVideo,
                DisplayedPictures =
                    displayedPictures,
                PlayerTimeMs =
                    playerTimeMs
            };
        }

        public static PlaybackHealthResult Unsupported(
            int decodedVideo,
            int displayedPictures,
            long playerTimeMs)
        {
            return new PlaybackHealthResult
            {
                IsUnsupported =
                    true,
                DecodedVideo =
                    decodedVideo,
                DisplayedPictures =
                    displayedPictures,
                PlayerTimeMs =
                    playerTimeMs
            };
        }

        public static PlaybackHealthResult Suspect(
            int decodedVideo,
            int displayedPictures,
            long playerTimeMs,
            double noProgressSeconds)
        {
            return new PlaybackHealthResult
            {
                DecodedVideo =
                    decodedVideo,
                DisplayedPictures =
                    displayedPictures,
                PlayerTimeMs =
                    playerTimeMs,
                NoProgressSeconds =
                    noProgressSeconds
            };
        }

        public static PlaybackHealthResult Stalled(
            int decodedVideo,
            int displayedPictures,
            long playerTimeMs,
            double noProgressSeconds)
        {
            return new PlaybackHealthResult
            {
                IsStalled =
                    true,
                DecodedVideo =
                    decodedVideo,
                DisplayedPictures =
                    displayedPictures,
                PlayerTimeMs =
                    playerTimeMs,
                NoProgressSeconds =
                    noProgressSeconds
            };
        }
    }
}
