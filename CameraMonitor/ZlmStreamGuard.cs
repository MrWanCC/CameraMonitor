using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace CameraMonitor
{
    public enum ZlmStreamGuardState
    {
        Unknown = 0,
        Healthy = 1,
        AuthFailed = 2,
        Failed = 3
    }


    /// <summary>
    /// 当前进程内，某个StreamId是否已经按“当前CameraConfig.SourceUrl”
    /// 完成过ZLM代理准备。
    ///
    /// 这个状态与ZLM中是否存在同名代理不是一回事：
    /// 配置发生变化后，ZLM里可能暂时还保留旧代理；在新SourceUrl
    /// Recreate/Ensure完成前必须保持Unknown/Preparing，避免VLC误连旧画面。
    /// </summary>
    public enum ZlmStreamPreparationState
    {
        Unknown = 0,
        Preparing = 1,
        Ready = 2,
        Failed = 3
    }


    internal sealed class ZlmStreamPreparationInfo
    {
        public string SourceUrl { get; set; }

        public ZlmStreamPreparationState State { get; set; }

        public DateTime UpdatedUtc { get; set; }
    }


    public sealed class ZlmStreamGuardResult
    {
        public ZlmStreamGuardResult()
        {
            State = ZlmStreamGuardState.Unknown;
            Success = false;
            AuthFailed = false;
            Message = string.Empty;
            AttemptUtc = DateTime.MinValue;
        }

        public ZlmStreamGuardState State { get; set; }

        public bool Success { get; set; }

        public bool AuthFailed { get; set; }

        public string Message { get; set; }

        public DateTime AttemptUtc { get; set; }

        public ZlmStreamGuardResult Clone()
        {
            ZlmStreamGuardResult copy =
                new ZlmStreamGuardResult();

            copy.State = State;
            copy.Success = Success;
            copy.AuthFailed = AuthFailed;
            copy.Message = Message;
            copy.AttemptUtc = AttemptUtc;

            return copy;
        }
    }


    /// <summary>
    /// ZLM 拉流代理保护层。
    ///
    /// 目标：
    /// 1. config.ini 是视频源唯一配置源；
    /// 2. 程序首次连接 ZLM 时，用当前 SourceUrl 同步代理；
    /// 3. 识别 DESCRIBE:401 Unauthorized；
    /// 4. 认证失败时降低重试频率，避免高频刷 addStreamProxy；
    /// 5. 保持 RTSP-only，避免 H265 走 RTMP 转协议路径。
    /// </summary>
    public sealed class ZlmStreamGuard
    {
        private const int RequestTimeoutMs = 5000;

        // 账号密码错误不需要每5秒打一次摄像头。
        private const int AuthRetryCooldownSeconds = 30;

        // 网络离线/超时采用递增退避，避免 Camera05/06 离线时
        // 每轮监控都再次触发5秒HTTP超时。
        private const int FailureRetryFirstSeconds = 10;
        private const int FailureRetrySecondSeconds = 30;
        private const int FailureRetryMaxSeconds = 60;

        private readonly object _syncRoot =
            new object();

        private readonly AppConfig _config;

        private readonly Dictionary<string, ZlmStreamGuardResult>
            _lastResults =
                new Dictionary<string, ZlmStreamGuardResult>(
                    StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, int>
            _failureCounts =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);

        // StreamId对应的“当前配置版本”准备状态。
        // SourceUrl必须匹配才允许返回Ready；旧SourceUrl留下的状态自动视为Unknown。
        private readonly Dictionary<string, ZlmStreamPreparationInfo>
            _preparationStates =
                new Dictionary<string, ZlmStreamPreparationInfo>(
                    StringComparer.OrdinalIgnoreCase);


        public ZlmStreamGuard(
            AppConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(
                    "config");
            }

            _config = config;
        }


        public void SyncAllStreams(
            IList<CameraConfig> cameras)
        {
            if (cameras == null)
            {
                return;
            }

            for (int i = 0;
                 i < cameras.Count;
                 i++)
            {
                CameraConfig camera =
                    cameras[i];

                if (camera == null)
                {
                    continue;
                }

                /*
                 * 首次连接 / ZLM恢复时强制同步。
                 * 即使 ZLM 中已有同名代理，也先删再按当前 config.ini 重建。
                 * 因此修改 IP、账号、密码后，客户只需重启 CameraMonitor。
                 */
                RecreateStream(
                    camera);
            }
        }


        public void EnsureAllStreams(
            IList<CameraConfig> cameras)
        {
            if (cameras == null)
            {
                return;
            }

            for (int i = 0;
                 i < cameras.Count;
                 i++)
            {
                CameraConfig camera =
                    cameras[i];

                if (camera == null)
                {
                    continue;
                }

                if (ShouldSkipFailureRetry(
                        camera.StreamId))
                {
                    continue;
                }

                EnsureStream(
                    camera,
                    false);
            }
        }


        public ZlmStreamGuardResult EnsureStream(
            CameraConfig camera,
            bool forceRecreate)
        {
            if (camera == null)
            {
                return CreateFailure(
                    "CameraConfig为空");
            }

            if (string.IsNullOrWhiteSpace(
                    camera.StreamId))
            {
                return CreateFailure(
                    "StreamId为空");
            }

            if (string.IsNullOrWhiteSpace(
                    camera.SourceUrl))
            {
                return CreateFailure(
                    "SourceUrl为空");
            }

            if (!forceRecreate)
            {
                ZlmStreamGuardResult cached =
                    GetLastResult(
                        camera.StreamId);

                if (cached != null &&
                    cached.AttemptUtc !=
                        DateTime.MinValue)
                {
                    double elapsedSeconds =
                        (DateTime.UtcNow -
                         cached.AttemptUtc)
                            .TotalSeconds;

                    if (cached.AuthFailed &&
                        elapsedSeconds <
                            AuthRetryCooldownSeconds)
                    {
                        return cached;
                    }

                }

                if (ProxyExists(
                        camera.StreamId))
                {
                    MarkHealthy(
                        camera.StreamId);

                    // Ensure只在当前配置已经被允许使用时调用。
                    // 当前进程第一次确认到同名代理存在，也把它绑定到当前SourceUrl；
                    // 配置变化启动时隐藏组不会先走Ensure，而会走Recreate，因此旧代理
                    // 不会被这里误标为当前配置Ready。
                    MarkPrepared(
                        camera);

                    return GetLastResult(
                        camera.StreamId);
                }
            }
            else
            {
                MarkPreparing(
                    camera);

                DeleteProxy(
                    camera.StreamId);
            }

            if (!forceRecreate)
            {
                MarkPreparing(
                    camera);
            }

            return AddProxy(
                camera);
        }


        public ZlmStreamGuardResult RecreateStream(
            CameraConfig camera)
        {
            if (camera == null)
            {
                return CreateFailure(
                    "CameraConfig为空");
            }

            MarkPreparing(
                camera);

            DeleteProxy(
                camera.StreamId);

            return AddProxy(
                camera);
        }


        public ZlmStreamPreparationState GetPreparationState(
            CameraConfig camera)
        {
            if (camera == null ||
                string.IsNullOrWhiteSpace(
                    camera.StreamId) ||
                string.IsNullOrWhiteSpace(
                    camera.SourceUrl))
            {
                return ZlmStreamPreparationState.Unknown;
            }

            lock (_syncRoot)
            {
                ZlmStreamPreparationInfo info;

                if (!_preparationStates.TryGetValue(
                        camera.StreamId,
                        out info) ||
                    info == null)
                {
                    return ZlmStreamPreparationState.Unknown;
                }

                if (!string.Equals(
                        info.SourceUrl ?? string.Empty,
                        camera.SourceUrl ?? string.Empty,
                        StringComparison.Ordinal))
                {
                    // 同一个StreamId曾经准备过，但对应的是旧SourceUrl。
                    // 旧代理绝不能参与当前配置的VLC预热。
                    return ZlmStreamPreparationState.Unknown;
                }

                return info.State;
            }
        }


        public bool IsPreparedForCurrentConfig(
            CameraConfig camera)
        {
            return
                GetPreparationState(
                    camera) ==
                ZlmStreamPreparationState.Ready;
        }


        public ZlmStreamGuardResult GetLastResult(
            string streamId)
        {
            if (string.IsNullOrWhiteSpace(
                    streamId))
            {
                return null;
            }

            lock (_syncRoot)
            {
                ZlmStreamGuardResult result;

                if (!_lastResults.TryGetValue(
                        streamId,
                        out result))
                {
                    return null;
                }

                return result.Clone();
            }
        }


        public void MarkHealthy(
            string streamId)
        {
            if (string.IsNullOrWhiteSpace(
                    streamId))
            {
                return;
            }

            ZlmStreamGuardResult result =
                new ZlmStreamGuardResult();

            result.State =
                ZlmStreamGuardState.Healthy;

            result.Success =
                true;

            result.AuthFailed =
                false;

            result.Message =
                string.Empty;

            result.AttemptUtc =
                DateTime.UtcNow;

            ResetFailureCount(
                streamId);

            SaveResult(
                streamId,
                result);
        }


        private bool ProxyExists(
            string streamId)
        {
            try
            {
                string key =
                    "__defaultVhost__/live/" +
                    streamId;

                string response =
                    HttpGet(
                        BuildApiUrl(
                            "getProxyInfo",
                            new Dictionary<string, string>
                            {
                                { "secret", _config.ZlmSecret },
                                { "key", key }
                            }));

                return ExtractCode(
                           response) == 0;
            }
            catch
            {
                return false;
            }
        }


        private void DeleteProxy(
            string streamId)
        {
            if (string.IsNullOrWhiteSpace(
                    streamId))
            {
                return;
            }

            try
            {
                string key =
                    "__defaultVhost__/live/" +
                    streamId;

                HttpGet(
                    BuildApiUrl(
                        "delStreamProxy",
                        new Dictionary<string, string>
                        {
                            { "secret", _config.ZlmSecret },
                            { "key", key }
                        }));
            }
            catch
            {
                // 代理本来就不存在时删除失败不影响后续 add。
            }
        }


        private ZlmStreamGuardResult AddProxy(
            CameraConfig camera)
        {
            DateTime now =
                DateTime.UtcNow;

            try
            {
                string response =
                    HttpGet(
                        BuildApiUrl(
                            "addStreamProxy",
                            new Dictionary<string, string>
                            {
                                { "secret", _config.ZlmSecret },
                                { "vhost", "__defaultVhost__" },
                                { "app", "live" },
                                { "stream", camera.StreamId },
                                { "url", camera.SourceUrl },
                                { "enable_rtsp", "1" },
                                { "enable_rtmp", "0" },
                                { "enable_hls", "0" },
                                { "enable_hls_fmp4", "0" },
                                { "enable_ts", "0" },
                                { "enable_fmp4", "0" },
                                { "enable_mp4", "0" },
                                { "rtp_type", "0" },
                                { "retry_count", "-1" },
                                // 无人观看时保持代理常活，
                                // 切换组画面时不用等ZLM重新向摄像头拉流。
                                { "auto_close", "0" },
                                // GOP 缓存：开启后播放器重连/无感切回时
                                // 立即拿到最近一个 GOP，不必等下个 I 帧
                                // （海康 GOP=50 @25fps = 2 秒）。
                                // 这是程序侧唯一能消除"重连等 I 帧"感知的开关，
                                // 对降低卡顿最直接。
                                { "gop_cache", "1" }
                            }));

                ZlmStreamGuardResult result =
                    ParseAddStreamProxyResponse(
                        response);

                result.AttemptUtc =
                    now;

                if (result.Success ||
                    result.AuthFailed)
                {
                    ResetFailureCount(
                        camera.StreamId);
                }
                else
                {
                    IncrementFailureCount(
                        camera.StreamId);
                }

                SaveResult(
                    camera.StreamId,
                    result);

                if (result.Success)
                {
                    MarkPrepared(
                        camera);
                }
                else
                {
                    MarkPreparationFailed(
                        camera);
                }

                if (result.AuthFailed)
                {
                    AppLogger.Warn(
                        GetCameraName(camera) +
                        " RTSP视频认证失败，ZLM返回401");
                }
                else if (!result.Success)
                {
                    AppLogger.Warn(
                        GetCameraName(camera) +
                        " ZLM创建RTSP代理失败：" +
                        result.Message);
                }
                else
                {
                    AppLogger.Info(
                        GetCameraName(camera) +
                        " 创建RTSP代理成功");
                }

                return result;
            }
            catch (Exception ex)
            {
                ZlmStreamGuardResult result =
                    CreateFailure(
                        ex.Message);

                result.AttemptUtc =
                    now;

                IncrementFailureCount(
                    camera.StreamId);

                SaveResult(
                    camera.StreamId,
                    result);

                MarkPreparationFailed(
                    camera);

                AppLogger.Warn(
                    GetCameraName(camera) +
                    " ZLM创建RTSP代理异常：" +
                    ex.Message);

                return result;
            }
        }


        public static ZlmStreamGuardResult ParseAddStreamProxyResponse(
            string response)
        {
            string text =
                response ??
                string.Empty;

            if (IsAuthFailureResponse(
                    text))
            {
                ZlmStreamGuardResult auth =
                    new ZlmStreamGuardResult();

                auth.State =
                    ZlmStreamGuardState.AuthFailed;

                auth.Success =
                    false;

                auth.AuthFailed =
                    true;

                auth.Message =
                    "DESCRIBE:401 Unauthorized";

                return auth;
            }

            int code =
                ExtractCode(
                    text);

            if (code == 0)
            {
                ZlmStreamGuardResult ok =
                    new ZlmStreamGuardResult();

                ok.State =
                    ZlmStreamGuardState.Healthy;

                ok.Success =
                    true;

                ok.AuthFailed =
                    false;

                ok.Message =
                    string.Empty;

                return ok;
            }

            ZlmStreamGuardResult failed =
                new ZlmStreamGuardResult();

            failed.State =
                ZlmStreamGuardState.Failed;

            failed.Success =
                false;

            failed.AuthFailed =
                false;

            failed.Message =
                ExtractMessage(
                    text);

            if (string.IsNullOrWhiteSpace(
                    failed.Message))
            {
                failed.Message =
                    "ZLM返回失败，code=" +
                    code.ToString();
            }

            return failed;
        }


        public static bool IsAuthFailureResponse(
            string response)
        {
            if (string.IsNullOrWhiteSpace(
                    response))
            {
                return false;
            }

            return
                response.IndexOf(
                    "DESCRIBE:401",
                    StringComparison.OrdinalIgnoreCase) >= 0
                ||
                response.IndexOf(
                    "401 Unauthorized",
                    StringComparison.OrdinalIgnoreCase) >= 0;
        }


        private static int ExtractCode(
            string response)
        {
            Dictionary<string, object> root;

            if (!ZlmJson.TryParse(
                    response,
                    out root))
            {
                return int.MinValue;
            }

            return ZlmJson.GetInt(
                root,
                "code",
                int.MinValue);
        }


        private static string ExtractMessage(
            string response)
        {
            Dictionary<string, object> root;

            if (!ZlmJson.TryParse(
                    response,
                    out root))
            {
                return string.Empty;
            }

            return ZlmJson.GetString(
                       root,
                       "msg") ??
                   string.Empty;
        }


        private string BuildApiUrl(
            string apiName,
            IDictionary<string, string> parameters)
        {
            StringBuilder builder =
                new StringBuilder();

            builder.Append(
                "http://");

            builder.Append(
                _config.ZlmHost);

            builder.Append(":");

            builder.Append(
                _config.ZlmHttpPort);

            builder.Append(
                "/index/api/");

            builder.Append(
                apiName);

            bool first =
                true;

            foreach (KeyValuePair<string, string> pair
                     in parameters)
            {
                builder.Append(
                    first
                        ? "?"
                        : "&");

                first =
                    false;

                builder.Append(
                    Uri.EscapeDataString(
                        pair.Key));

                builder.Append("=");

                builder.Append(
                    Uri.EscapeDataString(
                        pair.Value ??
                        string.Empty));
            }

            return builder.ToString();
        }


        private void MarkPreparing(
            CameraConfig camera)
        {
            SavePreparationState(
                camera,
                ZlmStreamPreparationState.Preparing);
        }


        private void MarkPrepared(
            CameraConfig camera)
        {
            SavePreparationState(
                camera,
                ZlmStreamPreparationState.Ready);
        }


        private void MarkPreparationFailed(
            CameraConfig camera)
        {
            SavePreparationState(
                camera,
                ZlmStreamPreparationState.Failed);
        }


        private void SavePreparationState(
            CameraConfig camera,
            ZlmStreamPreparationState state)
        {
            if (camera == null ||
                string.IsNullOrWhiteSpace(
                    camera.StreamId) ||
                string.IsNullOrWhiteSpace(
                    camera.SourceUrl))
            {
                return;
            }

            ZlmStreamPreparationInfo info =
                new ZlmStreamPreparationInfo();

            info.SourceUrl =
                camera.SourceUrl;

            info.State =
                state;

            info.UpdatedUtc =
                DateTime.UtcNow;

            lock (_syncRoot)
            {
                _preparationStates[
                    camera.StreamId] =
                    info;
            }
        }


        private bool ShouldSkipFailureRetry(
            string streamId)
        {
            ZlmStreamGuardResult cached =
                GetLastResult(
                    streamId);

            if (cached == null ||
                cached.State !=
                    ZlmStreamGuardState.Failed ||
                cached.AttemptUtc ==
                    DateTime.MinValue)
            {
                return false;
            }

            int failureCount =
                GetFailureCount(
                    streamId);

            int cooldownSeconds =
                GetFailureRetryCooldownSeconds(
                    failureCount);

            return
                (DateTime.UtcNow -
                 cached.AttemptUtc)
                    .TotalSeconds <
                cooldownSeconds;
        }


        private int GetFailureCount(
            string streamId)
        {
            if (string.IsNullOrWhiteSpace(
                    streamId))
            {
                return 0;
            }

            lock (_syncRoot)
            {
                int count;

                if (!_failureCounts.TryGetValue(
                        streamId,
                        out count))
                {
                    return 0;
                }

                return count;
            }
        }


        private int IncrementFailureCount(
            string streamId)
        {
            if (string.IsNullOrWhiteSpace(
                    streamId))
            {
                return 0;
            }

            lock (_syncRoot)
            {
                int count;

                _failureCounts.TryGetValue(
                    streamId,
                    out count);

                count++;

                _failureCounts[
                    streamId] =
                    count;

                return count;
            }
        }


        private void ResetFailureCount(
            string streamId)
        {
            if (string.IsNullOrWhiteSpace(
                    streamId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _failureCounts.Remove(
                    streamId);
            }
        }


        private static int GetFailureRetryCooldownSeconds(
            int failureCount)
        {
            if (failureCount <= 1)
            {
                return FailureRetryFirstSeconds;
            }

            if (failureCount == 2)
            {
                return FailureRetrySecondSeconds;
            }

            return FailureRetryMaxSeconds;
        }


        private static string HttpGet(
            string url)
        {
            HttpWebRequest request =
                (HttpWebRequest)
                WebRequest.Create(
                    url);

            request.Method =
                "GET";

            request.Timeout =
                RequestTimeoutMs;

            request.ReadWriteTimeout =
                RequestTimeoutMs;

            request.KeepAlive =
                false;

            using (HttpWebResponse response =
                   (HttpWebResponse)
                   request.GetResponse())
            using (Stream stream =
                   response.GetResponseStream())
            using (StreamReader reader =
                   new StreamReader(
                       stream))
            {
                return reader.ReadToEnd();
            }
        }


        private void SaveResult(
            string streamId,
            ZlmStreamGuardResult result)
        {
            if (string.IsNullOrWhiteSpace(
                    streamId) ||
                result == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                _lastResults[
                    streamId] =
                    result.Clone();
            }
        }


        private static ZlmStreamGuardResult CreateFailure(
            string message)
        {
            ZlmStreamGuardResult result =
                new ZlmStreamGuardResult();

            result.State =
                ZlmStreamGuardState.Failed;

            result.Success =
                false;

            result.AuthFailed =
                false;

            result.Message =
                message ??
                string.Empty;

            result.AttemptUtc =
                DateTime.UtcNow;

            return result;
        }


        private static string GetCameraName(
            CameraConfig camera)
        {
            if (camera == null)
            {
                return "Camera";
            }

            if (!string.IsNullOrWhiteSpace(
                    camera.Name))
            {
                return camera.Name;
            }

            if (!string.IsNullOrWhiteSpace(
                    camera.StreamId))
            {
                return camera.StreamId;
            }

            return "Camera";
        }
    }
}
