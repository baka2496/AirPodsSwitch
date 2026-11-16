using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AirPodsSwitch
{
    internal sealed class SwitchResult
    {
        public string Action { get; set; }
        public bool Success { get; set; }
        public bool Connected { get; set; }
        public bool? Muted { get; set; }
        public float? VolumePercent { get; set; }
        public bool DefaultOutput { get; set; }
        public bool ConnectedBefore { get; set; }
        public bool ReconnectRequested { get; set; }
        public string DriverResult { get; set; }
        public int Attempts { get; set; }
        public long Milliseconds { get; set; }
        public string Message { get; set; }
    }

    internal static class SwitchOperations
    {
        internal static readonly string Root = AppPaths.DataDirectory;
        private static readonly string VolumeFile = Path.Combine(Root, "last-volume.txt");
        internal static List<Endpoint> Targets(Audio audio)
        {
            return audio.Snapshot(new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Where(e => e.Target && e.State != 4).ToList();
        }
        internal static Endpoint Current(Audio audio)
        {
            return Targets(audio).OrderByDescending(e => e.State == 1).ThenBy(e => e.Name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0).FirstOrDefault();
        }
        internal static void RememberVolume(Endpoint endpoint)
        {
            if (endpoint != null && endpoint.VolumePercent.HasValue && endpoint.VolumePercent.Value > 0.01f)
            {
                string value = (endpoint.VolumePercent.Value / 100f).ToString("R", CultureInfo.InvariantCulture);
                if (!File.Exists(VolumeFile) || File.ReadAllText(VolumeFile) != value) File.WriteAllText(VolumeFile, value);
            }
        }
        private static void RestoreVolumeIfZero(Audio audio, Endpoint endpoint)
        {
            if (!endpoint.VolumePercent.HasValue || endpoint.VolumePercent.Value > 0.01f || !File.Exists(VolumeFile)) return;
            float scalar;
            if (!float.TryParse(File.ReadAllText(VolumeFile), NumberStyles.Float, CultureInfo.InvariantCulture, out scalar) || scalar <= 0 || scalar > 1) return;
            IMMDevice device;
            Marshal.ThrowExceptionForHR(audio.Enumerator.GetDevice(endpoint.Id, out device));
            object raw = null;
            try
            {
                Guid iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out raw));
                Guid context = Program.EventContext;
                Marshal.ThrowExceptionForHR(((IAudioEndpointVolume)raw).SetMasterVolumeLevelScalar(scalar, ref context));
            }
            finally
            {
                if (raw != null) Marshal.ReleaseComObject(raw);
                Marshal.ReleaseComObject(device);
            }
        }
        private static bool IsDefault(Audio audio, Endpoint endpoint)
        {
            IMMDevice device = null;
            try
            {
                if (audio.Enumerator.GetDefaultAudioEndpoint(0, 1, out device) < 0) return false;
                string id;
                Marshal.ThrowExceptionForHR(device.GetId(out id));
                return string.Equals(id, endpoint.Id, StringComparison.OrdinalIgnoreCase);
            }
            finally { if (device != null) Marshal.ReleaseComObject(device); }
        }
        internal static SwitchResult Run(string action)
        {
            Stopwatch timer = Stopwatch.StartNew();
            using (Mutex single = new Mutex(false, "Local\\AirPodsSwitch.Action"))
            {
                bool acquired = false;
                int attempts = 0;
                int lastResult = 0;
                bool reconnectRequested = false;
                bool connectedBefore = false;
                try
                {
                    try { acquired = single.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return new SwitchResult { Message = "切換正在進行，請稍候。" };
                    if (SimulateFailure && action != "disconnect")
                    {
                        Thread.Sleep(8000);
                        reconnectRequested = true;
                        throw new InvalidOperationException("（測試）模擬連線逾時。");
                    }
                    using (Audio audio = new Audio())
                    {
                        Endpoint initial = Current(audio);
                        if (initial == null) throw new InvalidOperationException("找不到選定的耳機，請從浮動圖示的右鍵選單重新選擇耳機。");
                        RememberVolume(initial);
                        connectedBefore = initial.State == 1;
                        bool connect = action == "connect" || (action == "toggle" && initial.State != 1);
                        for (int attempt = 1; attempt <= 3; attempt++)
                        {
                            attempts = attempt;
                            Endpoint endpoint = Current(audio);
                            if (endpoint == null) { Thread.Sleep(500); continue; }
                            if (connect || (endpoint.State == 1) != connect)
                            {
                                lastResult = NativeBluetoothAudio.Request(audio, endpoint.Id, connect, false);
                                reconnectRequested = connect;
                                if (lastResult < 0) { Thread.Sleep(500); continue; }
                                for (int tick = 0; tick < 24; tick++)
                                {
                                    Thread.Sleep(250);
                                    endpoint = Current(audio);
                                    if (endpoint != null && (endpoint.State == 1) == connect) break;
                                }
                            }
                            if (endpoint == null || (endpoint.State == 1) != connect) continue;
                            if (connect)
                            {
                                Thread.Sleep(600);
                                endpoint = Current(audio);
                                if (endpoint == null || endpoint.State != 1) continue;
                                RestoreVolumeIfZero(audio, endpoint);
                                audio.Unmute(endpoint.Id);
                                endpoint = Current(audio);
                                if (endpoint == null || endpoint.State != 1 || endpoint.Muted != false) continue;
                                RememberVolume(endpoint);
                            }
                            SwitchResult result = new SwitchResult {
                                Action = action, Success = true, Connected = connect, Muted = endpoint.Muted,
                                ConnectedBefore = connectedBefore, ReconnectRequested = reconnectRequested, DriverResult = lastResult.ToString("X8"),
                                VolumePercent = endpoint.VolumePercent, Attempts = attempt,
                                Milliseconds = timer.ElapsedMilliseconds, DefaultOutput = connect && IsDefault(audio, endpoint),
                                Message = connect ? "已連回電腦，已解除靜音。" : "已中斷電腦連線，可改用 iPhone。"
                            };
                            Save(result);
                            return result;
                        }
                        throw new InvalidOperationException("未能完成連線切換。請確認 AirPods 已戴上；若正在手機通話，請先結束通話再試。");
                    }
                }
                catch (Exception error)
                {
                    SwitchResult result = new SwitchResult { Action = action, Message = error.Message, Attempts = attempts, ConnectedBefore = connectedBefore, ReconnectRequested = reconnectRequested, DriverResult = lastResult.ToString("X8"), Milliseconds = timer.ElapsedMilliseconds };
                    Save(result);
                    return result;
                }
                finally { if (acquired) single.ReleaseMutex(); }
            }
        }
        // 連回電腦逾時後，耳機常常晚幾秒才真的連上。浮動圖示會在背景繼續看；
        // 一看到已連線就呼叫這裡：補做解除靜音、恢復音量，並記一筆成功紀錄。
        // 回傳 null 代表耳機仍未連上（或另一個切換正在進行）。
        internal static SwitchResult FinishLateConnect(long waitedMilliseconds)
        {
            using (Mutex single = new Mutex(false, "Local\\AirPodsSwitch.Action"))
            {
                bool acquired = false;
                try
                {
                    try { acquired = single.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return null;
                    using (Audio audio = new Audio())
                    {
                        Endpoint endpoint = Current(audio);
                        if (endpoint == null || endpoint.State != 1) return null;
                        RestoreVolumeIfZero(audio, endpoint);
                        audio.Unmute(endpoint.Id);
                        endpoint = Current(audio);
                        if (endpoint == null || endpoint.State != 1) return null;
                        RememberVolume(endpoint);
                        SwitchResult result = new SwitchResult {
                            Action = "connect-late", Success = true, Connected = true, Muted = endpoint.Muted,
                            VolumePercent = endpoint.VolumePercent, Milliseconds = waitedMilliseconds,
                            DefaultOutput = IsDefault(audio, endpoint), DriverResult = "00000000",
                            Message = "耳機較晚連上，已連回電腦並解除靜音。"
                        };
                        Save(result);
                        return result;
                    }
                }
                catch (Exception error) { Program.Log("LATE CONNECT " + error.Message); return null; }
                finally { if (acquired) single.ReleaseMutex(); }
            }
        }
        // 測試用：環境變數 AIRPODS_SIMULATE_FAIL=1 時，「連回電腦」等 8 秒後直接回報逾時，
        // 不呼叫藍牙驅動，用來在不中斷耳機的情況下測試「背景繼續等」與提示流程。
        // 設為 2 時，背景等待也一律當作沒連上，用來測試 60 秒後的失敗提示。
        internal static bool SimulateFailure
        {
            get { string v = Environment.GetEnvironmentVariable("AIRPODS_SIMULATE_FAIL"); return v == "1" || v == "2"; }
        }
        internal static bool SimulateNeverConnect
        {
            get { return Environment.GetEnvironmentVariable("AIRPODS_SIMULATE_FAIL") == "2"; }
        }
        private static void Save(SwitchResult result)
        {
            string json = new JavaScriptSerializer().Serialize(result);
            File.WriteAllText(Path.Combine(Root, "last-result.json"), json);
            File.AppendAllText(Path.Combine(Root, "switch.log"), DateTimeOffset.Now.ToString("o") + " " + json + Environment.NewLine);
        }
    }

    internal static class SwitchMain
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--configure")) return HeadphoneSetup.Configure() ? 0 : 2;
            if (args.Contains("--quiet") && !HeadphoneSetup.HasConfiguredTarget()) return 2;
            if (!HeadphoneSetup.EnsureConfigured()) return 2;
            string action = args.FirstOrDefault(a => a == "--connect" || a == "--disconnect" || a == "--toggle");
            if (action != null)
            {
                SwitchResult result = SwitchOperations.Run(action.Substring(2));
                if (!result.Success && !args.Contains("--quiet")) MessageBox.Show(result.Message, "AirPods 一鍵切換", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return result.Success ? 0 : 1;
            }
            bool created;
            using (Mutex panel = new Mutex(true, "Local\\AirPodsSwitch.Panel", out created))
            {
                if (!created)
                {
                    if (!args.Contains("--background"))
                        using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AirPodsSwitch.ShowPanel")) show.Set();
                    return 0;
                }
                try { Application.Run(new CompactSwitchWindow(args.Contains("--background"))); }
                finally { panel.ReleaseMutex(); }
            }
            return 0;
        }
    }

}
