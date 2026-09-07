using System;
using System.Text.RegularExpressions;
using LibVLCSharp.Shared;

namespace CameraMonitor
{
    /// <summary>
    /// 收集LibVLC底层日志，同时避免日志本身拖慢6/3路实时视频。
    ///
    /// VLC在解码跟不上时会产生大量
    /// "picture is too late to be displayed"，如果每条都同步写磁盘，
    /// 诊断日志本身会增加I/O和锁竞争。因此这里对高频消息聚合/限流。
    /// </summary>
    internal sealed class VlcLogService : IDisposable
    {
        private static readonly Regex RtspPasswordRegex =
            new Regex(
                "(rtsp://[^:/@\\s]+:)[^@\\s/]+@",
                RegexOptions.IgnoreCase |
                RegexOptions.Compiled);

        private static readonly Regex LatePictureMsRegex =
            new Regex(
                "missing\\s+(\\d+)\\s*ms",
                RegexOptions.IgnoreCase |
                RegexOptions.Compiled);

        private readonly object _syncRoot =
            new object();

        private LibVLC _libVLC;
        private bool _attached;

        private int _latePictureCount;
        private int _latePictureMaxMs;
        private DateTime _lastLatePictureFlushUtc =
            DateTime.MinValue;

        private DateTime _lastDeadlockLogUtc =
            DateTime.MinValue;

        private DateTime _lastPcrLogUtc =
            DateTime.MinValue;

        private DateTime _lastParameterSetLogUtc =
            DateTime.MinValue;

        private DateTime _lastHardwareDecoderLogUtc =
            DateTime.MinValue;

        // 高频VLC告警不逐条写磁盘。
        // LibVLC.Log回调可能来自解码线程；日志风暴时同步AutoFlush会放大I/O和锁竞争。
        private int _lateFrameDropCount;
        private int _timestampErrorCount;
        private int _earlyPictureSkipCount;
        private DateTime _lastBurstFlushUtc =
            DateTime.MinValue;

        private DateTime _lastDrawableLogUtc =
            DateTime.MinValue;

        private DateTime _lastThumbnailLogUtc =
            DateTime.MinValue;

        private DateTime _lastSurfaceMismatchLogUtc =
            DateTime.MinValue;

        public void Attach(
            LibVLC libVLC)
        {
            if (libVLC == null ||
                _attached)
            {
                return;
            }

            _libVLC =
                libVLC;

            _libVLC.Log +=
                LibVlc_Log;

            _attached =
                true;

            AppLogger.Info(
                "LibVLC底层日志监听已启用（高频late/debug日志已聚合限流）");
        }

