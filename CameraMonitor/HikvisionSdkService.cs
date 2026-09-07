using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using PreviewDemo;

namespace CameraMonitor
{
    /// <summary>
    /// 海康设备登录结果。
    /// </summary>
    public sealed class HikvisionLoginResult
    {
        public HikvisionLoginResult()
        {
            Success = false;
            UserId = -1;
            ErrorCode = 0;
            ErrorMessage = string.Empty;
            DeviceSerialNumber = string.Empty;
        }

        public bool Success { get; set; }

        public int UserId { get; set; }

        public uint ErrorCode { get; set; }

        public string ErrorMessage { get; set; }

        public string DeviceSerialNumber { get; set; }

        public byte DeviceType { get; set; }

        public byte AnalogChannelCount { get; set; }

        public int DigitalChannelCount { get; set; }
    }


    /// <summary>
    /// 海康设备主动健康检测结果。
    /// </summary>
    public sealed class HikvisionHealthResult
    {
        public HikvisionHealthResult()
        {
            Success = false;
            ErrorCode = 0;
            ErrorMessage = string.Empty;
            DeviceStatic = 0;
        }

        public bool Success { get; set; }

        public uint ErrorCode { get; set; }

        public string ErrorMessage { get; set; }

        /// <summary>
        /// 海康工作状态中的设备状态：
        /// 0=正常，1=CPU占用率过高，2=硬件异常。
        /// </summary>
        public uint DeviceStatic { get; set; }
    }



    /// <summary>
    /// 海康设备网络 SDK 服务。
    ///
    /// 当前只负责：
    /// 1. SDK 初始化；
    /// 2. 设置连接超时；
    /// 3. 设置自动重连；
    /// 4. NET_DVR_Login_V40 登录设备；
    /// 5. 获取错误码；
    /// 6. 注销；
    /// 7. SDK 释放。
    ///
    /// 本类不负责视频预览。
    /// CameraMonitor 视频仍然走：
    /// 摄像头 -> RTSP -> ZLMediaKit -> LibVLC
    /// </summary>
    public sealed class HikvisionSdkService : IDisposable
    {
        // SDK连接设备的最长等待时间。
        private const uint ConnectTimeoutMs = 3000;

        // 登录时连接尝试次数。
        private const uint ConnectTryTimes = 1;

        // SDK内部断线重连间隔。
        private const uint ReconnectIntervalMs = 10000;


        private readonly object _syncRoot =
            new object();

        private readonly HashSet<int> _loggedInUserIds =
            new HashSet<int>();

        private bool _initialized;

        // 正在执行的原生SDK调用数量。
        // Login/CheckHealth/Logout 可以并发，但 Shutdown 必须
        // 等这些调用全部退出后才能 NET_DVR_Cleanup。
        private int _activeSdkOperations;

        // Shutdown 开始后禁止新的SDK调用进入。
        private bool _shutdownRequested;

        private bool _disposed;


        public bool IsInitialized
        {
            get
            {
                lock (_syncRoot)
                {
                    return _initialized;
                }
            }
        }


