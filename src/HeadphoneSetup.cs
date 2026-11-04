using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace AirPodsSwitch
{
    internal static class HeadphoneSetup
    {
        internal static bool HasConfiguredTarget()
        {
            try
            {
                if (!File.Exists(AppPaths.NamesFile)) return false;
                using (Audio audio = new Audio())
                    return audio.Snapshot(new HashSet<string>()).Any(endpoint => endpoint.Target && endpoint.State != 4);
            }
            catch { return false; }
        }

        internal static bool EnsureConfigured()
        {
            return HasConfiguredTarget() || Configure();
        }

        internal static bool Configure()
        {
            using (HeadphonePicker picker = new HeadphonePicker()) return picker.ShowDialog() == DialogResult.OK;
        }
    }

    internal sealed class HeadphonePicker : Form
    {
        private readonly ComboBox devices;
        private readonly Label status;
        private readonly Button confirm;

        internal HeadphonePicker()
        {
            Text = "AirPodsSwitch — 選擇耳機";
            ClientSize = new Size(590, 285);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft JhengHei UI", 10.5f);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(24, 27, 34);
            ForeColor = Color.White;
            Label title = new Label { Text = "選擇要快速連線的耳機", Location = new Point(24, 22), Size = new Size(540, 36), Font = new Font(Font.FontFamily, 17, FontStyle.Bold) };
            Label description = new Label { Text = "先在 Windows 完成藍牙配對，再從下方選擇這副耳機。", Location = new Point(24, 66), Size = new Size(540, 28) };
            devices = new ComboBox { AccessibleName = "已配對的藍牙耳機", DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Name", Location = new Point(24, 105), Size = new Size(542, 31), DropDownWidth = 700 };
            status = new Label { Text = "正在尋找已配對的藍牙耳機…", Location = new Point(24, 151), Size = new Size(542, 45), ForeColor = Color.FromArgb(177, 188, 206) };
            Button bluetooth = new Button { Text = "開啟藍牙設定", Location = new Point(24, 224), Size = new Size(145, 36) };
            bluetooth.Click += (sender, e) => Process.Start(new ProcessStartInfo { FileName = "ms-settings:bluetooth", UseShellExecute = true });
            Button refresh = new Button { Text = "重新整理", Location = new Point(180, 224), Size = new Size(100, 36) };
            refresh.Click += (sender, e) => RefreshDevices();
            Button cancel = new Button { Text = "取消", Location = new Point(353, 224), Size = new Size(90, 36), DialogResult = DialogResult.Cancel };
            confirm = new Button { Text = "使用這副耳機", Location = new Point(454, 224), Size = new Size(112, 36), Enabled = false };
            confirm.Click += (sender, e) => {
                Endpoint selected = devices.SelectedItem as Endpoint;
                if (selected == null) return;
                try
                {
                    File.WriteAllLines(AppPaths.NamesFile, new string[] { selected.Name }, new UTF8Encoding(false));
                    DialogResult = DialogResult.OK;
                }
                catch { status.Text = "目前無法儲存設定，請稍後再試。"; }
            };
            devices.SelectedIndexChanged += (sender, e) => confirm.Enabled = devices.SelectedItem != null;
            Controls.AddRange(new Control[] { title, description, devices, status, bluetooth, refresh, cancel, confirm });
            AcceptButton = confirm;
            CancelButton = cancel;
            Shown += (sender, e) => RefreshDevices();
        }

        private void RefreshDevices()
        {
            devices.Items.Clear();
            confirm.Enabled = false;
            try
            {
                List<Endpoint> available = new List<Endpoint>();
                using (Audio audio = new Audio())
                {
                    foreach (Endpoint endpoint in audio.Snapshot(new HashSet<string>()).Where(e => e.State == 1 || e.State == 8))
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(endpoint.Name) && NativeBluetoothAudio.Request(audio, endpoint.Id, true, true) >= 0)
                                available.Add(endpoint);
                        }
                        catch { }
                    }
                }
                foreach (Endpoint endpoint in available.OrderBy(e => e.Name.IndexOf("AirPods", StringComparison.OrdinalIgnoreCase) < 0).ThenBy(e => e.Name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0).ThenBy(e => e.Name))
                    if (!devices.Items.Cast<Endpoint>().Any(e => string.Equals(e.Name, endpoint.Name, StringComparison.OrdinalIgnoreCase))) devices.Items.Add(endpoint);
                if (devices.Items.Count > 0)
                {
                    devices.SelectedIndex = 0;
                    status.Text = "選擇後會記住設定；之後可從浮動圖示的右鍵選單更換。";
                }
                else status.Text = "尚未找到可用的藍牙耳機。請先完成配對，再按「重新整理」。";
            }
            catch { status.Text = "暫時無法讀取耳機清單，請稍後按「重新整理」。"; }
        }
    }
}
