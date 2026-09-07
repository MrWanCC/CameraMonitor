using System;
using System.Collections.Generic;
using System.Threading;

namespace CameraMonitor
{
    public enum HikvisionDeviceState
    {
        Unknown = 0,
        Connecting = 1,
        Online = 2,
        AuthenticationFailed = 3,
        Offline = 4,
        ConnectionLimit = 5,
        ChannelError = 6,
        Error = 7
    }


    public sealed class HikvisionDeviceStatus
    {
        public HikvisionDeviceStatus()
        {
            CameraIndex = -1;
            CameraName = string.Empty;
            DeviceAddress = string.Empty;
            SdkPort = 8000;
            UserId = -1;
            State = HikvisionDeviceState.Unknown;
            ErrorCode = 0;
            ErrorMessage = string.Empty;
            ConsecutiveFailures = 0;
            LastCheckUtc = DateTime.MinValue;
            LastSuccessUtc = DateTime.MinValue;
        }

        public int CameraIndex { get; set; }

        public string CameraName { get; set; }

        public string DeviceAddress { get; set; }

        public ushort SdkPort { get; set; }

        public int UserId { get; set; }

        public HikvisionDeviceState State { get; set; }

        public uint ErrorCode { get; set; }

        public string ErrorMessage { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTime LastCheckUtc { get; set; }

        public DateTime LastSuccessUtc { get; set; }

        public HikvisionDeviceStatus Clone()
        {
            HikvisionDeviceStatus copy =
                new HikvisionDeviceStatus();

            copy.CameraIndex =
                CameraIndex;

            copy.CameraName =
                CameraName;

            copy.DeviceAddress =
                DeviceAddress;

            copy.SdkPort =
                SdkPort;

            copy.UserId =
                UserId;

            copy.State =
                State;

            copy.ErrorCode =
                ErrorCode;

            copy.ErrorMessage =
                ErrorMessage;

            copy.ConsecutiveFailures =
                ConsecutiveFailures;

            copy.LastCheckUtc =
                LastCheckUtc;

            copy.LastSuccessUtc =
                LastSuccessUtc;

            return copy;
        }
    }


    public sealed class HikvisionDeviceStatusChangedEventArgs :
        EventArgs
    {
        public HikvisionDeviceStatusChangedEventArgs(
            HikvisionDeviceStatus status)
        {
            Status = status;
        }

        public HikvisionDeviceStatus Status
        {
            get;
            private set;
        }
    }


    /// <summary>
    /// 6路海康设备状态管理。
    ///
    /// 只负责设备SDK：
    /// - 登录
    /// - 在线检测
    /// - 错误码
    /// - 自动重新登录
    ///
    /// 不参与视频播放。
    /// </summary>
    public sealed class HikvisionDeviceManager :
        IDisposable
    {
        private const int MonitorIntervalMs =
            10000;

        // 网络类错误连续2次才正式判离线。
        private const int OfflineFailureThreshold =
            2;

        // 当前现场默认使用海康SDK端口8000。
        // 后面把 SdkPort 正式加入 CameraConfig 后，
        // 这里只需要改 ResolveSdkPort()。
        private const ushort DefaultSdkPort =
            8000;


        private readonly object _syncRoot =
            new object();

        private readonly HikvisionSdkService _sdkService;

        private readonly List<CameraConfig> _cameras;

        private readonly HikvisionDeviceStatus[] _statuses;

        private System.Threading.Timer _timer;

        private int _checking;

        private bool _started;

        private bool _disposed;


        public HikvisionDeviceManager(
            IList<CameraConfig> cameras)
        {
            if (cameras == null)
            {
                throw new ArgumentNullException(
                    "cameras");
            }

            if (cameras.Count < 6)
            {
                throw new ArgumentException(
                    "海康设备状态管理要求至少配置 Camera01 ~ Camera06。",
                    "cameras");
            }

            _sdkService =
                new HikvisionSdkService();

            _cameras =
                new List<CameraConfig>();

            _statuses =
                new HikvisionDeviceStatus[6];

            for (int i = 0;
                 i < 6;
                 i++)
            {
                _cameras.Add(
                    cameras[i]);

                _statuses[i] =
                    CreateInitialStatus(
                        i,
                        cameras[i]);
            }
        }


        public event EventHandler<HikvisionDeviceStatusChangedEventArgs>
            StatusChanged;


        public void Start()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(
                        "HikvisionDeviceManager");
                }