        /// <summary>
        /// 初始化海康SDK。
        /// </summary>
        public bool Initialize(
            out string errorMessage)
        {
            lock (_syncRoot)
            {
                errorMessage =
                    string.Empty;

                if (_disposed)
                {
                    errorMessage =
                        "HikvisionSdkService 已经释放。";

                    return false;
                }

                if (_initialized)
                {
                    return true;
                }

                string sdkPath =
                    Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory,
                        "HCNetSDK.dll");

                if (!File.Exists(
                    sdkPath))
                {
                    errorMessage =
                        "找不到 HCNetSDK.dll，请确认海康SDK运行库已经复制到 CameraMonitor.exe 同目录。"
                        + Environment.NewLine
                        + sdkPath;

                    AppLogger.Warn(
                        errorMessage);

                    return false;
                }

                try
                {
                    bool success =
                        CHCNetSDK.NET_DVR_Init();

                    if (!success)
                    {
                        uint errorCode =
                            SafeGetLastError();

                        errorMessage =
                            "NET_DVR_Init 失败，错误码="
                            + errorCode
                            + "，"
                            + GetErrorDescription(
                                errorCode);

                        AppLogger.Warn(
                            errorMessage);

                        return false;
                    }

                    _initialized =
                        true;

                    _shutdownRequested =
                        false;

                    // 登录设备时最多等待3秒，只尝试1次。
                    if (!CHCNetSDK.NET_DVR_SetConnectTime(
                            ConnectTimeoutMs,
                            ConnectTryTimes))
                    {
                        uint errorCode =
                            SafeGetLastError();

                        AppLogger.Warn(
                            "NET_DVR_SetConnectTime 失败，ErrorCode="
                            + errorCode
                            + "，"
                            + GetErrorDescription(
                                errorCode));
                    }

                    // SDK检测到断线后，每10秒尝试重连。
                    if (!CHCNetSDK.NET_DVR_SetReconnect(
                            ReconnectIntervalMs,
                            1))
                    {
                        uint errorCode =
                            SafeGetLastError();

                        AppLogger.Warn(
                            "NET_DVR_SetReconnect 失败，ErrorCode="
                            + errorCode
                            + "，"
                            + GetErrorDescription(
                                errorCode));
                    }

                    uint sdkVersion =
                        CHCNetSDK.NET_DVR_GetSDKVersion();

                    AppLogger.Info(
                        "海康 SDK 初始化成功，SDKVersion=0x"
                        + sdkVersion.ToString("X8"));

                    return true;
                }
                catch (DllNotFoundException ex)
                {
                    CleanupAfterInitializeFailure();

                    errorMessage =
                        "HCNetSDK.dll 或其依赖 DLL 加载失败："
                        + ex.Message;

                    AppLogger.Error(
                        "海康 SDK DLL 加载失败",
                        ex);

                    return false;
                }
                catch (BadImageFormatException ex)
                {
                    CleanupAfterInitializeFailure();

                    errorMessage =
                        "海康 SDK 位数不匹配。CameraMonitor 当前必须使用 x64 + Win64 SDK。";

                    AppLogger.Error(
                        errorMessage,
                        ex);

                    return false;
                }
                catch (EntryPointNotFoundException ex)
                {
                    CleanupAfterInitializeFailure();

                    errorMessage =
                        "CHCNetSDK.cs 与 HCNetSDK.dll 版本不匹配，找不到SDK接口："
                        + ex.Message;

                    AppLogger.Error(
                        "海康 SDK 接口加载失败",
                        ex);

                    return false;
                }
                catch (Exception ex)
                {
                    CleanupAfterInitializeFailure();

                    errorMessage =
                        "海康 SDK 初始化异常："
                        + ex.Message;

                    AppLogger.Error(
                        "海康 SDK 初始化异常",
                        ex);

                    return false;
                }
            }
        }


