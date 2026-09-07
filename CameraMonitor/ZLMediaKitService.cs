using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CameraMonitor
{
    // =============================================================
    // ZLM流健康状态
    // =============================================================

    public sealed class StreamHealthInfo
    {
        public string StreamId
        {
            get;
            set;
        }

        public bool ProxyExists
        {
            get;
            set;
        }

        public bool IsPlaying
        {
            get;
            set;
        }

        public string StatusText
        {
            get;
            set;
        }

        public int RePullCount
        {
            get;
            set;
        }

        public int LiveSeconds
        {
            get;
            set;
        }

        public long VideoFrames
        {
            get;
            set;
        }
    }


    // =============================================================
    // ZLMediaKit服务
    // =============================================================

    public class ZLMediaKitService
    {
        private readonly AppConfig _config;

        /*
         * HTTP超时。
         *
         * 后台检测不应该因为一台ZLM挂掉
         * 阻塞几十秒。
         */
        private const int HttpTimeout =
            2500;


        public ZLMediaKitService(
            AppConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException(
                    "config");
            }

            _config =
                config;

            AppLogger.Info(
                "ZLMediaKitService 初始化");

            AppLogger.Info(
                "ZLM Host=" +
                _config.ZlmHost +
                ", HttpPort=" +
                _config.ZlmHttpPort +
                ", RtspPort=" +
                _config.ZlmRtspPort);
        }


        // =========================================================
        // 检查ZLM服务器
        // =========================================================

        public bool IsServerAvailable()
        {
            try
            {
                string url =
                    GetApiBaseUrl() +
                    "/index/api/getServerConfig" +
                    "?secret=" +
                    Encode(
                        _config.ZlmSecret);

                string result =
                    DownloadString(
                        url);

                return IsApiSuccess(
                    result);
            }
            catch (WebException)
            {
                /*
                 * ZLM离线/重启/网络断开
                 * 属于正常恢复流程。
                 *
                 * 这里不每5秒写一次完整异常。
                 * MainForm会记录：
                 * ZLM离线
                 * ZLM恢复
                 */
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "检查ZLM在线状态失败：" +
                    ex.Message);

                return false;
            }
        }


        // =========================================================
        // 确保6路代理存在
        // =========================================================

        public bool EnsureAllStreams()
        {
            bool changedAny =
                false;

            if (_config.Cameras == null ||
                _config.Cameras.Count == 0)
            {
                return false;
            }

            foreach (CameraConfig camera
                in _config.Cameras)
            {
                if (camera == null)
                {
                    continue;
                }

                try
                {
                    bool changed =
                        EnsureStream(
                            camera);

                    if (changed)
                    {
                        changedAny =
                            true;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(
                        SafeStreamName(
                            camera) +
                        " EnsureStream异常",
                        ex);
                }
            }

            return changedAny;
        }


        // =========================================================
        // 确保单路代理
        // =========================================================

        public bool EnsureStream(
            CameraConfig camera)
        {
            if (camera == null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(
                camera.StreamId))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(
                camera.SourceUrl))
            {
                AppLogger.Warn(
                    SafeStreamName(
                        camera) +
                    " SourceUrl为空");

                return false;
            }

            string currentSourceUrl =
                GetProxySourceUrl(
                    camera.StreamId);

            // -----------------------------------------------------
            // 代理不存在
            // -----------------------------------------------------

            if (string.IsNullOrWhiteSpace(
                currentSourceUrl))
            {
                bool added =
                    AddStreamProxy(
                        camera);

                if (added)
                {
                    AppLogger.Info(
                        SafeStreamName(
                            camera) +
                        " 创建RTSP代理成功");
                }

                return added;
            }


            // -----------------------------------------------------
            // SourceUrl没有改变
            // -----------------------------------------------------

            if (SourceUrlEquals(
                currentSourceUrl,
                camera.SourceUrl))
            {
                return false;
            }


            // -----------------------------------------------------
            // SourceUrl改变
            // -----------------------------------------------------

            AppLogger.Info(
                SafeStreamName(
                    camera) +
                " SourceUrl已变化，重建代理");

            bool deleted =
                DeleteStreamProxy(
                    camera.StreamId);

            if (!deleted)
            {
                AppLogger.Warn(
                    SafeStreamName(
                        camera) +
                    " 删除旧代理失败");

                return false;
            }

            bool recreated =
                AddStreamProxy(
                    camera);

            if (recreated)
            {
                AppLogger.Info(
                    SafeStreamName(
                        camera) +
                    " SourceUrl更新完成");
            }

            return recreated;
        }


        // =========================================================
        // 强制删除 + 重建
        // =========================================================

        public bool RecreateStream(
            CameraConfig camera)
        {
            if (camera == null ||
                string.IsNullOrWhiteSpace(
                    camera.StreamId) ||
                string.IsNullOrWhiteSpace(
                    camera.SourceUrl))
            {
                return false;
            }

            try
            {
                Dictionary<string, object> data =
                    GetProxyInfoData(
                        camera.StreamId);

                if (data != null)
                {
                    DeleteStreamProxy(
                        camera.StreamId);

                    /*
                     * 给ZLM一点时间释放旧PlayerProxy。
                     *
                     * 只有强制重建才执行，
                     * 不会高频调用。
                     */
                    Thread.Sleep(
                        100);
                }

                bool success =
                    AddStreamProxy(
                        camera);

                if (success)
                {
                    AppLogger.Info(
                        SafeStreamName(
                            camera) +
                        " 强制重建代理成功");
                }

                return success;
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    SafeStreamName(
                        camera) +
                    " 强制重建代理异常",
                    ex);

                return false;
            }
        }


        // =========================================================
        // 获取流运行状态
        // =========================================================

        public StreamHealthInfo GetStreamHealth(
            string streamId)
        {
            StreamHealthInfo info =
                new StreamHealthInfo();

            info.StreamId =
                streamId;

            info.ProxyExists =
                false;

            info.IsPlaying =
                false;

            info.StatusText =
                string.Empty;

            if (string.IsNullOrWhiteSpace(
                streamId))
            {
                return info;
            }

            try
            {
                Dictionary<string, object> data =
                    GetProxyInfoData(
                        streamId);

                if (data == null)
                {
                    return info;
                }

                info.ProxyExists =
                    true;

                info.StatusText =
                    ZlmJson.GetString(
                        data,
                        "status_str") ??
                    string.Empty;

                /*
                 * ZLM 不同版本字段命名不一致：
                 *
                 *   re_pull_count / rePullCount
                 *   live_secs / liveSecs
                 *
                 * 两个候选都取，兼容两种版本。
                 */
                info.RePullCount =
                    ZlmJson.GetInt(
                        data,
                        0,
                        "re_pull_count",
                        "rePullCount");

                info.LiveSeconds =
                    ZlmJson.GetInt(
                        data,
                        0,
                        "live_secs",
                        "liveSecs");

                info.VideoFrames =
                    GetVideoFrames(
                        data);

                info.IsPlaying =
                    string.Equals(
                        info.StatusText,
                        "playing",
                        StringComparison
                            .OrdinalIgnoreCase);

                return info;
            }
            catch (WebException)
            {
                /*
                 * 短暂网络异常。
                 * 返回当前unknown状态，
                 * MainForm下一轮继续检查。
                 */
                return info;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    streamId +
                    " GetStreamHealth失败：" +
                    ex.Message);

                return info;
            }
        }


        // =========================================================
        // getProxyInfo
        //
        // 返回反序列化后的 data 对象；
        // 代理不存在 / 解析失败 / code!=0 时返回 null。
        // =========================================================

        private Dictionary<string, object> GetProxyInfoData(
            string streamId)
        {
            string key =
                "__defaultVhost__/live/" +
                streamId;

            string url =
                GetApiBaseUrl() +
                "/index/api/getProxyInfo" +
                "?secret=" +
                Encode(
                    _config.ZlmSecret) +
                "&key=" +
                Encode(
                    key);

            string result =
                DownloadString(
                    url);

            if (string.IsNullOrWhiteSpace(
                result))
            {
                return null;
            }

            Dictionary<string, object> root;

            if (!ZlmJson.TryParse(
                    result,
                    out root))
            {
                return null;
            }

            /*
             * 代理不存在：
             *
             * {
             *   "code": -500,
             *   "msg": "can not find the proxy"
             * }
             */
            int code =
                ZlmJson.GetInt(
                    root,
                    "code",
                    int.MinValue);

            if (code == -500)
            {
                return null;
            }

            if (code != 0)
            {
                return null;
            }

            string msg =
                ZlmJson.GetString(
                    root,
                    "msg");

            if (!string.IsNullOrEmpty(
                    msg) &&
                msg.IndexOf(
                    "can not find the proxy",
                    StringComparison
                        .OrdinalIgnoreCase) >= 0)
            {
                return null;
            }

            return ZlmJson.GetObject(
                root,
                "data");
        }


        // =========================================================
        // 获取当前代理SourceUrl
        // =========================================================

        private string GetProxySourceUrl(
            string streamId)
        {
            try
            {
                Dictionary<string, object> data =
                    GetProxyInfoData(
                        streamId);

                if (data == null)
                {
                    return null;
                }

                string sourceUrl =
                    ZlmJson.GetString(
                        data,
                        "url");

                if (string.IsNullOrWhiteSpace(
                    sourceUrl))
                {
                    return null;
                }

                /*
                 * JavaScriptSerializer 已自动处理 JSON 转义
                 * （如 \/ -> /、\" -> "），无需手工反转义。
                 */
                return sourceUrl;
            }
            catch (WebException)
            {
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    streamId +
                    " 获取代理SourceUrl失败：" +
                    ex.Message);

                return null;
            }
        }


        // =========================================================
        // 创建拉流代理
        // =========================================================

        private bool AddStreamProxy(
            CameraConfig camera)
        {
            try
            {
                /*
                 * CameraMonitor只使用：
                 *
                 * 摄像头
                 *   ↓ RTSP
                 * ZLM
                 *   ↓ RTSP
                 * LibVLC
                 *
                 * 所以不生成：
                 * RTMP
                 * HLS
                 * TS
                 * FMP4
                 * MP4
                 *
                 * 这也是我们之前解决某台海康
                 * H265 extra_data异常的关键。
                 */

                string url =
                    GetApiBaseUrl() +
                    "/index/api/addStreamProxy" +

                    "?secret=" +
                    Encode(
                        _config.ZlmSecret) +

                    "&vhost=" +
                    Encode(
                        "__defaultVhost__") +

                    "&app=" +
                    Encode(
                        "live") +

                    "&stream=" +
                    Encode(
                        camera.StreamId) +

                    "&url=" +
                    Encode(
                        camera.SourceUrl) +

                    // RTSP走TCP
                    "&rtp_type=0" +

                    // 无限重试
                    "&retry_count=-1" +

                    // 无人观看时保持拉流不自动断开
                    // （否则切组时 ZLM 需重新向摄像头拉流，延迟 1~3 秒）
                    "&auto_close=0" +

                    // 只开RTSP
                    "&enable_rtsp=1" +

                    // 关闭其它协议
                    "&enable_rtmp=0" +
                    "&enable_hls=0" +
                    "&enable_hls_fmp4=0" +
                    "&enable_ts=0" +
                    "&enable_fmp4=0" +
                    "&enable_mp4=0";

                string result =
                    DownloadString(
                        url);

                bool success =
                    IsApiSuccess(
                        result);

                if (!success)
                {
                    AppLogger.Warn(
                        SafeStreamName(
                            camera) +
                        " addStreamProxy失败：" +
                        SafeApiResult(
                            result));
                }

                return success;
            }
            catch (WebException ex)
            {
                AppLogger.Warn(
                    SafeStreamName(
                        camera) +
                    " addStreamProxy网络异常：" +
                    ex.Message);

                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    SafeStreamName(
                        camera) +
                    " AddStreamProxy异常",
                    ex);

                return false;
            }
        }


        // =========================================================
        // 删除代理
        // =========================================================

        private bool DeleteStreamProxy(
            string streamId)
        {
            try
            {
                string key =
                    "__defaultVhost__/live/" +
                    streamId;

                string url =
                    GetApiBaseUrl() +
                    "/index/api/delStreamProxy" +

                    "?secret=" +
                    Encode(
                        _config.ZlmSecret) +

                    "&key=" +
                    Encode(
                        key);

                string result =
                    DownloadString(
                        url);

                if (!IsApiSuccess(
                    result))
                {
                    AppLogger.Warn(
                        streamId +
                        " delStreamProxy失败：" +
                        SafeApiResult(
                            result));

                    return false;
                }

                /*
                 * 不同ZLM版本返回：
                 *
                 * code=0 + flag=true
                 *
                 * 或仅code=0。
                 *
                 * code=0直接视为成功。
                 */
                return true;
            }
            catch (WebException ex)
            {
                AppLogger.Warn(
                    streamId +
                    " 删除代理网络异常：" +
                    ex.Message);

                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    streamId +
                    " DeleteStreamProxy异常",
                    ex);

                return false;
            }
        }


        // =========================================================
        // API基础URL
        // =========================================================

        private string GetApiBaseUrl()
        {
            return
                "http://" +
                _config.ZlmHost +
                ":" +
                _config.ZlmHttpPort;
        }


        // =========================================================
        // HTTP GET
        // =========================================================

        private string DownloadString(
            string url)
        {
            using (
                TimeoutWebClient client =
                    new TimeoutWebClient(
                        HttpTimeout))
            {
                client.Encoding =
                    Encoding.UTF8;

                return client
                    .DownloadString(
                        url);
            }
        }


        // =========================================================
        // JSON解析
        //
        // 统一走 ZlmJson（JavaScriptSerializer 反序列化），
        // 不再用正则匹配 JSON。
        // =========================================================

        private static bool IsApiSuccess(
            string json)
        {
            if (string.IsNullOrWhiteSpace(
                json))
            {
                return false;
            }

            Dictionary<string, object> root;

            if (!ZlmJson.TryParse(
                    json,
                    out root))
            {
                return false;
            }

            return ZlmJson.GetInt(
                       root,
                       "code",
                       -1) == 0;
        }


        /// <summary>
        /// 从data.tracks中取video track的frames。
        ///
        /// tracks数组中每个元素：
        ///
        /// {
        ///   "codec_type": 0,   // 0=视频，1=音频
        ///   "frames": 258,
        ///   ...
        /// }
        ///
        /// 取第一个 codec_type=0 的track。
        /// </summary>
        private static long GetVideoFrames(
            Dictionary<string, object> data)
        {
            object[] tracks =
                ZlmJson.GetArray(
                    data,
                    "tracks");

            if (tracks == null ||
                tracks.Length == 0)
            {
                return 0;
            }

            foreach (object item in tracks)
            {
                Dictionary<string, object> track =
                    item as
                        Dictionary<string, object>;

                if (track == null)
                {
                    continue;
                }

                if (ZlmJson.GetInt(
                        track,
                        "codec_type",
                        -1) != 0)
                {
                    continue;
                }

                return ZlmJson.GetLong(
                    track,
                    "frames",
                    0);
            }

            return 0;
        }


        // =========================================================
        // SourceUrl比较
        // =========================================================

        private static bool SourceUrlEquals(
            string url1,
            string url2)
        {
            if (url1 == null ||
                url2 == null)
            {
                return false;
            }

            return string.Equals(
                url1.Trim(),
                url2.Trim(),
                StringComparison.Ordinal);
        }


        // =========================================================
        // URL编码
        // =========================================================

        private static string Encode(
            string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            return Uri.EscapeDataString(
                value);
        }


        // =========================================================
        // 安全日志
        // =========================================================

        private static string SafeStreamName(
            CameraConfig camera)
        {
            if (camera == null)
            {
                return "UnknownCamera";
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

            return "UnknownCamera";
        }


        private static string SafeApiResult(
            string result)
        {
            if (string.IsNullOrWhiteSpace(
                result))
            {
                return "(empty)";
            }

            string safe =
                result;

            /*
             * RTSP URL中包含摄像头密码，
             * 所以绝对不能原样写日志。
             */
            safe =
                Regex.Replace(
                    safe,
                    "rtsp://[^\"\\s]+",
                    "rtsp://***",
                    RegexOptions.IgnoreCase);

            /*
             * 避免API Secret进入日志。
             */
            safe =
                Regex.Replace(
                    safe,
                    "secret=[^&\\s\"]+",
                    "secret=***",
                    RegexOptions.IgnoreCase);

            if (safe.Length >
                300)
            {
                safe =
                    safe.Substring(
                        0,
                        300) +
                    "...";
            }

            return safe;
        }


        // =========================================================
        // WebClient Timeout
        // =========================================================

        private sealed class TimeoutWebClient
            : WebClient
        {
            private readonly int _timeout;


            public TimeoutWebClient(
                int timeout)
            {
                _timeout =
                    timeout;
            }


            protected override WebRequest GetWebRequest(
                Uri address)
            {
                WebRequest request =
                    base.GetWebRequest(
                        address);

                if (request != null)
                {
                    request.Timeout =
                        _timeout;

                    HttpWebRequest httpRequest =
                        request as
                            HttpWebRequest;

                    if (httpRequest != null)
                    {
                        httpRequest
                            .ReadWriteTimeout =
                            _timeout;

                        /*
                         * 每5秒健康检测时无需维持
                         * 一大堆长期HTTP KeepAlive连接。
                         */
                        httpRequest.KeepAlive =
                            false;
                    }
                }

                return request;
            }
        }
    }


    // =============================================================
    // 统一日志
    //
    // 放在本文件中，
    // 所以无需额外创建AppLogger.cs
    // =============================================================

    internal static class AppLogger
    {
        private static readonly object SyncRoot =
            new object();

        /*
         * 单个日志最大10MB。
         */
        private const long MaxLogBytes =
            10L * 1024L * 1024L;

        /*
         * 保留：
         *
         * camera-monitor.log
         * camera-monitor.1.log
         * camera-monitor.2.log
         * camera-monitor.3.log
         */
        private const int BackupCount =
            3;

        private static StreamWriter _writer;

        private static readonly string LogFilePath =
            Path.Combine(
                AppDomain.CurrentDomain
                    .BaseDirectory,
                "camera-monitor.log");


        // =========================================================
        // INFO
        // =========================================================

        public static void Info(
            string message)
        {
            Write(
                "INFO",
                message,
                null);
        }


        // =========================================================
        // WARN
        // =========================================================

        public static void Warn(
            string message)
        {
            Write(
                "WARN",
                message,
                null);
        }


        // =========================================================
        // ERROR
        // =========================================================

        public static void Error(
            string message,
            Exception ex)
        {
            Write(
                "ERROR",
                message,
                ex);
        }


        // =========================================================
        // 写日志
        // =========================================================

        private static void Write(
            string level,
            string message,
            Exception ex)
        {
            try
            {
                lock (SyncRoot)
                {
                    EnsureWriter();

                    RotateIfNeeded();

                    if (_writer == null)
                    {
                        return;
                    }

                    string safeMessage =
                        Redact(
                            message);

                    _writer.WriteLine(
                        DateTime.Now.ToString(
                            "yyyy-MM-dd HH:mm:ss.fff") +
                        " [" +
                        level +
                        "] " +
                        safeMessage);

                    if (ex != null)
                    {
                        string exceptionText =
                            ex.GetType()
                                .FullName +
                            ": " +
                            ex.Message;

                        exceptionText =
                            Redact(
                                exceptionText);

                        _writer.WriteLine(
                            "    " +
                            exceptionText);

                        if (!string.IsNullOrWhiteSpace(
                            ex.StackTrace))
                        {
                            _writer.WriteLine(
                                Redact(
                                    ex.StackTrace));
                        }

                        if (ex.InnerException != null)
                        {
                            string inner =
                                ex.InnerException
                                    .GetType()
                                    .FullName +
                                ": " +
                                ex.InnerException
                                    .Message;

                            _writer.WriteLine(
                                "    InnerException: " +
                                Redact(
                                    inner));
                        }
                    }
                }
            }
            catch
            {
                /*
                 * 日志系统本身绝对不能导致
                 * CameraMonitor退出。
                 */
            }
        }


        // =========================================================
        // 创建持久StreamWriter
        // =========================================================

        private static void EnsureWriter()
        {
            if (_writer != null)
            {
                return;
            }

            try
            {
                FileStream stream =
                    new FileStream(
                        LogFilePath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite);

                _writer =
                    new StreamWriter(
                        stream,
                        new UTF8Encoding(
                            false));

                /*
                 * 每条日志及时落盘。
                 *
                 * 程序异常退出时，
                 * 最后几条排障信息也能保留下来。
                 */
                _writer.AutoFlush =
                    true;
            }
            catch
            {
                _writer =
                    null;
            }
        }


        // =========================================================
        // 日志滚动
        // =========================================================

        private static void RotateIfNeeded()
        {
            try
            {
                if (_writer == null)
                {
                    return;
                }

                if (_writer.BaseStream.Length <
                    MaxLogBytes)
                {
                    return;
                }

                try
                {
                    _writer.Flush();

                    _writer.Dispose();
                }
                catch
                {
                }

                _writer =
                    null;


                // camera-monitor.3.log 删除
                string oldest =
                    GetBackupPath(
                        BackupCount);

                if (File.Exists(
                    oldest))
                {
                    File.Delete(
                        oldest);
                }


                // 2 -> 3
                // 1 -> 2
                for (int i =
                         BackupCount - 1;
                     i >= 1;
                     i--)
                {
                    string source =
                        GetBackupPath(
                            i);

                    string target =
                        GetBackupPath(
                            i + 1);

                    if (File.Exists(
                        source))
                    {
                        File.Move(
                            source,
                            target);
                    }
                }


                // 主日志 -> .1
                if (File.Exists(
                    LogFilePath))
                {
                    File.Move(
                        LogFilePath,
                        GetBackupPath(
                            1));
                }


                EnsureWriter();

                if (_writer != null)
                {
                    _writer.WriteLine(
                        DateTime.Now.ToString(
                            "yyyy-MM-dd HH:mm:ss.fff") +
                        " [INFO] 日志文件已滚动");
                }
            }
            catch
            {
                /*
                 * 滚动失败不影响程序。
                 *
                 * 尝试重新打开主日志。
                 */
                _writer =
                    null;

                EnsureWriter();
            }
        }


        private static string GetBackupPath(
            int index)
        {
            string directory =
                Path.GetDirectoryName(
                    LogFilePath);

            string fileName =
                Path.GetFileNameWithoutExtension(
                    LogFilePath);

            string extension =
                Path.GetExtension(
                    LogFilePath);

            return Path.Combine(
                directory,
                fileName +
                "." +
                index +
                extension);
        }


        // =========================================================
        // 脱敏
        // =========================================================

        private static string Redact(
            string text)
        {
            if (string.IsNullOrEmpty(
                text))
            {
                return text;
            }

            string safe =
                text;

            /*
             * rtsp://admin:password@ip/...
             */
            safe =
                Regex.Replace(
                    safe,
                    "rtsp://[^\\s\"']+",
                    "rtsp://***",
                    RegexOptions.IgnoreCase);

            /*
             * secret=xxxxx
             */
            safe =
                Regex.Replace(
                    safe,
                    "secret=[^&\\s\"']+",
                    "secret=***",
                    RegexOptions.IgnoreCase);

            return safe;
        }


        // =========================================================
        // 程序退出
        // =========================================================

        public static void Shutdown()
        {
            try
            {
                lock (SyncRoot)
                {
                    if (_writer == null)
                    {
                        return;
                    }

                    try
                    {
                        _writer.Flush();
                    }
                    catch
                    {
                    }

                    try
                    {
                        _writer.Dispose();
                    }
                    catch
                    {
                    }

                    _writer =
                        null;
                }
            }
            catch
            {
            }
        }
    }
}