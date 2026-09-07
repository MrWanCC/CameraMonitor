using System;
using System.Collections.Generic;
using System.IO;

namespace CameraMonitor
{
    public class CameraConfig
    {
        public string Name { get; set; }

        public string StreamId { get; set; }

        // 摄像头原始 RTSP 地址
        public string SourceUrl { get; set; }
    }


    public class AppConfig
    {
        // ZLMediaKit
        public string ZlmHost { get; set; }

        public int ZlmRtspPort { get; set; }

        public int ZlmHttpPort { get; set; }

        public string ZlmSecret { get; set; }


        // 副屏
        public int DisplayLeft { get; set; }

        public int DisplayTop { get; set; }

        public int VideoHeight { get; set; }


        // 6个摄像头
        public List<CameraConfig> Cameras { get; set; }


        public AppConfig()
        {
            Cameras =
                new List<CameraConfig>();
        }


        /// <summary>
        /// 根据配置拼 ZLM RTSP 播放地址
        /// </summary>
        public string GetPlayUrl(
            CameraConfig camera)
        {
            return string.Format(
                "rtsp://{0}:{1}/live/{2}",
                ZlmHost,
                ZlmRtspPort,
                camera.StreamId);
        }
    }


    public static class ConfigManager
    {
        /// <summary>
        /// 读取整个 config.ini
        /// </summary>
        public static AppConfig Load(
            string configPath)
        {
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException(
                    "找不到配置文件：" +
                    configPath);
            }


            Dictionary<string,
                Dictionary<string, string>>
                ini =
                    ParseIni(configPath);


            AppConfig config =
                new AppConfig();


            // ==========================================
            // ZLMediaKit
            // ==========================================

            config.ZlmHost =
                GetString(
                    ini,
                    "ZLMediaKit",
                    "Host",
                    "127.0.0.1");


            config.ZlmRtspPort =
                GetInt(
                    ini,
                    "ZLMediaKit",
                    "RtspPort",
                    554);


            config.ZlmHttpPort =
                GetInt(
                    ini,
                    "ZLMediaKit",
                    "HttpPort",
                    80);


            config.ZlmSecret =
                GetString(
                    ini,
                    "ZLMediaKit",
                    "Secret",
                    "");


            // ==========================================
            // Display
            // ==========================================

            config.DisplayLeft =
                GetInt(
                    ini,
                    "Display",
                    "Left",
                    0);


            config.DisplayTop =
                GetInt(
                    ini,
                    "Display",
                    "Top",
                    1080);


            config.VideoHeight =
                GetInt(
                    ini,
                    "Display",
                    "VideoHeight",
                    540);


            // ==========================================
            // Camera01 ~ Camera06
            // ==========================================

            for (int i = 1;
                 i <= 6;
                 i++)
            {
                string section =
                    "Camera" +
                    i.ToString("00");


                CameraConfig camera =
                    new CameraConfig();


                camera.Name =
                    GetString(
                        ini,
                        section,
                        "Name",
                        "Camera " +
                        i.ToString("00"));


                camera.StreamId =
                    GetString(
                        ini,
                        section,
                        "StreamId",
                        "camera" +
                        i.ToString("000"));


                camera.SourceUrl =
                    GetString(
                        ini,
                        section,
                        "SourceUrl",
                        "");


                config.Cameras.Add(
                    camera);
            }


            return config;
        }


        /// <summary>
        /// 自己解析 INI 文件
        /// </summary>
        private static Dictionary<string,
            Dictionary<string, string>>
            ParseIni(
                string path)
        {
            Dictionary<string,
                Dictionary<string, string>>
                result =
                    new Dictionary<string,
                        Dictionary<string, string>>(
                            StringComparer.OrdinalIgnoreCase);


            string currentSection =
                "";


            string[] lines =
                File.ReadAllLines(path);


            foreach (string rawLine in lines)
            {
                if (rawLine == null)
                {
                    continue;
                }


                string line =
                    rawLine.Trim();


                // 空行
                if (line.Length == 0)
                {
                    continue;
                }


                // 注释
                if (line.StartsWith(";") ||
                    line.StartsWith("#"))
                {
                    continue;
                }


                // Section
                if (line.StartsWith("[") &&
                    line.EndsWith("]"))
                {
                    currentSection =
                        line.Substring(
                            1,
                            line.Length - 2)
                            .Trim();


                    if (!result.ContainsKey(
                        currentSection))
                    {
                        result[
                            currentSection] =
                            new Dictionary<string,
                                string>(
                                    StringComparer
                                        .OrdinalIgnoreCase);
                    }


                    continue;
                }


                // Key=Value
                int equalsIndex =
                    line.IndexOf('=');


                if (equalsIndex <= 0)
                {
                    continue;
                }


                if (string.IsNullOrEmpty(
                    currentSection))
                {
                    continue;
                }


                string key =
                    line.Substring(
                        0,
                        equalsIndex)
                        .Trim();


                string value =
                    line.Substring(
                        equalsIndex + 1)
                        .Trim();


                result[
                    currentSection][key] =
                    value;
            }


            return result;
        }


        /// <summary>
        /// 读取字符串
        /// </summary>
        private static string GetString(
            Dictionary<string,
                Dictionary<string, string>> ini,
            string section,
            string key,
            string defaultValue)
        {
            Dictionary<string, string>
                sectionData;


            if (!ini.TryGetValue(
                section,
                out sectionData))
            {
                return defaultValue;
            }


            string value;


            if (!sectionData.TryGetValue(
                key,
                out value))
            {
                return defaultValue;
            }


            if (string.IsNullOrWhiteSpace(
                value))
            {
                return defaultValue;
            }


            return value.Trim();
        }


        /// <summary>
        /// 读取整数
        /// </summary>
        private static int GetInt(
            Dictionary<string,
                Dictionary<string, string>> ini,
            string section,
            string key,
            int defaultValue)
        {
            string value =
                GetString(
                    ini,
                    section,
                    key,
                    defaultValue.ToString());


            int result;


            if (int.TryParse(
                value,
                out result))
            {
                return result;
            }


            return defaultValue;
        }
    }
}