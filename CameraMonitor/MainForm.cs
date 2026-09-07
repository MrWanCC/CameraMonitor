using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;

namespace CameraMonitor
{
    public partial class MainForm : Form
    {
        // =========================================================
        // 常量
        // =========================================================

        private const int MonitorIntervalMs = 5000;

        // 网络缓存
        // 300ms：抖动容错（比最早的200ms厚50%）与基础延迟之间的折中。
        // 500ms版本实测延迟感知明显（09-02），200ms在抖动期扛不住。
        private const int NetworkCachingMs = 300;

        // 连续异常3次才强制重建ZLM代理
        private const int BadStreamThreshold = 3;

        // 视频帧连续3轮不增长，认为流可能卡住
        private const int StreamStallThreshold = 3;

        // ZLM代理至少30秒才允许强制重建一次
        private const int ProxyRecreateCooldownSeconds = 30;

        // VLC播放器至少15秒才重新打开一次。
        // 重连本身要重建RTSP会话+硬解上下文（实测5~16秒），
        // 5秒冷却挡不住重连风暴；配合PlayerRecoveryManager的指数退避。
        private const int PlayerRestartCooldownSeconds = 15;

        // 全局重开错峰：任意一路刚重开，2秒内其他路的重开请求先拒绝。
        // 看门狗每2秒采样一次，被拒的下一轮自然重试，形成串行错峰，
        // 避免三路同时重建RTSP会话+硬解把设备和GPU打爆。
        private const int PlayerRestartStaggerSeconds = 2;

        // ZLM正常，但VLC 3秒仍没进入Playing
        private const int PlayerStartupTimeoutSeconds = 3;

        // ZLM HTTP 健康探测偶发一次超时，不立刻判定整个视频服务离线。
        // 连续2次失败（约10秒）才进入 confirmed offline，避免一次抖动就全量恢复。
        private const int ZlmOfflineConfirmFailures = 2;

        // ZLM确认恢复后给代理/拉流一点稳定时间，期间不做单路强制重建。
        private const int ZlmRecoveryGraceSeconds = 15;

        // VLC真实画面检测：每2秒读取Media统计。
        private const int PlaybackHealthIntervalMs = 2000;

        // VLC已经Playing，且ZLM持续有新帧，但可见画面8秒没有新输出，判定播放器假死。
        // 监控场景实时优先，但不等积压追帧的前提下，
        // 4秒在25fps下仅100帧，网络抖动+缓冲重排时容易误判（原4秒）。
        private const int PlaybackStallSeconds = 8;

        // 不使用MediaPlayer.Time与墙钟比较实时延迟。
        // RTSP直播发生PCR/时间戳重建时，Time不是可靠的实时基准；
        // 这里只保留“ZLM帧增长 + 可见画面连续不推进”的冻结判定。

        // 每次新开播放器/切换到可见组后先给15秒启动宽限。
        // 实测重连后Opening+硬解初始化要5~16秒（d3d11va就绪日志），
        // 原8秒在真正出画面之前就过期，导致刚重开又被判冻结（原8秒）。
        private const int PlaybackStartupGraceSeconds = 15;

        // 无感切组：先在旧画面后面预热新组，检测第一帧后再切换Z序。
        private const int SeamlessSwitchPollIntervalMs = 100;
        private const int SeamlessSwitchTimeoutMs = 2500;

        // 新组已经显示后只保留180ms交叠，随后释放旧组，缩短临时6路解码时间。
        private const int SeamlessOldGroupStopDelayMs = 180;

        // 用户打开右键菜单就提前预热另一组；如果没有切换，菜单关闭后稍等800ms释放。
        // 800ms也允许用户误关菜单后马上再次右键时复用已起好的播放器。
        private const int MenuPrewarmCleanupDelayMs = 800;

        // 长稳测试健康摘要：不用一直盯画面，每分钟记录当前组3路状态。
        private const int HealthSummaryIntervalMs = 60000;

        // 当前可见组播放器进入 Ended / Error / Stopped 后，连续2个采样确认再恢复。
        // 2秒采样一次，因此通常约2~4秒内自动拉回，避免瞬态状态误触发。
        private const int PlayerTerminalStateConfirmSamples = 2;

        // 保存当前视频源配置的哈希。正常启动配置未变化时只 Ensure，
        // 只有首次运行或配置发生变化才执行破坏性的 SyncAllStreams。
        private const string ZlmConfigFingerprintFileName =
            "zlm-config.fingerprint";

        // VLC 的视频输出是异步创建的。
        // Playing 事件发生时，AspectRatio 有概率随后被 VLC 的 Vout 初始化覆盖。
        // 因此开播后在 UI 线程短时间重复应用显示比例，保证最终稳定生效。
        private const int AspectRatioRefreshIntervalMs = 150;

        // 150ms × 8 ≈ 1.2 秒，覆盖大部分机器上的 Vout 初始化阶段。
        private const int AspectRatioRefreshMaxTicks = 8;


        // =========================================================
        // 配置
        // =========================================================

        private AppConfig _config;


        // =========================================================
        // ZLMediaKit
        // =========================================================

        private ZLMediaKitService _zlmService;

        // 负责把 config.ini 中的 SourceUrl 同步到 ZLM，
        // 并识别 401 视频认证失败。
        private ZlmStreamGuard _zlmStreamGuard;

        private System.Threading.Timer _zlmTimer;

        private int _zlmChecking = 0;

        private int _monitorTick = 0;

        private bool _zlmStateKnown = false;

        private bool _zlmWasOnline = false;

        // ZLM可用性确认：单次HTTP超时只记一次失败，不立刻触发全局重建。
        private int _zlmAvailabilityFailureCount = 0;

        // ZLM恢复后短暂保护期，防止代理刚恢复时健康状态波动又被反复删除/重建。
        private DateTime _zlmRecoveryGraceUntilUtc =
            DateTime.MinValue;

        // 每路ZLM是否正在稳定产生新帧。
        // 1 = ZLM端流健康且帧在推进；0 = 未确认/异常。
        private readonly int[] _zlmStreamHealthyFlags =
            new int[6];

        // 启动阶段是否已经在 VLC 播放前完成过一次 ZLM 全量同步。
        // 这个标志用于区分“程序第一次看到 ZLM 在线”和“ZLM 真正离线后恢复”。
        private bool _initialZlmSyncCompleted = false;

        // 配置变化但启动时ZLM离线/同步失败时保留。
        // ZLM真正恢复后先完成一次全量同步，再重连当前3路播放器。
        private bool _zlmFullSyncPending = false;

        // 启动视频准备只允许进入一次。
        private int _initialVideoPreparationStarted = 0;


        // =========================================================
        // VLC
        // =========================================================

        private LibVLC _libVLC;

        // VLC底层日志（自动脱敏RTSP密码）。
        private VlcLogService _vlcLogService;

        // 真实画面健康检测 + 自动恢复节流。
        private VideoPlaybackHealthMonitor _playbackHealthMonitor;
        private PlayerRecoveryManager _playerRecoveryManager;
        private System.Windows.Forms.Timer _playbackHealthTimer;
        private System.Windows.Forms.Timer _healthSummaryTimer;


        // 当前可见播放器进入终止状态的连续采样次数。
        // 隐藏组/切组期间会清零，不会把程序主动 Stop 当成故障。
        private readonly int[] _terminalPlayerStateCounts =
            new int[6];

        // 无感切组只在切换窗口内短暂并行解码6路。
        // 平时仍保持3路，避免把V1.3稳定性优化撤回。
        private System.Windows.Forms.Timer _groupSwitchTimer;
        private System.Windows.Forms.Timer _oldGroupStopTimer;
        private System.Windows.Forms.Timer _menuPrewarmCleanupTimer;
        private bool _groupSwitchInProgress = false;
        private int _pendingGroup = 0;
        private int _switchOldGroup = 0;
        private DateTime _groupSwitchStartedUtc = DateTime.MinValue;

        // 右键菜单预热状态。它与真正的切组状态分开，避免“只是打开菜单”就修改_currentGroup。
        private int _menuPrewarmGroup = 0;
        private int _menuPrewarmSourceGroup = 0;
        private DateTime _menuPrewarmStartedUtc = DateTime.MinValue;


        // =========================================================
        // 六个画面槽位 + 仅当前3路VLC解码
        //
        // ZLM端6路RTSP代理始终保持；客户端只播放当前可见组3路。
        // 这样仍然能快速切组，但把H265硬解/Vout压力直接减半，
        // 避免6路2K H265长期同时解码后出现late picture、buffer deadlock、
        // 画面停顿后加速追帧等实时性问题。
        // 组1 = slot 0,1,2；组2 = slot 3,4,5。
        // =========================================================

        private readonly Panel[] _videoPanels =
            new Panel[6];

        private readonly Label[] _statusLabels =
            new Label[6];

        private readonly VideoView[] _videoViews =
            new VideoView[6];

        // =========================================================
        // 每路画面的显示尺寸缓存
        //
        // LayoutVideoPanels（UI线程）写，
        // VLC 的 Playing 事件（后台线程）读，
        // 不跨线程碰控件属性。
        // =========================================================

        private readonly Size[] _slotVideoSizes =
            new Size[6];

        private readonly MediaPlayer[] _mediaPlayers =
            new MediaPlayer[6];

        private readonly Media[] _currentMedias =
            new Media[6];

        // 每路一个 WinForms Timer。
        // 只在开播/切组/布局变化后短暂运行，用于稳定 VLC 拉伸比例。
        private readonly System.Windows.Forms.Timer[] _aspectRatioTimers =
            new System.Windows.Forms.Timer[6];

        private readonly int[] _aspectRatioRefreshTicks =
            new int[6];


        // =========================================================
        // 每个画面的运行状态
        //
        // slot 即摄像头索引（0~5），固定绑定，不再动态变化。
        // =========================================================

        private readonly int[] _slotCameraIndexes =
        {
            0,
            1,
            2,
            3,
            4,
            5
        };

        private readonly string[] _slotStateKeys =
            new string[6];

        private readonly int[] _badStreamCounts =
            new int[6];

        private readonly long[] _lastZlmFrames =
            new long[6];

        private readonly int[] _stalledFrameCounts =
            new int[6];

        private readonly int[] _slotPlayedFlags =
            new int[6];

        private readonly int[] _vlcErrorFlags =
            new int[6];

        private readonly DateTime[] _lastPlayRequestUtc =
        {
            DateTime.MinValue,
            DateTime.MinValue,
            DateTime.MinValue,
            DateTime.MinValue,
            DateTime.MinValue,
            DateTime.MinValue
        };

        private readonly DateTime[] _lastProxyRecreateUtc =
        {
            DateTime.MinValue,
            DateTime.MinValue,
            DateTime.MinValue,
            DateTime.MinValue,
            DateTime.MinValue,
            DateTime.MinValue
        };


        // =========================================================
        // 右键菜单
        // =========================================================

        private ContextMenuStrip _contextMenu;

        private ToolStripMenuItem _group1MenuItem;

        private ToolStripMenuItem _group2MenuItem;

        private ToolStripMenuItem _advancedSettingsMenuItem;

        private ToolStripMenuItem _closeMenuItem;


        // 启动阶段覆盖在视频槽位上方。ZLM网络操作放后台线程后，
        // 即使代理检查需要数秒，窗口也能立即刷新，不表现为假死。
        private Label _startupStatusLabel;


        // =========================================================
        // 当前组
        // =========================================================

        private int _currentGroup = 1;

        private volatile bool _closing = false;


        // =========================================================
        // 构造
        // =========================================================

        public MainForm()
        {
            InitializeComponent();

            Text = "CameraMonitor";

            BackColor = Color.Black;

            FormBorderStyle =
                FormBorderStyle.None;

            StartPosition =
                FormStartPosition.Manual;

            KeyPreview = true;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);

            UpdateStyles();

            Shown +=
                MainForm_Shown;

            Resize +=
                MainForm_Resize;

            KeyDown +=
                MainForm_KeyDown;

            FormClosed +=
                MainForm_FormClosed;
        }


        // =========================================================
        // 启动
        // =========================================================

        private void MainForm_Shown(
            object sender,
            EventArgs e)
        {
            try
            {
                AppLogger.Info(
                    "========================================");

                AppLogger.Info(
                    "CameraMonitor 启动");

                // 1. 加载配置
                LoadConfig();

                // 2. 精确设置目标屏幕
                SetTargetScreen();

                // 3. 初始化VLC
                InitializeVlc();

                // 4. 右键菜单
                CreateContextMenu();

                // 5. 六个视频区域槽位（客户端只播放当前3路）
                CreateVideoLayout();

                // 6. 先完成全部 Panel 的最终尺寸。
                LayoutVideoPanels();

                // 7. ZLM服务对象本身只做轻量初始化。
                _zlmService =
                    new ZLMediaKitService(
                        _config);

                _zlmStreamGuard =
                    new ZlmStreamGuard(
                        _config);

                // 8. 默认显示组先确定，但此时不启动VLC。
                ShowGroup(1);

                // 9. 窗口先正常绘制，耗时的ZLM网络检查放后台线程。
                ShowStartupOverlay(
                    "正在初始化视频...");

                BeginInitialVideoPreparation();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "CameraMonitor 启动失败",
                    ex);

                MessageBox.Show(
                    ex.Message,
                    "CameraMonitor 启动失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                Close();
            }
        }


