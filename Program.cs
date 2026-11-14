// UpTimeTracker - 轻量级 Windows 开机时长记录工具
// 目标：.NET Framework 4.8（Windows 自带，无需安装运行时），C# 5 语法
// 功能：开机后自动计时 / 关闭后重置 / 单位可选（秒、分、时、天、年、自动）/ 开机自启动（HKCU）
//       托盘常驻 / 窗口置顶 / 低内存占用（纯 WinForms，无第三方依赖）

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Timer = System.Windows.Forms.Timer;
using System.Windows.Forms;

namespace UpTimeTracker
{
    internal static class Program
    {
        internal static Mutex SingleInstanceMutex;
        internal static EventWaitHandle RestoreEvent;

        [STAThread]
        private static void Main()
        {
            // 单实例：已在后台运行时，再次启动不新开窗口，而是唤醒已有实例
            bool createdNew;
            SingleInstanceMutex = new Mutex(true, "UpTimeTracker_SingleInstance", out createdNew);
            if (!createdNew)
            {
                EventWaitHandle existing;
                if (EventWaitHandle.TryOpenExisting("UpTimeTracker_RestoreEvent", out existing))
                {
                    existing.Set(); // 让第一个实例把窗口从托盘提起来
                }
                return;
            }
            RestoreEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "UpTimeTracker_RestoreEvent");

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private const string AppName = "UpTimeTracker";
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const int CornerRadius = 18;
        private const uint WM_APP_UNHIDE = 0x8000; // WM_APP 基址

        // ---- 计时状态 ----
        private DateTime _startUtc;          // 本次计时开始时间（UTC）
        private bool _startLoaded;           // 是否已从磁盘恢复过计时起点

        // ---- 设置 ----
        private int _unitIndex;              // 0=自动 1=秒 2=分钟 3=小时 4=天 5=年
        private bool _topMost;
        private bool _minimizeToTray = true; // 默认：点 ✕ 隐藏到托盘后台运行
        private bool _startWithWindows;

        // ---- 控件 ----
        private Label _timeLabel;
        private Label _breakdownLabel;       // 天/时/分/秒 分解
        private Label _sysUptimeLabel;       // 系统本次开机总时长
        private Label _memLabel;             // 程序内存占用
        private ComboBox _unitCombo;
        private CheckBox _chkTopMost;
        private CheckBox _chkTray;
        private CheckBox _chkAutoStart;
        private Panel _titleBar;
        private Label _titleLabel;
        private Label _btnMin;
        private Label _btnClose;
        private Timer _timer;
        private NotifyIcon _tray;
        private int _lastMemUpdate;          // Environment.TickCount，内存标签每 2 秒刷新一次
        private int _lastTrimTick;           // 上次修剪工作集时间，每 5 分钟一次
        private bool _trayHintShown;         // 首次点 ✕ 后台运行时的气泡提示只弹一次

        private bool _closeReallyExit;       // 托盘菜单“退出”时直接结束
        private bool _suppressSettingsSave;  // 启动回填设置时不落盘

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr min, IntPtr max);

        public MainForm()
        {
            LoadSettings();
            BuildUi();
            RestoreOrStartTimer();
            InitTray();
        }

        // ================= UI 构建 =================

        private void BuildUi()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(520, 420);
            BackColor = Color.FromArgb(18, 20, 27);
            DoubleBuffered = true;
            KeyPreview = true;

            // ---- 标题栏 ----
            _titleBar = new Panel();
            _titleBar.Dock = DockStyle.Top;
            _titleBar.Height = 46;
            _titleBar.BackColor = Color.FromArgb(24, 27, 36);
            _titleBar.MouseDown += OnTitleBarMouseDown;

            _titleLabel = new Label();
            _titleLabel.Text = "⏱  开机时长记录";
            _titleLabel.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            _titleLabel.ForeColor = Color.FromArgb(200, 206, 220);
            _titleLabel.AutoSize = true;
            _titleLabel.Location = new Point(18, 12);
            _titleLabel.MouseDown += OnTitleBarMouseDown;
            _titleBar.Controls.Add(_titleLabel);

