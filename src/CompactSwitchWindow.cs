using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

    internal sealed class StatusBubble : Form
    {
        private readonly Label title = new Label { AutoSize = true, Font = new Font("Microsoft JhengHei UI", 10, FontStyle.Bold), ForeColor = Color.White, Location = new Point(14, 9), BackColor = Color.Transparent };
        private readonly Label body = new Label { AutoSize = true, Font = new Font("Microsoft JhengHei UI", 9), ForeColor = Color.FromArgb(196, 206, 224), Location = new Point(14, 32), MaximumSize = new Size(310, 0), BackColor = Color.Transparent };
        private readonly System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer();
        private Color accentColor = Color.FromArgb(108, 155, 255);

        internal StatusBubble()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(26, 30, 39);
            AutoScaleMode = AutoScaleMode.None;
            Controls.AddRange(new Control[] { title, body });
            hide.Tick += (sender, e) => { hide.Stop(); Hide(); };
            EventHandler close = (sender, e) => { hide.Stop(); Hide(); };
            Click += close; title.Click += close; body.Click += close;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (SolidBrush bar = new SolidBrush(accentColor))
                e.Graphics.FillRectangle(bar, 0, 0, 4, Height);
            using (Pen border = new Pen(Color.FromArgb(62, 72, 92), 1f))
                e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }
        internal string LastTitle { get; private set; }
        internal void ShowNear(Rectangle anchor, string heading, string text, Color color, int milliseconds)
        {
            LastTitle = heading;
            title.Text = heading;
            body.Text = text;
            accentColor = color;
            int width = Math.Max(title.PreferredWidth, body.PreferredWidth) + 28;
            int height = 32 + body.PreferredHeight + 11;
            ClientSize = new Size(width, height);
            Rectangle area = Screen.FromRectangle(anchor).WorkingArea;
            int x = anchor.Left - width - 8; if (x < area.Left) x = anchor.Right + 8;
            int y = anchor.Bottom - height; if (y < area.Top) y = area.Top;
            Location = new Point(Math.Max(area.Left, Math.Min(area.Right - width, x)), Math.Min(area.Bottom - height, y));
            if (!Visible) Show();
            Invalidate(true);
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
        private static readonly Color MuteColor = Color.FromArgb(245, 176, 65);
        private static readonly Color WarnColor = Color.FromArgb(237, 111, 107);
        private const string StartupKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private readonly string settingsPath = Path.Combine(SwitchOperations.Root, "widget-settings.json");
        private readonly EventWaitHandle showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AirPodsSwitch.ShowPanel");
        private readonly System.Windows.Forms.Timer refresh = new System.Windows.Forms.Timer { Interval = 1500 };
        private readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 60 };
        private readonly System.Windows.Forms.Timer clickTimer = new System.Windows.Forms.Timer { Interval = 260 };
        private readonly ToolTip tip = new ToolTip { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 9000 };
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem connectItem;
        private readonly ToolStripMenuItem disconnectItem;
        private readonly ToolStripMenuItem onTopItem;
        private readonly ToolStripMenuItem startupItem;
        private readonly ToolStripMenuItem sizesItem;
        private readonly NotifyIcon tray;
        private readonly PassiveConnectDetector passiveDetector = new PassiveConnectDetector();

        private bool busy;
        private bool passiveGuarding;
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
        private DateTime nextLateRetryAt;
        private int lateRetryCount;
        private readonly System.Diagnostics.Stopwatch actionTimer = new System.Diagnostics.Stopwatch();
        private const int LateWaitSeconds = 60;
        private const int SlowNoticeSeconds = 6;
        private float volume;
        private int? batteryPercent;
        private DateTime nextBatteryCheck = DateTime.MinValue;
        private bool batteryChecking;
        private List<string> batteryKeywords = new List<string>();
        private int phase;
        private Point mouseOrigin;
        private Point locationOrigin;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll", SetLastError = true)] private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref NativeSize psize, IntPtr hdcSrc, ref NativePoint pptSrc, int crKey, ref BlendFunction pblend, int dwFlags);

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X; internal int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { internal int Width; internal int Height; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            internal byte BlendOp;
            internal byte BlendFlags;
            internal byte SourceConstantAlpha;
            internal byte AlphaFormat;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= 0x00080000 | 0x00000080;
                return p;
            }
        }

        internal CompactSwitchWindow(bool hidden)
        {
            Text = "AirPods 一鍵切換";
            AccessibleName = "AirPods 浮動圖示";
            AccessibleDescription = "左鍵連回電腦，滾輪調音量，中鍵或雙擊中斷連線，右鍵開啟功能選單，可拖曳圖示。";
            FormBorderStyle = FormBorderStyle.None;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            MinimumSize = new Size(40, 40);
            MaximumSize = new Size(64, 64);
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(28, 32, 41);
            Cursor = Cursors.Hand;
            TopMost = true;

            WidgetSettings settings = LoadSettings();
            ClientSize = new Size(settings.Size, settings.Size);
            Location = KeepVisible(new Point(settings.X, settings.Y));
            TopMost = settings.OnTop;

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
                new ToolStripMenuItem("選擇耳機…", null, (sender, e) => { if (!busy && HeadphoneSetup.Configure()) { failed = false; batteryKeywords.Clear(); RefreshStatus(); } }),
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

            clickTimer.Tick += (sender, e) => { clickTimer.Stop(); StartAction("connect"); };
            refresh.Tick += (sender, e) => { if (showRequest.WaitOne(0)) ShowWidget(); RefreshStatus(); };
            animation.Tick += (sender, e) => {
                phase = (phase + 18) % 360;
                RenderLayered();
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
                RenderLayered();
                RefreshStatus();
                refresh.Start();
                if (hidden) BeginInvoke((Action)delegate { Hide(); });
            };
            VisibleChanged += (sender, e) => { if (Visible && IsHandleCreated) RenderLayered(); };
            // 浮動圖示不該被最小化：被最小化就馬上還原並重畫，圖示不會消失。
            Resize += (sender, e) => {
                if (WindowState == FormWindowState.Minimized)
                {
                    Program.Log("WIDGET minimized by system; restoring");
                    BeginInvoke((Action)delegate { if (!IsDisposed) { WindowState = FormWindowState.Normal; ClientSize = new Size(Math.Max(40, ClientSize.Width), Math.Max(40, ClientSize.Height)); RenderLayered(); } });
                }
                else RenderLayered();
            };
            FormClosing += (sender, e) => {
                if (!allowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
            FormClosed += (sender, e) => {
                SaveSettings();
                if (registeredConnect) UnregisterHotKey(Handle, 1);
                if (registeredDisconnect) UnregisterHotKey(Handle, 2);
                refresh.Dispose(); animation.Dispose(); clickTimer.Dispose(); tip.Dispose(); showRequest.Dispose(); tray.Dispose(); trayMenu.Dispose(); menu.Dispose(); bubble.Dispose(); if (trayIcon != null) trayIcon.Dispose();
            };
        }

        private Icon trayIcon;
        private string trayIconState;
        private Icon LoadTrayIcon() { UpdateTrayIcon(); return trayIcon ?? SystemIcons.Information; }
        private void UpdateTrayIcon()
        {
            bool spinning = busy || waitingLate;
            string state = spinning ? "busy" : failed ? "failed" : connected ? (muted ? "muted" : "on") : "off";
            if (state == trayIconState) return;
            try
            {
                Size size = SystemInformation.SmallIconSize;
                using (Bitmap bitmap = RenderBitmap(Math.Max(size.Width, size.Height), connected, muted, failed && !spinning, false, false, 0))
                {
                    if (spinning)
                    {
                        using (Graphics g = Graphics.FromImage(bitmap))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.ScaleTransform(bitmap.Width / 48f, bitmap.Height / 48f);
                            using (SolidBrush dot = new SolidBrush(Color.FromArgb(112, 163, 255)))
                                g.FillEllipse(dot, 33, 33, 12, 12);
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
            // 被最小化時位置／大小是假的（0×0），存下去下次會跑到螢幕角落，所以不存。
            if (WindowState == FormWindowState.Minimized || ClientSize.Width < 40 || ClientSize.Height < 40) return;
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
                ClientSize = new Size(pixels, pixels);
                Location = KeepVisible(Location);
                foreach (ToolStripMenuItem option in sizesItem.DropDownItems) option.Checked = option == item;
                SaveSettings();
                RenderLayered();
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

        internal static Bitmap RenderBitmap(int dimension, bool connected, bool muted, bool failed, bool busy, bool hovered, int phase)
        {
            // 視窗被最小化（Win+M 等）時大小是 0×0，new Bitmap(0,0) 會丟「參數無效」。
            if (dimension < 1) dimension = 1;
            Bitmap bmp = new Bitmap(dimension, dimension, PixelFormat.Format32bppPArgb);
            using (Graphics graphics = Graphics.FromImage(bmp))
            {
                graphics.Clear(Color.Transparent);
                DrawIcon(graphics, new Size(dimension, dimension), Color.FromArgb(28, 32, 41), hovered, failed, connected, muted, busy, phase);
            }
            return bmp;
        }

        private void RenderLayered()
        {
            if (IsDisposed || !IsHandleCreated || !Visible) return;
            // 被最小化時不畫（大小是 0×0）；還原時 Resize 事件會再畫一次。
            if (WindowState == FormWindowState.Minimized || ClientSize.Width < 1 || ClientSize.Height < 1) return;
            try { RenderLayeredCore(); }
            catch (Exception error) { Program.Log("RENDER " + error.GetType().Name + ": " + error.Message); }
        }
        private void RenderLayeredCore()
        {
            using (Bitmap bitmap = RenderBitmap(ClientSize.Width, connected, muted, failed, busy || waitingLate, hovered, phase))
            {
                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hBitmap = IntPtr.Zero;
                IntPtr oldBitmap = IntPtr.Zero;
                try
                {
                    hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                    oldBitmap = SelectObject(memDc, hBitmap);
                    NativeSize size = new NativeSize { Width = bitmap.Width, Height = bitmap.Height };
                    NativePoint src = new NativePoint { X = 0, Y = 0 };
                    BlendFunction blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                    UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, 2);
                }
                finally
                {
                    if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
                    if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }

        internal static void DrawIcon(Graphics graphics, Size dimensions, Color backgroundColor, bool hovered, bool failed, bool connected, bool muted, bool busy, int phase)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.ScaleTransform(dimensions.Width / 48f, dimensions.Height / 48f);

            if (hovered || busy)
            {
                Color glow = busy ? Color.FromArgb(65, 112, 163, 255) : Color.FromArgb(55, 108, 155, 255);
                using (GraphicsPath halo = Rounded(new RectangleF(0.6f, 0.6f, 46.8f, 46.8f), 12.2f))
                using (Pen haloPen = new Pen(glow, 1.8f))
                    graphics.DrawPath(haloPen, halo);
            }
            else
            {
                using (GraphicsPath shadow = Rounded(new RectangleF(1.2f, 1.8f, 45.6f, 45.6f), 11.5f))
                using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(42, 0, 0, 0)))
                    graphics.FillPath(shadowBrush, shadow);
            }

            using (GraphicsPath background = Rounded(new RectangleF(1.5f, 1.5f, 45f, 45f), 11f))
            using (LinearGradientBrush fill = new LinearGradientBrush(
                new RectangleF(1.5f, 1.5f, 45f, 45f),
                hovered ? Color.FromArgb(48, 58, 78) : Color.FromArgb(36, 41, 53),
                hovered ? Color.FromArgb(35, 43, 59) : backgroundColor,
                LinearGradientMode.Vertical))
            using (Pen edge = new Pen(
                failed ? Color.FromArgb(237, 111, 107)
                : busy ? Color.FromArgb(112, 163, 255)
                : hovered ? Color.FromArgb(120, 168, 255)
                : (connected && muted) ? Color.FromArgb(245, 176, 65)
                : Color.FromArgb(82, 98, 130), 1.35f))
            {
                graphics.FillPath(fill, background);
                graphics.DrawPath(edge, background);
            }

            using (Pen band = new Pen(Color.FromArgb(238, 243, 255), 2.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                graphics.DrawArc(band, 12.5f, 10, 23, 25, 180, 180);
            using (SolidBrush white = new SolidBrush(Color.FromArgb(238, 243, 255)))
            using (GraphicsPath left = Rounded(new RectangleF(10.5f, 22, 7, 14), 3))
            using (GraphicsPath right = Rounded(new RectangleF(30.5f, 22, 7, 14), 3))
            {
                graphics.FillPath(white, left);
                graphics.FillPath(white, right);
            }

            if (busy)
            {
                using (Pen spinner = new Pen(Color.FromArgb(112, 163, 255), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    graphics.DrawArc(spinner, 5.2f, 5.2f, 37.6f, 37.6f, phase, 105);
            }
            else
            {
                using (SolidBrush border = new SolidBrush(backgroundColor))
                    graphics.FillEllipse(border, 32.5f, 32.5f, 12.5f, 12.5f);
                Color dotColor = failed ? WarnColor
                    : (connected && muted) ? MuteColor
                    : connected ? OkColor
                    : Color.FromArgb(128, 142, 168);
                using (SolidBrush dot = new SolidBrush(dotColor))
                    graphics.FillEllipse(dot, 34.5f, 34.5f, 8.5f, 8.5f);
                if (connected && muted && !failed)
                {
                    using (Pen slash = new Pen(backgroundColor, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        graphics.DrawLine(slash, 36.2f, 41.2f, 41.2f, 36.2f);
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; RenderLayered(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; RenderLayered(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Middle)
            {
                StartAction(connected ? "disconnect" : "connect");
                return;
            }
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
            if (dragged) { SaveSettings(); return; }
            if (busy) return;

            if (!connected || muted)
            {
                clickTimer.Stop();
                StartAction("connect");
            }
            else if (clickTimer.Enabled)
            {
                clickTimer.Stop();
                StartAction("disconnect");
            }
            else
            {
                clickTimer.Start();
            }
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (busy || !connected || e.Delta == 0) return;
            float delta = e.Delta > 0 ? 0.02f : -0.02f;
            bool unmutedNow;
            float? next = SwitchOperations.AdjustVolume(delta, out unmutedNow);
            if (!next.HasValue) return;
            volume = next.Value;
            if (unmutedNow) muted = false;
            UpdateTip();
            RenderLayered();
            string msg = "目前音量 " + volume.ToString("0") + "%" + (unmutedNow ? "（已解除靜音）" : "");
            Notify("AirPods 音量", msg, OkColor, 1500);
        }

        private void ShowWidget() { Location = KeepVisible(Location); Show(); BringToFront(); RenderLayered(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x312 && message.WParam.ToInt32() == 1) { Program.Log("HOTKEY connect requested"); StartAction("connect"); return; }
            if (message.Msg == 0x312 && message.WParam.ToInt32() == 2) { Program.Log("HOTKEY disconnect requested"); StartAction("disconnect"); return; }
            base.WndProc(ref message);
        }

        private void RefreshStatus()
        {
            if (busy) return;
            bool wasGuarding = passiveGuarding;
            try
            {
                using (Audio audio = new Audio())
                {
                    if (batteryKeywords.Count == 0)
                        batteryKeywords = SwitchOperations.ExtractBaseKeywords(audio.ConfiguredNames);
                    Endpoint endpoint = SwitchOperations.Current(audio);
                    connected = endpoint != null && endpoint.State == 1;
                    volume = endpoint == null ? 0 : endpoint.VolumePercent ?? 0;
                    muted = endpoint != null && endpoint.Muted == true;

                    if (!waitingLate && !wasGuarding)
                    {
                        if (passiveDetector.Observe(connected))
                        {
                            StartPassiveGuard();
                        }
                        else if (connected)
                        {
                            SwitchOperations.RememberVolume(endpoint);
                        }
                    }
                }
            }
            catch { connected = false; }

            if (connected) RefreshBatteryAsync();
            else batteryPercent = null;

            if (waitingLate) CheckLateConnect();
            UpdateTip();
            RenderLayered();
        }

        private void RefreshBatteryAsync()
        {
            if (batteryChecking || DateTime.Now < nextBatteryCheck) return;
            batteryChecking = true;
            nextBatteryCheck = DateTime.Now.AddSeconds(15);
            List<string> keys = new List<string>(batteryKeywords);
            ThreadPool.QueueUserWorkItem(delegate {
                int? pct = BluetoothBattery.TryReadPercent(keys);
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate {
                    batteryChecking = false;
                    if (connected && batteryPercent != pct)
                    {
                        batteryPercent = pct;
                        UpdateTip();
                    }
                });
            });
        }

        private void StartPassiveGuard()
        {
            if (passiveGuarding || busy) return;
            passiveGuarding = true;
            ThreadPool.QueueUserWorkItem(delegate {
                SwitchResult result = SwitchOperations.GuardPassiveConnect();
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate {
                    passiveGuarding = false;
                    if (result != null && result.Success)
                    {
                        connected = true;
                        volume = result.VolumePercent ?? volume;
                        muted = result.Muted == true;
                        passiveDetector.Sync(true);
                        UpdateTip();
                        RenderLayered();
                        Notify("AirPods 已自動連上", BuildConnectDetail(result, true), OkColor, result.VolumeRestoredFrom.HasValue ? 4000 : 2600);
                    }
                });
            });
        }

        private void CheckLateConnect()
        {
            SwitchResult late = connected && !SwitchOperations.SimulateNeverConnect
                ? SwitchOperations.FinishLateConnect((long)actionTimer.Elapsed.TotalMilliseconds) : null;
            if (late != null)
            {
                waitingLate = false; failed = false; animation.Stop(); actionTimer.Stop();
                volume = late.VolumePercent ?? volume; muted = late.Muted == true;
                passiveDetector.Sync(true);
                Program.Log("LATE CONNECT ok after " + late.Milliseconds + " ms");
                Notify("AirPods 已連回電腦", "耳機較晚連上 · " + BuildConnectDetail(late, false), OkColor, 4000);
            }
            else if (DateTime.Now >= lateUntil)
            {
                waitingLate = false; failed = true; animation.Stop(); actionTimer.Stop();
                passiveDetector.Sync(false);
                Program.Log("LATE CONNECT gave up");
                Notify("AirPods 沒有連上", "已多等 " + LateWaitSeconds + " 秒仍未連上電腦。請確認耳機已戴上，再按一次重試。", WarnColor, 8000);
            }
            else if (lateRetryCount < 2 && DateTime.Now >= nextLateRetryAt)
            {
                lateRetryCount++;
                nextLateRetryAt = DateTime.Now.AddSeconds(20);
                ThreadPool.QueueUserWorkItem(delegate { SwitchOperations.SendGentleRetry(); });
            }
        }

        private void Notify(string heading, string text, Color color, int milliseconds)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Rectangle anchor = Visible ? Bounds : new Rectangle(area.Right - 8, area.Bottom - 8, 0, 0);
            bubble.ShowNear(anchor, heading, text, color, milliseconds);
            Program.Log("NOTICE " + heading + " | " + text);
        }

        private void UpdateTip()
        {
            string bat = batteryPercent.HasValue ? " · 電量 " + batteryPercent.Value + "%" : "";
            string state = busy ? "正在切換…（" + actionTimer.Elapsed.TotalSeconds.ToString("0") + " 秒）"
                : waitingLate ? "等待耳機連上…（最多再 " + Math.Max(0, (lateUntil - DateTime.Now).TotalSeconds).ToString("0") + " 秒）"
                : connected ? "已連線 · 音量 " + volume.ToString("0") + "%" + (muted ? " · 已靜音" : "") + bat : "電腦端未連線";
            tip.SetToolTip(this,
                "AirPods｜" + state +
                "\n左鍵：連回電腦　滾輪：調音量" +
                "\n中鍵／已連線雙擊：中斷連線　拖曳：移動" +
                "\n右鍵：選單　Ctrl+Alt+A 連回｜Ctrl+Alt+D 中斷");
            string trayText = "AirPods｜" + state;
            tray.Text = trayText.Length <= 63 ? trayText : trayText.Substring(0, 63);
            connectItem.Enabled = !busy;
            disconnectItem.Enabled = !busy;
            UpdateTrayIcon();
        }

        internal static string VolumeText(SwitchResult result)
        {
            string now = (result.VolumePercent ?? 0).ToString("0") + "%";
            return result.VolumeRestoredFrom.HasValue
                ? "音量已從 " + result.VolumeRestoredFrom.Value.ToString("0") + "% 調回 " + now
                : "音量 " + now;
        }

        private static string BuildConnectDetail(SwitchResult result, bool passive)
        {
            List<string> parts = new List<string>();
            parts.Add(passive ? "已自動確認有聲" : "已解除靜音");
            parts.Add(VolumeText(result));
            if (result.DefaultOutputForced) parts.Add("已切為預設播放");
            return string.Join(" · ", parts.ToArray());
        }

        private void StartAction(string action)
        {
            if (busy) return;
            clickTimer.Stop();
            busy = true; failed = false; waitingLate = false; slowNotified = false; currentAction = action;
            actionTimer.Restart(); animation.Start(); UpdateTip(); RenderLayered();
            ThreadPool.QueueUserWorkItem(delegate {
                SwitchResult result = SwitchOperations.Run(action);
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)delegate {
                    busy = false;
                    bool canWaitLonger = !result.Success && action == "connect" && result.ReconnectRequested;
                    if (canWaitLonger)
                    {
                        waitingLate = true; failed = false;
                        lateRetryCount = 0;
                        lateUntil = DateTime.Now.AddSeconds(LateWaitSeconds);
                        nextLateRetryAt = DateTime.Now.AddSeconds(15);
                        Program.Log("CONNECT timed out, waiting up to " + LateWaitSeconds + "s in background: " + result.Message);
                        RefreshStatus();
                        return;
                    }
                    failed = !result.Success; animation.Stop(); actionTimer.Stop();
                    if (result.Success) passiveDetector.Sync(result.Connected);
                    RefreshStatus();
                    if (!result.Success) Notify("AirPods 切換失敗", result.Message, WarnColor, 6000);
                    else if (result.Connected) Notify("AirPods 已連回電腦", BuildConnectDetail(result, false), OkColor, result.VolumeRestoredFrom.HasValue ? 4000 : 2600);
                    else Notify("AirPods 已中斷電腦連線", "現在可以從 iPhone 選擇 AirPods。", InfoColor, 3000);
                });
            });
        }
    }
}
