using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace CameraMonitor
{
    // =============================================================
    // ZLM HTTP API JSON 解析工具
    //
    // 使用 .NET Framework 内置 JavaScriptSerializer
    // （System.Web.Extensions，4.5.2 自带），
    // 不引入第三方 NuGet 依赖。
    //
    // ZLM 响应结构：
    // {
    //   "code": 0,
    //   "msg": "success",
    //   "data": { ... }
    // }
    // =============================================================

    internal static class ZlmJson
    {
        /*
         * JavaScriptSerializer 默认 MaxJsonLength 只有 2MB。
         *
         * getProxyInfo 的 tracks 数据偶尔较大，
         * 放宽到 16MB。
         */
        private const int MaxJsonLength =
            16 * 1024 * 1024;

        /*
         * JavaScriptSerializer 未承诺线程安全。
         *
         * 解析调用频率低（健康检测 5 秒 6 路），
         * 用锁串行化即可。
         */
        private static readonly object SyncRoot =
            new object();

        private static readonly JavaScriptSerializer Serializer =
            CreateSerializer();


        private static JavaScriptSerializer CreateSerializer()
        {
            JavaScriptSerializer serializer =
                new JavaScriptSerializer();

            serializer.MaxJsonLength =
                MaxJsonLength;

            return serializer;
        }


        // =========================================================
        // 反序列化
        // =========================================================

        public static bool TryParse(
            string json,
            out Dictionary<string, object> root)
        {
            root =
                null;

            if (string.IsNullOrWhiteSpace(
                json))
            {
                return false;
            }

            try
            {
                lock (SyncRoot)
                {
                    root =
                        Serializer.Deserialize<
                            Dictionary<string, object>>(
                            json);
                }

                return root != null;
            }
            catch
            {
                root =
                    null;

                return false;
            }
        }


        // =========================================================
        // 取嵌套对象
        // =========================================================

        public static Dictionary<string, object> GetObject(
            Dictionary<string, object> node,
            string key)
        {
            object value;

            if (node != null &&
                node.TryGetValue(
                    key,
                    out value))
            {
                return value as
                    Dictionary<string, object>;
            }

            return null;
        }


        // =========================================================
        // 取数组
        // =========================================================

        public static object[] GetArray(
            Dictionary<string, object> node,
            string key)
        {
            object value;

            if (node == null ||
                !node.TryGetValue(
                    key,
                    out value) ||
                value == null)
            {
                return null;
            }

            /*
             * JavaScriptSerializer 反序列化 JSON 数组时，
             * 实际返回的是 ArrayList，不是 object[]。
             *
             * 这里同时兼容 object[] / ArrayList / IList 三种形态。
             */
            object[] objectArray =
                value as object[];

            if (objectArray != null)
            {
                return objectArray;
            }

            ArrayList arrayList =
                value as ArrayList;

            if (arrayList != null)
            {
                return arrayList.ToArray();
            }

            IList list =
                value as IList;

            if (list != null)
            {
                object[] result =
                    new object[list.Count];

                list.CopyTo(
                    result,
                    0);

                return result;
            }

            return null;
        }


        // =========================================================
        // 取字符串
        // =========================================================

        public static string GetString(
            Dictionary<string, object> node,
            string key)
        {
            object value;

            if (node != null &&
                node.TryGetValue(
                    key,
                    out value) &&
                value != null)
            {
                return Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture);
            }

            return null;
        }


        // =========================================================
        // 取整数（支持多个候选key）
        //
        // ZLM 不同版本字段命名不一致：
        //   re_pull_count / rePullCount
        //   live_secs / liveSecs
        //
        // 依次尝试每个候选key，取到即返回。
        // =========================================================

        public static int GetInt(
            Dictionary<string, object> node,
            int defaultValue,
            params string[] keys)
        {
            if (node == null ||
                keys == null)
            {
                return defaultValue;
            }

            foreach (string key in keys)
            {
                object value;

                if (node.TryGetValue(
                        key,
                        out value))
                {
                    try
                    {
                        return Convert.ToInt32(
                            value,
                            CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        // 类型不匹配时尝试下一个候选key
                    }
                }
            }

            return defaultValue;
        }


        public static int GetInt(
            Dictionary<string, object> node,
            string key,
            int defaultValue)
        {
            return GetInt(
                node,
                defaultValue,
                key);
        }


        // =========================================================
        // 取64位整数
        // =========================================================

        public static long GetLong(
            Dictionary<string, object> node,
            string key,
            long defaultValue)
        {
            object value;

            if (node != null &&
                node.TryGetValue(
                    key,
                    out value))
            {
                try
                {
                    return Convert.ToInt64(
                        value,
                        CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }

            return defaultValue;
        }
    }
}