            _btnMin = MakeTitleButton("—", 90);
            _btnMin.Location = new Point(520 - 46 - 90, 0); // 右上角：最小化
            _btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            _btnClose = MakeTitleButton("✕", 46);
            _btnClose.Location = new Point(520 - 46, 0);    // 最右上角：关闭/后台运行
            _btnClose.Click += OnCloseClicked;
            _titleBar.Controls.Add(_btnMin);
            _titleBar.Controls.Add(_btnClose);
            Controls.Add(_titleBar);

            // ---- 主时间 ----
            Label caption = new Label();
            caption.Text = "本次开机已持续";
            caption.Font = new Font("Microsoft YaHei UI", 9f);
            caption.ForeColor = Color.FromArgb(120, 128, 148);
            caption.AutoSize = true;
            caption.Location = new Point(26, 66);
            Controls.Add(caption);

            _timeLabel = new Label();
            _timeLabel.Text = "0";
            _timeLabel.Font = new Font("Consolas", 42f, FontStyle.Bold);
            _timeLabel.ForeColor = Color.FromArgb(88, 230, 217);
            _timeLabel.AutoSize = true;
            _timeLabel.Location = new Point(26, 90);
            Controls.Add(_timeLabel);

            _unitCombo = new ComboBox();
            _unitCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _unitCombo.DrawMode = DrawMode.OwnerDrawFixed;
            _unitCombo.DrawItem += OnDrawUnitItem;
            _unitCombo.Font = new Font("Microsoft YaHei UI", 9.5f);
            _unitCombo.BackColor = Color.FromArgb(32, 36, 48);
            _unitCombo.ForeColor = Color.FromArgb(220, 224, 236);
            _unitCombo.FlatStyle = FlatStyle.Flat;
            _unitCombo.Items.AddRange(new object[]
            {
                "自动", "秒", "分钟", "小时", "天", "年"
            });
            _unitCombo.Location = new Point(26, 168);
            _unitCombo.Size = new Size(130, 30);
            _unitCombo.SelectedIndexChanged += delegate
            {
                if (_suppressSettingsSave) return;
                _unitIndex = _unitCombo.SelectedIndex;
                SaveSettings();
                UpdateClock();
            };
            Controls.Add(_unitCombo);

            // 重置计时按钮（扁平深色）
            _btnReset = MakeFlatButton("↺ 重置计时");
            _btnReset.Location = new Point(168, 168);
            _btnReset.Size = new Size(110, 30);
            _btnReset.Click += delegate { ResetTimer(); };
            Controls.Add(_btnReset);

            _breakdownLabel = new Label();
            _breakdownLabel.Text = "";
            _breakdownLabel.Font = new Font("Consolas", 11.5f);
            _breakdownLabel.ForeColor = Color.FromArgb(150, 158, 178);
            _breakdownLabel.AutoSize = true;
            _breakdownLabel.Location = new Point(292, 174);
            Controls.Add(_breakdownLabel);

            // ---- 信息卡片 ----
            Panel card = new Panel();
            card.Location = new Point(26, 216);
            card.Size = new Size(468, 74);
            card.BackColor = Color.FromArgb(26, 29, 39);
            card.Region = new Region(RoundedRegion(card.Size, 12));
            Controls.Add(card);

            _sysUptimeLabel = MakeInfoLabel(card, new Point(16, 12));
            _memLabel = MakeInfoLabel(card, new Point(16, 42));

            // ---- 选项区 ----
            _chkTopMost = MakeCheckBox("窗口置顶", new Point(26, 308));
            _chkTopMost.CheckedChanged += delegate
            {
                if (_suppressSettingsSave) return;
                _topMost = _chkTopMost.Checked;
                TopMost = _topMost;
                SaveSettings();
            };