        /// <summary>
        /// 使用 NET_DVR_Login_V40 同步登录一台设备。
        /// </summary>
        public HikvisionLoginResult Login(
            string deviceAddress,
            ushort port,
            string userName,
            string password)
        {
            HikvisionLoginResult result =
                new HikvisionLoginResult();

            if (string.IsNullOrWhiteSpace(
                deviceAddress))
            {
                result.ErrorMessage =
                    "设备IP不能为空。";

                return result;
            }

            if (port == 0)
            {
                result.ErrorMessage =
                    "SDK端口不能为0。";

                return result;
            }

            if (string.IsNullOrWhiteSpace(
                userName))
            {
                result.ErrorMessage =
                    "设备用户名不能为空。";

                return result;
            }

            if (password == null)
            {
                password =
                    string.Empty;
            }

            uint enterErrorCode;
            string enterErrorMessage;

            if (!EnterSdkOperation(
                    out enterErrorCode,
                    out enterErrorMessage))
            {
                result.ErrorCode =
                    enterErrorCode;

                result.ErrorMessage =
                    enterErrorMessage;

                return result;
            }

            try
            {
                CHCNetSDK.NET_DVR_USER_LOGIN_INFO loginInfo =
                    CreateLoginInfo(
                        deviceAddress,
                        port,
                        userName,
                        password);

                CHCNetSDK.NET_DVR_DEVICEINFO_V40 deviceInfo =
                    CreateDeviceInfo();

                AppLogger.Info(
                    "海康 SDK 开始登录设备："
                    + deviceAddress
                    + ":"
                    + port);

                int userId =
                    CHCNetSDK.NET_DVR_Login_V40(
                        ref loginInfo,
                        ref deviceInfo);

                if (userId < 0)
                {
                    // 必须在当前工作线程、紧跟失败调用读取错误码。
                    uint errorCode =
                        SafeGetLastError();

                    result.ErrorCode =
                        errorCode;

                    result.ErrorMessage =
                        GetErrorDescription(
                            errorCode);

                    AppLogger.Warn(
                        "海康 SDK 登录失败："
                        + deviceAddress
                        + ":"
                        + port
                        + "，ErrorCode="
                        + errorCode
                        + "，"
                        + result.ErrorMessage);

                    return result;
                }

                lock (_syncRoot)
                {
                    _loggedInUserIds.Add(
                        userId);
                }

                result.Success =
                    true;

                result.UserId =
                    userId;

                result.ErrorCode =
                    CHCNetSDK.NET_DVR_NOERROR;

                result.ErrorMessage =
                    "登录成功";

                result.DeviceSerialNumber =
                    ByteArrayToString(
                        deviceInfo
                            .struDeviceV30
                            .sSerialNumber);

                result.DeviceType =
                    deviceInfo
                        .struDeviceV30
                        .byDVRType;

                result.AnalogChannelCount =
                    deviceInfo
                        .struDeviceV30
                        .byChanNum;

                // 数字通道数：低8位 + 高8位。
                result.DigitalChannelCount =
                    deviceInfo
                        .struDeviceV30
                        .byIPChanNum
                    +
                    (deviceInfo
                        .struDeviceV30
                        .byHighDChanNum << 8);

                AppLogger.Info(
                    "海康 SDK 登录成功："
                    + deviceAddress
                    + ":"
                    + port
                    + "，UserID="
                    + userId);

                return result;
            }
            catch (DllNotFoundException ex)
            {
                result.ErrorMessage =
                    "海康 SDK DLL 加载失败："
                    + ex.Message;

                AppLogger.Error(
                    "海康 SDK 登录时 DLL 加载失败",
                    ex);

                return result;
            }
            catch (BadImageFormatException ex)
            {
                result.ErrorMessage =
                    "海康 SDK 位数不匹配，请确认项目平台为 x64，并使用 Win64 SDK。";

                AppLogger.Error(
                    result.ErrorMessage,
                    ex);

                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage =
                    "海康 SDK 登录异常："
                    + ex.Message;

                AppLogger.Error(
                    "海康 SDK 登录异常："
                    + deviceAddress,
                    ex);

                return result;
            }
            finally
            {
                ExitSdkOperation();
            }
        }