        /// <summary>
        /// 把启动阶段的 ZLM HTTP 检查/代理准备移出 UI 线程。
        /// 完成后才回 UI 线程启动当前3路VLC和健康监控。
        /// </summary>
        private void BeginInitialVideoPreparation()
        {
            if (Interlocked.Exchange(
                    ref _initialVideoPreparationStarted,
                    1) == 1)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(
                delegate
                {
                    try
                    {
                        PrepareInitialZlmInBackground();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(
                            "启动阶段后台准备ZLM异常，将继续启动播放器并交给后台恢复",
                            ex);
                    }

                    SafeUi(
                        delegate
                        {
                            if (_closing)
                            {
                                return;
                            }

                            HideStartupOverlay();

                            // 只启动当前可见组3路VLC。
                            StartInitialGroupStreams();

                            // 启动VLC真实画面看门狗。
                            StartPlaybackHealthMonitor();

                            // 每分钟写一次当前组健康摘要。
                            StartHealthSummaryTimer();

                            // 当前3路已经启动，剩余3路ZLM代理转后台补齐。
                            // 补齐完成后再启动ZLM定时监控，避免两条维护线程并发操作代理。
                            QueueRemainingInitialZlmPreparation();
                        });
                });
        }


        private void ShowStartupOverlay(
            string text)
        {
            if (_startupStatusLabel == null)
            {
                Label label =
                    new Label();

                label.Dock =
                    DockStyle.Fill;

                label.BackColor =
                    Color.Black;

                label.ForeColor =
                    Color.White;

                label.TextAlign =
                    ContentAlignment.MiddleCenter;

                label.Font =
                    new Font(
                        Font.FontFamily,
                        16.0F,
                        FontStyle.Regular);

                _startupStatusLabel =
                    label;

                Controls.Add(
                    label);
            }

            _startupStatusLabel.Text =
                string.IsNullOrWhiteSpace(text)
                    ? "正在初始化视频..."
                    : text;

            _startupStatusLabel.Visible =
                true;

            _startupStatusLabel.BringToFront();
        }


        private void HideStartupOverlay()
        {
            if (_startupStatusLabel == null)
            {
                return;
            }

            _startupStatusLabel.Visible =
                false;
        }


        // =========================================================
        // config.ini
        // =========================================================

        private void LoadConfig()
        {
            string configPath =
                Path.Combine(
                    Application.StartupPath,
                    "config.ini");

            _config =
                ConfigManager.Load(
                    configPath);

            if (_config.Cameras == null ||
                _config.Cameras.Count < 6)
            {
                throw new Exception(
                    "config.ini 必须配置 Camera01 ~ Camera06。");
            }

            if (string.IsNullOrWhiteSpace(
                _config.ZlmHost))
            {
                throw new Exception(
                    "config.ini 没有配置 ZLMediaKit Host。");
            }
        }


        // =========================================================
        // 精确指定屏幕
        // =========================================================

        private void SetTargetScreen()
        {
            Screen targetScreen = null;

            AppLogger.Info("开始选择目标显示器");

            // 记录当前 Windows 实际识别到的所有显示器
            foreach (Screen screen in Screen.AllScreens)
            {
                AppLogger.Info(
                    "发现显示器：" +
                    "Left=" + screen.Bounds.Left +
                    ", Top=" + screen.Bounds.Top +
                    ", Width=" + screen.Bounds.Width +
                    ", Height=" + screen.Bounds.Height +
                    ", Primary=" + screen.Primary);

                // 优先严格匹配 config.ini 里的坐标
                if (screen.Bounds.Left == _config.DisplayLeft &&
                    screen.Bounds.Top == _config.DisplayTop)
                {
                    targetScreen = screen;
                }
            }

            // 找不到说明：
            // 1. 正在远程桌面
            // 2. Windows显示器布局改变
            // 3. 当前副屏没有连接
            //
            // 这时绝对不能继续使用配置坐标，
            // 否则窗口会跑到屏幕外。
            if (targetScreen == null)
            {
                targetScreen = Screen.PrimaryScreen;

                AppLogger.Warn(
                    "未找到配置中的目标显示器：" +
                    "Left=" + _config.DisplayLeft +
                    ", Top=" + _config.DisplayTop +
                    "，自动回退到主显示器。");
            }

            WindowState =
                FormWindowState.Normal;

            FormBorderStyle =
                FormBorderStyle.None;

            StartPosition =
                FormStartPosition.Manual;

            /*
             * 关键：
             * 使用当前真实存在的显示器 Bounds，
             * 不再强行使用 config.ini 的坐标。
             */
            Bounds =
                targetScreen.Bounds;

            BringToFront();

            AppLogger.Info(
                "CameraMonitor最终显示位置：" +
                "Left=" + Bounds.Left +
                ", Top=" + Bounds.Top +
                ", Width=" + Bounds.Width +
                ", Height=" + Bounds.Height);
        }


        // =========================================================
        // VLC
        // =========================================================

        private void InitializeVlc()
        {
            string vlcPath =
                Path.Combine(
                    Application.StartupPath,
                    "vlc");

            if (!Directory.Exists(
                vlcPath))
            {
                throw new Exception(
                    "找不到 VLC 目录：" +
                    vlcPath);
            }

            string libVlcPath =
                Path.Combine(
                    vlcPath,
                    "libvlc.dll");

            if (!File.Exists(
                libVlcPath))
            {
                throw new Exception(
                    "找不到 libvlc.dll：" +
                    libVlcPath);
            }

            Core.Initialize(
                vlcPath);

            /*
             * 一个LibVLC实例 + 六个MediaPlayer槽位，
             * 但运行时仅当前组3路真正播放/解码。
             *
             * 监控实时优先：
             * - 硬件解码
             * - 不解码音频（本项目无监听需求，减少资源）
             * - 丢弃过晚帧 + 跳帧
             * - 禁止VLC通过不断增加时钟延迟来追历史帧
             *
             * 必须保留 --skip-frames（09-02 实测教训）：
             * 解码积压时它无条件跳过积压帧保实时。
             * 若移除，会形成死循环——
             *   解码慢 → 播放时钟变慢 → 帧按慢时钟衡量不算"迟到"
             *   → drop-late-frames 不丢帧 → 积压更重 → 延迟无上限累积。
             * 实测移除后6.5小时：时钟比率跌到0.2~0.95（延迟累积到分钟级）、
             * 重开165次；恢复后回到25fps精确同步。
             * 理论上跳参考帧有花屏风险，但长期实测未出现，实时性优先。
             */
            _libVLC =
                new LibVLC(
                    "--no-video-title-show",
                    "--no-audio",
                    "--avcodec-hw=any",
                    "--drop-late-frames",
                    "--skip-frames",
                    "--clock-jitter=0",
                    "--clock-synchro=0");

            _vlcLogService =
                new VlcLogService();

            _vlcLogService.Attach(
                _libVLC);

            AppLogger.Info(
                "LibVLC 初始化完成，实时优先策略已启用（仅当前3路VLC解码 + drop/skip late + clock-jitter=0 + clock-synchro=0）");
        }


        // =========================================================
        // 创建视频区域
        // =========================================================

        private void CreateVideoLayout()
        {
            SuspendLayout();

            try
            {
                for (int i = 0;
                     i < 6;
                     i++)
                {
                    CreateVideoSlot(i);
                }
            }
            finally
            {
                ResumeLayout(true);
            }
        }


        private void CreateVideoSlot(
            int slotIndex)
        {
            // -----------------------------------------------------
            // 外层Panel
            //
            // 不再创建顶部Camera01/02/03标题条
            // -----------------------------------------------------

            Panel panel =
                new Panel();

            panel.BackColor =
                Color.Black;

            panel.Margin =
                new Padding(0);

            panel.Padding =
                new Padding(0);

            panel.Tag =
                slotIndex;

            panel.ContextMenuStrip =
                _contextMenu;


            // -----------------------------------------------------
            // 视频区域
            // -----------------------------------------------------

            VideoView videoView =
                new VideoView();

            videoView.Dock =
                DockStyle.Fill;

            videoView.Margin =
                new Padding(0);

            videoView.BackColor =
                Color.Black;

            videoView.Tag =
                slotIndex;

            videoView.ContextMenuStrip =
                _contextMenu;


            // -----------------------------------------------------
            // 状态提示
            //
            // 正常时隐藏。
            //
            // 只有：
            // 掉线
            // ZLM异常
            // VLC异常
            //
            // 才出现。
            // -----------------------------------------------------

            Label statusLabel =
                new Label();

            statusLabel.Dock =
                DockStyle.Bottom;

            statusLabel.Height =
                32;

            statusLabel.ForeColor =
                Color.White;

            statusLabel.BackColor =
                Color.FromArgb(
                    55,
                    55,
                    55);

            statusLabel.TextAlign =
                ContentAlignment.MiddleCenter;

            statusLabel.Text =
                "正在连接...";

            statusLabel.Visible =
                true;

            statusLabel.Tag =
                slotIndex;

            statusLabel.ContextMenuStrip =
                _contextMenu;


            // -----------------------------------------------------
            // VLC播放器
            // -----------------------------------------------------

            MediaPlayer player =
                new MediaPlayer(
                    _libVLC);

            player.EnableMouseInput =
                false;

            player.EnableKeyInput =
                false;

            videoView.MediaPlayer =
                player;


            // -----------------------------------------------------
            // VLC事件
            // -----------------------------------------------------

            int capturedSlot =
                slotIndex;

            player.Playing +=
                delegate
                {
                    if (_closing)
                    {
                        return;
                    }

                    Interlocked.Exchange(
                        ref _slotPlayedFlags[
                            capturedSlot],
                        1);

                    Interlocked.Exchange(
                        ref _vlcErrorFlags[
                            capturedSlot],
                        0);

                    int cameraIndex =
                        _slotCameraIndexes[
                            capturedSlot];

                    /*
                     * Playing 事件来自 VLC 后台线程。
                     * 不在这里直接碰显示参数，而是切回 UI 线程。
                     *
                     * 另外 Playing 并不代表 Vout 已完全稳定，
                     * 所以启动一个约 1.2 秒的短周期刷新，
                     * 防止 AspectRatio 被后续 Vout 初始化覆盖。
                     */
                    if (_playbackHealthMonitor != null)
                    {
                        _playbackHealthMonitor.Reset(
                            capturedSlot,
                            DateTime.UtcNow,
                            PlaybackStartupGraceSeconds);
                    }

                    SafeUi(
                        delegate
                        {
                            StartAspectRatioRefresh(
                                capturedSlot);

                            UpdateSlotState(
                                capturedSlot,
                                cameraIndex,
                                "playing",
                                string.Empty,
                                false);
                        });
                };


            player.EncounteredError +=
                delegate
                {
                    if (_closing)
                    {
                        return;
                    }

                    Interlocked.Exchange(
                        ref _slotPlayedFlags[
                            capturedSlot],
                        0);

                    Interlocked.Exchange(
                        ref _vlcErrorFlags[
                            capturedSlot],
                        1);

                    if (_playbackHealthMonitor != null)
                    {
                        _playbackHealthMonitor.Reset(
                            capturedSlot,
                            DateTime.UtcNow,
                            PlaybackStartupGraceSeconds);
                    }

                    int cameraIndex =
                        _slotCameraIndexes[
                            capturedSlot];

                    SafeUi(
                        delegate
                        {
                            UpdateSlotState(
                                capturedSlot,
                                cameraIndex,
                                "vlc-error",
                                "视频播放异常，正在重新连接...",
                                true);
                        });
                };


            // -----------------------------------------------------
            // 组合
            //
            // 没有Camera标题栏
            // -----------------------------------------------------

            panel.Controls.Add(
                videoView);

            panel.Controls.Add(
                statusLabel);

            statusLabel.BringToFront();

            Controls.Add(
                panel);


            // -----------------------------------------------------
            // 保存
            // -----------------------------------------------------

            _videoPanels[
                slotIndex] =
                panel;

            _statusLabels[
                slotIndex] =
                statusLabel;

            _videoViews[
                slotIndex] =
                videoView;

            _mediaPlayers[
                slotIndex] =
                player;

            InitializeAspectRatioTimer(
                slotIndex);
        }


        // =========================================================
        // 三路视频直接铺满整个窗体
        // =========================================================

