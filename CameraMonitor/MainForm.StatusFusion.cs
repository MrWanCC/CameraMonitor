using System;
using System.Drawing;
using System.Windows.Forms;

namespace CameraMonitor
{
    /// <summary>
    /// MainForm 三层状态融合：
    /// Hikvision SDK + ZLMediaKit + VLC。
    /// </summary>
    public partial class MainForm
    {
        /*
         * 这里保存“视频层原始状态”。
         *
         * MainForm.cs 原来的 UpdateSlotState() 每次收到
         * ZLM / VLC 状态后先写入这里，再和 SDK 状态融合。
         *
         * _slotStateKeys 则继续用于记录“最终显示状态”，
         * 防止同一状态反复刷日志。
         */
        private readonly string[] _rawVideoStateKeys =
            new string[6];

        private readonly string[] _rawVideoStateTexts =
            new string[6];

        private readonly bool[] _rawVideoStateWarnings =
            new bool[6];


        private void UpdateFusedSlotState(
            int slot,
            int cameraIndex,
            string videoStateKey,
            string videoText,
            bool videoWarning)
        {
            if (_closing)
            {
                return;
            }

            if (slot < 0 ||
                slot >= 6)
            {
                return;
            }

            if (_slotCameraIndexes[
                    slot] !=
                cameraIndex)
            {
                return;
            }

            _rawVideoStateKeys[
                slot] =
                videoStateKey;

            _rawVideoStateTexts[
                slot] =
                videoText;

            _rawVideoStateWarnings[
                slot] =
                videoWarning;

            RenderFusedSlotState(
                slot,
                cameraIndex);
        }


        /// <summary>
        /// SDK状态变化时重新计算当前可见画面的最终状态。
        /// </summary>
        private void RefreshFusedSlotStateForCamera(
            int cameraIndex)
        {
            SafeUi(
                delegate
                {
                    for (int slot = 0;
                         slot < 6;
                         slot++)
                    {
                        if (_slotCameraIndexes[
                                slot] ==
                            cameraIndex)
                        {
                            RenderFusedSlotState(
                                slot,
                                cameraIndex);
                        }
                    }
                });
        }


        private void RenderFusedSlotState(
            int slot,
            int cameraIndex)
        {
            if (_closing)
            {
                return;
            }

            if (slot < 0 ||
                slot >= 6)
            {
                return;
            }

            if (_slotCameraIndexes[
                    slot] !=
                cameraIndex)
            {
                return;
            }

            Label statusLabel =
                _statusLabels[
                    slot];

            if (statusLabel == null)
            {
                return;
            }

            HikvisionDeviceStatus sdkStatus =
                null;

            HikvisionDeviceManager manager =
                _hikvisionDeviceManager;

            if (manager != null)
            {
                try
                {
                    sdkStatus =
                        manager.GetStatus(
                            cameraIndex);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "读取海康SDK状态失败：" +
                        ex.Message);
                }
            }

            CameraDisplayStatus displayStatus =
                CameraStatusFusion.Fuse(
                    sdkStatus,
                    _rawVideoStateKeys[
                        slot],
                    _rawVideoStateTexts[
                        slot],
                    _rawVideoStateWarnings[
                        slot],
                    GetCameraLogName(
                        cameraIndex));

            if (displayStatus == null)
            {
                return;
            }

            bool stateChanged =
                !string.Equals(
                    _slotStateKeys[
                        slot],
                    displayStatus.StateKey,
                    StringComparison.Ordinal);

            _slotStateKeys[
                slot] =
                displayStatus.StateKey;

            /*
             * 无感切换架构：切组只切换面板可见性，
             * 不再使用过渡遮罩。
             *
             * 状态条策略：
             * - 正常播放（HideLabel=true）：隐藏，画面本身即提示。
             * - 连接中 / 恢复中（非警告）：显示灰色状态条。
             * - 真故障（Warning=true）：显示底部红色故障条。
             */
            if (displayStatus.HideLabel)
            {
                statusLabel.Visible =
                    false;

                statusLabel.Text =
                    string.Empty;
            }
            else
            {
                statusLabel.Text =
                    displayStatus.Text;

                statusLabel.BackColor =
                    displayStatus.Warning
                        ? Color.FromArgb(
                            95,
                            45,
                            45)
                        : Color.FromArgb(
                            55,
                            55,
                            55);

                statusLabel.Visible =
                    true;

                statusLabel.BringToFront();
            }

            if (stateChanged)
            {
                string cameraName =
                    GetCameraLogName(
                        cameraIndex);

                if (displayStatus.Warning)
                {
                    AppLogger.Warn(
                        cameraName +
                        " 最终状态 -> " +
                        displayStatus.StateKey);
                }
                else
                {
                    AppLogger.Info(
                        cameraName +
                        " 最终状态 -> " +
                        displayStatus.StateKey);
                }
            }
        }
    }
}