                if (_started)
                {
                    return;
                }

                _started =
                    true;

                _timer =
                    new System.Threading.Timer(
                        TimerCallback,
                        null,
                        0,
                        MonitorIntervalMs);

                AppLogger.Info(
                    "海康6路设备状态管理已启动");
            }
        }


        public HikvisionDeviceStatus GetStatus(
            int cameraIndex)
        {
            lock (_syncRoot)
            {
                if (cameraIndex < 0 ||
                    cameraIndex >= _statuses.Length)
                {
                    return null;
                }

                return
                    _statuses[cameraIndex]
                        .Clone();
            }
        }


        public HikvisionDeviceStatus[] GetAllStatuses()
        {
            lock (_syncRoot)
            {
                HikvisionDeviceStatus[] result =
                    new HikvisionDeviceStatus[
                        _statuses.Length];

                for (int i = 0;
                     i < _statuses.Length;
                     i++)
                {
                    result[i] =
                        _statuses[i]
                            .Clone();
                }

                return result;
            }
        }


        private void TimerCallback(
            object state)
        {
            if (_disposed ||
                !_started)
            {
                return;
            }

            if (Interlocked.Exchange(
                    ref _checking,
                    1) == 1)
            {
                return;
            }

            try
            {
                EnsureSdkInitialized();

                if (!_sdkService.IsInitialized)
                {
                    return;
                }

                /*
                 * 6 路设备状态检测并行执行。
                 *
                 * 原来这里按 01 -> 06 串行 CheckCamera，
                 * 任意一台离线都会让后面的设备排队等待
                 * NET_DVR_Login_V40 的 3 秒连接超时。
                 *
                 * 现在一轮仍然由 _checking 保证不会重入，
                 * 但轮内 6 路各自在 ThreadPool 中独立检测；
                 * CountdownEvent 等全部设备完成后才结束本轮，
                 * 保持原来的“每轮完整结束后再允许下一轮”语义。
                 */
                using (CountdownEvent countdown =
                    new CountdownEvent(6))
                {
                    for (int i = 0;
                         i < 6;
                         i++)
                    {
                        int cameraIndex =
                            i;

                        bool queued =
                            ThreadPool.QueueUserWorkItem(
                                delegate
                                {
                                    try
                                    {
                                        if (_disposed ||
                                            !_started)
                                        {
                                            return;
                                        }

                                        CheckCamera(
                                            cameraIndex);
                                    }
                                    catch (Exception ex)
                                    {
                                        AppLogger.Error(
                                            "Camera "
                                            + (cameraIndex + 1)
                                                .ToString("00")
                                            + " 海康SDK状态检测异常",
                                            ex);
                                    }
                                    finally
                                    {
                                        countdown.Signal();
                                    }
                                });

                        if (!queued)
                        {
                            countdown.Signal();

                            AppLogger.Warn(
                                "Camera "
                                + (cameraIndex + 1)
                                    .ToString("00")
                                + " 海康SDK检测任务加入线程池失败");
                        }
                    }

                    countdown.Wait();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "海康设备状态后台检测异常",
                    ex);
            }
            finally
            {
                Interlocked.Exchange(
                    ref _checking,
                    0);
            }
        }


        private void EnsureSdkInitialized()
        {
            if (_sdkService.IsInitialized)
            {
                return;
            }

            string errorMessage;

            bool success =
                _sdkService.Initialize(
                    out errorMessage);

            if (success)
            {
                return;
            }

            AppLogger.Warn(
                "海康 SDK 尚未就绪："
                + errorMessage);

            for (int i = 0;
                 i < _statuses.Length;
                 i++)
            {
                SetState(
                    i,
                    HikvisionDeviceState.Error,
                    0,
                    "海康SDK初始化失败",
                    false);
            }
        }


        private void CheckCamera(
            int cameraIndex)
        {
            HikvisionDeviceStatus status;

            lock (_syncRoot)
            {
                status =
                    _statuses[cameraIndex];
            }

            if (status.UserId < 0)
            {
                TryLogin(
                    cameraIndex);

                return;
            }

            HikvisionHealthResult health =
                _sdkService.CheckHealth(
                    status.UserId);

            if (health.Success)
            {
                lock (_syncRoot)
                {
                    status =
                        _statuses[cameraIndex];

                    status.LastCheckUtc =
                        DateTime.UtcNow;

                    status.LastSuccessUtc =
                        DateTime.UtcNow;

                    status.ConsecutiveFailures =
                        0;

                    status.ErrorCode =
                        0;

                    status.ErrorMessage =
                        string.Empty;
                }

                if (health.DeviceStatic == 0)
                {
                    SetState(
                        cameraIndex,
                        HikvisionDeviceState.Online,
                        0,
                        string.Empty,
                        false);
                }
                else
                {
                    SetState(
                        cameraIndex,
                        HikvisionDeviceState.Error,
                        0,
                        "设备工作状态异常，DeviceStatic="
                        + health.DeviceStatic,
                        false);
                }

                return;
            }

            HandleHealthFailure(
                cameraIndex,
                health);
        }


        private void TryLogin(
            int cameraIndex)
        {
            CameraConfig camera =
                _cameras[cameraIndex];

            string deviceAddress;
            string userName;
            string password;

            if (!TryParseCameraConnection(
                    camera,
                    out deviceAddress,
                    out userName,
                    out password))
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.Error,
                    0,
                    "无法从SourceUrl解析设备IP或账号",
                    true);

                return;
            }

            ushort sdkPort =
                ResolveSdkPort(
                    camera);

            HikvisionDeviceState stateBeforeLogin;

            lock (_syncRoot)
            {
                HikvisionDeviceStatus status =
                    _statuses[cameraIndex];

                status.DeviceAddress =
                    deviceAddress;

                status.SdkPort =
                    sdkPort;

                status.LastCheckUtc =
                    DateTime.UtcNow;

                stateBeforeLogin =
                    status.State;
            }

            /*
             * 只有首次启动/尚未确认故障时，才发布 Connecting。
             *
             * 一旦已经确认：
             * - Offline
             * - AuthenticationFailed
             * - ConnectionLimit
             * - ChannelError
             * - Error
             *
             * 后台重试登录期间继续保持原故障状态，
             * 避免 UI 在“离线”和“正常/连接中”之间闪烁。
             *
             * 登录真正成功后，再由下面的 Online 状态恢复界面。
             */
            if (ShouldPublishConnectingState(
                    stateBeforeLogin))
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.Connecting,
                    0,
                    string.Empty,
                    false);
            }

            HikvisionLoginResult result =
                _sdkService.Login(
                    deviceAddress,
                    sdkPort,
                    userName,
                    password);

            if (result.Success)
            {
                lock (_syncRoot)
                {
                    HikvisionDeviceStatus status =
                        _statuses[cameraIndex];

                    status.UserId =
                        result.UserId;

                    status.ErrorCode =
                        0;

                    status.ErrorMessage =
                        string.Empty;

                    status.ConsecutiveFailures =
                        0;

                    status.LastCheckUtc =
                        DateTime.UtcNow;

                    status.LastSuccessUtc =
                        DateTime.UtcNow;
                }

                SetState(
                    cameraIndex,
                    HikvisionDeviceState.Online,
                    0,
                    string.Empty,
                    false);

                return;
            }

            HandleLoginFailure(
                cameraIndex,
                result);
        }


        private void HandleLoginFailure(
            int cameraIndex,
            HikvisionLoginResult result)
        {
            uint errorCode =
                result == null
                    ? 0
                    : result.ErrorCode;

            string errorMessage =
                result == null
                    ? "登录失败"
                    : result.ErrorMessage;

            if (IsAuthenticationError(
                    errorCode))
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.AuthenticationFailed,
                    errorCode,
                    errorMessage,
                    true);

                return;
            }

            if (errorCode ==
                PreviewDemo.CHCNetSDK.NET_DVR_OVER_MAXLINK)
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.ConnectionLimit,
                    errorCode,
                    errorMessage,
                    true);

                return;
            }

            if (errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_CHAN_EXCEPTION ||
                errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_IPCHAN_NOTALIVE)
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.ChannelError,
                    errorCode,
                    errorMessage,
                    true);

                return;
            }

            int failures =
                IncrementFailure(
                    cameraIndex,
                    errorCode,
                    errorMessage);

            if (IsNetworkError(
                    errorCode) &&
                failures >=
                    OfflineFailureThreshold)
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.Offline,
                    errorCode,
                    errorMessage,
                    true);

                return;
            }

            SetState(
                cameraIndex,
                HikvisionDeviceState.Connecting,
                errorCode,
                errorMessage,
                false);
        }


        private void HandleHealthFailure(
            int cameraIndex,
            HikvisionHealthResult health)
        {
            uint errorCode =
                health == null
                    ? 0
                    : health.ErrorCode;

            string errorMessage =
                health == null
                    ? "设备健康检测失败"
                    : health.ErrorMessage;

            int failures =
                IncrementFailure(
                    cameraIndex,
                    errorCode,
                    errorMessage);

            if (failures <
                OfflineFailureThreshold)
            {
                // 第一次瞬时失败先保持当前状态，
                // 避免网络抖一下UI就闪离线。
                return;
            }

            int userId;

            lock (_syncRoot)
            {
                userId =
                    _statuses[cameraIndex]
                        .UserId;
            }

            if (userId >= 0)
            {
                _sdkService.Logout(
                    userId);
            }

            lock (_syncRoot)
            {
                _statuses[cameraIndex]
                    .UserId =
                    -1;
            }

            if (IsAuthenticationError(
                    errorCode))
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.AuthenticationFailed,
                    errorCode,
                    errorMessage,
                    true);

                return;
            }

            if (errorCode ==
                PreviewDemo.CHCNetSDK.NET_DVR_OVER_MAXLINK)
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.ConnectionLimit,
                    errorCode,
                    errorMessage,
                    true);

                return;
            }

            if (errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_CHAN_EXCEPTION ||
                errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_IPCHAN_NOTALIVE)
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.ChannelError,
                    errorCode,
                    errorMessage,
                    true);

                return;
            }

            if (IsNetworkError(
                    errorCode))
            {
                SetState(
                    cameraIndex,
                    HikvisionDeviceState.Offline,
                    errorCode,
                    errorMessage,
                    true);

                return;
            }

            SetState(
                cameraIndex,
                HikvisionDeviceState.Error,
                errorCode,
                errorMessage,
                true);
        }


        private int IncrementFailure(
            int cameraIndex,
            uint errorCode,
            string errorMessage)
        {
            lock (_syncRoot)
            {
                HikvisionDeviceStatus status =
                    _statuses[cameraIndex];

                status.ConsecutiveFailures++;

                status.ErrorCode =
                    errorCode;

                status.ErrorMessage =
                    errorMessage ?? string.Empty;

                status.LastCheckUtc =
                    DateTime.UtcNow;

                return
                    status.ConsecutiveFailures;
            }
        }


        private void SetState(
            int cameraIndex,
            HikvisionDeviceState newState,
            uint errorCode,
            string errorMessage,
            bool warning)
        {
            HikvisionDeviceStatus snapshot;

            bool changed;

            lock (_syncRoot)
            {
                HikvisionDeviceStatus status =
                    _statuses[cameraIndex];

                changed =
                    status.State !=
                    newState;

                status.State =
                    newState;

                status.ErrorCode =
                    errorCode;

                status.ErrorMessage =
                    errorMessage ?? string.Empty;

                status.LastCheckUtc =
                    DateTime.UtcNow;

                snapshot =
                    status.Clone();
            }

            if (changed)
            {
                string message =
                    snapshot.CameraName
                    + " SDK状态 -> "
                    + snapshot.State;

                if (snapshot.ErrorCode != 0)
                {
                    message +=
                        "，ErrorCode="
                        + snapshot.ErrorCode
                        + "，"
                        + snapshot.ErrorMessage;
                }

                if (warning)
                {
                    AppLogger.Warn(
                        message);
                }
                else
                {
                    AppLogger.Info(
                        message);
                }

                RaiseStatusChanged(
                    snapshot);
            }
        }


        private void RaiseStatusChanged(
            HikvisionDeviceStatus status)
        {
            EventHandler<HikvisionDeviceStatusChangedEventArgs> handler =
                StatusChanged;

            if (handler == null)
            {
                return;
            }

            try
            {
                handler(
                    this,
                    new HikvisionDeviceStatusChangedEventArgs(
                        status));
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "海康SDK状态事件处理失败："
                    + ex.Message);
            }
        }


        private static HikvisionDeviceStatus CreateInitialStatus(
            int cameraIndex,
            CameraConfig camera)
        {
            HikvisionDeviceStatus status =
                new HikvisionDeviceStatus();

            status.CameraIndex =
                cameraIndex;

            status.CameraName =
                camera != null &&
                !string.IsNullOrWhiteSpace(
                    camera.Name)
                    ? camera.Name
                    : "Camera "
                      + (cameraIndex + 1)
                          .ToString("00");

            status.SdkPort =
                DefaultSdkPort;

            return status;
        }


        private static ushort ResolveSdkPort(
            CameraConfig camera)
        {
            // 当前版本先统一使用海康默认SDK端口8000。
            // 下一步再把 SdkPort 正式加入 config.ini。
            return DefaultSdkPort;
        }


        private static bool TryParseCameraConnection(
            CameraConfig camera,
            out string deviceAddress,
            out string userName,
            out string password)
        {
            deviceAddress =
                string.Empty;

            userName =
                string.Empty;

            password =
                string.Empty;

            if (camera == null ||
                string.IsNullOrWhiteSpace(
                    camera.SourceUrl))
            {
                return false;
            }

            Uri uri;

            if (!Uri.TryCreate(
                    camera.SourceUrl,
                    UriKind.Absolute,
                    out uri))
            {
                return false;
            }

            deviceAddress =
                uri.Host;

            ParseUserInfo(
                uri.UserInfo,
                out userName,
                out password);

            return
                !string.IsNullOrWhiteSpace(
                    deviceAddress)
                &&
                !string.IsNullOrWhiteSpace(
                    userName);
        }


        private static void ParseUserInfo(
            string userInfo,
            out string userName,
            out string password)
        {
            userName =
                string.Empty;

            password =
                string.Empty;

            if (string.IsNullOrEmpty(
                userInfo))
            {
                return;
            }

            int separatorIndex =
                userInfo.IndexOf(':');

            if (separatorIndex < 0)
            {
                userName =
                    Uri.UnescapeDataString(
                        userInfo);

                return;
            }

            userName =
                Uri.UnescapeDataString(
                    userInfo.Substring(
                        0,
                        separatorIndex));

            password =
                Uri.UnescapeDataString(
                    userInfo.Substring(
                        separatorIndex + 1));
        }


        private static bool ShouldPublishConnectingState(
            HikvisionDeviceState currentState)
        {
            /*
             * Unknown：程序刚启动，允许显示“正在连接”。
             * Connecting：尚未达到正式故障判定阈值，继续保持连接中。
             *
             * 其他状态都属于已经有明确结论的状态。
             * 后台重试时不覆盖它，只有登录成功才切回 Online。
             */
            return
                currentState ==
                    HikvisionDeviceState.Unknown
                ||
                currentState ==
                    HikvisionDeviceState.Connecting;
        }


        private static bool IsAuthenticationError(
            uint errorCode)
        {
            return
                errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_PASSWORD_ERROR
                ||
                errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_USERNOTEXIST
                ||
                errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_USER_LOCKED;
        }


        private static bool IsNetworkError(
            uint errorCode)
        {
            return
                errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_NETWORK_FAIL_CONNECT
                ||
                errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_NETWORK_SEND_ERROR
                ||
                errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_NETWORK_RECV_ERROR
                ||
                errorCode ==
                    PreviewDemo.CHCNetSDK.NET_DVR_NETWORK_RECV_TIMEOUT
                ||
                errorCode == 73;
        }


        public void Stop()
        {
            lock (_syncRoot)
            {
                if (!_started)
                {
                    return;
                }

                _started =
                    false;

                if (_timer != null)
                {
                    try
                    {
                        _timer.Dispose();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            "海康SDK Timer Dispose失败："
                            + ex.Message);
                    }

                    _timer =
                        null;
                }
            }

            // 并行检测时单路登录最长约3秒；这里最多等4秒，
            // 让当前一轮优先自然退出。SdkService 自身还有
            // active-operation 保护，Cleanup 不会和SDK调用并发。
            for (int i = 0;
                 i < 80 &&
                 Interlocked.CompareExchange(
                     ref _checking,
                     0,
                     0) == 1;
                 i++)
            {
                Thread.Sleep(
                    50);
            }

            try
            {
                _sdkService.Dispose();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "关闭海康设备状态管理失败",
                    ex);
            }

            lock (_syncRoot)
            {
                for (int i = 0;
                     i < _statuses.Length;
                     i++)
                {
                    _statuses[i].UserId =
                        -1;
                }
            }

            AppLogger.Info(
                "海康6路设备状态管理已停止");
        }


        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();

            _disposed =
                true;
        }
    }
}