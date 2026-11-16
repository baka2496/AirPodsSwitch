using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AirPodsSwitch
{
    internal sealed class WidgetSettings
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Size { get; set; }
        public bool OnTop { get; set; }
    }

    // 浮動圖示旁邊的小提示框。Windows「通知」被關掉時，系統匣的通知泡泡不會出現，
    // 所以狀態訊息改用這個自己畫的小框顯示：不搶焦點、幾秒後自動消失、點一下就關。
    internal sealed class StatusBubble : Form
    {
        private readonly Label title = new Label { AutoSize = true, Font = new Font("Microsoft JhengHei UI", 10, FontStyle.Bold), ForeColor = Color.White, Location = new Point(12, 9) };
        private readonly Label body = new Label { AutoSize = true, Font = new Font("Microsoft JhengHei UI", 9), ForeColor = Color.FromArgb(190, 200, 218), Location = new Point(12, 32), MaximumSize = new Size(300, 0) };
        private readonly System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer();
        private readonly Panel accent = new Panel { Location = new Point(0, 0), Width = 4 };
        internal StatusBubble()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; BackColor = Color.FromArgb(28, 32, 41); AutoScaleMode = AutoScaleMode.None;
            Controls.AddRange(new Control[] { accent, title, body });
            hide.Tick += (sender, e) => { hide.Stop(); Hide(); };
            EventHandler close = (sender, e) => { hide.Stop(); Hide(); };
            Click += close; title.Click += close; body.Click += close;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; } // 不搶焦點、不出現在 Alt+Tab
        }
        internal string LastTitle { get; private set; }
        internal void ShowNear(Rectangle anchor, string heading, string text, Color color, int milliseconds)
        {
            LastTitle = heading;
            title.Text = heading; body.Text = text; accent.BackColor = color;
            int width = Math.Max(title.PreferredWidth, body.PreferredWidth) + 26;
            int height = 32 + body.PreferredHeight + 10;
            ClientSize = new Size(width, height); accent.Height = height;
            Rectangle area = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Left - width - 8; if (x < area.Left) x = anchor.Right + 8;
            int y = anchor.Bottom - height; if (y < area.Top) y = area.Top;
            Location = new Point(Math.Max(area.Left, Math.Min(area.Right - width, x)), Math.Min(area.Bottom - height, y));
            if (!Visible) Show(); else Invalidate(true);
            TopMost = true;
            hide.Stop(); hide.Interval = milliseconds; hide.Start();
        }
        protected override void Dispose(bool disposing) { if (disposing) hide.Dispose(); base.Dispose(disposing); }
    }

    internal sealed class CompactSwitchWindow : Form
    {
        private readonly StatusBubble bubble = new StatusBubble();
        private static readonly Color InfoColor = Color.FromArgb(108, 155, 255);
        private static readonly Color OkColor = Color.FromArgb(104, 217, 157);
        private static readonly Color WarnColor = Color.FromArgb(237, 111, 107);
        private const string StartupKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private readonly string settingsPath = Path.Combine(SwitchOperations.Root, "widget-settings.json");
        private readonly EventWaitHandle showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AirPodsSwitch.ShowPanel");
        private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 1500 };
        private readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 90 };
        private readonly ToolTip tip = new ToolTip { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 9000 };
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem connectItem;
        private readonly ToolStripMenuItem disconnectItem;
        private readonly ToolStripMenuItem onTopItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly ToolStripMenuItem sizesItem;
        private readonly NotifyIcon tray;
        private bool busy;
        private bool connected;
        private bool muted;
        private bool failed;
        private bool hovered;
        private bool pointerDown;
        private bool dragged;
        private bool allowExit;
        private bool registeredConnect;
        private bool registeredDisconnect;
        private bool waitingLate;
        private bool slowNotified;
        private int shownSecond = -1;
        private string currentAction;
        private DateTime lateUntil;
        private readonly System.Diagnostics.Stopwatch actionTimer = new System.Diagnostics.Stopwatch();
        private const int LateWaitSeconds = 60;
        private const int SlowNoticeSeconds = 6;
        private float volume;
        private int phase;
        private Point mouseOrigin;
        private Point locationOrigin;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);

        internal CompactSwitchWindow(bool hidden)
        {
            Text = "AirPods 一鍵切換";
            AccessibleName = "AirPods 浮動圖示";
            AccessibleDescription = "左鍵連回電腦，右鍵開啟功能選單，可拖曳圖示。";
            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            MinimumSize = new Size(40, 40);
            MaximumSize = new Size(64, 64);
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 32, 41);
            Cursor = Cursors.Hand;
            TopMost = true;

            WidgetSettings settings = LoadSettings();
            ClientSize = new Size(settings.Size, settings.Size);
            Location = KeepVisible(new Point(settings.X, settings.Y));
            TopMost = settings.OnTop;
            SetRoundedRegion();

            connectItem = new ToolStripMenuItem("連回電腦    Ctrl+Alt+A", null, (sender, e) => StartAction("connect"));
            disconnectItem = new ToolStripMenuItem("中斷電腦連線    Ctrl+Alt+D", null, (sender, e) => StartAction("disconnect"));
            sizesItem = new ToolStripMenuItem("圖示大小");
            AddSize("小（40）", 40);
            AddSize("標準（48）", 48);
            AddSize("大（64）", 64);
            onTopItem = new ToolStripMenuItem("保持在最上層") { Checked = TopMost, CheckOnClick = true };
            onTopItem.CheckedChanged += (sender, e) => { TopMost = onTopItem.Checked; SaveSettings(); };
            startupItem = new ToolStripMenuItem("登入時啟動") { Checked = IsStartupEnabled(), CheckOnClick = true };
            startupItem.CheckedChanged += (sender, e) => {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupKey))
                    if (startupItem.Checked) key.SetValue("AirPodsOneClick", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                    else key.DeleteValue("AirPodsOneClick", false);
            };
            menu.Items.AddRange(new ToolStripItem[] {
                connectItem, disconnectItem, new ToolStripSeparator(),
                new ToolStripMenuItem("選擇耳機…", null, (sender, e) => { if (!busy && HeadphoneSetup.Configure()) { failed = false; RefreshStatus(); } }),
                sizesItem, onTopItem, startupItem,
                new ToolStripSeparator(), new ToolStripMenuItem("隱藏浮動圖示", null, (sender, e) => Hide()),
                new ToolStripMenuItem("結束程式", null, (sender, e) => { allowExit = true; Close(); })
            });
            ContextMenuStrip = menu;

            ContextMenuStrip trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("顯示浮動圖示", null, (sender, e) => ShowWidget());
            trayMenu.Items.Add("連回電腦", null, (sender, e) => StartAction("connect"));
            trayMenu.Items.Add("中斷電腦連線", null, (sender, e) => StartAction("disconnect"));
            trayMenu.Items.Add("結束程式", null, (sender, e) => { allowExit = true; Close(); });
            tray = new NotifyIcon { Icon = LoadTrayIcon(), Text = "AirPods 一鍵切換", Visible = true, ContextMenuStrip = trayMenu };
            tray.MouseClick += (sender, e) => { if (e.Button == MouseButtons.Left) ShowWidget(); };

            refresh.Tick += (sender, e) => { if (showRequest.WaitOne(0)) ShowWidget(); RefreshStatus(); };
            animation.Tick += (sender, e) => {
                phase = (phase + 24) % 360; Invalidate();
                int second = (int)actionTimer.Elapsed.TotalSeconds;
                if ((busy || waitingLate) && second != shownSecond) { shownSecond = second; UpdateTip(); }
                if (busy && currentAction == "connect" && !slowNotified && actionTimer.Elapsed.TotalSeconds >= SlowNoticeSeconds)
                {
                    slowNotified = true;
                    UpdateTip();
                    Notify("AirPods 還在連線…", "請確認耳機已戴上；若手機正在通話，請先結束通話。", InfoColor, 6000);
                }
            };
            Shown += (sender, e) => {
                registeredConnect = RegisterHotKey(Handle, 1, 0x4003, 0x41);
                registeredDisconnect = RegisterHotKey(Handle, 2, 0x4003, 0x44);
                Program.Log("WIDGET " + ClientSize.Width + "x" + ClientSize.Height + " at " + Left + "," + Top + "; HOTKEY connect=" + registeredConnect + " disconnect=" + registeredDisconnect);
                RefreshStatus(); refresh.Start();
                if (hidden) BeginInvoke((Action)delegate { Hide(); });
            };
            FormClosing += (sender, e) => {
                if (!allowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
            FormClosed += (sender, e) => {
                SaveSettings();
                if (registeredConnect) UnregisterHotKey(Handle, 1);
                if (registeredDisconnect) UnregisterHotKey(Handle, 2);
                refresh.Dispose(); animation.Dispose(); tip.Dispose(); showRequest.Dispose(); tray.Dispose(); trayMenu.Dispose(); menu.Dispose(); bubble.Dispose(); if (trayIcon != null) trayIcon.Dispose();
            };
        }

        // 右下角圖示：用和浮動圖示同一套畫法畫耳機，右下角小圓點顯示狀態（綠＝已連線、灰＝未連線、紅＝失敗）。
        // 不直接讀 .ico 檔，因為該檔的小尺寸是 PNG 壓縮格式，.NET Framework 讀出來會變成雜訊。
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        private Icon trayIcon;
        private string trayIconState;
        private Icon LoadTrayIcon() { UpdateTrayIcon(); return trayIcon ?? SystemIcons.Information; }
        private void UpdateTrayIcon()
        {
            bool spinning = busy || waitingLate;
            string state = (spinning ? "busy" : failed ? "failed" : connected ? "on" : "off");
            if (state == trayIconState) return;
            try
            {
                Size size = SystemInformation.SmallIconSize;
                using (Bitmap bitmap = new Bitmap(size.Width, size.Height))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.Transparent);
                        DrawIcon(graphics, size, BackColor, false, failed && !spinning, connected, false, 0);
                        if (spinning)
                        {
                            graphics.ResetTransform(); graphics.ScaleTransform(size.Width / 48f, size.Height / 48f);
                            using (SolidBrush dot = new SolidBrush(Color.FromArgb(112, 163, 255))) graphics.FillEllipse(dot, 33, 33, 12, 12);
                        }
                    }
                    IntPtr handle = bitmap.GetHicon();
                    Icon fresh = (Icon)Icon.FromHandle(handle).Clone();
                    DestroyIcon(handle);
                    Icon old = trayIcon;
                    trayIcon = fresh; trayIconState = state;
                    if (tray != null) tray.Icon = trayIcon;
                    if (old != null) old.Dispose();
                }
            }
            catch (Exception error) { Program.Log("TRAY ICON " + error.Message); }
        }
        private WidgetSettings LoadSettings()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            WidgetSettings settings = new WidgetSettings { X = area.Right - 72, Y = area.Bottom - 72, Size = 48, OnTop = true };
            try
            {
                if (File.Exists(settingsPath))
                {
                    WidgetSettings saved = new JavaScriptSerializer().Deserialize<WidgetSettings>(File.ReadAllText(settingsPath));
                    if (saved != null) settings = saved;
                }
                if (settings.Size != 40 && settings.Size != 48 && settings.Size != 64) settings.Size = 48;
            }
            catch { }
            return settings;
        }
        private Point KeepVisible(Point point)
        {
            Rectangle area = Screen.FromPoint(new Point(point.X + Width / 2, point.Y + Height / 2)).WorkingArea;
            return new Point(Math.Max(area.Left, Math.Min(area.Right - Width, point.X)), Math.Max(area.Top, Math.Min(area.Bottom - Height, point.Y)));
        }
        private void SaveSettings()
        {
            try { File.WriteAllText(settingsPath, new JavaScriptSerializer().Serialize(new WidgetSettings { X = Left, Y = Top, Size = ClientSize.Width, OnTop = TopMost })); }
            catch { }
        }
        private bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupKey)) return key != null && key.GetValue("AirPodsOneClick") != null;
        }
        private void AddSize(string label, int pixels)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label) { Checked = ClientSize.Width == pixels };
            item.Click += (sender, e) => {
                ClientSize = new Size(pixels, pixels); Location = KeepVisible(Location); SetRoundedRegion();
                foreach (ToolStripMenuItem option in sizesItem.DropDownItems) option.Checked = option == item;
                SaveSettings(); Invalidate();
            };
            sizesItem.DropDownItems.Add(item);
        }
        private static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        private void SetRoundedRegion()
        {
            Region old = Region;
            using (GraphicsPath path = Rounded(new RectangleF(0, 0, Width, Height), Width / 4f)) Region = new Region(path);
            if (old != null) old.Dispose();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawIcon(e.Graphics, ClientSize, BackColor, hovered, failed, connected, busy || waitingLate, phase);
        }
        internal static void DrawIcon(Graphics graphics, Size dimensions, Color backgroundColor, bool hovered, bool failed, bool connected, bool busy, int phase)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.ScaleTransform(dimensions.Width / 48f, dimensions.Height / 48f);
            using (GraphicsPath background = Rounded(new RectangleF(1, 1, 46, 46), 11))
            using (SolidBrush fill = new SolidBrush(hovered ? Color.FromArgb(43, 52, 70) : backgroundColor))
            using (Pen edge = new Pen(failed ? Color.FromArgb(237, 111, 107) : hovered ? Color.FromArgb(108, 155, 255) : Color.FromArgb(79, 96, 128), 1.4f))
            { graphics.FillPath(fill, background); graphics.DrawPath(edge, background); }
            using (Pen band = new Pen(Color.FromArgb(237, 242, 255), 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                graphics.DrawArc(band, 12.5f, 10, 23, 25, 180, 180);
            using (SolidBrush white = new SolidBrush(Color.FromArgb(237, 242, 255)))
            using (GraphicsPath left = Rounded(new RectangleF(10.5f, 22, 7, 14), 3))
            using (GraphicsPath right = Rounded(new RectangleF(30.5f, 22, 7, 14), 3))
            { graphics.FillPath(white, left); graphics.FillPath(white, right); }
            if (busy)
            {
                using (Pen spinner = new Pen(Color.FromArgb(112, 163, 255), 2) { StartCap = LineCap.Round, EndCap = LineCap.Round }) graphics.DrawArc(spinner, 5, 5, 38, 38, phase, 100);
            }
            else
            {
                using (SolidBrush border = new SolidBrush(backgroundColor)) graphics.FillEllipse(border, 33, 33, 12, 12);
                using (SolidBrush dot = new SolidBrush(failed ? Color.FromArgb(237, 111, 107) : connected ? Color.FromArgb(104, 217, 157) : Color.FromArgb(128, 142, 168))) graphics.FillEllipse(dot, 35, 35, 8, 8);
            }
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pointerDown = true; dragged = false; mouseOrigin = MousePosition; locationOrigin = Location; Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!pointerDown) return;
            Point now = MousePosition;
            int dx = now.X - mouseOrigin.X, dy = now.Y - mouseOrigin.Y;
            if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4) dragged = true;
            if (dragged) Location = KeepVisible(new Point(locationOrigin.X + dx, locationOrigin.Y + dy));
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !pointerDown) return;
            pointerDown = false; Capture = false;
            if (dragged) SaveSettings(); else StartAction("connect");
        }
        private void ShowWidget() { Location = KeepVisible(Location); Show(); BringToFront(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x312 && message.WParam.ToInt32() == 1) { Program.Log("HOTKEY connect requested"); StartAction("connect"); return; }
            if (message.Msg == 0x312 && message.WParam.ToInt32() == 2) { Program.Log("HOTKEY disconnect requested"); StartAction("disconnect"); return; }
            base.WndProc(ref message);
        }
        private void RefreshStatus()
        {
            if (busy) return;
            try
            {
                using (Audio audio = new Audio())
                {
                    Endpoint endpoint = SwitchOperations.Current(audio);
                    connected = endpoint != null && endpoint.State == 1;
                    volume = endpoint == null ? 0 : endpoint.VolumePercent ?? 0;
                    muted = endpoint != null && endpoint.Muted == true;
                    if (connected) SwitchOperations.RememberVolume(endpoint);
                }
            }
            catch { connected = false; }
            if (waitingLate) CheckLateConnect();
            UpdateTip(); Invalidate();
        }
        // 「連回電腦」逾時後，在背景再等最多 60 秒：耳機一連上就補做解除靜音並通知；
        // 時間到還沒連上，才真的顯示失敗。
        private void CheckLateConnect()
        {
            SwitchResult late = connected && !SwitchOperations.SimulateNeverConnect
                ? SwitchOperations.FinishLateConnect((long)actionTimer.Elapsed.TotalMilliseconds) : null;
            if (late != null)
            {
                waitingLate = false; failed = false; animation.Stop(); actionTimer.Stop();
                volume = late.VolumePercent ?? volume; muted = late.Muted == true;
                Program.Log("LATE CONNECT ok after " + late.Milliseconds + " ms");
                Notify("AirPods 已連回電腦", "耳機較晚連上，已自動解除靜音 · 音量 " + volume.ToString("0") + "%", OkColor, 4000);
            }
            else if (DateTime.Now >= lateUntil)
            {
                waitingLate = false; failed = true; animation.Stop(); actionTimer.Stop();
                Program.Log("LATE CONNECT gave up");
                Notify("AirPods 沒有連上", "已多等 " + LateWaitSeconds + " 秒仍未連上電腦。請確認耳機已戴上，再按一次重試。", WarnColor, 8000);
            }
        }
        // 在浮動圖示旁顯示提示；圖示被隱藏時，改顯示在螢幕右下角。
        private void Notify(string heading, string text, Color color, int milliseconds)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Rectangle anchor = Visible ? Bounds : new Rectangle(area.Right - 8, area.Bottom - 8, 0, 0);
            bubble.ShowNear(anchor, heading, text, color, milliseconds);
            Program.Log("NOTICE " + heading + " | " + text);
        }
        private void UpdateTip()
        {
            string state = busy ? "正在切換…（" + actionTimer.Elapsed.TotalSeconds.ToString("0") + " 秒）"
                : waitingLate ? "等待耳機連上…（最多再 " + Math.Max(0, (lateUntil - DateTime.Now).TotalSeconds).ToString("0") + " 秒）"
                : connected ? "電腦端已連線 · 音量 " + volume.ToString("0") + "%" + (muted ? " · 靜音" : "") : "電腦端未連線";
            tip.SetToolTip(this, "AirPods｜" + state + "\n左鍵：連回電腦　拖曳：移動\n右鍵：功能選單\nCtrl+Alt+A 連回電腦｜Ctrl+Alt+D 中斷");
            tray.Text = "AirPods｜" + state;
            connectItem.Enabled = !busy; disconnectItem.Enabled = !busy;
            UpdateTrayIcon();
        }
        private void StartAction(string action)
        {
            if (busy) return;
            busy = true; failed = false; waitingLate = false; slowNotified = false; currentAction = action;
            actionTimer.Restart(); animation.Start(); UpdateTip(); Invalidate();
            ThreadPool.QueueUserWorkItem(delegate {
                SwitchResult result = SwitchOperations.Run(action);
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate {
                    busy = false;
                    bool canWaitLonger = !result.Success && action == "connect" && result.ReconnectRequested;
                    if (canWaitLonger)
                    {
                        // 驅動已送出連線要求但耳機還沒出現：先不報失敗，背景繼續等。
                        waitingLate = true; failed = false; lateUntil = DateTime.Now.AddSeconds(LateWaitSeconds);
                        Program.Log("CONNECT timed out, waiting up to " + LateWaitSeconds + "s in background: " + result.Message);
                        RefreshStatus();
                        return;
                    }
                    failed = !result.Success; animation.Stop(); actionTimer.Stop(); RefreshStatus();
                    if (!result.Success) Notify("AirPods 切換失敗", result.Message, WarnColor, 6000);
                    else if (result.Connected) Notify("AirPods 已連回電腦", "已解除靜音 · 音量 " + (result.VolumePercent ?? 0).ToString("0") + "%", OkColor, 2500);
                    else Notify("AirPods 已中斷電腦連線", "現在可以從 iPhone 選擇 AirPods。", InfoColor, 3000);
                });
            });
        }
    }
}
