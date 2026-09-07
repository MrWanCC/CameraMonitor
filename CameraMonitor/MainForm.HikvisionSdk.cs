using System;

namespace CameraMonitor
{
    /// <summary>
    /// MainForm 的海康SDK设备状态扩展。
    ///
    /// 注意：
    /// 请删除/移除旧的 MainForm.HikvisionSdkTest.cs，
    /// 否则会因为重复 OnShown / OnFormClosed 而编译失败。
    /// </summary>
    public partial class MainForm
    {
        private HikvisionDeviceManager _hikvisionDeviceManager;

        private bool _hikvisionManagerStarted;


        protected override void OnShown(
            EventArgs e)
        {
            /*
             * 先执行MainForm原有Shown流程。
             * base.OnShown(e) 会触发现有 MainForm_Shown：
             * 配置 -> VLC -> ZLM -> 三路视频。
             */
            base.OnShown(e);

            if (_closing ||
                _hikvisionManagerStarted)
            {
                return;
            }

            if (_config == null ||
                _config.Cameras == null ||
                _config.Cameras.Count < 6)
            {
                AppLogger.Warn(
                    "海康设备状态管理未启动：Camera01~Camera06配置不完整。");

                return;
            }

            try
            {
                _hikvisionDeviceManager =
                    new HikvisionDeviceManager(
                        _config.Cameras);

                _hikvisionDeviceManager
                    .StatusChanged +=
                    HikvisionDeviceManager_StatusChanged;

                _hikvisionDeviceManager
                    .Start();

                _hikvisionManagerStarted =
                    true;
            }
            catch (Exception ex)
            {
                /*
                 * SDK状态模块失败不影响视频播放。
                 */
                AppLogger.Error(
                    "启动海康6路设备状态管理失败",
                    ex);
            }
        }


        private void HikvisionDeviceManager_StatusChanged(
            object sender,
            HikvisionDeviceStatusChangedEventArgs e)
        {
            if (e == null ||
                e.Status == null)
            {
                return;
            }

            /*
             * SDK状态变化后，重新计算当前可见槽位的最终状态。
             *
             * 例如：
             * SDK Offline + VLC Playing
             * 最终仍然显示“设备离线”。
             */
            RefreshFusedSlotStateForCamera(
                e.Status.CameraIndex);
        }


        protected override void OnFormClosed(
            System.Windows.Forms.FormClosedEventArgs e)
        {
            try
            {
                if (_hikvisionDeviceManager != null)
                {
                    _hikvisionDeviceManager
                        .StatusChanged -=
                        HikvisionDeviceManager_StatusChanged;

                    _hikvisionDeviceManager
                        .Dispose();

                    _hikvisionDeviceManager =
                        null;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "关闭海康设备状态管理异常",
                    ex);
            }

            /*
             * 再进入 MainForm.cs 已有的 FormClosed 事件，
             * 继续释放 ZLM / VLC / Logger。
             */
            base.OnFormClosed(e);
        }
    }
}