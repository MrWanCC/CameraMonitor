using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace CameraMonitor
{
    /// <summary>
    /// CameraMonitor 高级设置。
    ///
    /// VS2015 / .NET Framework 4.5.2 / WinForms。
    /// 不依赖 Designer，不引入第三方 UI 库。
    /// </summary>
    public sealed class AdvancedSettingsForm : Form
    {
        // =========================================================
        // Design tokens
        // =========================================================
        private static readonly Color PageBackColor =
            Color.FromArgb(247, 249, 252);

        private static readonly Color CardColor =
            Color.White;

        private static readonly Color BorderColor =
            Color.FromArgb(222, 228, 236);

        private static readonly Color DividerColor =
            Color.FromArgb(235, 239, 244);

        private static readonly Color TextColor =
            Color.FromArgb(20, 32, 51);

        private static readonly Color SecondaryTextColor =
            Color.FromArgb(94, 108, 128);

        private static readonly Color MutedTextColor =
            Color.FromArgb(128, 141, 158);

        private static readonly Color PrimaryColor =
            Color.FromArgb(29, 93, 220);

        private static readonly Color PrimaryHoverColor =
            Color.FromArgb(23, 78, 187);

        private static readonly Color PrimarySoftColor =
            Color.FromArgb(239, 246, 255);

        private static readonly Color SuccessColor =
            Color.FromArgb(22, 154, 86);

        private static readonly Color SuccessSoftColor =
            Color.FromArgb(235, 249, 241);

        private static readonly Color WarningColor =
            Color.FromArgb(211, 115, 0);

        private static readonly Color WarningSoftColor =
            Color.FromArgb(255, 247, 230);

        private static readonly Color ErrorColor =
            Color.FromArgb(205, 53, 54);

        private static readonly Color ErrorSoftColor =
            Color.FromArgb(254, 239, 239);

        private const int FormRadius = 10;
        private const int CardRadius = 10;
        private const int InputRadius = 7;


        // =========================================================
        // State
        // =========================================================
        private readonly string _configPath;
        private AdvancedSettingsModel _model;

        private int _selectedCameraIndex = -1;
        private int _selectedPageIndex = 0;
        private bool _loadingCameraEditor;
        private bool _busy;

        public bool RestartRequested
        {
            get;
            private set;
        }


        // =========================================================
        // Root UI
        // =========================================================
        private Panel _pageHost;
        private Panel _cameraPage;
        private Panel _zlmPage;

        private NavItem _cameraNav;
        private NavItem _zlmNav;

        private ModernButton _saveButton;
        private ModernButton _cancelButton;


        // =========================================================
        // Camera UI
        // =========================================================
        private FlowLayoutPanel _cameraListFlow;

        private readonly List<CameraCardView> _cameraCards =
            new List<CameraCardView>();

        private Label _cameraTitleLabel;
        private Label _cameraSubtitleLabel;
        private StatusBadge _cameraStatusBadge;

        private TextBox _cameraNameTextBox;
        private TextBox _cameraStreamIdTextBox;
        private TextBox _cameraIpTextBox;
        private TextBox _cameraRtspPortTextBox;
        private TextBox _cameraChannelTextBox;
        private TextBox _cameraUserTextBox;
        private TextBox _cameraPasswordTextBox;
        private ModernButton _cameraPasswordToggleButton;

        private ModernButton _testCameraButton;
        private InlineNotice _cameraTestNotice;


        // =========================================================
        // ZLM UI
        // =========================================================
        private TextBox _zlmHostTextBox;
        private TextBox _zlmHttpPortTextBox;
        private TextBox _zlmRtspPortTextBox;
        private TextBox _zlmSecretTextBox;
        private ModernButton _zlmSecretToggleButton;
        private ModernButton _testZlmButton;
        private InlineNotice _zlmTestNotice;
        private StatusBadge _zlmStatusBadge;


        private sealed class CameraCardView
        {
            public int CameraIndex;
            public RoundedPanel Card;
            public IconGlyph Icon;
            public Label NameLabel;
            public Label IpLabel;
            public StatusBadge StatusBadge;
        }


        // =========================================================
        // ctor
        // =========================================================
        public AdvancedSettingsForm(
            string configPath)
        {
            _configPath =
                configPath;

            RestartRequested =
                false;

            InitializeWindow();
            BuildUi();

            Shown +=
                AdvancedSettingsForm_Shown;
        }


        private void InitializeWindow()
        {
            Text =
                "高级设置";

            StartPosition =
                FormStartPosition.CenterParent;

            FormBorderStyle =
                FormBorderStyle.Sizable;

            MinimizeBox =
                false;

            MaximizeBox =
                false;

            ShowInTaskbar =
                false;

            ClientSize =
                new Size(
                    960,
                    640);

            MinimumSize =
                new Size(
                    800,
                    560);

            Font =
                new Font(
                    "Microsoft YaHei UI",
                    9F,
                    FontStyle.Regular,
                    GraphicsUnit.Point);

            BackColor =
                PageBackColor;

            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer,
                true);

            DoubleBuffered =
                true;

            UpdateStyles();
        }


        private void ApplyAdaptiveWindowSize()
        {
            Screen screen =
                Screen.FromControl(this);

            Rectangle working =
                screen.WorkingArea;

            int availableWidth =
                Math.Max(760, working.Width - 32);

            int availableHeight =
                Math.Max(560, working.Height - 32);

            int targetWidth =
                Math.Min(960, availableWidth);

            int targetHeight =
                Math.Min(640, availableHeight);

            MinimumSize =
                new Size(
                    Math.Min(800, targetWidth),
                    Math.Min(560, targetHeight));

            Size =
                new Size(
                    targetWidth,
                    targetHeight);

            Left =
                working.Left +
                Math.Max(0, (working.Width - Width) / 2);

            Top =
                working.Top +
                Math.Max(0, (working.Height - Height) / 2);
        }


        // =========================================================
        // Root layout
        // =========================================================
        private void BuildUi()
        {
            SuspendLayout();

            try
            {
                Panel header =
                    BuildHeader();

                Panel nav =
                    BuildNavigation();

                Panel bottom =
                    BuildBottomBar();

                _pageHost =
                    new BufferedPanel();

                _pageHost.Dock =
                    DockStyle.Fill;

                _pageHost.BackColor =
                    PageBackColor;

                Controls.Add(
                    _pageHost);

                Controls.Add(
                    bottom);

                Controls.Add(
                    nav);

                Controls.Add(
                    header);

                BuildCameraPage();
                BuildZlmPage();
                ShowPage(0);
            }
            finally
            {
                ResumeLayout(true);
            }
        }


        private Panel BuildHeader()
        {
            Panel header =
                new BufferedPanel();

            header.Dock =
                DockStyle.Top;

            header.Height =
                52;

            header.BackColor =
                Color.White;

            header.Paint +=
                delegate (
                    object sender,
                    PaintEventArgs e)
                {
                    DrawBottomLine(
                        e.Graphics,
                        header);
                };

            IconGlyph icon =
                new IconGlyph();

            icon.Icon =
                UiIcon.Camera;

            icon.IconColor =
                TextColor;

            icon.Size =
                new Size(28, 28);

            icon.Location =
                new Point(28, 12);

            header.Controls.Add(
                icon);

            Label title =
                new Label();

            title.AutoSize =
                true;

            title.Text =
                "高级设置";

            title.ForeColor =
                TextColor;

            title.Font =
                new Font(
                    Font.FontFamily,
                    14F,
                    FontStyle.Bold);

            title.Location =
                new Point(74, 9);

            header.Controls.Add(
                title);

            Label subtitle =
                new Label();

            subtitle.AutoSize =
                true;

            subtitle.Text =
                "摄像头与视频服务配置";

            subtitle.ForeColor =
                SecondaryTextColor;

            subtitle.Font =
                new Font(
                    Font.FontFamily,
                    9F,
                    FontStyle.Regular);

            subtitle.Location =
                new Point(76, 33);

            header.Controls.Add(
                subtitle);

            return header;
        }


        private Panel BuildNavigation()
        {
            Panel nav =
                new BufferedPanel();

            nav.Dock =
                DockStyle.Top;

            nav.Height =
                44;

            nav.BackColor =
                Color.White;

            nav.Paint +=
                delegate (
                    object sender,
                    PaintEventArgs e)
                {
                    DrawBottomLine(
                        e.Graphics,
                        nav);
                };

            _cameraNav =
                new NavItem(
                    "摄像头",
                    UiIcon.Camera);

            _cameraNav.Location =
                new Point(28, 0);

            _cameraNav.Size =
                new Size(110, 44);

            _cameraNav.ItemClicked +=
                delegate
                {
                    if (TryCommitCurrentCamera())
                    {
                        ShowPage(0);
                    }
                };

            nav.Controls.Add(
                _cameraNav);

            _zlmNav =
                new NavItem(
                    "ZLMediaKit",
                    UiIcon.Server);

            _zlmNav.Location =
                new Point(150, 0);

            _zlmNav.Size =
                new Size(140, 44);

            _zlmNav.ItemClicked +=
                delegate
                {
                    if (TryCommitCurrentCamera())
                    {
                        ShowPage(1);
                    }
                };

            nav.Controls.Add(
                _zlmNav);

            return nav;
        }


        private Panel BuildBottomBar()
        {
            Panel bottom =
                new BufferedPanel();

            bottom.Dock =
                DockStyle.Bottom;

            bottom.Height =
                60;

            bottom.BackColor =
                Color.White;

            bottom.Paint +=
                delegate (
                    object sender,
                    PaintEventArgs e)
                {
                    using (Pen pen =
                           new Pen(DividerColor))
                    {
                        e.Graphics.DrawLine(
                            pen,
                            0,
                            0,
                            bottom.Width,
                            0);
                    }
                };

            IconGlyph hintIcon =
                new IconGlyph();

            hintIcon.Icon =
                UiIcon.Info;

            hintIcon.IconColor =
                PrimaryColor;

            hintIcon.Size =
                new Size(18, 18);

            hintIcon.Location =
                new Point(30, 21);

            bottom.Controls.Add(
                hintIcon);

            Label hint =
                new Label();

            hint.AutoSize =
                true;

            hint.Text =
                "保存后程序会自动重启，并使用新配置重新连接设备。";

            hint.ForeColor =
                SecondaryTextColor;

            hint.Location =
                new Point(56, 22);

            bottom.Controls.Add(
                hint);

            _saveButton =
                new ModernButton();

            _saveButton.Text =
                "保存并应用";

            _saveButton.Primary =
                true;

            _saveButton.Size =
                new Size(136, 36);

            _saveButton.Anchor =
                AnchorStyles.Top |
                AnchorStyles.Right;

            _saveButton.Location =
                new Point(
                    bottom.ClientSize.Width - 164,
                    12);

            _saveButton.Click +=
                SaveButton_Click;

            bottom.Controls.Add(
                _saveButton);

            _cancelButton =
                new ModernButton();

            _cancelButton.Text =
                "取消";

            _cancelButton.Size =
                new Size(92, 36);

            _cancelButton.Anchor =
                AnchorStyles.Top |
                AnchorStyles.Right;

            _cancelButton.Location =
                new Point(
                    bottom.ClientSize.Width - 264,
                    12);

            _cancelButton.Click +=
                delegate
                {
                    DialogResult =
                        DialogResult.Cancel;

                    Close();
                };

            bottom.Controls.Add(
                _cancelButton);

            bottom.Resize +=
                delegate
                {
                    _saveButton.Location =
                        new Point(
                            Math.Max(0,
                                bottom.ClientSize.Width - 164),
                            17);

                    _cancelButton.Location =
                        new Point(
                            Math.Max(0,
                                bottom.ClientSize.Width - 270),
                            17);
                };

            return bottom;
        }


        private void ShowPage(
            int pageIndex)
        {
            _selectedPageIndex =
                pageIndex == 1
                    ? 1
                    : 0;

            bool cameraSelected =
                _selectedPageIndex == 0;

            if (_cameraPage != null)
            {
                _cameraPage.Visible =
                    cameraSelected;
            }

            if (_zlmPage != null)
            {
                _zlmPage.Visible =
                    !cameraSelected;
            }

            if (_cameraNav != null)
            {
                _cameraNav.Selected =
                    cameraSelected;
            }

            if (_zlmNav != null)
            {
                _zlmNav.Selected =
                    !cameraSelected;
            }
        }


        // =========================================================
        // Camera page
        // =========================================================
        private void BuildCameraPage()
        {
            _cameraPage =
                new BufferedPanel();

            _cameraPage.Dock =
                DockStyle.Fill;

            _cameraPage.BackColor =
                PageBackColor;

            _cameraPage.Padding =
                new Padding(14, 12, 14, 12);

            _pageHost.Controls.Add(
                _cameraPage);

            TableLayoutPanel split =
                new TableLayoutPanel();

            split.Dock =
                DockStyle.Fill;

            split.BackColor =
                PageBackColor;

            split.ColumnCount =
                3;

            split.RowCount =
                1;

            split.Margin =
                new Padding(0);

            split.Padding =
                new Padding(0);

            split.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Absolute,
                    286F));

            split.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Absolute,
                    18F));

            split.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    100F));

            split.RowStyles.Clear();

            split.RowStyles.Add(
                new RowStyle(
                    SizeType.AutoSize));

            _cameraPage.Controls.Add(
                split);

            RoundedPanel listCard =
                CreateCard();

            listCard.Dock =
                DockStyle.Fill;

            listCard.Padding =
                new Padding(12, 12, 12, 10);

            split.Controls.Add(
                listCard,
                0,
                0);

            BuildCameraList(
                listCard);

            RoundedPanel detailCard =
                CreateCard();

            detailCard.Dock =
                DockStyle.Fill;

            detailCard.Padding =
                new Padding(18, 12, 18, 10);

            split.Controls.Add(
                detailCard,
                2,
                0);

            BuildCameraDetail(
                detailCard);
        }


        private void BuildCameraList(
            Control parent)
        {
            Panel header =
                new BufferedPanel();

            header.Dock =
                DockStyle.Top;

            header.Height =
                46;

            header.BackColor =
                Color.White;

            Label title =
                new Label();

            title.AutoSize =
                true;

            title.Text =
                "摄像头列表";

            title.ForeColor =
                TextColor;

            title.Font =
                new Font(
                    Font.FontFamily,
                    11F,
                    FontStyle.Bold);

            title.Location =
                new Point(2, 2);

            header.Controls.Add(
                title);

            Label subtitle =
                new Label();

            subtitle.AutoSize =
                true;

            subtitle.Text =
                "选择设备进行配置";

            subtitle.ForeColor =
                SecondaryTextColor;

            subtitle.Location =
                new Point(3, 25);

            header.Controls.Add(
                subtitle);

            parent.Controls.Add(
                header);

            _cameraListFlow =
                new BufferedFlowLayoutPanel();

            _cameraListFlow.Dock =
                DockStyle.Fill;

            _cameraListFlow.FlowDirection =
                FlowDirection.TopDown;

            _cameraListFlow.WrapContents =
                false;

            _cameraListFlow.AutoScroll =
                true;

            _cameraListFlow.HorizontalScroll.Enabled =
                false;

            _cameraListFlow.BackColor =
                Color.White;

            _cameraListFlow.Padding =
                new Padding(0, 4, 0, 0);

            parent.Controls.Add(
                _cameraListFlow);

            _cameraListFlow.BringToFront();

            _cameraListFlow.Resize +=
                delegate
                {
                    UpdateCameraCardWidths();
                };
        }


        private void BuildCameraDetail(
            Control parent)
        {
            Panel header =
                new BufferedPanel();

            header.Dock =
                DockStyle.Top;

            header.Height =
                58;

            header.BackColor =
                Color.White;

            _cameraTitleLabel =
                new Label();

            _cameraTitleLabel.AutoSize =
                true;

            _cameraTitleLabel.Text =
                "Camera 01";

            _cameraTitleLabel.ForeColor =
                TextColor;

            _cameraTitleLabel.Font =
                new Font(
                    Font.FontFamily,
                    14F,
                    FontStyle.Bold);

            _cameraTitleLabel.Location =
                new Point(0, 0);

            header.Controls.Add(
                _cameraTitleLabel);

            _cameraSubtitleLabel =
                new Label();

            _cameraSubtitleLabel.AutoSize =
                true;

            _cameraSubtitleLabel.Text =
                "设备信息 · SDK 端口 8000";

            _cameraSubtitleLabel.ForeColor =
                SecondaryTextColor;

            _cameraSubtitleLabel.Location =
                new Point(1, 30);

            header.Controls.Add(
                _cameraSubtitleLabel);

            _cameraStatusBadge =
                new StatusBadge();

            _cameraStatusBadge.SetState(
                BadgeState.Success,
                "配置正常");

            _cameraStatusBadge.Anchor =
                AnchorStyles.Top |
                AnchorStyles.Right;

            _cameraStatusBadge.Location =
                new Point(
                    Math.Max(0,
                        parent.ClientSize.Width - 120),
                    2);

            header.Controls.Add(
                _cameraStatusBadge);

            header.Resize +=
                delegate
                {
                    _cameraStatusBadge.Location =
                        new Point(
                            Math.Max(0,
                                header.ClientSize.Width -
                                _cameraStatusBadge.Width),
                            2);
                };

            parent.Controls.Add(
                header);

            Panel scroll =
                new BufferedPanel();

            scroll.Dock =
                DockStyle.Fill;

            scroll.BackColor =
                Color.White;

            scroll.AutoScroll =
                true;

            scroll.Padding =
                new Padding(0, 4, 0, 2);

            parent.Controls.Add(
                scroll);

            scroll.BringToFront();

            FlowLayoutPanel content =
                new BufferedFlowLayoutPanel();

            content.FlowDirection =
                FlowDirection.TopDown;

            content.WrapContents =
                false;

            content.AutoSize =
                true;

            content.AutoSizeMode =
                AutoSizeMode.GrowAndShrink;

            content.BackColor =
                Color.White;

            content.Margin =
                new Padding(0);

            content.Padding =
                new Padding(0);

            scroll.Controls.Add(
                content);

            RoundedPanel basic =
                CreateSectionCard(
                    "基本信息",
                    UiIcon.User);

            basic.Height =
                102;

            basic.Margin =
                new Padding(0, 0, 0, 6);

            BuildTwoColumnFields(
                basic,
                delegate (TableLayoutPanel grid)
                {
                    _cameraNameTextBox =
                        CreateTextBox();

                    grid.Controls.Add(
                        CreateField(
                            "名称",
                            CreateInputHost(
                                _cameraNameTextBox,
                                null)),
                        0,
                        0);

                    _cameraStreamIdTextBox =
                        CreateTextBox();

                    grid.Controls.Add(
                        CreateField(
                            "StreamId",
                            CreateInputHost(
                                _cameraStreamIdTextBox,
                                null)),
                        1,
                        0);
                });

            content.Controls.Add(
                basic);

            RoundedPanel network =
                CreateSectionCard(
                    "网络设置",
                    UiIcon.Globe);

            network.Height =
                160;

            network.Margin =
                new Padding(0, 0, 0, 6);

            BuildTwoColumnFields(
                network,
                delegate (TableLayoutPanel grid)
                {
                    grid.RowCount = 2;
                    grid.RowStyles.Add(
                        new RowStyle(
                            SizeType.Absolute,
                            58F));
                    grid.RowStyles.Add(
                        new RowStyle(
                            SizeType.Absolute,
                            58F));

                    _cameraIpTextBox =
                        CreateTextBox();

                    grid.Controls.Add(
                        CreateField(
                            "IP 地址",
                            CreateInputHost(
                                _cameraIpTextBox,
                                null)),
                        0,
                        0);

                    _cameraRtspPortTextBox =
                        CreateTextBox();

                    grid.Controls.Add(
                        CreateField(
                            "RTSP 端口",
                            CreateInputHost(
                                _cameraRtspPortTextBox,
                                null)),
                        1,
                        0);

                    _cameraChannelTextBox =
                        CreateTextBox();

                    grid.Controls.Add(
                        CreateField(
                            "通道",
                            CreateInputHost(
                                _cameraChannelTextBox,
                                null)),
                        0,
                        1);

                    grid.Controls.Add(
                        CreateField(
                            "SDK 端口",
                            CreateReadOnlyValue(
                                "8000",
                                "当前版本固定")),
                        1,
                        1);
                });

            content.Controls.Add(
                network);

            RoundedPanel credentials =
                CreateSectionCard(
                    "凭据信息",
                    UiIcon.Lock);

            credentials.Height =
                102;

            credentials.Margin =
                new Padding(0, 0, 0, 6);

            BuildTwoColumnFields(
                credentials,
                delegate (TableLayoutPanel grid)
                {
                    _cameraUserTextBox =
                        CreateTextBox();

                    grid.Controls.Add(
                        CreateField(
                            "账号",
                            CreateInputHost(
                                _cameraUserTextBox,
                                null)),
                        0,
                        0);

                    _cameraPasswordTextBox =
                        CreateTextBox();

                    _cameraPasswordTextBox.UseSystemPasswordChar =
                        true;

                    _cameraPasswordToggleButton =
                        CreateTextActionButton(
                            "显示");

                    _cameraPasswordToggleButton.Click +=
                        delegate
                        {
                            TogglePassword(
                                _cameraPasswordTextBox,
                                _cameraPasswordToggleButton);
                        };

                    grid.Controls.Add(
                        CreateField(
                            "密码",
                            CreateInputHost(
                                _cameraPasswordTextBox,
                                _cameraPasswordToggleButton)),
                        1,
                        0);
                });

            content.Controls.Add(
                credentials);

            Panel testRow =
                new BufferedPanel();

            testRow.Height =
                54;

            testRow.BackColor =
                Color.White;

            testRow.Margin =
                new Padding(0, 4, 0, 0);

            _cameraTestNotice =
                new InlineNotice();

            _cameraTestNotice.SetState(
                NoticeState.Info,
                "修改后可先测试连接，确认参数正确后再保存。");

            _cameraTestNotice.Anchor =
                AnchorStyles.Left |
                AnchorStyles.Top |
                AnchorStyles.Right;

            _cameraTestNotice.Location =
                new Point(0, 5);

            _cameraTestNotice.Height =
                42;

            testRow.Controls.Add(
                _cameraTestNotice);

            _testCameraButton =
                new ModernButton();

            _testCameraButton.Text =
                "测试连接";

            _testCameraButton.Size =
                new Size(132, 36);

            _testCameraButton.Anchor =
                AnchorStyles.Top |
                AnchorStyles.Right;

            _testCameraButton.Location =
                new Point(0, 6);

            _testCameraButton.Click +=
                TestCameraButton_Click;

            testRow.Controls.Add(
                _testCameraButton);

            testRow.Resize +=
                delegate
                {
                    _testCameraButton.Location =
                        new Point(
                            Math.Max(0,
                                testRow.ClientSize.Width -
                                _testCameraButton.Width),
                            6);

                    _cameraTestNotice.Width =
                        Math.Max(
                            220,
                            testRow.ClientSize.Width -
                            _testCameraButton.Width - 14);
                };

            content.Controls.Add(
                testRow);

            scroll.Resize +=
                delegate
                {
                    int width =
                        Math.Max(
                            520,
                            scroll.ClientSize.Width - 18);

                    content.Width =
                        width;

                    basic.Width =
                        width;

                    network.Width =
                        width;

                    credentials.Width =
                        width;

                    testRow.Width =
                        width;
                };
        }


        private void BuildTwoColumnFields(
            RoundedPanel section,
            Action<TableLayoutPanel> builder)
        {
            TableLayoutPanel grid =
                new TableLayoutPanel();

            grid.Dock =
                DockStyle.Fill;

            grid.Padding =
                new Padding(16, 40, 16, 4);

            grid.Margin =
                new Padding(0);

            grid.BackColor =
                Color.White;

            grid.ColumnCount =
                2;

            grid.RowCount =
                1;

            grid.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    50F));

            grid.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    50F));

            grid.RowStyles.Add(
                new RowStyle(
                    SizeType.Absolute,
                    58F));

            section.Controls.Add(
                grid);

            builder(
                grid);
        }


        // =========================================================
        // Camera cards — created once, then only updated.
        // =========================================================
        private void EnsureCameraCardsCreated()
        {
            if (_model == null ||
                _cameraListFlow == null ||
                _cameraCards.Count > 0)
            {
                return;
            }

            int width =
                GetCameraCardWidth();

            _cameraListFlow.SuspendLayout();

            try
            {
                for (int i = 0;
                     i < _model.Cameras.Count;
                     i++)
                {
                    CameraCardView view =
                        CreateCameraCard(
                            i,
                            width);

                    _cameraCards.Add(
                        view);

                    _cameraListFlow.Controls.Add(
                        view.Card);
                }
            }
            finally
            {
                _cameraListFlow.ResumeLayout(true);
            }

            // AutoScroll 的纵向滚动条只有在所有卡片加入后才能确定。
            // 先立即统一一次宽度，再延迟到本轮 WinForms 布局结束后
            // 再统一一次，避免最后一张卡片沿用滚动条出现前的旧宽度。
            _cameraListFlow.PerformLayout();
            UpdateCameraCardWidths();

            SafeBeginInvoke(
                delegate
                {
                    if (_cameraListFlow == null ||
                        _cameraListFlow.IsDisposed)
                    {
                        return;
                    }

                    _cameraListFlow.PerformLayout();
                    UpdateCameraCardWidths();
                    UpdateAllCameraCards();
                });
        }


        private CameraCardView CreateCameraCard(
            int cameraIndex,
            int width)
        {
            CameraCardView view =
                new CameraCardView();

            view.CameraIndex =
                cameraIndex;

            RoundedPanel card =
                new RoundedPanel();

            card.Width =
                width;

            card.Height =
                64;

            card.Radius =
                8;

            card.BorderWidth =
                1;

            card.BorderColor =
                BorderColor;

            card.FillColor =
                Color.White;

            card.Margin =
                new Padding(0, 0, 0, 8);

            card.Cursor =
                Cursors.Hand;

            card.Tag =
                cameraIndex;

            view.Card =
                card;

            IconGlyph icon =
                new IconGlyph();

            icon.Icon =
                UiIcon.Camera;

            icon.IconColor =
                SecondaryTextColor;

            icon.Location =
                new Point(12, 19);

            icon.Size =
                new Size(26, 26);

            icon.Tag =
                cameraIndex;

            card.Controls.Add(
                icon);

            view.Icon =
                icon;

            Label name =
                new Label();

            name.AutoSize =
                false;

            name.AutoEllipsis =
                true;

            name.ForeColor =
                TextColor;

            name.Font =
                new Font(
                    Font.FontFamily,
                    10F,
                    FontStyle.Bold);

            name.Location =
                new Point(48, 9);

            name.Size =
                new Size(
                    Math.Max(120, width - 72),
                    22);

            name.Tag =
                cameraIndex;

            card.Controls.Add(
                name);

            view.NameLabel =
                name;

            Label ip =
                new Label();

            ip.AutoSize =
                false;

            ip.ForeColor =
                SecondaryTextColor;

            ip.Location =
                new Point(48, 36);

            ip.Size =
                new Size(
                    Math.Max(70, width - 150),
                    18);

            ip.Tag =
                cameraIndex;

            card.Controls.Add(
                ip);

            view.IpLabel =
                ip;

            StatusBadge badge =
                new StatusBadge();

            badge.Tag =
                cameraIndex;

            badge.Anchor =
                AnchorStyles.Top |
                AnchorStyles.Right;

            badge.Location =
                new Point(
                    Math.Max(0, width - 84),
                    33);

            card.Controls.Add(
                badge);

            view.StatusBadge =
                badge;

            WireCameraCardClick(
                card,
                cameraIndex);

            return view;
        }


        private void WireCameraCardClick(
            Control control,
            int cameraIndex)
        {
            control.Tag =
                cameraIndex;

            control.Click +=
                CameraCard_Click;

            for (int i = 0;
                 i < control.Controls.Count;
                 i++)
            {
                WireCameraCardClick(
                    control.Controls[i],
                    cameraIndex);
            }
        }


        private void CameraCard_Click(
            object sender,
            EventArgs e)
        {
            if (_busy ||
                _loadingCameraEditor)
            {
                return;
            }

            Control control =
                sender as Control;

            if (control == null ||
                control.Tag == null)
            {
                return;
            }

            int index;

            try
            {
                index =
                    Convert.ToInt32(
                        control.Tag);
            }
            catch
            {
                return;
            }

            if (index ==
                _selectedCameraIndex)
            {
                return;
            }

            if (!TryCommitCurrentCamera())
            {
                return;
            }

            SelectCamera(
                index);
        }


        private void SelectCamera(
            int cameraIndex)
        {
            if (_model == null ||
                cameraIndex < 0 ||
                cameraIndex >= _model.Cameras.Count)
            {
                return;
            }

            _selectedCameraIndex =
                cameraIndex;

            LoadCameraEditor(
                cameraIndex);

            UpdateAllCameraCards();
            UpdateSelectedCameraBadge();

            _cameraTestNotice.SetState(
                NoticeState.Info,
                "修改后可先测试连接，确认参数正确后再保存。");
        }


        private void UpdateAllCameraCards()
        {
            if (_model == null)
            {
                return;
            }

            for (int i = 0;
                 i < _cameraCards.Count &&
                 i < _model.Cameras.Count;
                 i++)
            {
                CameraCardView view =
                    _cameraCards[i];

                CameraEditorItem camera =
                    _model.Cameras[i];

                bool selected =
                    i == _selectedCameraIndex;

                view.NameLabel.Text =
                    string.IsNullOrWhiteSpace(camera.Name)
                        ? "Camera " + (i + 1).ToString("00")
                        : camera.Name;

                view.IpLabel.Text =
                    string.IsNullOrWhiteSpace(camera.IpAddress)
                        ? "未配置 IP"
                        : camera.IpAddress;

                view.Card.FillColor =
                    selected
                        ? PrimarySoftColor
                        : Color.White;

                view.Card.BorderColor =
                    selected
                        ? Color.FromArgb(186, 210, 250)
                        : BorderColor;

                view.Card.BorderWidth =
                    selected
                        ? 1
                        : 1;

                view.Icon.IconColor =
                    selected
                        ? PrimaryColor
                        : SecondaryTextColor;

                view.StatusBadge.SetState(
                    BadgeState.Success,
                    "正常");

                view.StatusBadge.Location =
                    new Point(
                        Math.Max(
                            0,
                            view.Card.Width -
                            view.StatusBadge.Width - 12),
                        33);

                view.NameLabel.Width =
                    Math.Max(
                        120,
                        view.Card.Width - 72);

                view.IpLabel.Width =
                    Math.Max(
                        70,
                        view.Card.Width - 56 -
                        view.StatusBadge.Width - 24);

                view.Card.Invalidate();
            }
        }


        private void UpdateSelectedCameraBadge()
        {
            if (_model == null ||
                _selectedCameraIndex < 0 ||
                _selectedCameraIndex >= _model.Cameras.Count)
            {
                return;
            }

            CameraEditorItem camera =
                _model.Cameras[
                    _selectedCameraIndex];

            _cameraStatusBadge.SetState(
                BadgeState.Success,
                "配置正常");
        }


        private void UpdateCameraCardWidths()
        {
            if (_cameraListFlow == null)
            {
                return;
            }

            int width =
                GetCameraCardWidth();

            for (int i = 0;
                 i < _cameraCards.Count;
                 i++)
            {
                CameraCardView view =
                    _cameraCards[i];

                view.Card.Width =
                    width;

                view.NameLabel.Width =
                    Math.Max(120, width - 72);

                view.StatusBadge.Location =
                    new Point(
                        Math.Max(
                            0,
                            width -
                            view.StatusBadge.Width - 12),
                        33);

                view.IpLabel.Width =
                    Math.Max(
                        70,
                        width - 56 -
                        view.StatusBadge.Width - 24);
            }
        }


        private int GetCameraCardWidth()
        {
            if (_cameraListFlow == null)
            {
                return 260;
            }

            return Math.Max(
                180,
                _cameraListFlow.ClientSize.Width -
                SystemInformation.VerticalScrollBarWidth - 4);
        }


        // =========================================================
        // ZLM page
        // =========================================================
        private void BuildZlmPage()
        {
            _zlmPage =
                new BufferedPanel();

            _zlmPage.Dock =
                DockStyle.Fill;

            _zlmPage.BackColor =
                PageBackColor;

            _zlmPage.Padding =
                new Padding(14, 12, 14, 12);

            _pageHost.Controls.Add(
                _zlmPage);

            TableLayoutPanel center =
                new TableLayoutPanel();

            center.Dock =
                DockStyle.Fill;

            center.BackColor =
                PageBackColor;

            center.ColumnCount =
                3;

            center.RowCount =
                3;

            center.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    12F));

            center.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    76F));

            center.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    12F));

            center.RowStyles.Add(
                new RowStyle(
                    SizeType.Percent,
                    4F));

            center.RowStyles.Add(
                new RowStyle(
                    SizeType.Percent,
                    92F));

            center.RowStyles.Add(
                new RowStyle(
                    SizeType.Percent,
                    4F));

            _zlmPage.Controls.Add(
                center);

            RoundedPanel card =
                CreateCard();

            card.Dock =
                DockStyle.Fill;

            card.Padding =
                new Padding(22, 14, 22, 14);

            center.Controls.Add(
                card,
                1,
                1);

            BuildZlmDetail(
                card);
        }


        private void BuildZlmDetail(
            Control parent)
        {
            Panel header =
                new BufferedPanel();

            header.Dock =
                DockStyle.Top;

            header.Height =
                58;

            header.BackColor =
                Color.White;

            IconGlyph icon =
                new IconGlyph();

            icon.Icon =
                UiIcon.Server;

            icon.IconColor =
                PrimaryColor;

            icon.Location =
                new Point(0, 2);

            icon.Size =
                new Size(32, 32);

            header.Controls.Add(
                icon);

            Label title =
                new Label();

            title.AutoSize =
                true;

            title.Text =
                "ZLMediaKit";

            title.ForeColor =
                TextColor;

            title.Font =
                new Font(
                    Font.FontFamily,
                    14F,
                    FontStyle.Bold);

            title.Location =
                new Point(44, 0);

            header.Controls.Add(
                title);

            Label subtitle =
                new Label();

            subtitle.AutoSize =
                true;

            subtitle.Text =
                "视频服务连接与 API 配置";

            subtitle.ForeColor =
                SecondaryTextColor;

            subtitle.Location =
                new Point(45, 31);

            header.Controls.Add(
                subtitle);

            _zlmStatusBadge =
                new StatusBadge();

            _zlmStatusBadge.SetState(
                BadgeState.Neutral,
                "未测试");

            _zlmStatusBadge.Anchor =
                AnchorStyles.Top |
                AnchorStyles.Right;

            header.Controls.Add(
                _zlmStatusBadge);

            header.Resize +=
                delegate
                {
                    _zlmStatusBadge.Location =
                        new Point(
                            Math.Max(0,
                                header.ClientSize.Width -
                                _zlmStatusBadge.Width),
                            6);
                };

            parent.Controls.Add(
                header);

            Panel scroll =
                new BufferedPanel();

            scroll.Dock =
                DockStyle.Fill;

            scroll.BackColor =
                Color.White;

            scroll.AutoScroll =
                true;

            parent.Controls.Add(
                scroll);

            scroll.BringToFront();

            FlowLayoutPanel content =
                new BufferedFlowLayoutPanel();

            content.AutoSize =
                true;

            content.AutoSizeMode =
                AutoSizeMode.GrowAndShrink;

            content.FlowDirection =
                FlowDirection.TopDown;

            content.WrapContents =
                false;

            content.BackColor =
                Color.White;

            content.Margin =
                new Padding(0);

            scroll.Controls.Add(
                content);

            RoundedPanel address =
                CreateSectionCard(
                    "服务地址",
                    UiIcon.Globe);

            address.Height =
                106;

            address.Margin =
                new Padding(0, 0, 0, 6);

            TableLayoutPanel addressGrid =
                CreateGrid(1);

            address.Controls.Add(
                addressGrid);

            _zlmHostTextBox =
                CreateTextBox();

            addressGrid.Controls.Add(
                CreateField(
                    "Host / IP 地址",
                    CreateInputHost(
                        _zlmHostTextBox,
                        null)),
                0,
                0);

            addressGrid.SetColumnSpan(
                addressGrid.GetControlFromPosition(0, 0),
                2);

            content.Controls.Add(
                address);

            RoundedPanel ports =
                CreateSectionCard(
                    "访问端口",
                    UiIcon.Network);

            ports.Height =
                106;

            ports.Margin =
                new Padding(0, 0, 0, 6);

            TableLayoutPanel portsGrid =
                CreateGrid(1);

            ports.Controls.Add(
                portsGrid);

            _zlmHttpPortTextBox =
                CreateTextBox();

            portsGrid.Controls.Add(
                CreateField(
                    "HTTP 端口",
                    CreateInputHost(
                        _zlmHttpPortTextBox,
                        null)),
                0,
                0);

            _zlmRtspPortTextBox =
                CreateTextBox();

            portsGrid.Controls.Add(
                CreateField(
                    "RTSP 端口",
                    CreateInputHost(
                        _zlmRtspPortTextBox,
                        null)),
                1,
                0);

            content.Controls.Add(
                ports);

            RoundedPanel api =
                CreateSectionCard(
                    "API 凭据",
                    UiIcon.Lock);

            api.Height =
                106;

            api.Margin =
                new Padding(0, 0, 0, 6);

            TableLayoutPanel apiGrid =
                CreateGrid(1);

            api.Controls.Add(
                apiGrid);

            _zlmSecretTextBox =
                CreateTextBox();

            _zlmSecretTextBox.UseSystemPasswordChar =
                true;

            _zlmSecretToggleButton =
                CreateTextActionButton(
                    "显示");

            _zlmSecretToggleButton.Click +=
                delegate
                {
                    TogglePassword(
                        _zlmSecretTextBox,
                        _zlmSecretToggleButton);
                };

            apiGrid.Controls.Add(
                CreateField(
                    "API Secret",
                    CreateInputHost(
                        _zlmSecretTextBox,
                        _zlmSecretToggleButton)),
                0,
                0);

            apiGrid.SetColumnSpan(
                apiGrid.GetControlFromPosition(0, 0),
                2);

            content.Controls.Add(
                api);

            Panel testRow =
                new BufferedPanel();

            testRow.Height =
                62;

            testRow.BackColor =
                Color.White;

            testRow.Margin =
                new Padding(0, 4, 0, 0);

            _zlmTestNotice =
                new InlineNotice();

            _zlmTestNotice.SetState(
                NoticeState.Info,
                "修改服务地址或端口后，可先测试连接。");

            _zlmTestNotice.Location =
                new Point(0, 7);

            _zlmTestNotice.Height =
                46;

            testRow.Controls.Add(
                _zlmTestNotice);

            _testZlmButton =
                new ModernButton();

            _testZlmButton.Text =
                "测试连接";

            _testZlmButton.Size =
                new Size(132, 42);

            _testZlmButton.Click +=
                TestZlmButton_Click;

            testRow.Controls.Add(
                _testZlmButton);

            testRow.Resize +=
                delegate
                {
                    _testZlmButton.Location =
                        new Point(
                            Math.Max(0,
                                testRow.ClientSize.Width -
                                _testZlmButton.Width),
                            9);

                    _zlmTestNotice.Width =
                        Math.Max(
                            220,
                            testRow.ClientSize.Width -
                            _testZlmButton.Width - 14);
                };

            content.Controls.Add(
                testRow);

            scroll.Resize +=
                delegate
                {
                    int width =
                        Math.Max(
                            560,
                            scroll.ClientSize.Width - 18);

                    content.Width =
                        width;

                    address.Width =
                        width;

                    ports.Width =
                        width;

                    api.Width =
                        width;

                    testRow.Width =
                        width;
                };
        }


        private TableLayoutPanel CreateGrid(
            int rows)
        {
            TableLayoutPanel grid =
                new TableLayoutPanel();

            grid.Dock =
                DockStyle.Fill;

            grid.BackColor =
                Color.White;

            grid.Padding =
                new Padding(16, 40, 16, 4);

            grid.ColumnCount =
                2;

            grid.RowCount =
                rows;

            grid.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    50F));

            grid.ColumnStyles.Add(
                new ColumnStyle(
                    SizeType.Percent,
                    50F));

            for (int i = 0;
                 i < rows;
                 i++)
            {
                grid.RowStyles.Add(
                    new RowStyle(
                        SizeType.Absolute,
                        58F));
            }

            return grid;
        }


        // =========================================================
        // Model load / editor load
        // =========================================================
        private void AdvancedSettingsForm_Shown(
            object sender,
            EventArgs e)
        {
            ApplyAdaptiveWindowSize();

            try
            {
                _model =
                    CameraConfigEditor.Load(
                        _configPath);

                if (_model == null ||
                    _model.Cameras == null ||
                    _model.Cameras.Count == 0)
                {
                    throw new InvalidOperationException(
                        "没有读取到摄像头配置。");
                }

                EnsureCameraCardsCreated();
                LoadZlmEditor();
                SelectCamera(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "读取配置失败：\r\n" +
                    ex.Message,
                    "高级设置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                DialogResult =
                    DialogResult.Cancel;

                Close();
            }
        }


        private void LoadCameraEditor(
            int cameraIndex)
        {
            if (_model == null ||
                cameraIndex < 0 ||
                cameraIndex >= _model.Cameras.Count)
            {
                return;
            }

            _loadingCameraEditor =
                true;

            try
            {
                CameraEditorItem camera =
                    _model.Cameras[
                        cameraIndex];

                string displayName =
                    string.IsNullOrWhiteSpace(camera.Name)
                        ? "Camera " + (cameraIndex + 1).ToString("00")
                        : camera.Name;

                _cameraTitleLabel.Text =
                    displayName;

                _cameraSubtitleLabel.Text =
                    "设备 " +
                    (cameraIndex + 1).ToString("00") +
                    " · SDK 端口 8000";

                _cameraNameTextBox.Text =
                    camera.Name;

                _cameraStreamIdTextBox.Text =
                    camera.StreamId;

                _cameraIpTextBox.Text =
                    camera.IpAddress;

                _cameraRtspPortTextBox.Text =
                    camera.RtspPort.ToString();

                _cameraChannelTextBox.Text =
                    camera.Channel.ToString();

                _cameraUserTextBox.Text =
                    camera.UserName;

                _cameraPasswordTextBox.Text =
                    camera.Password;

                _cameraPasswordTextBox.UseSystemPasswordChar =
                    true;

                _cameraPasswordToggleButton.Text =
                    "显示";
            }
            finally
            {
                _loadingCameraEditor =
                    false;
            }
        }


        private void LoadZlmEditor()
        {
            if (_model == null ||
                _model.Zlm == null)
            {
                return;
            }

            _zlmHostTextBox.Text =
                _model.Zlm.Host;

            _zlmHttpPortTextBox.Text =
                _model.Zlm.HttpPort.ToString();

            _zlmRtspPortTextBox.Text =
                _model.Zlm.RtspPort.ToString();

            _zlmSecretTextBox.Text =
                _model.Zlm.Secret;

            _zlmSecretTextBox.UseSystemPasswordChar =
                true;

            _zlmSecretToggleButton.Text =
                "显示";

            _zlmStatusBadge.SetState(
                BadgeState.Neutral,
                "未测试");
        }


        // =========================================================
        // Apply UI -> model
        // =========================================================
        private bool TryCommitCurrentCamera()
        {
            if (_model == null ||
                _selectedCameraIndex < 0 ||
                _selectedCameraIndex >= _model.Cameras.Count)
            {
                return true;
            }

            string error;

            if (!TryApplyCameraEditor(
                    _selectedCameraIndex,
                    out error))
            {
                MessageBox.Show(
                    this,
                    error,
                    "摄像头配置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return false;
            }

            return true;
        }


        private bool TryApplyCameraEditor(
            int cameraIndex,
            out string error)
        {
            error =
                string.Empty;

            if (_model == null ||
                cameraIndex < 0 ||
                cameraIndex >= _model.Cameras.Count)
            {
                error =
                    "没有选中摄像头。";

                return false;
            }

            int rtspPort;
            int channel;

            if (!TryParseNumber(
                    _cameraRtspPortTextBox.Text,
                    1,
                    65535,
                    "RTSP 端口",
                    out rtspPort,
                    out error))
            {
                return false;
            }

            if (!TryParseNumber(
                    _cameraChannelTextBox.Text,
                    1,
                    999999,
                    "通道",
                    out channel,
                    out error))
            {
                return false;
            }

            CameraEditorItem camera =
                _model.Cameras[
                    cameraIndex];

            camera.Name =
                _cameraNameTextBox.Text.Trim();

            camera.StreamId =
                _cameraStreamIdTextBox.Text.Trim();

            camera.IpAddress =
                _cameraIpTextBox.Text.Trim();

            camera.RtspPort =
                rtspPort;

            camera.Channel =
                channel;

            camera.UserName =
                _cameraUserTextBox.Text.Trim();

            camera.Password =
                _cameraPasswordTextBox.Text;

            error =
                CameraConfigEditor.ValidateCamera(
                    camera);

            if (!string.IsNullOrEmpty(error))
            {
                return false;
            }

            UpdateAllCameraCards();
            UpdateSelectedCameraBadge();

            _cameraTitleLabel.Text =
                string.IsNullOrWhiteSpace(camera.Name)
                    ? "Camera " + (cameraIndex + 1).ToString("00")
                    : camera.Name;

            return true;
        }


        private bool TryApplyZlmEditor(
            out string error)
        {
            error =
                string.Empty;

            if (_model == null ||
                _model.Zlm == null)
            {
                error =
                    "ZLMediaKit 配置未加载。";

                return false;
            }

            int httpPort;
            int rtspPort;

            if (!TryParseNumber(
                    _zlmHttpPortTextBox.Text,
                    1,
                    65535,
                    "HTTP 端口",
                    out httpPort,
                    out error))
            {
                return false;
            }

            if (!TryParseNumber(
                    _zlmRtspPortTextBox.Text,
                    1,
                    65535,
                    "RTSP 端口",
                    out rtspPort,
                    out error))
            {
                return false;
            }

            _model.Zlm.Host =
                _zlmHostTextBox.Text.Trim();

            _model.Zlm.HttpPort =
                httpPort;

            _model.Zlm.RtspPort =
                rtspPort;

            _model.Zlm.Secret =
                _zlmSecretTextBox.Text;

            error =
                CameraConfigEditor.ValidateZlm(
                    _model.Zlm);

            return string.IsNullOrEmpty(
                error);
        }


        private static bool TryParseNumber(
            string text,
            int min,
            int max,
            string fieldName,
            out int value,
            out string error)
        {
            value =
                0;

            error =
                string.Empty;

            if (!int.TryParse(
                    (text ?? string.Empty).Trim(),
                    out value) ||
                value < min ||
                value > max)
            {
                error =
                    fieldName +
                    "必须是 " +
                    min +
                    " ~ " +
                    max +
                    " 之间的整数。";

                return false;
            }

            return true;
        }


        // =========================================================
        // Test connection
        // =========================================================
        private void TestCameraButton_Click(
            object sender,
            EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            if (!TryCommitCurrentCamera())
            {
                return;
            }

            string zlmError;

            if (!TryApplyZlmEditor(
                    out zlmError))
            {
                _cameraTestNotice.SetState(
                    NoticeState.Error,
                    "ZLMediaKit 配置有误：" + zlmError);

                return;
            }

            CameraEditorItem camera =
                CloneCamera(
                    _model.Cameras[
                        _selectedCameraIndex]);

            ZlmEditorSettings zlm =
                CloneZlm(
                    _model.Zlm);

            SetBusy(
                true);

            _cameraTestNotice.SetState(
                NoticeState.Info,
                "正在测试设备连接与 RTSP 认证...");

            ThreadPool.QueueUserWorkItem(
                delegate
                {
                    ConnectionTestResult result =
                        CameraConfigEditor.TestCamera(
                            camera,
                            zlm);

                    SafeBeginInvoke(
                        delegate
                        {
                            SetBusy(false);

                            _cameraTestNotice.SetState(
                                result.Success
                                    ? NoticeState.Success
                                    : NoticeState.Error,
                                result.Message);
                        });
                });
        }


        private void TestZlmButton_Click(
            object sender,
            EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            string error;

            if (!TryApplyZlmEditor(
                    out error))
            {
                _zlmTestNotice.SetState(
                    NoticeState.Error,
                    error);

                _zlmStatusBadge.SetState(
                    BadgeState.Error,
                    "配置错误");

                return;
            }

            ZlmEditorSettings zlm =
                CloneZlm(
                    _model.Zlm);

            SetBusy(
                true);

            _zlmTestNotice.SetState(
                NoticeState.Info,
                "正在连接 ZLMediaKit...");

            _zlmStatusBadge.SetState(
                BadgeState.Neutral,
                "测试中");

            ThreadPool.QueueUserWorkItem(
                delegate
                {
                    ConnectionTestResult result =
                        CameraConfigEditor.TestZlm(
                            zlm);

                    SafeBeginInvoke(
                        delegate
                        {
                            SetBusy(false);

                            _zlmTestNotice.SetState(
                                result.Success
                                    ? NoticeState.Success
                                    : NoticeState.Error,
                                result.Message);

                            _zlmStatusBadge.SetState(
                                result.Success
                                    ? BadgeState.Success
                                    : BadgeState.Error,
                                result.Success
                                    ? "服务正常"
                                    : "连接失败");
                        });
                });
        }


        // =========================================================
        // Save
        // =========================================================
        private void SaveButton_Click(
            object sender,
            EventArgs e)
        {
            if (_busy)
            {
                return;
            }

            if (!TryCommitCurrentCamera())
            {
                return;
            }

            string zlmError;

            if (!TryApplyZlmEditor(
                    out zlmError))
            {
                ShowPage(1);

                MessageBox.Show(
                    this,
                    zlmError,
                    "ZLMediaKit 配置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            try
            {
                CameraConfigEditor.Save(
                    _model);

                RestartRequested =
                    true;

                DialogResult =
                    DialogResult.OK;

                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "保存配置失败：\r\n" +
                    ex.Message,
                    "高级设置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }


        // =========================================================
        // Helpers
        // =========================================================
        private void SetBusy(
            bool busy)
        {
            _busy =
                busy;

            if (_testCameraButton != null)
            {
                _testCameraButton.Enabled =
                    !busy;
            }

            if (_testZlmButton != null)
            {
                _testZlmButton.Enabled =
                    !busy;
            }

            if (_saveButton != null)
            {
                _saveButton.Enabled =
                    !busy;
            }

            if (_cancelButton != null)
            {
                _cancelButton.Enabled =
                    !busy;
            }

            Cursor =
                busy
                    ? Cursors.WaitCursor
                    : Cursors.Default;
        }


        private void SafeBeginInvoke(
            MethodInvoker action)
        {
            if (action == null ||
                IsDisposed ||
                !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke(
                    action);
            }
            catch
            {
            }
        }


        private static CameraEditorItem CloneCamera(
            CameraEditorItem source)
        {
            CameraEditorItem copy =
                new CameraEditorItem();

            copy.Index = source.Index;
            copy.SectionName = source.SectionName;
            copy.Name = source.Name;
            copy.StreamId = source.StreamId;
            copy.IpAddress = source.IpAddress;
            copy.RtspPort = source.RtspPort;
            copy.Channel = source.Channel;
            copy.UserName = source.UserName;
            copy.Password = source.Password;

            return copy;
        }


        private static ZlmEditorSettings CloneZlm(
            ZlmEditorSettings source)
        {
            ZlmEditorSettings copy =
                new ZlmEditorSettings();

            copy.Host = source.Host;
            copy.HttpPort = source.HttpPort;
            copy.RtspPort = source.RtspPort;
            copy.Secret = source.Secret;

            return copy;
        }


        private void TogglePassword(
            TextBox textBox,
            ModernButton button)
        {
            if (textBox == null ||
                button == null)
            {
                return;
            }

            bool show =
                textBox.UseSystemPasswordChar;

            textBox.UseSystemPasswordChar =
                !show;

            button.Text =
                show
                    ? "隐藏"
                    : "显示";
        }


        // =========================================================
        // Visual factories
        // =========================================================
        private RoundedPanel CreateCard()
        {
            RoundedPanel panel =
                new RoundedPanel();

            panel.FillColor =
                CardColor;

            panel.BorderColor =
                BorderColor;

            panel.BorderWidth =
                1;

            panel.Radius =
                CardRadius;

            return panel;
        }


        private RoundedPanel CreateSectionCard(
            string title,
            UiIcon icon)
        {
            RoundedPanel panel =
                new RoundedPanel();

            panel.FillColor =
                Color.White;

            panel.BorderColor =
                BorderColor;

            panel.BorderWidth =
                1;

            panel.Radius =
                CardRadius;

            panel.Margin =
                new Padding(0, 0, 0, 8);

            IconGlyph glyph =
                new IconGlyph();

            glyph.Icon =
                icon;

            glyph.IconColor =
                PrimaryColor;

            glyph.Location =
                new Point(16, 15);

            glyph.Size =
                new Size(22, 22);

            panel.Controls.Add(
                glyph);

            Label titleLabel =
                new Label();

            titleLabel.AutoSize =
                true;

            titleLabel.Text =
                title;

            titleLabel.ForeColor =
                TextColor;

            titleLabel.Font =
                new Font(
                    Font.FontFamily,
                    10F,
                    FontStyle.Bold);

            titleLabel.Location =
                new Point(44, 15);

            panel.Controls.Add(
                titleLabel);

            return panel;
        }


        private Control CreateField(
            string labelText,
            Control input)
        {
            Panel field =
                new BufferedPanel();

            field.Dock =
                DockStyle.Fill;

            field.BackColor =
                Color.White;

            field.Margin =
                new Padding(0, 0, 10, 0);

            Label label =
                new Label();

            label.AutoSize =
                true;

            label.Text =
                labelText;

            label.ForeColor =
                SecondaryTextColor;

            label.Location =
                new Point(0, 0);

            field.Controls.Add(
                label);

            input.Anchor =
                AnchorStyles.Left |
                AnchorStyles.Top |
                AnchorStyles.Right;

            input.Location =
                new Point(0, 22);

            input.Size =
                new Size(
                    Math.Max(80, field.ClientSize.Width),
                    36);

            field.Controls.Add(
                input);

            field.Resize +=
                delegate
                {
                    input.Width =
                        Math.Max(
                            80,
                            field.ClientSize.Width - 2);
                };

            return field;
        }


        private TextBox CreateTextBox()
        {
            TextBox textBox =
                new TextBox();

            textBox.BorderStyle =
                BorderStyle.None;

            textBox.Font =
                new Font(
                    Font.FontFamily,
                    10F,
                    FontStyle.Regular);

            textBox.ForeColor =
                TextColor;

            textBox.BackColor =
                Color.White;

            return textBox;
        }


        private RoundedPanel CreateInputHost(
            TextBox textBox,
            Control trailingControl)
        {
            RoundedPanel host =
                new RoundedPanel();

            host.FillColor =
                Color.White;

            host.BorderColor =
                Color.FromArgb(210, 218, 228);

            host.BorderWidth =
                1;

            host.Radius =
                InputRadius;

            host.Height =
                42;

            textBox.Location =
                new Point(12, 11);

            textBox.Height =
                22;

            host.Controls.Add(
                textBox);

            if (trailingControl != null)
            {
                trailingControl.Anchor =
                    AnchorStyles.Top |
                    AnchorStyles.Right;

                trailingControl.Location =
                    new Point(
                        Math.Max(0,
                            host.ClientSize.Width -
                            trailingControl.Width - 10),
                        8);

                host.Controls.Add(
                    trailingControl);
            }

            host.Resize +=
                delegate
                {
                    int rightReserve =
                        trailingControl == null
                            ? 14
                            : trailingControl.Width + 20;

                    textBox.Width =
                        Math.Max(
                            40,
                            host.ClientSize.Width -
                            12 - rightReserve);

                    if (trailingControl != null)
                    {
                        trailingControl.Location =
                            new Point(
                                Math.Max(0,
                                    host.ClientSize.Width -
                                    trailingControl.Width - 10),
                                8);
                    }
                };

            textBox.Enter +=
                delegate
                {
                    host.BorderColor =
                        PrimaryColor;

                    host.Invalidate();
                };

            textBox.Leave +=
                delegate
                {
                    host.BorderColor =
                        Color.FromArgb(210, 218, 228);

                    host.Invalidate();
                };

            return host;
        }


        private RoundedPanel CreateReadOnlyValue(
            string value,
            string hint)
        {
            RoundedPanel host =
                new RoundedPanel();

            host.FillColor =
                Color.FromArgb(249, 250, 252);

            host.BorderColor =
                BorderColor;

            host.BorderWidth =
                1;

            host.Radius =
                InputRadius;

            Label valueLabel =
                new Label();

            valueLabel.AutoSize =
                true;

            valueLabel.Text =
                value;

            valueLabel.ForeColor =
                TextColor;

            valueLabel.Font =
                new Font(
                    Font.FontFamily,
                    10F,
                    FontStyle.Bold);

            valueLabel.Location =
                new Point(12, 11);

            host.Controls.Add(
                valueLabel);

            Label hintLabel =
                new Label();

            hintLabel.AutoSize =
                true;

            hintLabel.Text =
                hint;

            hintLabel.ForeColor =
                MutedTextColor;

            hintLabel.Location =
                new Point(66, 12);

            host.Controls.Add(
                hintLabel);

            return host;
        }


        private ModernButton CreateTextActionButton(
            string text)
        {
            ModernButton button =
                new ModernButton();

            button.Text =
                text;

            button.Size =
                new Size(48, 26);

            button.BorderWidth =
                0;

            button.FillColor =
                Color.White;

            button.HoverColor =
                PrimarySoftColor;

            button.TextColor =
                SecondaryTextColor;

            button.Radius =
                6;

            return button;
        }


        // =========================================================
        // Drawing helpers
        // =========================================================
        private static void DrawBottomLine(
            Graphics graphics,
            Control control)
        {
            using (Pen pen =
                   new Pen(DividerColor))
            {
                graphics.DrawLine(
                    pen,
                    0,
                    control.Height - 1,
                    control.Width,
                    control.Height - 1);
            }
        }


        private static GraphicsPath CreateRoundPath(
            Rectangle rect,
            int radius)
        {
            GraphicsPath path =
                new GraphicsPath();

            int diameter =
                Math.Max(2, radius * 2);

            diameter =
                Math.Min(
                    diameter,
                    Math.Max(2,
                        Math.Min(rect.Width, rect.Height)));

            Rectangle arc =
                new Rectangle(
                    rect.X,
                    rect.Y,
                    diameter,
                    diameter);

            path.AddArc(
                arc,
                180,
                90);

            arc.X =
                rect.Right - diameter;

            path.AddArc(
                arc,
                270,
                90);

            arc.Y =
                rect.Bottom - diameter;

            path.AddArc(
                arc,
                0,
                90);

            arc.X =
                rect.Left;

            path.AddArc(
                arc,
                90,
                90);

            path.CloseFigure();

            return path;
        }


        // =========================================================
        // Custom controls
        // =========================================================
        private enum UiIcon
        {
            Camera,
            Server,
            User,
            Globe,
            Network,
            Lock,
            Info
        }


        private enum BadgeState
        {
            Neutral,
            Success,
            Warning,
            Error
        }


        private enum NoticeState
        {
            Info,
            Success,
            Error
        }


        private class BufferedPanel : Panel
        {
            public BufferedPanel()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer,
                    true);

                DoubleBuffered =
                    true;
            }
        }


        private sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
        {
            public BufferedFlowLayoutPanel()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer,
                    true);

                DoubleBuffered =
                    true;
            }
        }


        private class RoundedPanel : Panel
        {
            private int _radius = 8;
            private int _borderWidth = 1;
            private Color _borderColor = AdvancedSettingsForm.BorderColor;
            private Color _fillColor = Color.White;

            public int Radius
            {
                get { return _radius; }
                set
                {
                    _radius = Math.Max(0, value);
                    UpdateRegion();
                    Invalidate();
                }
            }

            public int BorderWidth
            {
                get { return _borderWidth; }
                set
                {
                    _borderWidth = Math.Max(0, value);
                    Invalidate();
                }
            }

            public Color BorderColor
            {
                get { return _borderColor; }
                set
                {
                    _borderColor = value;
                    Invalidate();
                }
            }

            public Color FillColor
            {
                get { return _fillColor; }
                set
                {
                    _fillColor = value;
                    BackColor = value;
                    Invalidate();
                }
            }

            public RoundedPanel()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true);

                DoubleBuffered =
                    true;

                BackColor =
                    Color.White;
            }

            protected override void OnResize(
                EventArgs eventargs)
            {
                base.OnResize(
                    eventargs);

                UpdateRegion();
            }

            protected override void OnPaint(
                PaintEventArgs e)
            {
                e.Graphics.SmoothingMode =
                    SmoothingMode.AntiAlias;

                Rectangle rect =
                    new Rectangle(
                        0,
                        0,
                        Math.Max(1, Width - 1),
                        Math.Max(1, Height - 1));

                using (GraphicsPath path =
                       CreateRoundPath(
                           rect,
                           _radius))
                using (Brush brush =
                       new SolidBrush(
                           _fillColor))
                {
                    e.Graphics.FillPath(
                        brush,
                        path);

                    if (_borderWidth > 0)
                    {
                        using (Pen pen =
                               new Pen(
                                   _borderColor,
                                   _borderWidth))
                        {
                            pen.Alignment =
                                PenAlignment.Inset;

                            e.Graphics.DrawPath(
                                pen,
                                path);
                        }
                    }
                }

                base.OnPaint(e);
            }

            private void UpdateRegion()
            {
                if (Width <= 0 ||
                    Height <= 0)
                {
                    return;
                }

                using (GraphicsPath path =
                       CreateRoundPath(
                           new Rectangle(
                               0,
                               0,
                               Width,
                               Height),
                           _radius))
                {
                    Region old =
                        Region;

                    Region =
                        new Region(
                            path);

                    if (old != null)
                    {
                        old.Dispose();
                    }
                }
            }
        }


        private sealed class ModernButton : Button
        {
            private bool _hover;
            private bool _primary;
            private int _radius = 7;
            private int _borderWidth = 1;
            private Color _fillColor = Color.White;
            private Color _hoverColor = PrimarySoftColor;
            private Color _textColor = AdvancedSettingsForm.TextColor;

            public bool Primary
            {
                get { return _primary; }
                set
                {
                    _primary = value;

                    if (value)
                    {
                        _fillColor = PrimaryColor;
                        _hoverColor = PrimaryHoverColor;
                        _textColor = Color.White;
                        _borderWidth = 0;
                    }

                    Invalidate();
                }
            }

            public int Radius
            {
                get { return _radius; }
                set
                {
                    _radius = Math.Max(0, value);
                    UpdateRegion();
                    Invalidate();
                }
            }

            public int BorderWidth
            {
                get { return _borderWidth; }
                set
                {
                    _borderWidth = Math.Max(0, value);
                    Invalidate();
                }
            }

            public Color FillColor
            {
                get { return _fillColor; }
                set
                {
                    _fillColor = value;
                    Invalidate();
                }
            }

            public Color HoverColor
            {
                get { return _hoverColor; }
                set
                {
                    _hoverColor = value;
                    Invalidate();
                }
            }

            public Color TextColor
            {
                get { return _textColor; }
                set
                {
                    _textColor = value;
                    Invalidate();
                }
            }

            public ModernButton()
            {
                FlatStyle =
                    FlatStyle.Flat;

                FlatAppearance.BorderSize =
                    0;

                UseVisualStyleBackColor =
                    false;

                Cursor =
                    Cursors.Hand;

                Font =
                    new Font(
                        "Microsoft YaHei UI",
                        9.5F,
                        FontStyle.Regular);

                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true);
            }

            protected override void OnMouseEnter(
                EventArgs e)
            {
                _hover =
                    true;

                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(
                EventArgs e)
            {
                _hover =
                    false;

                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnResize(
                EventArgs e)
            {
                base.OnResize(e);
                UpdateRegion();
            }

            protected override void OnPaint(
                PaintEventArgs pevent)
            {
                pevent.Graphics.SmoothingMode =
                    SmoothingMode.AntiAlias;

                Color fill =
                    Enabled
                        ? (_hover ? _hoverColor : _fillColor)
                        : Color.FromArgb(239, 241, 244);

                Color text =
                    Enabled
                        ? _textColor
                        : Color.FromArgb(155, 164, 176);

                Rectangle rect =
                    new Rectangle(
                        0,
                        0,
                        Math.Max(1, Width - 1),
                        Math.Max(1, Height - 1));

                using (GraphicsPath path =
                       CreateRoundPath(
                           rect,
                           _radius))
                using (Brush brush =
                       new SolidBrush(fill))
                {
                    pevent.Graphics.FillPath(
                        brush,
                        path);

                    if (_borderWidth > 0)
                    {
                        using (Pen pen =
                               new Pen(
                                   _primary
                                       ? PrimaryColor
                                       : BorderColor,
                                   _borderWidth))
                        {
                            pen.Alignment =
                                PenAlignment.Inset;

                            pevent.Graphics.DrawPath(
                                pen,
                                path);
                        }
                    }
                }

                TextRenderer.DrawText(
                    pevent.Graphics,
                    Text,
                    Font,
                    ClientRectangle,
                    text,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
            }

            private void UpdateRegion()
            {
                if (Width <= 0 ||
                    Height <= 0)
                {
                    return;
                }

                using (GraphicsPath path =
                       CreateRoundPath(
                           new Rectangle(
                               0,
                               0,
                               Width,
                               Height),
                           _radius))
                {
                    Region old =
                        Region;

                    Region =
                        new Region(path);

                    if (old != null)
                    {
                        old.Dispose();
                    }
                }
            }
        }


        private sealed class StatusBadge : Control
        {
            private BadgeState _state;
            private string _text = string.Empty;

            public StatusBadge()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer,
                    true);

                Font =
                    new Font(
                        "Microsoft YaHei UI",
                        9F,
                        FontStyle.Regular);

                Height =
                    30;

                Cursor =
                    Cursors.Default;
            }

            public void SetState(
                BadgeState state,
                string text)
            {
                _state =
                    state;

                _text =
                    text ?? string.Empty;

                int width =
                    TextRenderer.MeasureText(
                        _text,
                        Font).Width + 32;

                Width =
                    Math.Max(62, width);

                Invalidate();
            }

            protected override void OnPaint(
                PaintEventArgs e)
            {
                e.Graphics.SmoothingMode =
                    SmoothingMode.AntiAlias;

                Color fore;
                Color back;

                GetBadgeColors(
                    _state,
                    out fore,
                    out back);

                Rectangle rect =
                    new Rectangle(
                        0,
                        0,
                        Math.Max(1, Width - 1),
                        Math.Max(1, Height - 1));

                using (GraphicsPath path =
                       CreateRoundPath(
                           rect,
                           6))
                using (Brush brush =
                       new SolidBrush(back))
                {
                    e.Graphics.FillPath(
                        brush,
                        path);
                }

                using (Brush dot =
                       new SolidBrush(fore))
                {
                    e.Graphics.FillEllipse(
                        dot,
                        10,
                        Height / 2 - 3,
                        6,
                        6);
                }

                Rectangle textRect =
                    new Rectangle(
                        22,
                        0,
                        Math.Max(1, Width - 26),
                        Height);

                TextRenderer.DrawText(
                    e.Graphics,
                    _text,
                    Font,
                    textRect,
                    fore,
                    TextFormatFlags.Left |
                    TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis);
            }

            private static void GetBadgeColors(
                BadgeState state,
                out Color fore,
                out Color back)
            {
                switch (state)
                {
                    case BadgeState.Success:
                        fore = SuccessColor;
                        back = SuccessSoftColor;
                        break;

                    case BadgeState.Warning:
                        fore = WarningColor;
                        back = WarningSoftColor;
                        break;

                    case BadgeState.Error:
                        fore = ErrorColor;
                        back = ErrorSoftColor;
                        break;

                    default:
                        fore = SecondaryTextColor;
                        back = Color.FromArgb(244, 246, 249);
                        break;
                }
            }
        }


        private sealed class InlineNotice : RoundedPanel
        {
            private readonly IconGlyph _icon;
            private readonly Label _label;

            public InlineNotice()
            {
                Radius =
                    7;

                BorderWidth =
                    1;

                Height =
                    46;

                _icon =
                    new IconGlyph();

                _icon.Icon =
                    UiIcon.Info;

                _icon.Size =
                    new Size(20, 20);

                _icon.Location =
                    new Point(12, 13);

                Controls.Add(
                    _icon);

                _label =
                    new Label();

                _label.AutoEllipsis =
                    true;

                _label.Location =
                    new Point(40, 0);

                _label.Height =
                    46;

                _label.TextAlign =
                    ContentAlignment.MiddleLeft;

                Controls.Add(
                    _label);

                Resize +=
                    delegate
                    {
                        _label.Width =
                            Math.Max(40,
                                ClientSize.Width - 52);
                    };
            }

            public void SetState(
                NoticeState state,
                string text)
            {
                Color fore;
                Color back;
                Color border;

                if (state == NoticeState.Success)
                {
                    fore = SuccessColor;
                    back = SuccessSoftColor;
                    border = Color.FromArgb(197, 234, 212);
                }
                else if (state == NoticeState.Error)
                {
                    fore = ErrorColor;
                    back = ErrorSoftColor;
                    border = Color.FromArgb(244, 205, 205);
                }
                else
                {
                    fore = PrimaryColor;
                    back = PrimarySoftColor;
                    border = Color.FromArgb(199, 220, 252);
                }

                FillColor =
                    back;

                BorderColor =
                    border;

                _icon.IconColor =
                    fore;

                _label.ForeColor =
                    fore;

                _label.Text =
                    (text ?? string.Empty)
                        .Replace("\r\n", "  ")
                        .Replace("\n", "  ");

                Invalidate();
            }
        }


        private sealed class NavItem : BufferedPanel
        {
            private readonly IconGlyph _icon;
            private readonly Label _label;
            private bool _selected;

            public event EventHandler ItemClicked;

            public bool Selected
            {
                get { return _selected; }
                set
                {
                    _selected = value;
                    UpdateVisual();
                    Invalidate();
                }
            }

            public NavItem(
                string text,
                UiIcon icon)
            {
                BackColor =
                    Color.White;

                Cursor =
                    Cursors.Hand;

                _icon =
                    new IconGlyph();

                _icon.Icon =
                    icon;

                _icon.Size =
                    new Size(20, 20);

                _icon.Location =
                    new Point(4, 12);

                _icon.Cursor =
                    Cursors.Hand;

                Controls.Add(
                    _icon);

                _label =
                    new Label();

                _label.AutoSize =
                    true;

                _label.Text =
                    text;

                _label.Font =
                    new Font(
                        "Microsoft YaHei UI",
                        10F,
                        FontStyle.Regular);

                _label.Location =
                    new Point(30, 13);

                _label.Cursor =
                    Cursors.Hand;

                Controls.Add(
                    _label);

                Click +=
                    HandleClick;

                _icon.Click +=
                    HandleClick;

                _label.Click +=
                    HandleClick;

                Paint +=
                    delegate (
                        object sender,
                        PaintEventArgs e)
                    {
                        if (_selected)
                        {
                            using (Brush brush =
                                   new SolidBrush(PrimaryColor))
                            {
                                e.Graphics.FillRectangle(
                                    brush,
                                    0,
                                    Height - 3,
                                    Width,
                                    3);
                            }
                        }
                    };

                UpdateVisual();
            }

            private void HandleClick(
                object sender,
                EventArgs e)
            {
                EventHandler handler =
                    ItemClicked;

                if (handler != null)
                {
                    handler(
                        this,
                        EventArgs.Empty);
                }
            }

            private void UpdateVisual()
            {
                Color color =
                    _selected
                        ? PrimaryColor
                        : SecondaryTextColor;

                _icon.IconColor =
                    color;

                _label.ForeColor =
                    color;

                _label.Font =
                    new Font(
                        "Microsoft YaHei UI",
                        10F,
                        _selected
                            ? FontStyle.Bold
                            : FontStyle.Regular);
            }
        }


        private sealed class IconGlyph : Control
        {
            private UiIcon _icon;
            private Color _iconColor = SecondaryTextColor;

            public UiIcon Icon
            {
                get { return _icon; }
                set
                {
                    _icon = value;
                    Invalidate();
                }
            }

            public Color IconColor
            {
                get { return _iconColor; }
                set
                {
                    _iconColor = value;
                    Invalidate();
                }
            }

            public IconGlyph()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.SupportsTransparentBackColor,
                    true);

                BackColor =
                    Color.Transparent;
            }

            protected override void OnPaint(
                PaintEventArgs e)
            {
                e.Graphics.SmoothingMode =
                    SmoothingMode.AntiAlias;

                Rectangle r =
                    new Rectangle(
                        3,
                        3,
                        Math.Max(10, Width - 7),
                        Math.Max(10, Height - 7));

                using (Pen pen =
                       new Pen(
                           _iconColor,
                           1.8F))
                {
                    pen.StartCap =
                        LineCap.Round;

                    pen.EndCap =
                        LineCap.Round;

                    switch (_icon)
                    {
                        case UiIcon.Camera:
                            DrawCamera(
                                e.Graphics,
                                pen,
                                r);
                            break;

                        case UiIcon.Server:
                            DrawServer(
                                e.Graphics,
                                pen,
                                r);
                            break;

                        case UiIcon.User:
                            DrawUser(
                                e.Graphics,
                                pen,
                                r);
                            break;

                        case UiIcon.Globe:
                        case UiIcon.Network:
                            DrawGlobe(
                                e.Graphics,
                                pen,
                                r);
                            break;

                        case UiIcon.Lock:
                            DrawLock(
                                e.Graphics,
                                pen,
                                r);
                            break;

                        default:
                            DrawInfo(
                                e.Graphics,
                                pen,
                                r);
                            break;
                    }
                }
            }

            private static void DrawCamera(
                Graphics g,
                Pen pen,
                Rectangle r)
            {
                Rectangle body =
                    new Rectangle(
                        r.Left + 1,
                        r.Top + 5,
                        r.Width - 2,
                        r.Height - 8);

                g.DrawRectangle(
                    pen,
                    body);

                g.DrawEllipse(
                    pen,
                    body.Left + body.Width / 2 - 4,
                    body.Top + body.Height / 2 - 4,
                    8,
                    8);

                g.DrawLine(
                    pen,
                    body.Left + 4,
                    body.Top,
                    body.Left + 7,
                    r.Top + 1);

                g.DrawLine(
                    pen,
                    body.Left + 7,
                    r.Top + 1,
                    body.Left + 13,
                    r.Top + 1);

                g.DrawLine(
                    pen,
                    body.Left + 13,
                    r.Top + 1,
                    body.Left + 16,
                    body.Top);
            }

            private static void DrawServer(
                Graphics g,
                Pen pen,
                Rectangle r)
            {
                int h =
                    Math.Max(5, r.Height / 4);

                Rectangle top =
                    new Rectangle(
                        r.Left,
                        r.Top + 2,
                        r.Width,
                        h);

                Rectangle bottom =
                    new Rectangle(
                        r.Left,
                        r.Bottom - h - 2,
                        r.Width,
                        h);

                g.DrawRectangle(pen, top);
                g.DrawRectangle(pen, bottom);
                g.DrawEllipse(pen, top.Left + 4, top.Top + h / 2 - 1, 2, 2);
                g.DrawEllipse(pen, bottom.Left + 4, bottom.Top + h / 2 - 1, 2, 2);
            }

            private static void DrawUser(
                Graphics g,
                Pen pen,
                Rectangle r)
            {
                int cx =
                    r.Left + r.Width / 2;

                g.DrawEllipse(
                    pen,
                    cx - 4,
                    r.Top + 1,
                    8,
                    8);

                g.DrawArc(
                    pen,
                    r.Left + 2,
                    r.Top + 10,
                    r.Width - 4,
                    r.Height - 9,
                    195,
                    150);
            }

            private static void DrawGlobe(
                Graphics g,
                Pen pen,
                Rectangle r)
            {
                g.DrawEllipse(pen, r);

                int cx =
                    r.Left + r.Width / 2;

                int cy =
                    r.Top + r.Height / 2;

                g.DrawLine(
                    pen,
                    r.Left + 1,
                    cy,
                    r.Right - 1,
                    cy);

                g.DrawArc(
                    pen,
                    cx - r.Width / 4,
                    r.Top,
                    r.Width / 2,
                    r.Height,
                    90,
                    180);

                g.DrawArc(
                    pen,
                    cx - r.Width / 4,
                    r.Top,
                    r.Width / 2,
                    r.Height,
                    270,
                    180);
            }

            private static void DrawLock(
                Graphics g,
                Pen pen,
                Rectangle r)
            {
                Rectangle body =
                    new Rectangle(
                        r.Left + 2,
                        r.Top + r.Height / 2,
                        r.Width - 4,
                        r.Height / 2 - 2);

                g.DrawRectangle(
                    pen,
                    body);

                g.DrawArc(
                    pen,
                    r.Left + r.Width / 4,
                    r.Top + 1,
                    r.Width / 2,
                    r.Height / 2 + 2,
                    180,
                    180);
            }

            private static void DrawInfo(
                Graphics g,
                Pen pen,
                Rectangle r)
            {
                g.DrawEllipse(pen, r);

                int cx =
                    r.Left + r.Width / 2;

                g.DrawLine(
                    pen,
                    cx,
                    r.Top + 8,
                    cx,
                    r.Bottom - 5);

                g.DrawEllipse(
                    pen,
                    cx - 1,
                    r.Top + 4,
                    2,
                    2);
            }
        }
    }
}