        /// <summary>
        /// 强制把指定 slot 的 VLC 画面拉伸到当前 Panel 的宽高比。
        ///
        /// 必须在每次媒体开始播放后调用，因为 PlayCamera 重建 Media
        /// 会重置 VLC 的显示参数；仅在 LayoutVideoPanels 里设一次不够。
        ///
        /// 尺寸从 _slotVideoSizes 缓存读取，
        /// VLC 的 Playing 事件在后台线程触发，
        /// 不跨线程读 Panel.ClientSize。
        /// </summary>
        private void SetPlayerAspectRatio(
            int slotIndex)
        {
            if (slotIndex < 0 ||
                slotIndex >= 6)
            {
                return;
            }

            MediaPlayer player =
                _mediaPlayers[slotIndex];

            if (player == null)
            {
                return;
            }

            Size size =
                _slotVideoSizes[slotIndex];

            int width =
                size.Width;

            int height =
                size.Height;

            if (width <= 0 ||
                height <= 0)
            {
                return;
            }

            try
            {
                player.AspectRatio =
                    width.ToString() +
                    ":" +
                    height.ToString();
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "slot " +
                    slotIndex +
                    " 设置视频拉伸比例失败：" +
                    ex.Message);
            }
        }


        /// <summary>
        /// 为指定画面创建一个 UI 线程定时器。
        ///
        /// 不依赖 LibVLCSharp 的额外事件，因此兼容当前 VS2015 /
        /// .NET Framework 4.5.2 / LibVLCSharp 3.5.1 组合。
        /// </summary>
        private void InitializeAspectRatioTimer(
            int slotIndex)
        {
            if (slotIndex < 0 ||
                slotIndex >= 6)
            {
                return;
            }

            if (_aspectRatioTimers[
                    slotIndex] != null)
            {
                return;
            }

            System.Windows.Forms.Timer timer =
                new System.Windows.Forms.Timer();

            timer.Interval =
                AspectRatioRefreshIntervalMs;

            int capturedSlot =
                slotIndex;

            timer.Tick +=
                delegate
                {
                    if (_closing ||
                        IsDisposed)
                    {
                        timer.Stop();
                        return;
                    }

                    SetPlayerAspectRatio(
                        capturedSlot);

                    _aspectRatioRefreshTicks[
                        capturedSlot]++;

                    if (_aspectRatioRefreshTicks[
                            capturedSlot] >=
                        AspectRatioRefreshMaxTicks)
                    {
                        timer.Stop();
                    }
                };

            _aspectRatioTimers[
                slotIndex] =
                timer;
        }


        /// <summary>
        /// 立即应用一次比例，然后在后续约 1.2 秒内重复应用。
        ///
        /// 用于三个时机：
        /// 1. VLC Playing；
        /// 2. 窗口/Panel 尺寸变化；
        /// 3. 隐藏组重新显示。
        /// </summary>
        private void StartAspectRatioRefresh(
            int slotIndex)
        {
            if (slotIndex < 0 ||
                slotIndex >= 6)
            {
                return;
            }

            SetPlayerAspectRatio(
                slotIndex);

            System.Windows.Forms.Timer timer =
                _aspectRatioTimers[
                    slotIndex];

            if (timer == null)
            {
                return;
            }

            _aspectRatioRefreshTicks[
                slotIndex] =
                0;

            timer.Stop();
            timer.Start();
        }


        private void LayoutVideoPanels()
        {
            if (_videoPanels[0] == null)
            {
                return;
            }

            if (ClientSize.Width <= 0 ||
                ClientSize.Height <= 0)
            {
                return;
            }

            // ==========================================
            // 高度固定540
            // ==========================================

            int videoHeight = 540;

            if (videoHeight > ClientSize.Height)
            {
                videoHeight = ClientSize.Height;
            }


            // ==========================================
            // 三路自动平分整个屏幕宽度
            // ==========================================

            int baseWidth =
                ClientSize.Width / 3;


            // ==========================================
            // 贴屏幕顶部
            // 不再垂直居中
            // ==========================================

            int top = 0;


            SuspendLayout();

            try
            {
                /*
                 * 双组重叠布局：
                 * 6 个面板，组内位置由 i % 3 决定。
                 *
                 * slot 0 与 slot 3 重叠在同一物理位置
                 * slot 1 与 slot 4 重叠在同一物理位置
                 * slot 2 与 slot 5 重叠在同一物理位置
                 *
                 * 切组时隐藏一组、显示另一组，
                 * 位置完全一致，画面无缝跳变。
                 */
                for (int i = 0;
                     i < 6;
                     i++)
                {
                    int posInGroup =
                        i % 3;

                    int left =
                        baseWidth * posInGroup;

                    int width;

                    if (posInGroup == 2)
                    {
                        // 最后一格吃掉剩余像素
                        width =
                            ClientSize.Width -
                            baseWidth * 2;
                    }
                    else
                    {
                        width =
                            baseWidth;
                    }


                    // ------------------------------------------
                    // Panel固定540高，宽度自动
                    // ------------------------------------------

                    _videoPanels[i].Bounds =
                        new Rectangle(
                            left,
                            top,
                            width,
                            videoHeight);

                    _slotVideoSizes[i] =
                        new Size(
                            width,
                            videoHeight);


                    // ------------------------------------------
                    // 强制VLC视频拉伸到整个Panel
                    //
                    // 例如：
                    // 640 × 540
                    //
                    // 不再保持原来的16:9
                    // ------------------------------------------

                    StartAspectRatioRefresh(i);
                }
            }
            finally
            {
                ResumeLayout(true);
            }
        }


        // =========================================================
        // 右键菜单
        // =========================================================

        private void CreateContextMenu()
        {
            _contextMenu =
                new ContextMenuStrip();

            _group1MenuItem =
                new ToolStripMenuItem(
                    "2#主溜井");

            _group2MenuItem =
                new ToolStripMenuItem(
                    "3#主溜井");

            _advancedSettingsMenuItem =
                new ToolStripMenuItem(
                    "高级设置...");

            _closeMenuItem =
                new ToolStripMenuItem(
                    "关闭");

            _group1MenuItem.Click +=
                delegate
                {
                    SwitchGroup(1);
                };

            _group2MenuItem.Click +=
                delegate
                {
                    SwitchGroup(2);
                };

            _advancedSettingsMenuItem.Click +=
                delegate
                {
                    OpenAdvancedSettings();
                };

            _closeMenuItem.Click +=
                delegate
                {
                    Close();
                };

            _contextMenu.Items.Add(
                _group1MenuItem);

            _contextMenu.Items.Add(
                _group2MenuItem);

            _contextMenu.Items.Add(
                new ToolStripSeparator());

            _contextMenu.Items.Add(
                _advancedSettingsMenuItem);

            _contextMenu.Items.Add(
                new ToolStripSeparator());

            _contextMenu.Items.Add(
                _closeMenuItem);

            _contextMenu.Opening +=
                ContextMenu_Opening;

            _contextMenu.Closed +=
                ContextMenu_Closed;
        }


        private void ContextMenu_Opening(
            object sender,
            CancelEventArgs e)
        {
            _group1MenuItem.Checked =
                _currentGroup == 1;

            _group2MenuItem.Checked =
                _currentGroup == 2;

            // 菜单再次打开时先取消上一次“未使用预热”的释放计时。
            StopAndDisposeTimer(
                ref _menuPrewarmCleanupTimer);

            if (_closing ||
                _groupSwitchInProgress)
            {
                return;
            }

            // 不阻塞ContextMenu的显示。菜单真正显示后立即开始另一组预热，
            // 用户看菜单/移动鼠标/点击的这段时间正好用于等待首帧。
            try
            {
                BeginInvoke(
                    (MethodInvoker)delegate
                    {
                        if (!_closing &&
                            _contextMenu != null &&
                            _contextMenu.Visible &&
                            !_groupSwitchInProgress)
                        {
                            StartMenuGroupPrewarm();
                        }
                    });
            }
            catch
            {
            }
        }


        private void ContextMenu_Closed(
            object sender,
            ToolStripDropDownClosedEventArgs e)
        {
            ScheduleMenuPrewarmCleanup();
        }


        /// <summary>
        /// 右键菜单一出现就预热“另一组”。
        /// 这里只让目标组在当前画面背后建立RTSP/Vout，不改变_currentGroup。
        /// </summary>
        private void StartMenuGroupPrewarm()
        {
            if (_closing ||
                _config == null ||
                _config.Cameras == null ||
                _groupSwitchInProgress)
            {
                return;
            }

            int sourceGroup =
                _currentGroup;

            int targetGroup =
                sourceGroup == 1
                    ? 2
                    : 1;

            StopAndDisposeTimer(
                ref _menuPrewarmCleanupTimer);

            if (_menuPrewarmGroup ==
                    targetGroup &&
                _menuPrewarmSourceGroup ==
                    sourceGroup)
            {
                // 上一次菜单刚关闭又重新打开，继续复用，不重复Stop/Play。
                PrepareGroupForSeamlessWarmup(
                    sourceGroup,
                    targetGroup);

                AppLogger.Info(
                    "右键菜单预热已存在，继续复用摄像头组" +
                    targetGroup);

                return;
            }

            if (_menuPrewarmGroup >= 1 &&
                _menuPrewarmGroup <= 2 &&
                _menuPrewarmGroup !=
                    sourceGroup)
            {
                StopGroupPlayers(
                    _menuPrewarmGroup,
                    "切换右键菜单预热目标");
            }

            _menuPrewarmGroup =
                targetGroup;

            _menuPrewarmSourceGroup =
                sourceGroup;

            _menuPrewarmStartedUtc =
                DateTime.UtcNow;

            AppLogger.Info(
                "右键菜单提前预热摄像头组：" +
                sourceGroup +
                " -> " +
                targetGroup);

            try
            {
                PrepareGroupForSeamlessWarmup(
                    sourceGroup,
                    targetGroup);

                StartGroupStreams(
                    targetGroup,
                    "右键菜单提前预热");
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "右键菜单提前预热失败：" +
                    ex.Message);

                try
                {
                    StopGroupPlayers(
                        targetGroup,
                        "右键菜单预热失败清理");

                    ShowGroup(
                        sourceGroup);
                }
                catch
                {
                }

                ResetMenuPrewarmState();
            }
        }


        private void ScheduleMenuPrewarmCleanup()
        {
            if (_closing ||
                _menuPrewarmGroup < 1 ||
                _menuPrewarmGroup > 2)
            {
                return;
            }

            StopAndDisposeTimer(
                ref _menuPrewarmCleanupTimer);

            System.Windows.Forms.Timer timer =
                new System.Windows.Forms.Timer();

            timer.Interval =
                MenuPrewarmCleanupDelayMs;

            timer.Tick +=
                delegate
                {
                    StopAndDisposeTimer(
                        ref _menuPrewarmCleanupTimer);

                    if (_closing ||
                        _groupSwitchInProgress)
                    {
                        return;
                    }

                    int prewarmGroup =
                        _menuPrewarmGroup;

                    if (prewarmGroup >= 1 &&
                        prewarmGroup <= 2 &&
                        prewarmGroup !=
                            _currentGroup)
                    {
                        StopGroupPlayers(
                            prewarmGroup,
                            "取消未使用的右键菜单预热");

                        ShowGroup(
                            _currentGroup);

                        AppLogger.Info(
                            "右键菜单关闭且未切组，已释放预热摄像头组" +
                            prewarmGroup);
                    }

                    ResetMenuPrewarmState();
                };

            _menuPrewarmCleanupTimer =
                timer;

            timer.Start();
        }


        private void ResetMenuPrewarmState()
        {
            _menuPrewarmGroup =
                0;

            _menuPrewarmSourceGroup =
                0;

            _menuPrewarmStartedUtc =
                DateTime.MinValue;
        }


        // =========================================================
        // 高级设置
        // =========================================================

        private void OpenAdvancedSettings()
        {
            if (_closing)
            {
                return;
            }

            string configPath =
                Path.Combine(
                    Application.StartupPath,
                    "config.ini");

            using (AdvancedSettingsForm form =
                   new AdvancedSettingsForm(
                       configPath))
            {
                DialogResult result =
                    form.ShowDialog(
                        this);

                if (result !=
                        DialogResult.OK ||
                    !form.RestartRequested)
                {
                    return;
                }

                AppLogger.Info(
                    "高级设置已保存，准备重启 CameraMonitor");

                try
                {
                    Application.Restart();
                }
                catch (Exception ex)
                {
                    AppLogger.Error(
                        "CameraMonitor 自动重启失败",
                        ex);

                    MessageBox.Show(
                        this,
                        "配置已经保存，但程序自动重启失败。\r\n" +
                        "请手工关闭后重新打开 CameraMonitor。\r\n\r\n" +
                        ex.Message,
                        "高级设置",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
            }
        }


        // =========================================================
        // 切组（预热无感 + 实时优先）
        //
        // 正常运行仍然只解码当前3路。
        // 用户切组时：旧组保持显示 -> 新组在背后短暂预热 ->
        // 新组真正显示至少2帧（或2.5秒超时）-> 只切换Z序覆盖旧组 ->
        // 180ms后再停止并隐藏旧组。
        // 因此只有很短的切换窗口可能同时解码6路，不恢复“6路永久常驻解码”。
        // =========================================================

        private void SwitchGroup(
            int group)
        {
            if (_config == null ||
                _config.Cameras == null ||
                group < 1 ||
                group > 2)
            {
                return;
            }

            if (_groupSwitchInProgress)
            {
                AppLogger.Info(
                    "切组请求已忽略：已有无感切换正在进行，目标组=" +
                    _pendingGroup);

                return;
            }

            if (group ==
                _currentGroup)
            {
                ShowGroup(
                    group);

                return;
            }

            int oldGroup =
                _currentGroup;

            bool reuseMenuPrewarm =
                _menuPrewarmGroup ==
                    group &&
                _menuPrewarmSourceGroup ==
                    oldGroup;

            double menuPrewarmAgeMs =
                reuseMenuPrewarm &&
                _menuPrewarmStartedUtc !=
                    DateTime.MinValue
                    ? (DateTime.UtcNow -
                       _menuPrewarmStartedUtc)
                        .TotalMilliseconds
                    : 0;

            // Item Click后ContextMenu会关闭并安排释放；真正切组时必须先取消该释放。
            StopAndDisposeTimer(
                ref _menuPrewarmCleanupTimer);

            _groupSwitchInProgress =
                true;

            _pendingGroup =
                group;

            _switchOldGroup =
                oldGroup;

            // 这里记录“用户点击到真正切换”的时间。
            // 菜单提前预热的时间单独写日志，不再让用户感觉这段时间是点击延迟。
            _groupSwitchStartedUtc =
                DateTime.UtcNow;

            if (reuseMenuPrewarm)
            {
                AppLogger.Info(
                    "切换摄像头组（菜单预热已命中）：" +
                    oldGroup +
                    " -> " +
                    group +
                    "；已提前预热约" +
                    ((int)menuPrewarmAgeMs) +
                    "ms");
            }
            else
            {
                AppLogger.Info(
                    "切换摄像头组（预热无感）：" +
                    oldGroup +
                    " -> " +
                    group +
                    "；旧画面保持显示，新组后台预热首帧");
            }

            try
            {
                PrepareGroupForSeamlessWarmup(
                    oldGroup,
                    group);

                if (!reuseMenuPrewarm)
                {
                    StartGroupStreams(
                        group,
                        "无感切换预热");
                }

                StartSeamlessSwitchTimer();

                // 菜单预热可能已经把3路首帧全部准备好。
                // 不必再等第一个100ms Timer Tick，点击后立即检查一次。
                CheckSeamlessSwitchReadiness();
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "无感切组预热失败，退化为普通切换",
                    ex);

                CompleteSeamlessSwitch(
                    true);
            }
        }


        /// <summary>
        /// 让目标组三个Panel保持Visible，但放在当前组三个Panel后面。
        /// 这样LibVLC能够真正创建Vout/输出第一帧，而用户仍然看到旧组画面。
        /// </summary>
        private void PrepareGroupForSeamlessWarmup(
            int oldGroup,
            int targetGroup)
        {
            int targetStart =
                targetGroup == 1
                    ? 0
                    : 3;

            SuspendLayout();

            try
            {
                for (int slot = targetStart;
                     slot < targetStart + 3;
                     slot++)
                {
                    Panel panel =
                        _videoPanels[
                            slot];

                    if (panel == null)
                    {
                        continue;
                    }

                    panel.Visible =
                        true;

                    panel.SendToBack();

                    StartAspectRatioRefresh(
                        slot);
                }

                KeepGroupPanelsOnTop(
                    oldGroup);
            }
            finally
            {
                ResumeLayout(true);
            }
        }


        private void KeepGroupPanelsOnTop(
            int group)
        {
            int startIndex =
                group == 1
                    ? 0
                    : 3;

            for (int slot = startIndex;
                 slot < startIndex + 3;
                 slot++)
            {
                Panel panel =
                    _videoPanels[
                        slot];

                if (panel != null &&
                    panel.Visible)
                {
                    panel.BringToFront();
                }
            }
        }


        private void StartSeamlessSwitchTimer()
        {
            StopAndDisposeTimer(
                ref _groupSwitchTimer);

            System.Windows.Forms.Timer timer =
                new System.Windows.Forms.Timer();

            timer.Interval =
                SeamlessSwitchPollIntervalMs;

            timer.Tick +=
                delegate
                {
                    CheckSeamlessSwitchReadiness();
                };

            _groupSwitchTimer =
                timer;

            timer.Start();
        }


        private void CheckSeamlessSwitchReadiness()
        {
            if (_closing ||
                !_groupSwitchInProgress ||
                _pendingGroup < 1 ||
                _pendingGroup > 2)
            {
                StopAndDisposeTimer(
                    ref _groupSwitchTimer);

                return;
            }

            int startIndex =
                _pendingGroup == 1
                    ? 0
                    : 3;

            int readyCount =
                0;

            int unavailableCount =
                0;

            for (int slot = startIndex;
                 slot < startIndex + 3;
                 slot++)
            {
                MediaPlayer player =
                    _mediaPlayers[
                        slot];

                Media media =
                    _currentMedias[
                        slot];

                bool playing =
                    false;

                int displayedPictures =
                    0;

                try
                {
                    playing =
                        player != null &&
                        player.State ==
                        VLCState.Playing;

                    if (media != null)
                    {
                        MediaStats stats =
                            media.Statistics;

                        displayedPictures =
                            stats.DisplayedPictures;
                    }
                }
                catch
                {
                    playing =
                        false;
                }

                // 无感切换必须等 VideoView 真正显示出画面后再交接。
                // DecodedVideo 只代表解码器已经拿到帧，不能证明 D3D11/Vout 已经显示；
                // 至少确认显示了2帧，避免“已解码但面板仍黑”的假就绪。
                bool firstFrameReady =
                    playing &&
                    displayedPictures >= 2;

                if (firstFrameReady)
                {
                    readyCount++;
                    continue;
                }

                int cameraIndex =
                    _slotCameraIndexes[
                        slot];

                if (IsCameraUnavailableForSeamlessSwitch(
                        cameraIndex))
                {
                    unavailableCount++;
                }
            }

            int resolvedCount =
                readyCount + unavailableCount;

            double elapsedMs =
                (DateTime.UtcNow -
                 _groupSwitchStartedUtc)
                    .TotalMilliseconds;

            KeepGroupPanelsOnTop(
                _switchOldGroup);

            if (resolvedCount >= 3)
            {
                if (unavailableCount > 0)
                {
                    AppLogger.Info(
                        "无感切换新组已可交接：画面就绪=" +
                        readyCount +
                        "/3，明确不可用=" +
                        unavailableCount +
                        "/3，耗时=" +
                        ((int)elapsedMs) +
                        "ms");
                }
                else
                {
                    AppLogger.Info(
                        "无感切换新组首帧已全部就绪：" +
                        readyCount +
                        "/3，耗时=" +
                        ((int)elapsedMs) +
                        "ms");
                }

                CompleteSeamlessSwitch(
                    false);

                return;
            }

            if (elapsedMs >=
                SeamlessSwitchTimeoutMs)
            {
                AppLogger.Warn(
                    "无感切换预热超时：画面就绪=" +
                    readyCount +
                    "/3，明确不可用=" +
                    unavailableCount +
                    "/3，等待=" +
                    ((int)elapsedMs) +
                    "ms；立即切换，未决画面继续自动连接");

                CompleteSeamlessSwitch(
                    true);
            }
        }


        /// <summary>
        /// 无感切组时，某一路虽然没有首帧，但海康SDK已经明确知道它当前不可用，
        /// 就不再为了这一路把整组强制拖到2.5秒超时。
        ///
        /// Connecting + ErrorCode != 0 也视为“当前已知不可用”：
        /// 这通常是第一次登录已经失败、但为了防抖尚未正式归类为Offline的阶段。
        /// SDK后续仍会在后台继续重试，设备恢复后不受这里影响。
        /// </summary>
        private bool IsCameraUnavailableForSeamlessSwitch(
            int cameraIndex)
        {
            HikvisionDeviceManager manager =
                _hikvisionDeviceManager;

            if (manager == null)
            {
                return false;
            }

            HikvisionDeviceStatus status;

            try
            {
                status =
                    manager.GetStatus(
                        cameraIndex);
            }
            catch
            {
                return false;
            }

            if (status == null)
            {
                return false;
            }

            switch (status.State)
            {
                case HikvisionDeviceState.AuthenticationFailed:
                case HikvisionDeviceState.Offline:
                case HikvisionDeviceState.ConnectionLimit:
                case HikvisionDeviceState.ChannelError:
                case HikvisionDeviceState.Error:
                    return true;

                case HikvisionDeviceState.Connecting:
                    return
                        status.ErrorCode != 0;

                case HikvisionDeviceState.Unknown:
                case HikvisionDeviceState.Online:
                default:
                    return false;
            }
        }


        private void CompleteSeamlessSwitch(
            bool timedOut)
        {
            if (!_groupSwitchInProgress)
            {
                return;
            }

            StopAndDisposeTimer(
                ref _groupSwitchTimer);

            int oldGroup =
                _switchOldGroup;

            int targetGroup =
                _pendingGroup;

            if (targetGroup < 1 ||
                targetGroup > 2)
            {
                ResetSeamlessSwitchState();
                return;
            }

            // 真正切组开始后，菜单预热已经完成使命，不能再被ContextMenu关闭逻辑释放。
            StopAndDisposeTimer(
                ref _menuPrewarmCleanupTimer);

            ResetMenuPrewarmState();

            // 真正切换只做 Z 序交接：目标组覆盖到旧组上面。
            // 这里绝不隐藏旧组；旧组继续保留180ms作为视觉底图，
            // 等新组已经稳定显示后再统一停止并隐藏。
            PromoteGroupToFrontWithoutHidingOld(
                targetGroup);

            AppLogger.Info(
                "无感切换画面已完成：" +
                oldGroup +
                " -> " +
                targetGroup +
                (timedOut
                    ? "（预热超时退化）"
                    : "（预热完成）"));

            ScheduleOldGroupStop(
                oldGroup,
                targetGroup);
        }


        /// <summary>
        /// 将目标组提升到最前面，但不隐藏旧组。
        /// 这是双层无感交接的关键：新画面直接覆盖旧画面，
        /// 避免 Visible=false -> D3D11 首帧尚未刷出之间出现黑屏。
        /// </summary>
        private void PromoteGroupToFrontWithoutHidingOld(
            int group)
        {
            if (group < 1 ||
                group > 2)
            {
                return;
            }

            int oldGroup =
                _currentGroup;

            _currentGroup =
                group;

            if (oldGroup != group)
            {
                AppLogger.Info(
                    "当前显示摄像头组：" +
                    group);
            }

            int startIndex =
                group == 1
                    ? 0
                    : 3;

            SuspendLayout();

            try
            {
                for (int slot = startIndex;
                     slot < startIndex + 3;
                     slot++)
                {
                    Panel panel =
                        _videoPanels[
                            slot];

                    if (panel == null)
                    {
                        continue;
                    }

                    panel.Visible =
                        true;

                    panel.BringToFront();

                    StartAspectRatioRefresh(
                        slot);

                    if (_playbackHealthMonitor != null)
                    {
                        _playbackHealthMonitor.Reset(
                            slot,
                            DateTime.UtcNow,
                            PlaybackStartupGraceSeconds);
                    }
                }
            }
            finally
            {
                ResumeLayout(true);
            }

            _group1MenuItem.Checked =
                _currentGroup == 1;

            _group2MenuItem.Checked =
                _currentGroup == 2;
        }


        /// <summary>
        /// 只隐藏指定组的3个面板，不改变当前组，也不触碰新组播放器。
        /// 仅在双层交接结束、旧组播放器已经停止后调用。
        /// </summary>
        private void HideGroupPanels(
            int group)
        {
            if (group < 1 ||
                group > 2)
            {
                return;
            }

            int startIndex =
                group == 1
                    ? 0
                    : 3;

            SuspendLayout();

            try
            {
                for (int slot = startIndex;
                     slot < startIndex + 3;
                     slot++)
                {
                    Panel panel =
                        _videoPanels[
                            slot];

                    if (panel != null)
                    {
                        panel.Visible =
                            false;
                    }
                }
            }
            finally
            {
                ResumeLayout(true);
            }
        }


        private void ScheduleOldGroupStop(
            int oldGroup,
            int targetGroup)
        {
            StopAndDisposeTimer(
                ref _oldGroupStopTimer);

            System.Windows.Forms.Timer timer =
                new System.Windows.Forms.Timer();

            timer.Interval =
                SeamlessOldGroupStopDelayMs;

            timer.Tick +=
                delegate
                {
                    StopAndDisposeTimer(
                        ref _oldGroupStopTimer);

                    if (!_closing)
                    {
                        StopGroupPlayers(
                            oldGroup,
                            "无感切换到摄像头组" +
                            targetGroup +
                            "后释放旧组");

                        // 新组已经覆盖在最前面，此时再隐藏旧组，
                        // 不让 Stop() 产生的黑帧暴露给用户。
                        HideGroupPanels(
                            oldGroup);
                    }

                    ResetSeamlessSwitchState();
                };

            _oldGroupStopTimer =
                timer;

            timer.Start();
        }


        private void ResetSeamlessSwitchState()
        {
            _groupSwitchInProgress =
                false;

            _pendingGroup =
                0;

            _switchOldGroup =
                0;

            _groupSwitchStartedUtc =
                DateTime.MinValue;
        }


        private void StopAndDisposeTimer(
            ref System.Windows.Forms.Timer timer)
        {
            if (timer == null)
            {
                return;
            }

            try
            {
                timer.Stop();
                timer.Dispose();
            }
            catch
            {
            }

            timer =
                null;
        }


        /// <summary>
        /// 启动时只播放默认可见组3路；ZLM端仍保持6路代理。
        /// </summary>
        private void StartInitialGroupStreams()
        {
            StartGroupStreams(
                _currentGroup,
                "启动加载当前3路");
        }


        /// <summary>
        /// 只启动目标组3路客户端VLC。
        /// ZLM端6路代理不受影响，隐藏组仍由ZLM持续拉流。
        /// </summary>
        private void StartGroupStreams(
            int group,
            string reason)
        {
            if (group < 1 ||
                group > 2)
            {
                return;
            }

            int startIndex =
                group == 1
                    ? 0
                    : 3;

            for (int cameraIndex = startIndex;
                 cameraIndex < startIndex + 3;
                 cameraIndex++)
            {
                if (_closing)
                {
                    return;
                }

                CameraConfig camera =
                    _config.Cameras[
                        cameraIndex];

                string playUrl =
                    _config.GetPlayUrl(
                        camera);

                // 这里只重置播放器侧，不清空ZLM健康标志。
                Interlocked.Exchange(
                    ref _slotPlayedFlags[
                        cameraIndex],
                    0);

                Interlocked.Exchange(
                    ref _vlcErrorFlags[
                        cameraIndex],
                    0);

                _lastPlayRequestUtc[
                    cameraIndex] =
                    DateTime.UtcNow;

                if (_playbackHealthMonitor != null)
                {
                    _playbackHealthMonitor.Reset(
                        cameraIndex,
                        DateTime.UtcNow,
                        PlaybackStartupGraceSeconds);
                }

                UpdateSlotState(
                    cameraIndex,
                    cameraIndex,
                    "connecting",
                    "正在连接...",
                    false);

                PlayCamera(
                    cameraIndex,
                    cameraIndex,
                    playUrl,
                    reason);
            }
        }


        /// <summary>
        /// 停止某组3路客户端VLC并释放Media。
        /// 仅影响CameraMonitor本机解码，不删除/重建ZLM代理。
        /// </summary>
        private void StopGroupPlayers(
            int group,
            string reason)
        {
            if (group < 1 ||
                group > 2)
            {
                return;
            }

            int startIndex =
                group == 1
                    ? 0
                    : 3;

            for (int slot = startIndex;
                 slot < startIndex + 3;
                 slot++)
            {
                MediaPlayer player =
                    _mediaPlayers[
                        slot];

                try
                {
                    if (player != null)
                    {
                        player.Stop();
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        GetCameraLogName(
                            slot) +
                        " 停止后台解码失败：" +
                        ex.Message);
                }

                Media media =
                    _currentMedias[
                        slot];

                if (media != null)
                {
                    try
                    {
                        media.Dispose();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            GetCameraLogName(
                                slot) +
                            " 释放后台Media失败：" +
                            ex.Message);
                    }

                    _currentMedias[
                        slot] =
                        null;
                }

                Interlocked.Exchange(
                    ref _slotPlayedFlags[
                        slot],
                    0);

                Interlocked.Exchange(
                    ref _vlcErrorFlags[
                        slot],
                    0);

                _terminalPlayerStateCounts[
                    slot] =
                    0;

                _lastPlayRequestUtc[
                    slot] =
                    DateTime.MinValue;

                if (_playbackHealthMonitor != null)
                {
                    _playbackHealthMonitor.Reset(
                        slot,
                        DateTime.UtcNow,
                        PlaybackStartupGraceSeconds);
                }

                AppLogger.Info(
                    GetCameraLogName(
                        slot) +
                    " 停止客户端VLC解码，原因：" +
                    reason);
            }
        }


        private bool IsSlotInCurrentGroup(
            int slot)
        {
            if (slot < 0 ||
                slot >= 6)
            {
                return false;
            }

            int startIndex =
                _currentGroup == 1
                    ? 0
                    : 3;

            return slot >=
                       startIndex &&
                   slot <
                       startIndex + 3;
        }


        /// <summary>
        /// 显示目标组3个面板，隐藏另一组。
        /// 播放/停止由SwitchGroup和StartGroupStreams负责。
        /// </summary>
        private void ShowGroup(
            int group)
        {
            if (group < 1 ||
                group > 2)
            {
                return;
            }

            int oldGroup =
                _currentGroup;

            _currentGroup =
                group;

            if (oldGroup != group)
            {
                AppLogger.Info(
                    "当前显示摄像头组：" +
                    group);
            }

            int startIndex =
                group == 1
                    ? 0
                    : 3;

            SuspendLayout();

            try
            {
                for (int i = 0;
                     i < 6;
                     i++)
                {
                    Panel panel =
                        _videoPanels[i];

                    if (panel == null)
                    {
                        continue;
                    }

                    bool visible =
                        i >= startIndex &&
                        i < startIndex + 3;

                    panel.Visible =
                        visible;

                    if (visible)
                    {
                        // 新组面板置顶，盖住隐藏组残留
                        panel.BringToFront();

                        // VideoView 从隐藏重新显示时，
                        // 再把最终拉伸比例稳定应用一次。
                        StartAspectRatioRefresh(i);

                        // 隐藏组重新显示时重新建立画面统计基线，
                        // 避免把后台不可见期间的统计变化误判为卡顿。
                        if (_playbackHealthMonitor != null)
                        {
                            _playbackHealthMonitor.Reset(
                                i,
                                DateTime.UtcNow,
                                PlaybackStartupGraceSeconds);
                        }
                    }
                }
            }
            finally
            {
                ResumeLayout(true);
            }

            _group1MenuItem.Checked =
                _currentGroup == 1;

            _group2MenuItem.Checked =
                _currentGroup == 2;
        }


        private void ResetSlotRuntimeState(
            int slot)
        {
            _badStreamCounts[
                slot] =
                0;

            _lastZlmFrames[
                slot] =
                0;

            _stalledFrameCounts[
                slot] =
                0;

            Interlocked.Exchange(
                ref _slotPlayedFlags[
                    slot],
                0);

            Interlocked.Exchange(
                ref _vlcErrorFlags[
                    slot],
                0);

            Interlocked.Exchange(
                ref _zlmStreamHealthyFlags[
                    slot],
                0);

            _terminalPlayerStateCounts[
                slot] =
                0;

            if (_playbackHealthMonitor != null)
            {
                _playbackHealthMonitor.Reset(
                    slot,
                    DateTime.UtcNow,
                    PlaybackStartupGraceSeconds);
            }

            _slotStateKeys[
                slot] =
                null;
        }


        // =========================================================
        // 播放
        // =========================================================

        private void PlayCamera(
            int slotIndex,
            int cameraIndex,
            string url,
            string reason)
        {
            if (_closing)
            {
                return;
            }

            if (slotIndex < 0 ||
                slotIndex >= 6)
            {
                return;
            }

            if (_slotCameraIndexes[
                    slotIndex] !=
                cameraIndex)
            {
                return;
            }

            MediaPlayer player =
                _mediaPlayers[
                    slotIndex];

            if (player == null)
            {
                return;
            }

            Interlocked.Exchange(
                ref _slotPlayedFlags[
                    slotIndex],
                0);

            Interlocked.Exchange(
                ref _vlcErrorFlags[
                    slotIndex],
                0);

            _lastPlayRequestUtc[
                slotIndex] =
                DateTime.UtcNow;

            try
            {
                player.Stop();
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    GetCameraLogName(
                        cameraIndex) +
                    " VLC Stop失败：" +
                    ex.Message);
            }

            if (_currentMedias[
                    slotIndex] != null)
            {
                try
                {
                    _currentMedias[
                        slotIndex]
                        .Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        GetCameraLogName(
                            cameraIndex) +
                        " Media Dispose失败：" +
                        ex.Message);
                }

                _currentMedias[
                    slotIndex] =
                    null;
            }

            try
            {
                Media media =
                    new Media(
                        _libVLC,
                        url,
                        FromType.FromLocation);

                media.AddOption(
                    ":network-caching=" +
                    NetworkCachingMs);

                media.AddOption(
                    ":rtsp-tcp");

                media.AddOption(
                    ":avcodec-hw=any");

                media.AddOption(
                    ":drop-late-frames");

                /*
                 * 跳帧已在 LibVLC 实例级启用（--skip-frames），
                 * 对所有 Media 全局生效，此处不再重复添加。
                 * 它是解码积压时的减压阀，具体见实例初始化处的说明。
                 */

                media.AddOption(
                    ":clock-jitter=0");

                media.AddOption(
                    ":clock-synchro=0");

                _currentMedias[
                    slotIndex] =
                    media;

                AppLogger.Info(
                    GetCameraLogName(
                        cameraIndex) +
                    " 打开视频，原因：" +
                    reason);

                if (_playbackHealthMonitor != null)
                {
                    _playbackHealthMonitor.Reset(
                        slotIndex,
                        DateTime.UtcNow,
                        PlaybackStartupGraceSeconds);
                }

                player.Play(
                    media);

                /*
                 * 播放启动是异步的，
                 * Playing 事件里会再次设置比例；
                 * 这里先设一次，覆盖播放器尚未真正开播的边界。
                 */
                SetPlayerAspectRatio(
                    slotIndex);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(
                    ref _vlcErrorFlags[
                        slotIndex],
                    1);

                AppLogger.Error(
                    GetCameraLogName(
                        cameraIndex) +
                    " PlayCamera异常",
                    ex);

                UpdateSlotState(
                    slotIndex,
                    cameraIndex,
                    "play-exception",
                    "视频播放失败，等待自动重连...",
                    true);
            }
        }


        // =========================================================
        // VLC真实画面健康检测
        // =========================================================

        private void StartPlaybackHealthMonitor()
        {
            if (_playbackHealthTimer != null)
            {
                return;
            }

            _playbackHealthMonitor =
                new VideoPlaybackHealthMonitor(
                    6,
                    PlaybackStallSeconds);

            _playerRecoveryManager =
                new PlayerRecoveryManager(
                    6,
                    PlayerRestartCooldownSeconds,
                    300,
                    3,
                    PlayerRestartStaggerSeconds);

            DateTime nowUtc =
                DateTime.UtcNow;

            for (int slot = 0;
                 slot < 6;
                 slot++)
            {
                _playbackHealthMonitor.Reset(
                    slot,
                    nowUtc,
                    PlaybackStartupGraceSeconds);
            }

            System.Windows.Forms.Timer timer =
                new System.Windows.Forms.Timer();

            timer.Interval =
                PlaybackHealthIntervalMs;

            timer.Tick +=
                PlaybackHealthTimer_Tick;

            _playbackHealthTimer =
                timer;

            timer.Start();

            AppLogger.Info(
                "VLC播放看门狗已启动：2秒采样；当前可见组在ZLM健康时，画面冻结或播放器进入Ended/Error/异常Stopped都会自动重开该路VLC");
        }


        private void PlaybackHealthTimer_Tick(
            object sender,
            EventArgs e)
        {
            if (_closing ||
                _playbackHealthMonitor == null)
            {
                return;
            }

            DateTime nowUtc =
                DateTime.UtcNow;

            for (int slot = 0;
                 slot < 6;
                 slot++)
            {
                Panel panel =
                    _videoPanels[
                        slot];

                MediaPlayer player =
                    _mediaPlayers[
                        slot];

                Media media =
                    _currentMedias[
                        slot];

                bool isVisible =
                    panel != null &&
                    panel.Visible &&
                    IsSlotInCurrentGroup(
                        slot);

                bool playerIsPlaying =
                    false;

                VLCState playerState =
                    VLCState.NothingSpecial;

                bool playerStateAvailable =
                    false;

                long playerTimeMs =
                    -1;

                if (player != null &&
                    isVisible)
                {
                    try
                    {
                        playerState =
                            player.State;

                        playerStateAvailable =
                            true;

                        playerIsPlaying =
                            playerState ==
                            VLCState.Playing;

                        playerTimeMs =
                            player.Time;
                    }
                    catch
                    {
                        playerStateAvailable =
                            false;

                        playerIsPlaying =
                            false;

                        playerTimeMs =
                            -1;
                    }
                }

                bool zlmFramesGrowing =
                    Interlocked.CompareExchange(
                        ref _zlmStreamHealthyFlags[
                            slot],
                        0,
                        0) == 1;

                bool terminalState =
                    playerStateAvailable &&
                    (playerState == VLCState.Ended ||
                     playerState == VLCState.Error ||
                     playerState == VLCState.Stopped);

                bool insideStartupGrace =
                    _lastPlayRequestUtc[slot] !=
                        DateTime.MinValue &&
                    (nowUtc -
                     _lastPlayRequestUtc[slot])
                        .TotalSeconds <
                    PlaybackStartupGraceSeconds;

                if (isVisible &&
                    IsSlotInCurrentGroup(slot) &&
                    !_groupSwitchInProgress &&
                    zlmFramesGrowing &&
                    terminalState &&
                    !insideStartupGrace)
                {
                    _terminalPlayerStateCounts[
                        slot]++;

                    if (_terminalPlayerStateCounts[
                            slot] >=
                        PlayerTerminalStateConfirmSamples)
                    {
                        int terminalCameraIndex =
                            _slotCameraIndexes[
                                slot];

                        AppLogger.Warn(
                            GetCameraLogName(
                                terminalCameraIndex) +
                            " VLC终止状态看门狗触发：当前可见组、ZLM帧正常，但播放器State=" +
                            playerState +
                            "，连续" +
                            _terminalPlayerStateCounts[slot] +
                            "次采样未恢复，自动重开该路VLC");

                        _terminalPlayerStateCounts[
                            slot] =
                            0;

                        UpdateSlotState(
                            slot,
                            terminalCameraIndex,
                            "vlc-reconnecting",
                            "客户端播放已结束，正在自动恢复...",
                            true);

                        RequestPlayerRestart(
                            slot,
                            terminalCameraIndex,
                            "VLC终止状态看门狗");
                    }

                    // 终止状态不再进入Playing冻结统计。
                    continue;
                }

                _terminalPlayerStateCounts[
                    slot] =
                    0;

                int decodedVideo =
                    0;

                int displayedPictures =
                    0;

                if (isVisible &&
                    playerIsPlaying &&
                    zlmFramesGrowing &&
                    media != null)
                {
                    try
                    {
                        MediaStats stats =
                            media.Statistics;

                        decodedVideo =
                            stats.DecodedVideo;

                        displayedPictures =
                            stats.DisplayedPictures;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            GetCameraLogName(
                                slot) +
                            " 读取VLC媒体统计失败：" +
                            ex.Message);
                    }
                }

                PlaybackHealthResult result =
                    _playbackHealthMonitor.Sample(
                        slot,
                        nowUtc,
                        isVisible,
                        playerIsPlaying,
                        zlmFramesGrowing,
                        decodedVideo,
                        displayedPictures,
                        playerTimeMs);

                if (result == null)
                {
                    continue;
                }

                int cameraIndex =
                    _slotCameraIndexes[
                        slot];

                if (!result.IsStalled)
                {
                    continue;
                }

                AppLogger.Warn(
                    GetCameraLogName(
                        cameraIndex) +
                    " VLC画面冻结看门狗触发：" +
                    "ZLM帧持续增长，VLC连续" +
                    ((int)result.NoProgressSeconds) +
                    "秒无新画面，PlayerTime=" +
                    result.PlayerTimeMs +
                    "ms，DecodedVideo=" +
                    result.DecodedVideo +
                    ", DisplayedPictures=" +
                    result.DisplayedPictures);

                UpdateSlotState(
                    slot,
                    cameraIndex,
                    "vlc-reconnecting",
                    "客户端播放卡住，正在自动恢复...",
                    true);

                // 只重开客户端Media，不动ZLM代理。
                RequestPlayerRestart(
                    slot,
                    cameraIndex,
                    "VLC画面冻结看门狗");
            }
        }


        // =========================================================
        // 每分钟健康摘要
        // =========================================================

        private void StartHealthSummaryTimer()
        {
            if (_healthSummaryTimer != null)
            {
                return;
            }

            System.Windows.Forms.Timer timer =
                new System.Windows.Forms.Timer();

            timer.Interval =
                HealthSummaryIntervalMs;

            timer.Tick +=
                delegate
                {
                    WriteHealthSummary();
                };

            _healthSummaryTimer =
                timer;

            timer.Start();

            AppLogger.Info(
                "长稳健康摘要已启用：每60秒记录当前组VLC/ZLM/内存状态");
        }


        private void WriteHealthSummary()
        {
            if (_closing)
            {
                return;
            }

            try
            {
                int startIndex =
                    _currentGroup == 1
                        ? 0
                        : 3;

                string summary =
                    "[HEALTH] Group=" +
                    _currentGroup;

                for (int slot = startIndex;
                     slot < startIndex + 3;
                     slot++)
                {
                    MediaPlayer player =
                        _mediaPlayers[
                            slot];

                    Media media =
                        _currentMedias[
                            slot];

                    string playerState =
                        "None";

                    long playerTimeMs =
                        -1;

                    int decodedVideo =
                        0;

                    int displayedPictures =
                        0;

                    try
                    {
                        if (player != null)
                        {
                            playerState =
                                player.State.ToString();

                            playerTimeMs =
                                player.Time;
                        }

                        if (media != null)
                        {
                            MediaStats stats =
                                media.Statistics;

                            decodedVideo =
                                stats.DecodedVideo;

                            displayedPictures =
                                stats.DisplayedPictures;
                        }
                    }
                    catch
                    {
                    }

                    bool zlmHealthy =
                        Interlocked.CompareExchange(
                            ref _zlmStreamHealthyFlags[
                                slot],
                            0,
                            0) == 1;

                    summary +=
                        " | C" +
                        (slot + 1).ToString("00") +
                        "=" +
                        playerState +
                        ",ZLM=" +
                        (zlmHealthy ? "OK" : "WAIT") +
                        ",T=" +
                        playerTimeMs +
                        ",D=" +
                        decodedVideo +
                        ",V=" +
                        displayedPictures;
                }

                long memoryMb =
                    System.Diagnostics.Process
                        .GetCurrentProcess()
                        .PrivateMemorySize64 /
                    1024 /
                    1024;

                summary +=
                    " | Memory=" +
                    memoryMb +
                    "MB";

                AppLogger.Info(
                    summary);
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "写入健康摘要失败：" +
                    ex.Message);
            }
        }


        // =========================================================
        // ZLM自动恢复
        // =========================================================

        /// <summary>
        /// 程序启动阶段的 ZLM 同步。
        ///
        /// 关键原则：
        /// 先准备当前可见组3路代理并立即启动VLC；
        /// 另一组3路随后在独立后台线程补齐，完成后才启动ZLM定时监控。
        /// 既避免离线隐藏摄像头阻塞首屏，也避免Timer与启动同步并发操作代理。
        /// </summary>
        private void PrepareInitialZlmInBackground()
        {
            if (_zlmService == null ||
                _zlmStreamGuard == null)
            {
                return;
            }

            bool fullSyncRequired =
                IsZlmFullSyncRequired();

            _zlmFullSyncPending =
                fullSyncRequired;

            bool zlmOnline = false;

            try
            {
                zlmOnline =
                    _zlmService
                        .IsServerAvailable();
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "启动阶段检测ZLMediaKit失败：" +
                    ex.Message);
            }

            if (!zlmOnline)
            {
                _zlmStateKnown =
                    true;

                _zlmWasOnline =
                    false;

                _initialZlmSyncCompleted =
                    false;

                AppLogger.Warn(
                    fullSyncRequired
                        ? "启动阶段ZLMediaKit离线，且检测到配置发生变化；保留全量同步任务，等待ZLM恢复后执行"
                        : "启动阶段ZLMediaKit离线，等待后台自动恢复");

                return;
            }

            int startupGroup =
                _currentGroup;

            CameraConfig[] visibleCameras =
                GetCameraGroupCameras(
                    startupGroup);

            try
            {
                if (fullSyncRequired)
                {
                    AppLogger.Info(
                        "启动阶段检测到视频源配置发生变化，优先同步当前可见组" +
                        startupGroup +
                        "（其余3路后台继续）");

                    _zlmStreamGuard
                        .SyncAllStreams(
                            visibleCameras);

                    AppLogger.Info(
                        "启动阶段当前组ZLM同步完成，先启动当前3路VLC");
                }
                else
                {
                    AppLogger.Info(
                        "启动阶段ZLM代理检查：配置未变化，优先Ensure当前可见组" +
                        startupGroup +
                        "的3路代理");

                    _zlmStreamGuard
                        .EnsureAllStreams(
                            visibleCameras);

                    AppLogger.Info(
                        "启动阶段当前组ZLM代理检查完成，先启动当前3路VLC");
                }

                // 这里表示“首屏所需代理已经准备完毕”。
                // 剩余3路由 QueueRemainingInitialZlmPreparation() 后台补齐。
                _initialZlmSyncCompleted =
                    true;
            }
            catch (Exception ex)
            {
                _initialZlmSyncCompleted =
                    false;

                // 配置变化场景只有剩余组也处理完后才清掉 pending。
                _zlmFullSyncPending =
                    fullSyncRequired;

                AppLogger.Error(
                    "启动阶段当前组ZLM代理准备异常，将继续启动并由后台自动恢复",
                    ex);
            }

            _zlmStateKnown =
                true;

            _zlmWasOnline =
                true;

            _zlmAvailabilityFailureCount =
                0;

            _zlmRecoveryGraceUntilUtc =
                DateTime.UtcNow.AddSeconds(
                    ZlmRecoveryGraceSeconds);
        }


        /// <summary>
        /// 首屏3路开始播放后，再后台补齐另一组3路ZLM代理。
        /// 这样 Camera05/06 离线时的HTTP超时不会继续阻塞首屏。
        /// </summary>
        private void QueueRemainingInitialZlmPreparation()
        {
            int startupGroup =
                _currentGroup;

            ThreadPool.QueueUserWorkItem(
                delegate
                {
                    try
                    {
                        PrepareRemainingInitialZlmInBackground(
                            startupGroup);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(
                            "启动阶段后台补齐剩余ZLM代理异常，交给定时监控继续恢复",
                            ex);
                    }
                    finally
                    {
                        SafeUi(
                            delegate
                            {
                                if (_closing)
                                {
                                    return;
                                }

                                // 剩余组的启动维护已经退出后再开Timer，
                                // 避免Timer与启动线程同时Ensure/Recreate同一代理。
                                StartZlmMonitor();
                            });
                    }
                });
        }


        private void PrepareRemainingInitialZlmInBackground(
            int startupGroup)
        {
            if (_zlmService == null ||
                _zlmStreamGuard == null)
            {
                return;
            }

            if (!_zlmStateKnown ||
                !_zlmWasOnline)
            {
                AppLogger.Info(
                    "启动阶段ZLM未确认在线，跳过隐藏组代理补齐，立即交给后台监控接管");

                return;
            }

            int remainingGroup =
                startupGroup == 1
                    ? 2
                    : 1;

            CameraConfig[] remainingCameras =
                GetCameraGroupCameras(
                    remainingGroup);

            if (_zlmFullSyncPending)
            {
                AppLogger.Info(
                    "首屏已启动，后台同步剩余摄像头组" +
                    remainingGroup +
                    "的3路ZLM代理");

                _zlmStreamGuard
                    .SyncAllStreams(
                        remainingCameras);

                // 保持原版本语义：SyncAllStreams完成一次尝试后即保存指纹。
                // 离线摄像头的代理后续会由Ensure按当前config.ini补回。
                SaveZlmConfigFingerprint();

                _zlmFullSyncPending =
                    false;

                AppLogger.Info(
                    "启动阶段剩余组ZLM同步完成，6路配置准备流程结束");
            }
            else
            {
                AppLogger.Info(
                    "首屏已启动，后台Ensure剩余摄像头组" +
                    remainingGroup +
                    "的3路ZLM代理");

                _zlmStreamGuard
                    .EnsureAllStreams(
                        remainingCameras);

                AppLogger.Info(
                    "启动阶段剩余组ZLM代理检查完成");
            }
        }


        private CameraConfig[] GetCameraGroupCameras(
            int group)
        {
            if (_config == null ||
                _config.Cameras == null ||
                group < 1 ||
                group > 2)
            {
                return new CameraConfig[0];
            }

            int startIndex =
                group == 1
                    ? 0
                    : 3;

            int availableCount =
                _config.Cameras.Count -
                startIndex;

            if (availableCount <= 0)
            {
                return new CameraConfig[0];
            }

            int count =
                Math.Min(
                    3,
                    availableCount);

            CameraConfig[] result =
                new CameraConfig[count];

            for (int i = 0;
                 i < count;
                 i++)
            {
                result[i] =
                    _config.Cameras[
                        startIndex + i];
            }

            return result;
        }


        private bool IsZlmFullSyncRequired()
        {
            try
            {
                string currentFingerprint =
                    ComputeZlmConfigFingerprint();

                string path =
                    GetZlmConfigFingerprintPath();

                if (!File.Exists(path))
                {
                    return true;
                }

                string savedFingerprint =
                    File.ReadAllText(path)
                        .Trim();

                return !string.Equals(
                    currentFingerprint,
                    savedFingerprint,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // 无法确认配置是否一致时宁可全量同步一次，避免旧账号/IP代理残留。
                AppLogger.Warn(
                    "读取ZLM配置指纹失败，将执行一次全量同步：" +
                    ex.Message);

                return true;
            }
        }


        private string ComputeZlmConfigFingerprint()
        {
            StringBuilder builder =
                new StringBuilder();

            builder.Append(
                (_config.ZlmHost ?? string.Empty).Trim());
            builder.Append('|');
            builder.Append(
                _config.ZlmHttpPort);
            builder.Append('|');
            builder.Append(
                _config.ZlmRtspPort);

            if (_config.Cameras != null)
            {
                for (int i = 0;
                     i < _config.Cameras.Count;
                     i++)
                {
                    CameraConfig camera =
                        _config.Cameras[i];

                    builder.Append('\n');
                    builder.Append(i);
                    builder.Append('|');

                    if (camera != null)
                    {
                        builder.Append(
                            camera.StreamId ?? string.Empty);
                        builder.Append('|');
                        builder.Append(
                            camera.SourceUrl ?? string.Empty);
                    }
                }
            }

            byte[] input =
                Encoding.UTF8.GetBytes(
                    builder.ToString());

            using (SHA256 sha =
                   SHA256.Create())
            {
                byte[] hash =
                    sha.ComputeHash(
                        input);

                StringBuilder result =
                    new StringBuilder(
                        hash.Length * 2);

                for (int i = 0;
                     i < hash.Length;
                     i++)
                {
                    result.Append(
                        hash[i].ToString(
                            "x2"));
                }

                return result.ToString();
            }
        }


        private string GetZlmConfigFingerprintPath()
        {
            return Path.Combine(
                Application.StartupPath,
                ZlmConfigFingerprintFileName);
        }


        private void SaveZlmConfigFingerprint()
        {
            try
            {
                File.WriteAllText(
                    GetZlmConfigFingerprintPath(),
                    ComputeZlmConfigFingerprint(),
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 只影响下次启动是否再次全量同步，不影响当前视频。
                AppLogger.Warn(
                    "保存ZLM配置指纹失败：" +
                    ex.Message);
            }
        }


        private void StartZlmMonitor()
        {
            _zlmTimer =
                new System.Threading.Timer(
                    ZlmTimerCallback,
                    null,
                    500,
                    MonitorIntervalMs);
        }


        private void ZlmTimerCallback(
            object state)
        {
            if (_closing)
            {
                return;
            }

            if (Interlocked.Exchange(
                    ref _zlmChecking,
                    1) == 1)
            {
                return;
            }

            try
            {
                if (_zlmService == null)
                {
                    return;
                }

                bool zlmOnline =
                    false;

                try
                {
                    zlmOnline =
                        _zlmService
                            .IsServerAvailable();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "ZLM健康探测异常：" +
                        ex.Message);
                }

                if (!zlmOnline)
                {
                    int failureCount =
                        Interlocked.Increment(
                            ref _zlmAvailabilityFailureCount);

                    // 一次HTTP超时并不能说明MediaServer真的挂了。
                    // 先暂停VLC画面冻结判定，避免把公共链路抖动误判成播放器问题。
                    MarkAllZlmStreamsUnhealthy();

                    if (failureCount <
                        ZlmOfflineConfirmFailures)
                    {
                        AppLogger.Warn(
                            "ZLM健康探测失败（" +
                            failureCount +
                            "/" +
                            ZlmOfflineConfirmFailures +
                            "），暂不判定视频服务离线");

                        return;
                    }

                    HandleZlmOffline();

                    return;
                }

                int previousFailures =
                    Interlocked.Exchange(
                        ref _zlmAvailabilityFailureCount,
                        0);

                bool recoveredFromOffline =
                    _zlmStateKnown &&
                    !_zlmWasOnline;

                if (previousFailures > 0 &&
                    !recoveredFromOffline)
                {
                    AppLogger.Info(
                        "ZLM健康探测已恢复，前一次为瞬时失败，不执行代理重建");
                }

                HandleZlmOnline();

                _monitorTick++;

                if (recoveredFromOffline)
                {
                    try
                    {
                        AppLogger.Info(
                            "ZLMediaKit恢复后开始补齐代理");

                        /*
                         * 普通恢复阶段只补缺失代理。
                         * 只有“配置指纹已变化且启动时没来得及应用”这一种情况，
                         * 才允许在恢复时做一次全量同步，随后马上重连当前3路VLC。
                         * 普通HTTP抖动绝不会触发全量重建。
                         */
                        if (_zlmFullSyncPending)
                        {
                            AppLogger.Info(
                                "ZLM恢复且存在待应用的视频源配置，先执行一次全量同步");

                            _zlmStreamGuard
                                .SyncAllStreams(
                                    _config.Cameras);

                            SaveZlmConfigFingerprint();

                            _zlmFullSyncPending =
                                false;
                        }
                        else
                        {
                            _zlmStreamGuard
                                .EnsureAllStreams(
                                    _config.Cameras);
                        }

                        _initialZlmSyncCompleted =
                            true;

                        _zlmRecoveryGraceUntilUtc =
                            DateTime.UtcNow.AddSeconds(
                                ZlmRecoveryGraceSeconds);

                        ResetAllStreamHealthCounters();

                        // 只有“已经连续确认离线”后又恢复，才主动重开VLC Session。
                        RestartAllPlayersAfterZlmSync(
                            "ZLM服务确认恢复后重新连接播放器");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(
                            "ZLM恢复后的代理补齐异常",
                            ex);
                    }
                }
                else if (_monitorTick % 2 == 0)
                {
                    try
                    {
                        // 正常运行时只补缺失代理；
                        // 401认证失败由ZlmStreamGuard内部降频处理。
                        _zlmStreamGuard
                            .EnsureAllStreams(
                                _config.Cameras);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(
                            "ZLM流检查异常",
                            ex);
                    }
                }

                // ZLM代理6路常驻，因此ZLM健康检测仍覆盖全部6路。
                for (int slot = 0;
                     slot < 6;
                     slot++)
                {
                    if (_closing)
                    {
                        return;
                    }

                    int cameraIndex =
                        slot;

                    if (_slotCameraIndexes[
                            slot] !=
                        cameraIndex)
                    {
                        continue;
                    }

                    CameraConfig camera =
                        _config.Cameras[
                            cameraIndex];

                    StreamHealthInfo health =
                        _zlmService
                            .GetStreamHealth(
                                camera.StreamId);

                    HandleStreamHealth(
                        slot,
                        cameraIndex,
                        camera,
                        health);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "ZLM后台检测异常",
                    ex);
            }
            finally
            {
                Interlocked.Exchange(
                    ref _zlmChecking,
                    0);
            }
        }


        // =========================================================
        // ZLM在线/离线
        // =========================================================

        private void HandleZlmOffline()
        {
            if (!_zlmStateKnown ||
                _zlmWasOnline)
            {
                AppLogger.Warn(
                    "ZLMediaKit 连续探测失败，已确认离线，等待自动恢复");
            }

            _zlmStateKnown =
                true;

            _zlmWasOnline =
                false;

            MarkAllZlmStreamsUnhealthy();

            for (int slot = 0;
                 slot < 6;
                 slot++)
            {
                int cameraIndex =
                    _slotCameraIndexes[
                        slot];

                SetSlotStateFromBackground(
                    slot,
                    cameraIndex,
                    "zlm-offline",
                    GetCameraLogName(
                        cameraIndex) +
                    " 视频服务离线，等待自动恢复...",
                    true);
            }
        }


        private void HandleZlmOnline()
        {
            if (!_zlmStateKnown)
            {
                AppLogger.Info(
                    "ZLMediaKit 在线");
            }
            else if (!_zlmWasOnline)
            {
                AppLogger.Info(
                    "ZLMediaKit 已恢复连接");
            }

            _zlmStateKnown =
                true;

            _zlmWasOnline =
                true;
        }


        /// <summary>
        /// ZLM一次公共恢复后清空旧的帧基线和异常计数。
        /// 后续各路重新采样，避免拿恢复前的frames和恢复后的新代理比较。
        /// </summary>
        private void ResetAllStreamHealthCounters()
        {
            for (int slot = 0;
                 slot < 6;
                 slot++)
            {
                _badStreamCounts[
                    slot] =
                    0;

                _lastZlmFrames[
                    slot] =
                    0;

                _stalledFrameCounts[
                    slot] =
                    0;

                Interlocked.Exchange(
                    ref _zlmStreamHealthyFlags[
                        slot],
                    0);

                if (_playbackHealthMonitor != null)
                {
                    _playbackHealthMonitor.Reset(
                        slot,
                        DateTime.UtcNow,
                        PlaybackStartupGraceSeconds);
                }
            }
        }


        private void MarkAllZlmStreamsUnhealthy()
        {
            for (int slot = 0;
                 slot < 6;
                 slot++)
            {
                Interlocked.Exchange(
                    ref _zlmStreamHealthyFlags[
                        slot],
                    0);
            }
        }


        private bool IsInZlmRecoveryGrace()
        {
            return DateTime.UtcNow <
                   _zlmRecoveryGraceUntilUtc;
        }


        // =========================================================
        // 单路视频健康检测
        // =========================================================

        private void HandleStreamHealth(
            int slot,
            int cameraIndex,
            CameraConfig camera,
            StreamHealthInfo health)
        {
            if (_slotCameraIndexes[
                    slot] !=
                cameraIndex)
            {
                return;
            }

            bool inRecoveryGrace =
                IsInZlmRecoveryGrace();

            // -----------------------------------------------------
            // 代理不存在
            // -----------------------------------------------------

            if (health == null ||
                !health.ProxyExists)
            {
                Interlocked.Exchange(
                    ref _zlmStreamHealthyFlags[
                        slot],
                    0);

                _badStreamCounts[
                    slot]++;

                _lastZlmFrames[
                    slot] =
                    0;

                _stalledFrameCounts[
                    slot] =
                    0;

                ZlmStreamGuardResult lastGuardResult =
                    _zlmStreamGuard != null
                        ? _zlmStreamGuard
                            .GetLastResult(
                                camera.StreamId)
                        : null;

                // ZLM已明确返回RTSP 401，显示认证错误而不是普通流异常。
                if (lastGuardResult != null &&
                    lastGuardResult.AuthFailed)
                {
                    SetSlotStateFromBackground(
                        slot,
                        cameraIndex,
                        "stream-auth-error",
                        "视频认证失败",
                        true);

                    _zlmStreamGuard
                        .EnsureStream(
                            camera,
                            false);

                    return;
                }

                SetSlotStateFromBackground(
                    slot,
                    cameraIndex,
                    "proxy-missing",
                    inRecoveryGrace
                        ? "视频服务刚恢复，正在重新建立视频流..."
                        : "视频流未建立，正在创建...",
                    true);

                try
                {
                    // Proxy缺失时允许Ensure创建；Ensure不会破坏已经存在的其他流。
                    ZlmStreamGuardResult createResult =
                        _zlmStreamGuard != null
                            ? _zlmStreamGuard
                                .EnsureStream(
                                    camera,
                                    false)
                            : null;

                    if (createResult != null &&
                        createResult.AuthFailed)
                    {
                        SetSlotStateFromBackground(
                            slot,
                            cameraIndex,
                            "stream-auth-error",
                            "视频认证失败",
                            true);
                    }
                    else if (createResult != null &&
                             createResult.Success)
                    {
                        AppLogger.Info(
                            GetCameraLogName(
                                cameraIndex) +
                            " 已重新创建ZLM代理");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(
                        GetCameraLogName(
                            cameraIndex) +
                        " 创建ZLM代理失败",
                        ex);
                }

                return;
            }

            // -----------------------------------------------------
            // 代理存在，但不是playing
            // -----------------------------------------------------

            if (!health.IsPlaying)
            {
                Interlocked.Exchange(
                    ref _zlmStreamHealthyFlags[
                        slot],
                    0);

                _badStreamCounts[
                    slot]++;

                _lastZlmFrames[
                    slot] =
                    0;

                _stalledFrameCounts[
                    slot] =
                    0;

                SetSlotStateFromBackground(
                    slot,
                    cameraIndex,
                    "stream-error",
                    inRecoveryGrace
                        ? camera.Name +
                          " 视频流正在恢复..."
                        : camera.Name +
                          " 视频流异常，正在重新连接...",
                    true);

                // ZLM刚恢复后的15秒内不做破坏性RecreateStream，
                // 防止代理尚在建立阶段就被我们再次删除重建。
                if (!inRecoveryGrace &&
                    _badStreamCounts[
                        slot] >=
                    BadStreamThreshold)
                {
                    TryRecreateProxy(
                        slot,
                        cameraIndex,
                        camera,
                        "ZLM连续无法进入playing状态");
                }

                return;
            }

            // -----------------------------------------------------
            // ZLM已经playing
            // -----------------------------------------------------

            if (_zlmStreamGuard != null)
            {
                _zlmStreamGuard
                    .MarkHealthy(
                        camera.StreamId);
            }

            _badStreamCounts[
                slot] =
                0;

            // -----------------------------------------------------
            // 检查ZLM帧数增长
            // -----------------------------------------------------

            bool zlmFramesGrowing =
                false;

            if (health.VideoFrames > 0)
            {
                long lastFrames =
                    _lastZlmFrames[
                        slot];

                if (lastFrames <= 0 ||
                    health.VideoFrames >
                    lastFrames)
                {
                    _stalledFrameCounts[
                        slot] =
                        0;

                    zlmFramesGrowing =
                        true;
                }
                else
                {
                    _stalledFrameCounts[
                        slot]++;
                }

                _lastZlmFrames[
                    slot] =
                    health.VideoFrames;
            }
            else
            {
                _stalledFrameCounts[
                    slot]++;
            }

            Interlocked.Exchange(
                ref _zlmStreamHealthyFlags[
                    slot],
                zlmFramesGrowing
                    ? 1
                    : 0);

            if (_stalledFrameCounts[
                    slot] >=
                StreamStallThreshold)
            {
                SetSlotStateFromBackground(
                    slot,
                    cameraIndex,
                    "stream-stalled",
                    inRecoveryGrace
                        ? camera.Name +
                          " 视频流正在恢复..."
                        : camera.Name +
                          " 视频流停滞，正在尝试恢复...",
                    true);

                if (!inRecoveryGrace)
                {
                    TryRecreateProxy(
                        slot,
                        cameraIndex,
                        camera,
                        "ZLM视频帧长时间未增长");
                }

                return;
            }

            // 后台组只维护ZLM代理健康，不启动/检查VLC。
            // 这样ZLM仍是6路常驻，但CameraMonitor本机始终只有当前3路解码。
            if (!IsSlotInCurrentGroup(
                    slot))
            {
                Interlocked.Exchange(
                    ref _vlcErrorFlags[
                        slot],
                    0);

                return;
            }

            // -----------------------------------------------------
            // VLC明确错误
            // -----------------------------------------------------

            if (Interlocked.Exchange(
                    ref _vlcErrorFlags[
                        slot],
                    0) == 1)
            {
                SetSlotStateFromBackground(
                    slot,
                    cameraIndex,
                    "vlc-reconnecting",
                    camera.Name +
                    " 视频播放异常，正在重新连接...",
                    true);

                RequestPlayerRestart(
                    slot,
                    cameraIndex,
                    "VLC EncounteredError");

                return;
            }

            // -----------------------------------------------------
            // ZLM正常，但VLC迟迟没进入Playing
            // -----------------------------------------------------

            if (Interlocked.CompareExchange(
                    ref _slotPlayedFlags[
                        slot],
                    0,
                    0) == 0)
            {
                DateTime requestTime =
                    _lastPlayRequestUtc[
                        slot];

                if (requestTime !=
                        DateTime.MinValue &&
                    (DateTime.UtcNow -
                     requestTime)
                        .TotalSeconds >=
                    PlayerStartupTimeoutSeconds)
                {
                    SetSlotStateFromBackground(
                        slot,
                        cameraIndex,
                        "vlc-not-playing",
                        camera.Name +
                        " 视频已就绪，正在重新打开播放器...",
                        false);

                    RequestPlayerRestart(
                        slot,
                        cameraIndex,
                        "ZLM正常但VLC未进入Playing");

                    return;
                }
            }

            // -----------------------------------------------------
            // 正常
            // -----------------------------------------------------

            SetSlotStateFromBackground(
                slot,
                cameraIndex,
                "playing",
                string.Empty,
                false);
        }


        // =========================================================
        // 强制重建ZLM代理
        // =========================================================

        private void TryRecreateProxy(
            int slot,
            int cameraIndex,
            CameraConfig camera,
            string reason)
        {
            DateTime now =
                DateTime.UtcNow;

            DateTime last =
                _lastProxyRecreateUtc[
                    slot];

            if (last !=
                    DateTime.MinValue &&
                (now - last)
                    .TotalSeconds <
                ProxyRecreateCooldownSeconds)
            {
                return;
            }

            _lastProxyRecreateUtc[
                slot] =
                now;

            try
            {
                AppLogger.Warn(
                    GetCameraLogName(
                        cameraIndex) +
                    " 强制重建ZLM代理，原因：" +
                    reason);

                ZlmStreamGuardResult recreateResult =
                    _zlmStreamGuard != null
                        ? _zlmStreamGuard
                            .RecreateStream(
                                camera)
                        : null;

                if (recreateResult != null &&
                    recreateResult.AuthFailed)
                {
                    SetSlotStateFromBackground(
                        slot,
                        cameraIndex,
                        "stream-auth-error",
                        "视频认证失败",
                        true);

                    return;
                }

                if (recreateResult != null &&
                    recreateResult.Success)
                {
                    _badStreamCounts[
                        slot] =
                        0;

                    _lastZlmFrames[
                        slot] =
                        0;

                    _stalledFrameCounts[
                        slot] =
                        0;

                    Interlocked.Exchange(
                        ref _zlmStreamHealthyFlags[
                            slot],
                        0);

                    Thread.Sleep(
                        300);

                    RequestPlayerRestart(
                        slot,
                        cameraIndex,
                        "ZLM代理已重建");
                }
                else
                {
                    AppLogger.Warn(
                        GetCameraLogName(
                            cameraIndex) +
                        " ZLM代理重建失败");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    GetCameraLogName(
                        cameraIndex) +
                    " RecreateStream异常",
                    ex);
            }
        }


        // =========================================================
        // VLC单路重连
        // =========================================================

        private void RequestPlayerRestart(
            int slot,
            int cameraIndex,
            string reason)
        {
            SafeUi(
                delegate
                {
                    if (_closing)
                    {
                        return;
                    }

                    if (_slotCameraIndexes[
                            slot] !=
                        cameraIndex)
                    {
                        return;
                    }

                    if (!IsSlotInCurrentGroup(
                            slot))
                    {
                        AppLogger.Info(
                            GetCameraLogName(
                                cameraIndex) +
                            " 后台组不启动VLC，忽略播放器重连请求，原因：" +
                            reason);

                        return;
                    }

                    if (_playerRecoveryManager == null)
                    {
                        _playerRecoveryManager =
                            new PlayerRecoveryManager(
                                6,
                                PlayerRestartCooldownSeconds,
                                300,
                                3,
                                PlayerRestartStaggerSeconds);
                    }

                    int recentRestartCount;
                    bool frequentRestart;

                    if (!_playerRecoveryManager
                            .TryBeginRestart(
                                slot,
                                DateTime.UtcNow,
                                out recentRestartCount,
                                out frequentRestart))
                    {
                        return;
                    }

                    if (frequentRestart)
                    {
                        AppLogger.Warn(
                            GetCameraLogName(
                                cameraIndex) +
                            " 5分钟内播放器已自动重连" +
                            recentRestartCount +
                            "次，请关注网络/GPU/解码稳定性");
                    }

                    string playUrl =
                        _config.GetPlayUrl(
                            _config.Cameras[
                                cameraIndex]);

                    UpdateSlotState(
                        slot,
                        cameraIndex,
                        "reconnecting",
                        GetCameraLogName(
                            cameraIndex) +
                        " 正在重新连接视频...",
                        false);

                    PlayCamera(
                        slot,
                        cameraIndex,
                        playUrl,
                        reason);
                });
        }


        /// <summary>
        /// ZLM 真正离线后恢复并完成代理补齐/必要的配置同步时，
        /// 主动重开当前可见组3路 VLC Session。
        ///
        /// 不能只等 EncounteredError：LibVLC 某些情况下仍保持 Playing，
        /// 但视频画面已经停在最后一帧。
        /// </summary>
        private void RestartAllPlayersAfterZlmSync(
            string reason)
        {
            SafeUi(
                delegate
                {
                    if (_closing)
                    {
                        return;
                    }

                    AppLogger.Info(
                        "ZLM服务确认恢复，主动重新打开当前3路VLC");

                    int startIndex =
                        _currentGroup == 1
                            ? 0
                            : 3;

                    for (int slot = startIndex;
                         slot < startIndex + 3;
                         slot++)
                    {
                        int cameraIndex =
                            _slotCameraIndexes[
                                slot];

                        if (_playerRecoveryManager != null)
                        {
                            _playerRecoveryManager
                                .ResetCooldown(
                                    slot);
                        }

                        if (_playbackHealthMonitor != null)
                        {
                            _playbackHealthMonitor.Reset(
                                slot,
                                DateTime.UtcNow,
                                PlaybackStartupGraceSeconds);
                        }

                        string playUrl =
                            _config.GetPlayUrl(
                                _config.Cameras[
                                    cameraIndex]);

                        UpdateSlotState(
                            slot,
                            cameraIndex,
                            "reconnecting",
                            "视频服务已恢复，正在重新连接...",
                            false);

                        PlayCamera(
                            slot,
                            cameraIndex,
                            playUrl,
                            reason);
                    }
                });
        }


        // =========================================================
        // 状态UI
        // =========================================================

        private void SetSlotStateFromBackground(
            int slot,
            int cameraIndex,
            string stateKey,
            string text,
            bool warning)
        {
            SafeUi(
                delegate
                {
                    UpdateSlotState(
                        slot,
                        cameraIndex,
                        stateKey,
                        text,
                        warning);
                });
        }


        private void UpdateSlotState(
            int slot,
            int cameraIndex,
            string stateKey,
            string text,
            bool warning)
        {
            /*
             * 视频层不再直接决定最终UI。
             *
             * 统一交给状态融合层：
             * Hikvision SDK + ZLMediaKit + VLC。
             */
            UpdateFusedSlotState(
                slot,
                cameraIndex,
                stateKey,
                text,
                warning);
        }


        // =========================================================
        // UI线程安全调用
        // =========================================================

        private void SafeUi(
            Action action)
        {
            if (action == null ||
                _closing ||
                IsDisposed)
            {
                return;
            }

            try
            {
                if (!IsHandleCreated)
                {
                    return;
                }

                if (InvokeRequired)
                {
                    BeginInvoke(
                        action);
                }
                else
                {
                    action();
                }
            }
            catch (ObjectDisposedException)
            {
                // 窗体正在关闭
            }
            catch (InvalidOperationException)
            {
                // Handle正在销毁
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "SafeUi执行失败：" +
                    ex.Message);
            }
        }


        // =========================================================
        // 摄像头日志名称
        // =========================================================

        private string GetCameraLogName(
            int cameraIndex)
        {
            try
            {
                if (_config != null &&
                    _config.Cameras != null &&
                    cameraIndex >= 0 &&
                    cameraIndex <
                    _config.Cameras.Count)
                {
                    CameraConfig camera =
                        _config.Cameras[
                            cameraIndex];

                    if (camera != null &&
                        !string.IsNullOrWhiteSpace(
                            camera.Name))
                    {
                        return camera.Name;
                    }

                    if (camera != null &&
                        !string.IsNullOrWhiteSpace(
                            camera.StreamId))
                    {
                        return camera.StreamId;
                    }
                }
            }
            catch
            {
                // 这里只做兜底，不调用日志，避免递归
            }

            return
                "Camera[" +
                cameraIndex +
                "]";
        }


        // =========================================================
        // 窗体事件
        // =========================================================

        private void MainForm_Resize(
            object sender,
            EventArgs e)
        {
            try
            {
                LayoutVideoPanels();
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "LayoutVideoPanels失败：" +
                    ex.Message);
            }
        }


        private void MainForm_KeyDown(
            object sender,
            KeyEventArgs e)
        {
            if (e.KeyCode ==
                Keys.Escape)
            {
                Close();
            }
        }


        // =========================================================
        // 关闭释放
        // =========================================================

        private void MainForm_FormClosed(
            object sender,
            FormClosedEventArgs e)
        {
            _closing =
                true;

            AppLogger.Info(
                "CameraMonitor 开始关闭");


            // -----------------------------------------------------
            // 停后台Timer
            // -----------------------------------------------------

            StopAndDisposeTimer(
                ref _groupSwitchTimer);

            StopAndDisposeTimer(
                ref _oldGroupStopTimer);

            StopAndDisposeTimer(
                ref _menuPrewarmCleanupTimer);

            StopAndDisposeTimer(
                ref _healthSummaryTimer);

            if (_playbackHealthTimer != null)
            {
                try
                {
                    _playbackHealthTimer.Stop();
                    _playbackHealthTimer.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "Playback Health Timer Dispose失败：" +
                        ex.Message);
                }

                _playbackHealthTimer =
                    null;
            }

            if (_zlmTimer != null)
            {
                try
                {
                    _zlmTimer.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "ZLM Timer Dispose失败：" +
                        ex.Message);
                }

                _zlmTimer =
                    null;
            }


            // -----------------------------------------------------
            // VLC
            // -----------------------------------------------------

            for (int i = 0;
                 i < 6;
                 i++)
            {
                if (_aspectRatioTimers[i] != null)
                {
                    try
                    {
                        _aspectRatioTimers[i]
                            .Stop();

                        _aspectRatioTimers[i]
                            .Dispose();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            "slot " +
                            i +
                            " AspectRatio Timer Dispose失败：" +
                            ex.Message);
                    }

                    _aspectRatioTimers[i] =
                        null;
                }

                if (_mediaPlayers[i] != null)
                {
                    try
                    {
                        _mediaPlayers[i]
                            .Stop();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            "slot " +
                            i +
                            " VLC Stop失败：" +
                            ex.Message);
                    }

                    try
                    {
                        _mediaPlayers[i]
                            .Dispose();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            "slot " +
                            i +
                            " MediaPlayer Dispose失败：" +
                            ex.Message);
                    }

                    _mediaPlayers[i] =
                        null;
                }

                if (_currentMedias[i] != null)
                {
                    try
                    {
                        _currentMedias[i]
                            .Dispose();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn(
                            "slot " +
                            i +
                            " Media Dispose失败：" +
                            ex.Message);
                    }

                    _currentMedias[i] =
                        null;
                }
            }


            if (_vlcLogService != null)
            {
                try
                {
                    _vlcLogService.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "VlcLogService Dispose失败：" +
                        ex.Message);
                }

                _vlcLogService =
                    null;
            }

            if (_libVLC != null)
            {
                try
                {
                    _libVLC.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn(
                        "LibVLC Dispose失败：" +
                        ex.Message);
                }

                _libVLC =
                    null;
            }

            AppLogger.Info(
                "CameraMonitor 已关闭");

            AppLogger.Shutdown();
        }
    }
}