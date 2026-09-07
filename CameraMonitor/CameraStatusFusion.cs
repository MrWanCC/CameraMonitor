using System;

namespace CameraMonitor
{
    public sealed class CameraDisplayStatus
    {
        public CameraDisplayStatus()
        {
            StateKey = string.Empty;
            Text = string.Empty;
            Warning = false;
            HideLabel = false;
        }

        public string StateKey { get; set; }

        public string Text { get; set; }

        public bool Warning { get; set; }

        public bool HideLabel { get; set; }
    }


    /// <summary>
    /// SDK设备状态 + ZLM视频流状态 + VLC播放状态的最终显示策略。
    ///
    /// 优先级：
    /// 设备级确定性故障 > 视频流故障 > 客户端播放器故障 > 正常。
    /// </summary>
    public static class CameraStatusFusion
    {
        public static CameraDisplayStatus Fuse(
            HikvisionDeviceStatus sdkStatus,
            string videoStateKey,
            string videoText,
            bool videoWarning,
            string cameraName)
        {
            if (string.IsNullOrWhiteSpace(cameraName))
            {
                cameraName = "Camera";
            }

            CameraDisplayStatus sdkOverride =
                TryCreateSdkOverride(
                    sdkStatus,
                    videoStateKey,
                    cameraName);

            if (sdkOverride != null)
            {
                return sdkOverride;
            }

            return CreateVideoStatus(
                videoStateKey,
                videoText,
                videoWarning,
                cameraName);
        }


        private static CameraDisplayStatus TryCreateSdkOverride(
            HikvisionDeviceStatus sdkStatus,
            string videoStateKey,
            string cameraName)
        {
            if (sdkStatus == null)
            {
                return null;
            }

            switch (sdkStatus.State)
            {
                case HikvisionDeviceState.AuthenticationFailed:
                    return CreateVisible(
                        "device-auth-failed",
                        BuildSdkErrorCodeText(
                            sdkStatus),
                        true);

                case HikvisionDeviceState.Offline:
                    return CreateVisible(
                        "device-offline",
                        BuildSdkErrorCodeText(
                            sdkStatus),
                        true);

                case HikvisionDeviceState.ConnectionLimit:
                    return CreateVisible(
                        "device-connection-limit",
                        BuildSdkErrorCodeText(
                            sdkStatus),
                        true);

                case HikvisionDeviceState.ChannelError:
                    return CreateVisible(
                        "device-channel-error",
                        BuildSdkErrorCodeText(
                            sdkStatus),
                        true);

                case HikvisionDeviceState.Error:
                    return CreateVisible(
                        "device-error",
                        BuildSdkErrorCodeText(
                            sdkStatus),
                        true);

                case HikvisionDeviceState.Connecting:
                    /*
                     * SDK正在判定设备状态时，优先等待SDK结果。
                     *
                     * 1. 如果SDK已经拿到了原始ErrorCode，直接显示ErrorCode，
                     *    不等待状态正式归类为Offline/AuthenticationFailed等。
                     *
                     * 2. 如果SDK还没有ErrorCode，而ZLM已经先发现单路流异常，
                     *    暂时显示“正在检测设备状态...”，避免界面先闪
                     *    “视频流异常”，几秒后又切成ErrorCode。
                     *
                     * 3. 如果视频本身正常，则不显示额外状态。
                     */
                    if (sdkStatus.ErrorCode != 0)
                    {
                        return CreateVisible(
                            "device-pending-error",
                            BuildSdkErrorCodeText(
                                sdkStatus),
                            true);
                    }

                    if (sdkStatus.LastSuccessUtc !=
                        DateTime.MinValue)
                    {
                        return CreateVisible(
                            "device-reconnecting",
                            "设备正在重新连接...",
                            false);
                    }

                    if (IsVideoStreamFault(
                            videoStateKey))
                    {
                        return CreateVisible(
                            "device-checking",
                            "正在检测设备状态...",
                            false);
                    }

                    return null;

                case HikvisionDeviceState.Online:
                case HikvisionDeviceState.Unknown:
                default:
                    return null;
            }
        }


        private static bool IsVideoStreamFault(
            string videoStateKey)
        {
            string key =
                videoStateKey ??
                string.Empty;

            return
                key == "stream-auth-error"
                ||
                key == "proxy-missing"
                ||
                key == "stream-error"
                ||
                key == "stream-stalled";
        }


        private static CameraDisplayStatus CreateVideoStatus(
            string videoStateKey,
            string videoText,
            bool videoWarning,
            string cameraName)
        {
            string key =
                videoStateKey ??
                string.Empty;

            switch (key)
            {
                case "playing":
                    return CreateHidden(
                        "normal");

                case "zlm-offline":
                    return CreateVisible(
                        "video-service-offline",
                        "视频服务离线，等待自动恢复...",
                        true);

                case "stream-auth-error":
                    return CreateVisible(
                        "stream-auth-error",
                        "视频认证失败",
                        true);

                case "proxy-missing":
                case "stream-error":
                case "stream-stalled":
                    return CreateVisible(
                        "stream-error",
                        "视频流异常，正在自动恢复...",
                        true);

                case "vlc-error":
                case "vlc-reconnecting":
                case "vlc-not-playing":
                case "play-exception":
                    return CreateVisible(
                        "player-error",
                        "客户端播放异常，正在重新连接...",
                        true);

                case "connecting":
                case "reconnecting":
                    return CreateVisible(
                        "connecting",
                        "正在连接视频...",
                        false);

                default:
                    if (string.IsNullOrWhiteSpace(videoText))
                    {
                        return CreateVisible(
                            string.IsNullOrWhiteSpace(key)
                                ? "unknown"
                                : key,
                            "状态检测中...",
                            videoWarning);
                    }

                    return CreateVisible(
                        string.IsNullOrWhiteSpace(key)
                            ? "unknown"
                            : key,
                        videoText,
                        videoWarning);
            }
        }


        private static string BuildSdkErrorCodeText(
            HikvisionDeviceStatus sdkStatus)
        {
            uint errorCode =
                sdkStatus != null
                    ? sdkStatus.ErrorCode
                    : 0;

            return
                "ErrorCode=" +
                errorCode.ToString();
        }


        private static CameraDisplayStatus CreateHidden(
            string stateKey)
        {
            CameraDisplayStatus result =
                new CameraDisplayStatus();

            result.StateKey =
                stateKey;

            result.Text =
                string.Empty;

            result.Warning =
                false;

            result.HideLabel =
                true;

            return result;
        }


        private static CameraDisplayStatus CreateVisible(
            string stateKey,
            string text,
            bool warning)
        {
            CameraDisplayStatus result =
                new CameraDisplayStatus();

            result.StateKey =
                stateKey;

            result.Text =
                text ?? string.Empty;

            result.Warning =
                warning;

            result.HideLabel =
                false;

            return result;
        }
    }
}