            _chkTray = MakeCheckBox("点 ✕ 后最小化到托盘（后台运行）", new Point(160, 308));
            _chkTray.CheckedChanged += delegate
            {
                if (_suppressSettingsSave) return;
                _minimizeToTray = _chkTray.Checked;
                SaveSettings();
            };

            _chkAutoStart = MakeCheckBox("开机自动运行（当前用户，无需管理员）", new Point(26, 340));
            _chkAutoStart.CheckedChanged += OnAutoStartToggled;

            _suppressSettingsSave = true;
            _unitCombo.SelectedIndex = Math.Max(0, Math.Min(_unitIndex, 5));
            _chkTopMost.Checked = _topMost;
            _chkTray.Checked = _minimizeToTray;
            _chkAutoStart.Checked = _startWithWindows;
            _suppressSettingsSave = false;

            _timer = new Timer();
            _timer.Interval = 500;
            _timer.Tick += OnTick;
            _timer.Start();

            Shown += delegate { UpdateClock(); TrimWorkingSet(); };
        }

        private Label MakeTitleButton(string text, int width)
        {
            Label b = new Label();
            b.Text = text;
            b.Size = new Size(width, 46);
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Font = new Font("Segoe UI", 10f);
            b.ForeColor = Color.FromArgb(180, 186, 200);
            b.BackColor = Color.Transparent;
            b.Cursor = Cursors.Hand;
            // 注意：按钮不能接 OnTitleBarMouseDown（HTCAPTION 拖动会进入模态移动循环，
            // 吞掉随后的 Click 事件，导致点关闭没反应）；拖动只挂在标题栏空白与标题文字上
            b.MouseEnter += delegate { b.ForeColor = Color.White; b.BackColor = Color.FromArgb(45, 50, 66); };
            b.MouseLeave += delegate { b.ForeColor = Color.FromArgb(180, 186, 200); b.BackColor = Color.Transparent; };
            return b;
        }

        private Label MakeInfoLabel(Panel parent, Point location)
        {
            Label l = new Label();
            l.Location = location;
            l.AutoSize = true;
            l.Font = new Font("Microsoft YaHei UI", 9f);
            l.ForeColor = Color.FromArgb(160, 168, 188);
            parent.Controls.Add(l);
            return l;
        }

        private CheckBox MakeCheckBox(string text, Point location)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Location = location;
            c.Font = new Font("Microsoft YaHei UI", 9f);
            c.ForeColor = Color.FromArgb(190, 196, 212);
            return c;
        }

        private Label _btnReset; // 扁平深色重置按钮

        private Label MakeFlatButton(string text)
        {
            Label b = new Label();
            b.Text = text;
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Font = new Font("Microsoft YaHei UI", 9f);
            b.ForeColor = Color.FromArgb(200, 206, 220);
            b.BackColor = Color.FromArgb(32, 36, 48);
            b.Cursor = Cursors.Hand;
            b.Region = new Region(RoundedRegion(b.Size, 8));
            b.MouseEnter += delegate { b.BackColor = Color.FromArgb(45, 50, 66); };
            b.MouseLeave += delegate { b.BackColor = Color.FromArgb(32, 36, 48); };
            return b;
        }

        private static GraphicsPath RoundedRegion(Size s, int r)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddArc(0, 0, r * 2, r * 2, 180, 90);
            p.AddArc(s.Width - r * 2, 0, r * 2, r * 2, 270, 90);
            p.AddArc(s.Width - r * 2, s.Height - r * 2, r * 2, r * 2, 0, 90);
            p.AddArc(0, s.Height - r * 2, r * 2, r * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 下拉框深色化：自绘每一项（含选中高亮）
        private void OnDrawUnitItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush bg = new SolidBrush(selected ? Color.FromArgb(50, 56, 74) : Color.FromArgb(32, 36, 48)))
            {
                e.Graphics.FillRectangle(bg, e.Bounds);
            }
            using (SolidBrush fg = new SolidBrush(selected ? Color.FromArgb(88, 230, 217) : Color.FromArgb(220, 224, 236)))
            {
                string text = _unitCombo.GetItemText(_unitCombo.Items[e.Index]);
                TextRenderer.DrawText(e.Graphics, text, e.Font, e.Bounds, fg.Color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }

        private void OnTitleBarMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 2, 0); // HTCAPTION 拖动无边框窗口
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            // 顶部细渐变装饰线
            using (LinearGradientBrush b = new LinearGradientBrush(
                new Rectangle(0, 0, Width, 3), Color.FromArgb(88, 230, 217), Color.FromArgb(120, 120, 235), 0f))
            {
                e.Graphics.FillRectangle(b, 0, 0, Width, 3);
            }
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            ApplyRoundedRegion();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyRoundedRegion();
        }

        private void ApplyRoundedRegion()
        {
            using (GraphicsPath path = new GraphicsPath())
            {
                Rectangle r = new Rectangle(0, 0, Width, Height);
                path.AddArc(r.X, r.Y, CornerRadius * 2, CornerRadius * 2, 180, 90);
                path.AddArc(r.Right - CornerRadius * 2, r.Y, CornerRadius * 2, CornerRadius * 2, 270, 90);
                path.AddArc(r.Right - CornerRadius * 2, r.Bottom - CornerRadius * 2, CornerRadius * 2, CornerRadius * 2, 0, 90);
                path.AddArc(r.X, r.Bottom - CornerRadius * 2, CornerRadius * 2, CornerRadius * 2, 90, 90);
                path.CloseFigure();
                Region = new Region(path);
            }
        }

        // ================= 计时 =================

        private void RestoreOrStartTimer()
        {
            // 用户要求“关闭后重置计时”：正常关闭时我们会删除标记文件，
            // 这里若发现残留的启动标记（上次是异常退出/断电），则续接计时而不是清零。
            string startIso = ReadSetting("startUtc");
            if (!string.IsNullOrEmpty(startIso))
            {
                DateTimeOffset saved;
                // 必须用 DateTimeOffset：DateTime.TryParse 会把带 Z 的 UTC 时间转成本地时钟，
                // 在正时区机器上会差出几个小时的负计时
                if (DateTimeOffset.TryParse(startIso, out saved))
                {
                    _startUtc = saved.UtcDateTime;
                    _startLoaded = true;
                    return;
                }
            }
            StartFresh();
        }

        private void StartFresh()
        {
            _startUtc = DateTime.UtcNow;
            _startLoaded = true;
            ReplaceSetting("startUtc", _startUtc.ToString("o"));
        }

        private void ResetTimer()
        {
            StartFresh();
            UpdateClock();
        }

        private static TimeSpan AppUptime(DateTime startUtc)
        {
            return DateTime.UtcNow - startUtc;
        }

        // ================= 时钟刷新 =================

        private void OnTick(object sender, EventArgs e)
        {
            // 第二实例发来的唤醒信号：从托盘恢复窗口
            if (Program.RestoreEvent != null && Program.RestoreEvent.WaitOne(0))
            {
                RestoreWindow();
            }
            UpdateClock();
        }

        private void UpdateClock()
        {
            if (!_startLoaded) return;
            TimeSpan up = AppUptime(_startUtc);

            _timeLabel.Text = FormatByUnit(up, _unitIndex);
            _timeLabel.Location = new Point(24, 78);

            _breakdownLabel.Text = string.Format("{0}天 {1:00}:{2:00}:{3:00}",
                (int)up.TotalDays, up.Hours, up.Minutes, up.Seconds);

            TimeSpan sysUp = TimeSpan.FromMilliseconds((uint)Environment.TickCount);
            _sysUptimeLabel.Text = string.Format("🖥  系统本次开机总时长：{0}", FormatAuto(sysUp));

            int nowTick = Environment.TickCount;
            if (unchecked(nowTick - _lastMemUpdate) >= 2000 || _lastMemUpdate == 0)
            {
                _lastMemUpdate = nowTick;
                long mem = 0;
                using (Process p = Process.GetCurrentProcess())
                {
                    mem = p.WorkingSet64;
                }
                _memLabel.Text = string.Format("💾  本程序内存占用：{0:F1} MB", mem / 1048576.0);
            }
            // 低频修剪工作集：把不再使用的页还给系统，保持常驻内存很低
            if (_lastTrimTick == 0 || unchecked((uint)(nowTick - _lastTrimTick)) >= 300000u)
            {
                _lastTrimTick = nowTick;
                TrimWorkingSet();
            }
        }

        private void TrimWorkingSet()
        {
            try
            {
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
            }
            catch { }
        }

        private static string FormatByUnit(TimeSpan up, int unit)
        {
            switch (unit)
            {
                case 1: return string.Format("{0:N0} 秒", up.TotalSeconds);
                case 2: return string.Format("{0:F2} 分钟", up.TotalMinutes);
                case 3: return string.Format("{0:F2} 小时", up.TotalHours);
                case 4: return string.Format("{0:F2} 天", up.TotalDays);
                case 5: return string.Format("{0:F4} 年", up.TotalDays / 365.2425);
                default: return FormatAuto(up);
            }
        }

        private static string FormatAuto(TimeSpan up)
        {
            if (up.TotalSeconds < 60) return string.Format("{0} 秒", (int)up.TotalSeconds);
            if (up.TotalMinutes < 60) return string.Format("{0} 分 {1} 秒", up.Minutes, up.Seconds);
            if (up.TotalHours < 24) return string.Format("{0} 时 {1:00} 分 {2:00} 秒", up.Hours, up.Minutes, up.Seconds);
            if (up.TotalDays < 365.2425) return string.Format("{0} 天 {1} 时 {2:00} 分", (int)up.TotalDays, up.Hours, up.Minutes);
            double years = up.TotalDays / 365.2425;
            return string.Format("{0:F2} 年（约 {1} 天）", years, (int)up.TotalDays);
        }

        // ================= 托盘 =================

        private void InitTray()
        {
            _tray = new NotifyIcon();
            _tray.Icon = MakeTrayIcon();
            _tray.Text = "开机时长记录";
            _tray.Visible = true;
            _tray.DoubleClick += delegate { RestoreWindow(); };

            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("显示主窗口", delegate { RestoreWindow(); });
            menu.MenuItems.Add("重置计时", delegate { ResetTimer(); });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("退出", delegate
            {
                _closeReallyExit = true;
                Close();
            });
            _tray.ContextMenu = menu;
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private static Icon MakeTrayIcon()
        {
            // 运行时画一个简单的时钟图标，避免携带资源文件
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(Color.FromArgb(88, 230, 217), 3f))
                {
                    g.DrawEllipse(pen, 3, 3, 25, 25);
                    g.DrawLine(pen, 16, 16, 16, 8);
                    g.DrawLine(pen, 16, 16, 22, 19);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        // ================= 关闭行为 =================

        private void OnCloseClicked(object sender, EventArgs e)
        {
            Close(); // 由 OnFormClosing 决定：勾选了托盘选项则隐藏到后台，否则真正退出
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool userIntent = e.CloseReason == CloseReason.UserClosing
                || e.CloseReason == CloseReason.TaskManagerClosing;
            if (!_closeReallyExit && _minimizeToTray && userIntent)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown && _tray != null)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloonTip(2000, "开机时长记录", "程序已最小化到托盘，仍在后台计时。\n双击托盘图标或右键退出可真正关闭。", ToolTipIcon.Info);
                }
                return;
            }

            // “关闭后重置计时”：正常退出时删除起点标记，下次启动从 0 开始
            if (e.CloseReason == CloseReason.UserClosing || e.CloseReason == CloseReason.ApplicationExitCall)
            {
                DeleteSetting("startUtc");
            }
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.OnFormClosing(e);
        }

        // ================= 开机自启动（HKCU 注册表，用户在界面勾选即授权） =================

        private void OnAutoStartToggled(object sender, EventArgs e)
        {
            if (_suppressSettingsSave) return;
            _startWithWindows = _chkAutoStart.Checked;
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (_startWithWindows)
                    {
                        key.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
                    }
                    else if (key.GetValue(AppName) != null)
                    {
                        key.DeleteValue(AppName);
                    }
                }
                SaveSettings();
            }
            catch (Exception ex)
            {
                // 写注册表失败（极少见）时还原勾选状态并提示
                _chkAutoStart.CheckedChanged -= OnAutoStartToggled;
                _chkAutoStart.Checked = !_startWithWindows;
                _chkAutoStart.CheckedChanged += OnAutoStartToggled;
                _startWithWindows = _chkAutoStart.Checked;
                MessageBox.Show(this, "设置开机自启动失败：\n" + ex.Message, "开机时长记录",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ================= 设置持久化（纯文本 key=value，避免引入 JSON） =================

        private static string GetSettingsDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            return dir;
        }

        private static string GetSettingsPath()
        {
            return Path.Combine(GetSettingsDir(), "settings.txt");
        }

        private void LoadSettings()
        {
            try
            {
                string[] lines = File.ReadAllLines(GetSettingsPath());
                for (int i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0) continue;
                    string k = lines[i].Substring(0, eq).Trim();
                    string v = lines[i].Substring(eq + 1).Trim();
                    if (k == "unit") int.TryParse(v, out _unitIndex);
                    else if (k == "topMost") _topMost = v == "1";
                    else if (k == "minimizeToTray") _minimizeToTray = v == "1";
                    else if (k == "startWithWindows") _startWithWindows = v == "1";
                }
            }
            catch { } // 首次运行没有配置文件
        }

        private void SaveSettings()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("unit=").Append(_unitIndex).Append('\n');
                sb.Append("topMost=").Append(_topMost ? "1" : "0").Append('\n');
                sb.Append("minimizeToTray=").Append(_minimizeToTray ? "1" : "0").Append('\n');
                sb.Append("startWithWindows=").Append(_startWithWindows ? "1" : "0").Append('\n');
                File.WriteAllText(GetSettingsPath(), sb.ToString());
            }
            catch { }
        }

        private void WriteSetting(string key, string value)
        {
            ReplaceSetting(key, value);
        }

        private void ReplaceSetting(string key, string value)
        {
            try
            {
                string path = GetSettingsPath();
                StringBuilder sb = new StringBuilder();
                try
                {
                    if (File.Exists(path))
                    {
                        string[] lines = File.ReadAllLines(path);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            int eq = lines[i].IndexOf('=');
                            if (eq > 0 && lines[i].Substring(0, eq).Trim() == key) continue;
                            if (lines[i].Length > 0) sb.Append(lines[i]).Append('\n');
                        }
                    }
                }
                catch { }
                sb.Append(key).Append('=').Append(value).Append('\n');
                File.WriteAllText(path, sb.ToString());
            }
            catch { }
        }

        private string ReadSetting(string key)
        {
            try
            {
                string[] lines = File.ReadAllLines(GetSettingsPath());
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0) continue;
                    if (lines[i].Substring(0, eq).Trim() == key)
                        return lines[i].Substring(eq + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        private void DeleteSetting(string key)
        {
            try
            {
                string path = GetSettingsPath();
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq > 0 && lines[i].Substring(0, eq).Trim() == key) continue;
                    if (lines[i].Length > 0) sb.Append(lines[i]).Append('\n');
                }
                File.WriteAllText(path, sb.ToString());
            }
            catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tray != null) _tray.Dispose();
            base.Dispose(disposing);
        }
    }
}