        /// <summary>
        /// 对已经登录的设备做一次轻量健康检测。
        ///
        /// 不拉视频，只调用 NET_DVR_GetDVRWorkState。
        /// 成功表示当前 SDK 会话仍能和设备正常通信。
        /// </summary>
        public HikvisionHealthResult CheckHealth(
            int userId)
        {
            HikvisionHealthResult result =
                new HikvisionHealthResult();

            if (userId < 0)
            {
                result.ErrorMessage =
                    "无效的 UserID。";

                return result;
            }

            uint enterErrorCode;
            string enterErrorMessage;

            if (!EnterSdkOperation(
                    out enterErrorCode,
                    out enterErrorMessage))
            {
                result.ErrorCode =
                    enterErrorCode;

                result.ErrorMessage =
                    enterErrorMessage;

                return result;
            }

            try
            {
                CHCNetSDK.NET_DVR_WORKSTATE workState =
                    new CHCNetSDK.NET_DVR_WORKSTATE();

                workState.Init();

                bool success =
                    CHCNetSDK.NET_DVR_GetDVRWorkState(
                        userId,
                        ref workState);

                if (!success)
                {
                    uint errorCode =
                        SafeGetLastError();

                    result.ErrorCode =
                        errorCode;

                    result.ErrorMessage =
                        GetErrorDescription(
                            errorCode);

                    return result;
                }

                result.Success =
                    true;

                result.ErrorCode =
                    CHCNetSDK.NET_DVR_NOERROR;

                result.ErrorMessage =
                    "设备通信正常";

                result.DeviceStatic =
                    workState.dwDeviceStatic;

                return result;
            }
            catch (Exception ex)
            {
                result.ErrorMessage =
                    "海康 SDK 健康检测异常："
                    + ex.Message;

                AppLogger.Error(
                    "海康 SDK 健康检测异常，UserID="
                    + userId,
                    ex);

                return result;
            }
            finally
            {
                ExitSdkOperation();
            }
        }