        private void LibVlc_Log(
            object sender,
            LogEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            try
            {
                string message =
                    Sanitize(
                        e.Message);

                if (string.IsNullOrEmpty(
                    message))
                {
                    return;
                }

                DateTime nowUtc =
                    DateTime.UtcNow;

                // 这些消息在画面开始掉帧时可能每毫秒产生多条。
                // 先聚合并短路，避免每一条都进入AppLogger同步落盘。
                if (HandleBurstMessage(
                        message,
                        nowUtc))
                {
                    return;
                }

                if (Contains(
                        message,
                        "picture is too late"))
                {
                    HandleLatePicture(
                        message,
                        nowUtc);

                    return;
                }

                if (Contains(
                        message,
                        "buffer deadlock prevented"))
                {
                    if (!TryPassRateLimit(
                            ref _lastDeadlockLogUtc,
                            nowUtc,
                            5))
                    {
                        return;
                    }
                }

                if (Contains(
                        message,
                        "ES_OUT_SET_") &&
                    Contains(
                        message,
                        "called too late"))
                {
                    if (!TryPassRateLimit(
                            ref _lastPcrLogUtc,
                            nowUtc,
                            5))
                    {
                        return;
                    }
                }

                if (Contains(
                        message,
                        "Waiting for VPS/SPS/PPS"))
                {
                    if (!TryPassRateLimit(
                            ref _lastParameterSetLogUtc,
                            nowUtc,
                            10))
                    {
                        return;
                    }
                }

                // WinForms/D3D11集成过程中会反复出现的已知诊断噪声。
                // 保留样本用于排障，但不允许连续刷盘。
                if (Contains(
                        message,
                        "unsupported control query 3"))
                {
                    if (!TryPassRateLimit(
                            ref _lastDrawableLogUtc,
                            nowUtc,
                            30))
                    {
                        return;
                    }
                }

                if (Contains(
                        message,
                        "SetThumbNailClip failed"))
                {
                    if (!TryPassRateLimit(
                            ref _lastThumbnailLogUtc,
                            nowUtc,
                            30))
                    {
                        return;
                    }
                }

                if (Contains(
                        message,
                        "surface dimensions") &&
                    Contains(
                        message,
                        "differ from avcodec dimensions"))
                {
                    if (!TryPassRateLimit(
                            ref _lastSurfaceMismatchLogUtc,
                            nowUtc,
                            30))
                    {
                        return;
                    }
                }

                string level =
                    e.Level.ToString();

                if (!ShouldWriteNormalMessage(
                        level,
                        message,
                        nowUtc))
                {
                    return;
                }

                string module =
                    string.IsNullOrWhiteSpace(
                        e.Module)
                        ? "core"
                        : e.Module;

                string text =
                    "[VLC][" +
                    e.Level +
                    "][" +
                    module +
                    "] " +
                    message;

                if (string.Equals(
                        level,
                        "Error",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        level,
                        "Warning",
                        StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warn(
                        text);
                }
                else
                {
                    AppLogger.Info(
                        text);
                }
            }
            catch
            {
                // VLC日志只是诊断辅助，绝不能反向影响播放线程。
            }
        }

        private bool HandleBurstMessage(
            string message,
            DateTime nowUtc)
        {
            int category =
                0;

            if (Contains(
                    message,
                    "More than 11 late frames, dropping frame"))
            {
                category =
                    1;
            }
            else if (Contains(
                         message,
                         "Timestamp conversion failed") ||
                     Contains(
                         message,
                         "Could not get display date") ||
                     Contains(
                         message,
                         "Could not convert timestamp"))
            {
                category =
                    2;
            }
            else if (Contains(
                         message,
                         "early picture skipped"))
            {
                category =
                    3;
            }
            else
            {
                return false;
            }

            int lateFrameDropCount =
                0;

            int timestampErrorCount =
                0;

            int earlyPictureSkipCount =
                0;

            lock (_syncRoot)
            {
                if (category == 1)
                {
                    _lateFrameDropCount++;
                }
                else if (category == 2)
                {
                    _timestampErrorCount++;
                }
                else
                {
                    _earlyPictureSkipCount++;
                }

                if (_lastBurstFlushUtc ==
                    DateTime.MinValue)
                {
                    _lastBurstFlushUtc =
                        nowUtc;

                    return true;
                }

                if ((nowUtc -
                     _lastBurstFlushUtc)
                        .TotalSeconds < 5)
                {
                    return true;
                }

                lateFrameDropCount =
                    _lateFrameDropCount;

                timestampErrorCount =
                    _timestampErrorCount;

                earlyPictureSkipCount =
                    _earlyPictureSkipCount;

                _lateFrameDropCount =
                    0;

                _timestampErrorCount =
                    0;

                _earlyPictureSkipCount =
                    0;

                _lastBurstFlushUtc =
                    nowUtc;
            }

            WriteBurstSummary(
                lateFrameDropCount,
                timestampErrorCount,
                earlyPictureSkipCount);

            return true;
        }

        private static void WriteBurstSummary(
            int lateFrameDropCount,
            int timestampErrorCount,
            int earlyPictureSkipCount)
        {
            if (lateFrameDropCount <= 0 &&
                timestampErrorCount <= 0 &&
                earlyPictureSkipCount <= 0)
            {
                return;
            }

            AppLogger.Warn(
                "[VLC][Burst] 过去5秒高频告警：" +
                "丢帧=" +
                lateFrameDropCount +
                "，时间戳异常=" +
                timestampErrorCount +
                "，early-picture=" +
                earlyPictureSkipCount);
        }

        private void FlushBurstSummary()
        {
            int lateFrameDropCount;
            int timestampErrorCount;
            int earlyPictureSkipCount;

            lock (_syncRoot)
            {
                lateFrameDropCount =
                    _lateFrameDropCount;

                timestampErrorCount =
                    _timestampErrorCount;

                earlyPictureSkipCount =
                    _earlyPictureSkipCount;

                _lateFrameDropCount =
                    0;

                _timestampErrorCount =
                    0;

                _earlyPictureSkipCount =
                    0;
            }

            WriteBurstSummary(
                lateFrameDropCount,
                timestampErrorCount,
                earlyPictureSkipCount);
        }

        private bool ShouldWriteNormalMessage(
            string level,
            string message,
            DateTime nowUtc)
        {
            if (string.Equals(
                    level,
                    "Error",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    level,
                    "Warning",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 普通Debug/Info全部忽略，只保留能确认实际硬解码器的信息。
            if (Contains(
                    message,
                    "Using D3D11VA") ||
                Contains(
                    message,
                    "Using DXVA"))
            {
                return TryPassRateLimit(
                    ref _lastHardwareDecoderLogUtc,
                    nowUtc,
                    30);
            }

            return false;
        }

        private void HandleLatePicture(
            string message,
            DateTime nowUtc)
        {
            int lateCountToWrite =
                0;

            int maxLateMsToWrite =
                0;

            lock (_syncRoot)
            {
                _latePictureCount++;

                int lateMs =
                    TryReadLateMilliseconds(
                        message);

                if (lateMs >
                    _latePictureMaxMs)
                {
                    _latePictureMaxMs =
                        lateMs;
                }

                if (_lastLatePictureFlushUtc ==
                    DateTime.MinValue)
                {
                    _lastLatePictureFlushUtc =
                        nowUtc;

                    return;
                }

                if ((nowUtc -
                     _lastLatePictureFlushUtc)
                        .TotalSeconds < 5)
                {
                    return;
                }

                lateCountToWrite =
                    _latePictureCount;

                maxLateMsToWrite =
                    _latePictureMaxMs;

                _latePictureCount =
                    0;

                _latePictureMaxMs =
                    0;

                _lastLatePictureFlushUtc =
                    nowUtc;
            }

            if (lateCountToWrite > 0)
            {
                AppLogger.Warn(
                    "[VLC][LatePicture] 过去5秒延迟画面=" +
                    lateCountToWrite +
                    "次，最大落后=" +
                    maxLateMsToWrite +
                    "ms");
            }
        }

        private bool TryPassRateLimit(
            ref DateTime lastUtc,
            DateTime nowUtc,
            int seconds)
        {
            lock (_syncRoot)
            {
                if (lastUtc !=
                        DateTime.MinValue &&
                    (nowUtc -
                     lastUtc)
                        .TotalSeconds < seconds)
                {
                    return false;
                }

                lastUtc =
                    nowUtc;

                return true;
            }
        }

        private static int TryReadLateMilliseconds(
            string message)
        {
            try
            {
                Match match =
                    LatePictureMsRegex.Match(
                        message);

                if (!match.Success ||
                    match.Groups.Count < 2)
                {
                    return 0;
                }

                int value;

                return int.TryParse(
                           match.Groups[1].Value,
                           out value)
                    ? value
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool Contains(
            string source,
            string value)
        {
            return source != null &&
                   source.IndexOf(
                       value,
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Sanitize(
            string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            try
            {
                return RtspPasswordRegex.Replace(
                    text,
                    "$1***@");
            }
            catch
            {
                return text;
            }
        }

        public void Dispose()
        {
            if (!_attached)
            {
                return;
            }

            try
            {
                if (_libVLC != null)
                {
                    _libVLC.Log -=
                        LibVlc_Log;
                }
            }
            catch
            {
            }

            // 退出前把尚未到5秒窗口的聚合告警写出，避免丢失最后一段诊断信息。
            try
            {
                FlushBurstSummary();
            }
            catch
            {
            }

            int lateCount =
                0;

            int maxLateMs =
                0;

            lock (_syncRoot)
            {
                lateCount =
                    _latePictureCount;

                maxLateMs =
                    _latePictureMaxMs;

                _latePictureCount =
                    0;

                _latePictureMaxMs =
                    0;
            }

            if (lateCount > 0)
            {
                try
                {
                    AppLogger.Warn(
                        "[VLC][LatePicture] 关闭前剩余延迟画面=" +
                        lateCount +
                        "次，最大落后=" +
                        maxLateMs +
                        "ms");
                }
                catch
                {
                }
            }

            _attached =
                false;

            _libVLC =
                null;
        }
    }
}
