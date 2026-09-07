using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CameraMonitor
{
    public sealed class CameraEditorItem
    {
        public int Index { get; set; }
        public string SectionName { get; set; }
        public string Name { get; set; }
        public string StreamId { get; set; }
        public string IpAddress { get; set; }
        public int RtspPort { get; set; }
        public int Channel { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }

        public CameraEditorItem()
        {
            SectionName = string.Empty;
            Name = string.Empty;
            StreamId = string.Empty;
            IpAddress = string.Empty;
            RtspPort = 554;
            Channel = 101;
            UserName = string.Empty;
            Password = string.Empty;
        }
    }


    public sealed class ZlmEditorSettings
    {
        public string Host { get; set; }
        public int HttpPort { get; set; }
        public int RtspPort { get; set; }
        public string Secret { get; set; }

        public ZlmEditorSettings()
        {
            Host = string.Empty;
            HttpPort = 80;
            RtspPort = 554;
            Secret = string.Empty;
        }
    }


    public sealed class AdvancedSettingsModel
    {
        public string ConfigPath { get; set; }
        public List<CameraEditorItem> Cameras { get; private set; }
        public ZlmEditorSettings Zlm { get; set; }

        public AdvancedSettingsModel()
        {
            ConfigPath = string.Empty;
            Cameras = new List<CameraEditorItem>();
            Zlm = new ZlmEditorSettings();
        }
    }


    public sealed class ConnectionTestResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }

        public ConnectionTestResult()
        {
            Success = false;
            Message = string.Empty;
        }

        public static ConnectionTestResult Ok(string message)
        {
            ConnectionTestResult result =
                new ConnectionTestResult();

            result.Success = true;
            result.Message = message ?? string.Empty;

            return result;
        }

        public static ConnectionTestResult Fail(string message)
        {
            ConnectionTestResult result =
                new ConnectionTestResult();

            result.Success = false;
            result.Message = message ?? string.Empty;

            return result;
        }
    }


    /// <summary>
    /// 高级设置使用的 config.ini 读写器。
    ///
    /// 设计目标：
    /// 1. 客户不需要手工编辑 config.ini；
    /// 2. 保留 Display 等未知 section / key；
    /// 3. 摄像头密码自动进行 RTSP URL 编码；
    /// 4. 仍然使用现有 config.ini 作为程序唯一配置源。
    /// </summary>
    public static class CameraConfigEditor
    {
        private const int DefaultRtspPort = 554;
        private const int DefaultZlmHttpPort = 80;
        private const int DefaultChannel = 101;
        private const ushort HikvisionSdkPort = 8000;
        private const int HttpTimeoutMs = 6000;


        public static AdvancedSettingsModel Load(
            string configPath)
        {
            if (string.IsNullOrWhiteSpace(
                configPath))
            {
                throw new ArgumentException(
                    "configPath不能为空。",
                    "configPath");
            }

            IniDocument ini =
                IniDocument.Load(
                    configPath);

            AdvancedSettingsModel model =
                new AdvancedSettingsModel();

            model.ConfigPath =
                configPath;

            model.Zlm.Host =
                ini.GetValue(
                    "ZLMediaKit",
                    "Host",
                    string.Empty);

            model.Zlm.HttpPort =
                ParseInt(
                    ini.GetValue(
                        "ZLMediaKit",
                        "HttpPort",
                        DefaultZlmHttpPort.ToString()),
                    DefaultZlmHttpPort);

            model.Zlm.RtspPort =
                ParseInt(
                    ini.GetValue(
                        "ZLMediaKit",
                        "RtspPort",
                        DefaultRtspPort.ToString()),
                    DefaultRtspPort);

            model.Zlm.Secret =
                ini.GetValue(
                    "ZLMediaKit",
                    "Secret",
                    string.Empty);

            for (int i = 0;
                 i < 6;
                 i++)
            {
                string sectionName =
                    "Camera" +
                    (i + 1).ToString("00");

                CameraEditorItem camera =
                    new CameraEditorItem();

                camera.Index = i;
                camera.SectionName = sectionName;
                camera.Name =
                    ini.GetValue(
                        sectionName,
                        "Name",
                        "Camera " +
                        (i + 1).ToString("00"));

                camera.StreamId =
                    ini.GetValue(
                        sectionName,
                        "StreamId",
                        "camera" +
                        (i + 1).ToString("000"));

                string sourceUrl =
                    ini.GetValue(
                        sectionName,
                        "SourceUrl",
                        string.Empty);

                ParseSourceUrl(
                    sourceUrl,
                    camera);

                model.Cameras.Add(
                    camera);
            }

            return model;
        }


        public static void Save(
            AdvancedSettingsModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(
                    "model");
            }

            string validationError =
                ValidateModel(
                    model);

            if (!string.IsNullOrEmpty(
                validationError))
            {
                throw new InvalidOperationException(
                    validationError);
            }

            IniDocument ini =
                IniDocument.Load(
                    model.ConfigPath);

            ini.SetValue(
                "ZLMediaKit",
                "Host",
                model.Zlm.Host.Trim());

            ini.SetValue(
                "ZLMediaKit",
                "HttpPort",
                model.Zlm.HttpPort.ToString());

            ini.SetValue(
                "ZLMediaKit",
                "RtspPort",
                model.Zlm.RtspPort.ToString());

            ini.SetValue(
                "ZLMediaKit",
                "Secret",
                model.Zlm.Secret ?? string.Empty);

            for (int i = 0;
                 i < model.Cameras.Count;
                 i++)
            {
                CameraEditorItem camera =
                    model.Cameras[i];

                string sectionName =
                    string.IsNullOrWhiteSpace(
                        camera.SectionName)
                        ? "Camera" +
                          (i + 1).ToString("00")
                        : camera.SectionName;

                ini.SetValue(
                    sectionName,
                    "Name",
                    camera.Name.Trim());

                ini.SetValue(
                    sectionName,
                    "StreamId",
                    camera.StreamId.Trim());

                ini.SetValue(
                    sectionName,
                    "SourceUrl",
                    BuildSourceUrl(
                        camera));
            }

            ini.Save(
                model.ConfigPath);
        }


        public static string ValidateModel(
            AdvancedSettingsModel model)
        {
            if (model == null)
            {
                return "配置为空。";
            }

            if (model.Cameras == null ||
                model.Cameras.Count < 6)
            {
                return "当前版本必须配置 Camera01 ~ Camera06 六路摄像头。";
            }

            string zlmError =
                ValidateZlm(
                    model.Zlm);

            if (!string.IsNullOrEmpty(
                zlmError))
            {
                return zlmError;
            }

            for (int i = 0;
                 i < 6;
                 i++)
            {
                string cameraError =
                    ValidateCamera(
                        model.Cameras[i]);

                if (!string.IsNullOrEmpty(
                    cameraError))
                {
                    return "Camera " +
                           (i + 1).ToString("00") +
                           "：" +
                           cameraError;
                }
            }

            return string.Empty;
        }


        public static string ValidateCamera(
            CameraEditorItem camera)
        {
            if (camera == null)
            {
                return "配置为空。";
            }

            if (string.IsNullOrWhiteSpace(
                camera.Name))
            {
                return "名称不能为空。";
            }

            IPAddress parsedAddress;

            if (!IPAddress.TryParse(
                    camera.IpAddress,
                    out parsedAddress))
            {
                return "IP地址格式不正确。";
            }

            if (camera.RtspPort <= 0 ||
                camera.RtspPort > 65535)
            {
                return "RTSP端口必须在 1 ~ 65535 之间。";
            }

            if (camera.Channel <= 0)
            {
                return "通道必须大于0。";
            }

            if (string.IsNullOrWhiteSpace(
                camera.UserName))
            {
                return "账号不能为空。";
            }

            if (string.IsNullOrEmpty(
                camera.Password))
            {
                return "密码不能为空。";
            }

            if (string.IsNullOrWhiteSpace(
                camera.StreamId))
            {
                return "StreamId不能为空。";
            }

            if (!Regex.IsMatch(
                    camera.StreamId,
                    "^[A-Za-z0-9_-]+$"))
            {
                return "StreamId只能包含字母、数字、下划线和短横线。";
            }

            return string.Empty;
        }


        public static string ValidateZlm(
            ZlmEditorSettings zlm)
        {
            if (zlm == null)
            {
                return "ZLM配置为空。";
            }

            if (string.IsNullOrWhiteSpace(
                zlm.Host))
            {
                return "ZLM地址不能为空。";
            }

            if (zlm.HttpPort <= 0 ||
                zlm.HttpPort > 65535)
            {
                return "ZLM HTTP端口必须在 1 ~ 65535 之间。";
            }

            if (zlm.RtspPort <= 0 ||
                zlm.RtspPort > 65535)
            {
                return "ZLM RTSP端口必须在 1 ~ 65535 之间。";
            }

            if (string.IsNullOrWhiteSpace(
                zlm.Secret))
            {
                return "ZLM Secret不能为空。";
            }

            return string.Empty;
        }


        public static string BuildSourceUrl(
            CameraEditorItem camera)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(
                    "camera");
            }

            string userName =
                Uri.EscapeDataString(
                    camera.UserName ??
                    string.Empty);

            string password =
                Uri.EscapeDataString(
                    camera.Password ??
                    string.Empty);

            return
                "rtsp://" +
                userName +
                ":" +
                password +
                "@" +
                camera.IpAddress.Trim() +
                ":" +
                camera.RtspPort +
                "/Streaming/Channels/" +
                camera.Channel;
        }


        public static ConnectionTestResult TestZlm(
            ZlmEditorSettings zlm)
        {
            string validationError =
                ValidateZlm(
                    zlm);

            if (!string.IsNullOrEmpty(
                validationError))
            {
                return ConnectionTestResult.Fail(
                    validationError);
            }

            try
            {
                string response =
                    HttpGet(
                        BuildZlmApiUrl(
                            zlm,
                            "getServerConfig",
                            new Dictionary<string, string>()));

                int code =
                    ExtractCode(
                        response);

                if (code == 0)
                {
                    return ConnectionTestResult.Ok(
                        "ZLMediaKit 连接成功。\r\nHTTP API 正常。\r\nRTSP端口配置：" +
                        zlm.RtspPort);
                }

                return ConnectionTestResult.Fail(
                    "ZLMediaKit 返回异常：" +
                    ExtractMessage(
                        response));
            }
            catch (Exception ex)
            {
                return ConnectionTestResult.Fail(
                    "ZLMediaKit 连接失败：" +
                    ex.Message);
            }
        }


        public static ConnectionTestResult TestCamera(
            CameraEditorItem camera,
            ZlmEditorSettings zlm)
        {
            string cameraError =
                ValidateCamera(
                    camera);

            if (!string.IsNullOrEmpty(
                cameraError))
            {
                return ConnectionTestResult.Fail(
                    cameraError);
            }

            ConnectionTestResult sdkPortResult =
                TestSdkPortReachability(
                    camera);

            if (!sdkPortResult.Success)
            {
                return sdkPortResult;
            }

            ConnectionTestResult rtspResult =
                TestRtspThroughZlm(
                    camera,
                    zlm);

            if (!rtspResult.Success)
            {
                return ConnectionTestResult.Fail(
                    "设备 SDK 端口 8000 可达。\r\n" +
                    rtspResult.Message);
            }

            return ConnectionTestResult.Ok(
                "设备 SDK 端口 8000 可达。\r\n" +
                "RTSP视频认证成功。\r\n" +
                "Camera可正常加入ZLMediaKit。" );
        }


        private static ConnectionTestResult TestSdkPortReachability(
            CameraEditorItem camera)
        {
            TcpClient client =
                new TcpClient();

            try
            {
                IAsyncResult asyncResult =
                    client.BeginConnect(
                        camera.IpAddress.Trim(),
                        HikvisionSdkPort,
                        null,
                        null);

                bool connected =
                    asyncResult.AsyncWaitHandle.WaitOne(
                        2500);

                if (!connected)
                {
                    return ConnectionTestResult.Fail(
                        "设备 SDK 端口 8000 连接超时。" );
                }

                client.EndConnect(
                    asyncResult);

                if (!client.Connected)
                {
                    return ConnectionTestResult.Fail(
                        "设备 SDK 端口 8000 无法连接。" );
                }

                return ConnectionTestResult.Ok(
                    "设备 SDK 端口 8000 可达。" );
            }
            catch (Exception ex)
            {
                return ConnectionTestResult.Fail(
                    "设备 SDK 端口 8000 无法连接：" +
                    ex.Message);
            }
            finally
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }
        }


        private static ConnectionTestResult TestRtspThroughZlm(
            CameraEditorItem camera,
            ZlmEditorSettings zlm)
        {
            string zlmError =
                ValidateZlm(
                    zlm);

            if (!string.IsNullOrEmpty(
                zlmError))
            {
                return ConnectionTestResult.Fail(
                    "RTSP测试需要有效的ZLM设置：" +
                    zlmError);
            }

            string temporaryStreamId =
                "__camera_test_" +
                Guid.NewGuid().ToString("N");

            string sourceUrl =
                BuildSourceUrl(
                    camera);

            try
            {
                Dictionary<string, string> parameters =
                    new Dictionary<string, string>();

                parameters["vhost"] = "__defaultVhost__";
                parameters["app"] = "live";
                parameters["stream"] = temporaryStreamId;
                parameters["url"] = sourceUrl;
                parameters["enable_rtsp"] = "1";
                parameters["enable_rtmp"] = "0";
                parameters["enable_hls"] = "0";
                parameters["enable_hls_fmp4"] = "0";
                parameters["enable_ts"] = "0";
                parameters["enable_fmp4"] = "0";
                parameters["enable_mp4"] = "0";
                parameters["rtp_type"] = "0";
                parameters["retry_count"] = "0";

                string response =
                    HttpGet(
                        BuildZlmApiUrl(
                            zlm,
                            "addStreamProxy",
                            parameters));

                if (Contains401(
                        response))
                {
                    return ConnectionTestResult.Fail(
                        "RTSP视频认证失败（401 Unauthorized）。");
                }

                int code =
                    ExtractCode(
                        response);

                if (code != 0)
                {
                    string message =
                        ExtractMessage(
                            response);

                    if (string.IsNullOrWhiteSpace(
                        message))
                    {
                        message = response;
                    }

                    return ConnectionTestResult.Fail(
                        "RTSP拉流失败：" +
                        message);
                }

                return ConnectionTestResult.Ok(
                    "RTSP测试成功。" );
            }
            catch (Exception ex)
            {
                return ConnectionTestResult.Fail(
                    "RTSP测试失败：" +
                    ex.Message);
            }
            finally
            {
                try
                {
                    Dictionary<string, string> deleteParameters =
                        new Dictionary<string, string>();

                    deleteParameters["key"] =
                        "__defaultVhost__/live/" +
                        temporaryStreamId;

                    HttpGet(
                        BuildZlmApiUrl(
                            zlm,
                            "delStreamProxy",
                            deleteParameters));
                }
                catch
                {
                    // 临时代理不存在或已自动释放时忽略。
                }
            }
        }


        private static void ParseSourceUrl(
            string sourceUrl,
            CameraEditorItem camera)
        {
            camera.RtspPort =
                DefaultRtspPort;

            camera.Channel =
                DefaultChannel;

            if (string.IsNullOrWhiteSpace(
                sourceUrl))
            {
                return;
            }

            Uri uri;

            if (!Uri.TryCreate(
                    sourceUrl,
                    UriKind.Absolute,
                    out uri))
            {
                return;
            }

            camera.IpAddress =
                uri.Host;

            camera.RtspPort =
                uri.IsDefaultPort
                    ? DefaultRtspPort
                    : uri.Port;

            ParseUserInfo(
                uri.UserInfo,
                camera);

            Match channelMatch =
                Regex.Match(
                    uri.AbsolutePath,
                    "/Streaming/Channels/(\\d+)",
                    RegexOptions.IgnoreCase);

            if (channelMatch.Success)
            {
                camera.Channel =
                    ParseInt(
                        channelMatch.Groups[1].Value,
                        DefaultChannel);
            }
        }


        private static void ParseUserInfo(
            string userInfo,
            CameraEditorItem camera)
        {
            if (string.IsNullOrEmpty(
                userInfo))
            {
                return;
            }

            int separatorIndex =
                userInfo.IndexOf(':');

            if (separatorIndex < 0)
            {
                camera.UserName =
                    Uri.UnescapeDataString(
                        userInfo);

                return;
            }

            camera.UserName =
                Uri.UnescapeDataString(
                    userInfo.Substring(
                        0,
                        separatorIndex));

            camera.Password =
                Uri.UnescapeDataString(
                    userInfo.Substring(
                        separatorIndex + 1));
        }


        private static string BuildZlmApiUrl(
            ZlmEditorSettings zlm,
            string apiName,
            IDictionary<string, string> parameters)
        {
            StringBuilder builder =
                new StringBuilder();

            builder.Append("http://");
            builder.Append(zlm.Host.Trim());
            builder.Append(":");
            builder.Append(zlm.HttpPort);
            builder.Append("/index/api/");
            builder.Append(apiName);

            builder.Append("?secret=");
            builder.Append(
                Uri.EscapeDataString(
                    zlm.Secret ??
                    string.Empty));

            if (parameters != null)
            {
                foreach (KeyValuePair<string, string> pair
                         in parameters)
                {
                    builder.Append("&");
                    builder.Append(
                        Uri.EscapeDataString(
                            pair.Key));
                    builder.Append("=");
                    builder.Append(
                        Uri.EscapeDataString(
                            pair.Value ??
                            string.Empty));
                }
            }

            return builder.ToString();
        }


        private static string HttpGet(
            string url)
        {
            HttpWebRequest request =
                (HttpWebRequest)
                WebRequest.Create(
                    url);

            request.Method = "GET";
            request.Timeout = HttpTimeoutMs;
            request.ReadWriteTimeout = HttpTimeoutMs;
            request.KeepAlive = false;

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


        private static bool Contains401(
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
            if (string.IsNullOrWhiteSpace(
                response))
            {
                return int.MinValue;
            }

            Match match =
                Regex.Match(
                    response,
                    "\\\"code\\\"\\s*:\\s*(-?\\d+)",
                    RegexOptions.IgnoreCase);

            int code;

            if (match.Success &&
                int.TryParse(
                    match.Groups[1].Value,
                    out code))
            {
                return code;
            }

            return int.MinValue;
        }


        private static string ExtractMessage(
            string response)
        {
            if (string.IsNullOrWhiteSpace(
                response))
            {
                return string.Empty;
            }

            Match match =
                Regex.Match(
                    response,
                    "\\\"msg\\\"\\s*:\\s*\\\"([^\\\"]*)\\\"",
                    RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                return string.Empty;
            }

            return match.Groups[1].Value;
        }


        private static int ParseInt(
            string value,
            int defaultValue)
        {
            int parsed;

            if (int.TryParse(
                    value,
                    out parsed))
            {
                return parsed;
            }

            return defaultValue;
        }


        private sealed class IniDocument
        {
            private readonly List<IniSection>
                _sections =
                    new List<IniSection>();

            private readonly Dictionary<string, IniSection>
                _sectionMap =
                    new Dictionary<string, IniSection>(
                        StringComparer.OrdinalIgnoreCase);


            public static IniDocument Load(
                string path)
            {
                IniDocument document =
                    new IniDocument();

                if (!File.Exists(
                    path))
                {
                    return document;
                }

                string[] lines =
                    File.ReadAllLines(
                        path,
                        Encoding.UTF8);

                IniSection currentSection =
                    null;

                for (int i = 0;
                     i < lines.Length;
                     i++)
                {
                    string line =
                        lines[i];

                    if (line == null)
                    {
                        continue;
                    }

                    string trimmed =
                        line.Trim();

                    if (trimmed.Length == 0 ||
                        trimmed.StartsWith(";") ||
                        trimmed.StartsWith("#"))
                    {
                        continue;
                    }

                    if (trimmed.StartsWith("[") &&
                        trimmed.EndsWith("]") &&
                        trimmed.Length > 2)
                    {
                        string sectionName =
                            trimmed.Substring(
                                1,
                                trimmed.Length - 2)
                                .Trim();

                        currentSection =
                            document.GetOrCreateSection(
                                sectionName);

                        continue;
                    }

                    if (currentSection == null)
                    {
                        continue;
                    }

                    int separatorIndex =
                        line.IndexOf('=');

                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    string key =
                        line.Substring(
                            0,
                            separatorIndex)
                            .Trim();

                    string value =
                        line.Substring(
                            separatorIndex + 1)
                            .Trim();

                    currentSection.SetValue(
                        key,
                        value);
                }

                return document;
            }


            public string GetValue(
                string sectionName,
                string key,
                string defaultValue)
            {
                IniSection section;

                if (!_sectionMap.TryGetValue(
                        sectionName,
                        out section))
                {
                    return defaultValue;
                }

                return section.GetValue(
                    key,
                    defaultValue);
            }


            public void SetValue(
                string sectionName,
                string key,
                string value)
            {
                IniSection section =
                    GetOrCreateSection(
                        sectionName);

                section.SetValue(
                    key,
                    value ??
                    string.Empty);
            }


            public void Save(
                string path)
            {
                string directory =
                    Path.GetDirectoryName(
                        path);

                if (!string.IsNullOrEmpty(
                    directory) &&
                    !Directory.Exists(
                        directory))
                {
                    Directory.CreateDirectory(
                        directory);
                }

                StringBuilder builder =
                    new StringBuilder();

                for (int i = 0;
                     i < _sections.Count;
                     i++)
                {
                    IniSection section =
                        _sections[i];

                    builder.Append("[");
                    builder.Append(section.Name);
                    builder.AppendLine("]");

                    for (int k = 0;
                         k < section.KeyOrder.Count;
                         k++)
                    {
                        string key =
                            section.KeyOrder[k];

                        builder.Append(key);
                        builder.Append("=");
                        builder.AppendLine(
                            section.Values[key]);
                    }

                    if (i < _sections.Count - 1)
                    {
                        builder.AppendLine();
                    }
                }

                File.WriteAllText(
                    path,
                    builder.ToString(),
                    new UTF8Encoding(
                        false));
            }


            private IniSection GetOrCreateSection(
                string sectionName)
            {
                IniSection section;

                if (_sectionMap.TryGetValue(
                        sectionName,
                        out section))
                {
                    return section;
                }

                section =
                    new IniSection(
                        sectionName);

                _sections.Add(
                    section);

                _sectionMap[
                    sectionName] =
                    section;

                return section;
            }
        }


        private sealed class IniSection
        {
            public string Name { get; private set; }

            public List<string> KeyOrder { get; private set; }

            public Dictionary<string, string> Values { get; private set; }


            public IniSection(
                string name)
            {
                Name = name;

                KeyOrder =
                    new List<string>();

                Values =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
            }


            public string GetValue(
                string key,
                string defaultValue)
            {
                string value;

                if (Values.TryGetValue(
                        key,
                        out value))
                {
                    return value;
                }

                return defaultValue;
            }


            public void SetValue(
                string key,
                string value)
            {
                string existingKey =
                    FindExistingKey(
                        key);

                if (existingKey == null)
                {
                    KeyOrder.Add(
                        key);

                    Values[
                        key] =
                        value;
                }
                else
                {
                    Values[
                        existingKey] =
                        value;
                }
            }


            private string FindExistingKey(
                string key)
            {
                for (int i = 0;
                     i < KeyOrder.Count;
                     i++)
                {
                    if (string.Equals(
                            KeyOrder[i],
                            key,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return KeyOrder[i];
                    }
                }

                return null;
            }
        }
    }
}