        /// <summary>
        /// 注销一个已经登录的SDK UserID。
        /// </summary>
        public bool Logout(
            int userId)
        {
            if (userId < 0)
            {
                return false;
            }

            uint enterErrorCode;
            string enterErrorMessage;

            if (!EnterSdkOperation(
                    out enterErrorCode,
                    out enterErrorMessage))
            {
                return false;
            }

            try
            {
                bool success =
                    CHCNetSDK.NET_DVR_Logout(
                        userId);

                if (success)
                {
                    lock (_syncRoot)
                    {
                        _loggedInUserIds.Remove(
                            userId);
                    }

                    AppLogger.Info(
                        "海康 SDK 注销成功，UserID="
                        + userId);

                    return true;
                }

                uint errorCode =
                    SafeGetLastError();

                AppLogger.Warn(
                    "海康 SDK 注销失败，UserID="
                    + userId
                    + "，ErrorCode="
                    + errorCode
                    + "，"
                    + GetErrorDescription(
                        errorCode));

                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "海康 SDK 注销异常，UserID="
                    + userId,
                    ex);

                return false;
            }
            finally
            {
                ExitSdkOperation();
            }
        }


        /// <summary>
        /// 注销所有设备并释放SDK。
        /// </summary>
        public void Shutdown()
        {
            lock (_syncRoot)
            {
                if (!_initialized)
                {
                    _shutdownRequested =
                        false;

                    return;
                }

                // 从这一刻起不再允许新的 Login/CheckHealth/Logout 进入。
                _shutdownRequested =
                    true;

                // 等所有已经进入原生SDK的调用自然返回。
                // Monitor.Wait 会暂时释放 _syncRoot，因此完成中的
                // Login 可以登记 UserID，ExitSdkOperation 也可以 Pulse。
                while (_activeSdkOperations > 0)
                {
                    Monitor.Wait(
                        _syncRoot);
                }

                int[] userIds =
                    new int[_loggedInUserIds.Count];

                _loggedInUserIds.CopyTo(
                    userIds);

                for (int i = 0;
                     i < userIds.Length;
                     i++)
                {
                    try
                    {
                        CHCNetSDK.NET_DVR_Logout(
                            userIds[i]);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            "海康 SDK 关闭时注销 UserID="
                            + userIds[i]
                            + " 失败："
                            + ex.Message);
                    }
                }

                _loggedInUserIds.Clear();

                try
                {
                    bool success =
                        CHCNetSDK.NET_DVR_Cleanup();

                    if (success)
                    {
                        AppLogger.Info(
                            "海康 SDK 已释放");
                    }
                    else
                    {
                        uint errorCode =
                            SafeGetLastError();

                        AppLogger.Warn(
                            "NET_DVR_Cleanup 失败，ErrorCode="
                            + errorCode
                            + "，"
                            + GetErrorDescription(
                                errorCode));
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(
                        "NET_DVR_Cleanup 异常",
                        ex);
                }
                finally
                {
                    _initialized =
                        false;

                    _shutdownRequested =
                        false;
                }
            }
        }


        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                // 先阻止 Initialize/新SDK操作进入，再做 Shutdown。
                _disposed =
                    true;
            }

            Shutdown();
        }




        /// <summary>
        /// 登记一次原生SDK调用。
        ///
        /// 只在进入/退出时短暂持锁，不把耗时的网络调用包在锁里，
        /// 因此不同摄像头可以同时 NET_DVR_Login_V40。
        /// </summary>
        private bool EnterSdkOperation(
            out uint errorCode,
            out string errorMessage)
        {
            lock (_syncRoot)
            {
                errorCode =
                    0;

                errorMessage =
                    string.Empty;

                if (_disposed)
                {
                    errorMessage =
                        "HikvisionSdkService 已经释放。";

                    return false;
                }

                if (!_initialized ||
                    _shutdownRequested)
                {
                    errorCode =
                        CHCNetSDK.NET_DVR_NOINIT;

                    errorMessage =
                        _shutdownRequested
                            ? "海康 SDK 正在释放。"
                            : "海康 SDK 尚未初始化。";

                    return false;
                }

                _activeSdkOperations++;

                return true;
            }
        }


        private void ExitSdkOperation()
        {
            lock (_syncRoot)
            {
                if (_activeSdkOperations > 0)
                {
                    _activeSdkOperations--;
                }

                if (_activeSdkOperations == 0)
                {
                    Monitor.PulseAll(
                        _syncRoot);
                }
            }
        }

        /// <summary>
        /// 第一阶段常用错误码中文说明。
        /// </summary>
        public static string GetErrorDescription(
            uint errorCode)
        {
            switch (errorCode)
            {
                case CHCNetSDK.NET_DVR_NOERROR:
                    return "没有错误";

                case CHCNetSDK.NET_DVR_PASSWORD_ERROR:
                    return "用户名或密码错误";

                case CHCNetSDK.NET_DVR_NOENOUGHPRI:
                    return "权限不足";

                case CHCNetSDK.NET_DVR_NOINIT:
                    return "SDK未初始化";

                case CHCNetSDK.NET_DVR_CHANNEL_ERROR:
                    return "通道号错误";

                case CHCNetSDK.NET_DVR_OVER_MAXLINK:
                    return "设备连接数达到上限";

                case CHCNetSDK.NET_DVR_VERSIONNOMATCH:
                    return "SDK与设备版本不匹配";

                case CHCNetSDK.NET_DVR_NETWORK_FAIL_CONNECT:
                    return "连接设备失败或设备离线";

                case CHCNetSDK.NET_DVR_NETWORK_SEND_ERROR:
                    return "向设备发送数据失败";

                case CHCNetSDK.NET_DVR_NETWORK_RECV_ERROR:
                    return "从设备接收数据失败";

                case CHCNetSDK.NET_DVR_NETWORK_RECV_TIMEOUT:
                    return "设备响应超时";

                case CHCNetSDK.NET_DVR_CHAN_EXCEPTION:
                    return "设备通道异常";

                case CHCNetSDK.NET_DVR_USERNOTEXIST:
                    return "用户不存在";

                case CHCNetSDK.NET_DVR_NOENCODEING:
                    return "通道没有编码";

                case CHCNetSDK.NET_DVR_USERID_ISUSING:
                    return "UserID正在被使用";

                case CHCNetSDK.NET_DVR_IPCHAN_NOTALIVE:
                    return "IP通道不在线";

                case CHCNetSDK.NET_DVR_USER_LOCKED:
                    return "用户已被锁定";

                default:
                    return "海康SDK错误";
            }
        }


        private static CHCNetSDK.NET_DVR_USER_LOGIN_INFO
            CreateLoginInfo(
                string deviceAddress,
                ushort port,
                string userName,
                string password)
        {
            CHCNetSDK.NET_DVR_USER_LOGIN_INFO loginInfo =
                new CHCNetSDK.NET_DVR_USER_LOGIN_INFO();

            loginInfo.sDeviceAddress =
                new byte[
                    CHCNetSDK.NET_DVR_DEV_ADDRESS_MAX_LEN];

            loginInfo.sUserName =
                new byte[
                    CHCNetSDK.NET_DVR_LOGIN_USERNAME_MAX_LEN];

            loginInfo.sPassword =
                new byte[
                    CHCNetSDK.NET_DVR_LOGIN_PASSWD_MAX_LEN];

            loginInfo.byRes3 =
                new byte[119];

            CopyStringToBuffer(
                deviceAddress,
                loginInfo.sDeviceAddress);

            CopyStringToBuffer(
                userName,
                loginInfo.sUserName);

            CopyStringToBuffer(
                password,
                loginInfo.sPassword);

            loginInfo.byUseTransport =
                0;

            loginInfo.wPort =
                port;

            // 当前使用同步登录，不需要回调。
            loginInfo.cbLoginResult =
                null;

            loginInfo.pUser =
                IntPtr.Zero;

            loginInfo.bUseAsynLogin =
                false;

            loginInfo.byProxyType =
                0;

            loginInfo.byUseUTCTime =
                0;

            // 0 = Private协议。
            loginInfo.byLoginMode =
                0;

            loginInfo.byHttps =
                0;

            loginInfo.iProxyID =
                0;

            loginInfo.byVerifyMode =
                0;

            return loginInfo;
        }


        private static CHCNetSDK.NET_DVR_DEVICEINFO_V40
            CreateDeviceInfo()
        {
            CHCNetSDK.NET_DVR_DEVICEINFO_V30 deviceInfoV30 =
                new CHCNetSDK.NET_DVR_DEVICEINFO_V30();

            deviceInfoV30.sSerialNumber =
                new byte[
                    CHCNetSDK.SERIALNO_LEN];

            deviceInfoV30.byRes2 =
                new byte[9];

            CHCNetSDK.NET_DVR_DEVICEINFO_V40 deviceInfoV40 =
                new CHCNetSDK.NET_DVR_DEVICEINFO_V40();

            deviceInfoV40.struDeviceV30 =
                deviceInfoV30;

            deviceInfoV40.byRes2 =
                new byte[243];

            return deviceInfoV40;
        }


        private static void CopyStringToBuffer(
            string value,
            byte[] target)
        {
            if (target == null ||
                target.Length == 0)
            {
                return;
            }

            Array.Clear(
                target,
                0,
                target.Length);

            if (string.IsNullOrEmpty(
                value))
            {
                return;
            }

            byte[] source =
                Encoding.Default.GetBytes(
                    value);

            int copyLength =
                Math.Min(
                    source.Length,
                    target.Length - 1);

            Array.Copy(
                source,
                0,
                target,
                0,
                copyLength);

            target[copyLength] =
                0;
        }


        private static string ByteArrayToString(
            byte[] value)
        {
            if (value == null ||
                value.Length == 0)
            {
                return string.Empty;
            }

            int length =
                Array.IndexOf(
                    value,
                    (byte)0);

            if (length < 0)
            {
                length =
                    value.Length;
            }

            if (length == 0)
            {
                return string.Empty;
            }

            return Encoding.Default
                .GetString(
                    value,
                    0,
                    length)
                .Trim();
        }


        private static uint SafeGetLastError()
        {
            try
            {
                return CHCNetSDK.NET_DVR_GetLastError();
            }
            catch
            {
                return 0;
            }
        }


        private void CleanupAfterInitializeFailure()
        {
            if (!_initialized)
            {
                return;
            }

            try
            {
                CHCNetSDK.NET_DVR_Cleanup();
            }
            catch
            {
            }

            _initialized =
                false;
        }
    }
}