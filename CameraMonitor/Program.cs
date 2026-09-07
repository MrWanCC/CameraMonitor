using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CameraMonitor
{
    static class Program
    {
        /*
         * 单实例互斥锁：
         * 本程序独占管理ZLM代理（创建/删除/重建），
         * 双开会形成两个实例互相删除对方代理、
         * 重复登录海康设备，画面直接紊乱。
         * 因此第二个实例启动即退出。
         */
        private static Mutex _singleInstanceMutex;

        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        [STAThread]
        static void Main()
        {
            bool createdNew;

            _singleInstanceMutex =
                new Mutex(
                    true,
                    "Local\\CameraMonitor.SingleInstance",
                    out createdNew);

            if (!createdNew)
            {
                _singleInstanceMutex =
                    null;

                MessageBox.Show(
                    "CameraMonitor 已在运行中。\r\n" +
                    "请勿重复启动（本程序独占管理ZLM代理）。",
                    "CameraMonitor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());

            /*
             * 正常退出时释放互斥锁，
             * 避免异常退出后系统等待回收导致再启动被误判。
             */
            try
            {
                if (_singleInstanceMutex != null)
                {
                    _singleInstanceMutex.ReleaseMutex();

                    _singleInstanceMutex.Dispose();

                    _singleInstanceMutex =
                        null;
                }
            }
            catch
            {
                /* 释放失败不影响退出 */
            }
        }
    }
}
