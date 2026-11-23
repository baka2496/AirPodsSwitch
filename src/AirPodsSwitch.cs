using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

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
        public bool DefaultOutputForced { get; set; }
        public bool ConnectedBefore { get; set; }
        public bool ReconnectRequested { get; set; }
        public string DriverResult { get; set; }
        public int Attempts { get; set; }
        public long Milliseconds { get; set; }
        public string Message { get; set; }
        public string Timeline { get; set; }
        public float? VolumeRestoredFrom { get; set; }
    }

    // 偵測「從未連線變成已連線」的純狀態機：啟動時若原本就連著不誤觸發。
    internal sealed class PassiveConnectDetector
    {
        private bool? previous;
        internal bool Observe(bool connected)
        {
            bool justConnected = previous.HasValue && !previous.Value && connected;
            previous = connected;
            return justConnected;
        }
        internal void Sync(bool connected)
        {
            previous = connected;
        }
    }

    internal static class SwitchOperations
    {
        internal static readonly string Root = AppPaths.DataDirectory;
        private static readonly string VolumeFile = Path.Combine(Root, "last-volume.txt");
        private static readonly string DevicesFile = Path.Combine(Root, "devices.txt");
        private const string ActionMutexName = "Local\\AirPodsSwitch.Action";

        // 漸進節奏：前 3 次每 2.5 秒搶快，第 4～6 次放寬到 4.0～4.5 秒，
        // 避免藍牙交接進行中一直被打斷重來（總時間約 20.5 秒）。
        internal static readonly int[] AttemptWindowsMs = new int[] { 2500, 2500, 2500, 4000, 4500, 4500 };
        private const int PollMs = 100;
        private const int AudioGuardMs = 600;
        internal const float VolumeDriftLimit = 10f;

        internal static List<string> ExtractBaseKeywords(IEnumerable<string> configuredNames)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (configuredNames != null)
            {
                foreach (string raw in configuredNames)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string s = raw.Trim();
                    int open = s.IndexOf('(');
                    int close = s.LastIndexOf(')');
                    if (open >= 0 && close > open + 1) s = s.Substring(open + 1, close - open - 1).Trim();
                    string[] suffixes = new string[] { " - Find My", " Hands-Free AG Audio", " Hands-Free", " Stereo" };
                    foreach (string suffix in suffixes)
                    {
                        if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                            s = s.Substring(0, s.Length - suffix.Length).Trim();
                    }
                    if (s.Length >= 2) keys.Add(s);
                }
            }
            keys.Add("AirPods");
            return keys.ToList();
        }

        internal static bool MatchFallbackName(string endpointName, IEnumerable<string> keywords)
        {
            if (string.IsNullOrWhiteSpace(endpointName) || keywords == null) return false;
            foreach (string key in keywords)
            {
                if (!string.IsNullOrEmpty(key) && endpointName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        internal static List<Endpoint> Targets(Audio audio)
        {
            HashSet<string> devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(DevicesFile))
                foreach (string line in File.ReadAllLines(DevicesFile))
                    if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("#")) devices.Add(line.Trim());
            List<Endpoint> all = audio.Snapshot(devices);
            List<Endpoint> matches = all.Where(e => e.Target && e.State != 4).ToList();

            if (matches.Count == 0)
            {
                List<string> keywords = ExtractBaseKeywords(audio.ConfiguredNames);
                matches = all.Where(e => e.State != 4 && MatchFallbackName(e.Name, keywords)).ToList();
                if (matches.Count > 0)
                    Program.Log("AUTO-REBIND matched " + matches.Count + " endpoint(s)");
            }

            bool changed = false;
            foreach (Endpoint endpoint in matches) changed |= devices.Add(endpoint.Id);
            if (changed) File.WriteAllLines(DevicesFile, devices.OrderBy(id => id).ToArray());
            return matches;
        }

        internal static Endpoint Current(Audio audio)
        {
            return Targets(audio)
                .OrderByDescending(e => e.State == 1)
                .ThenBy(e => e.Name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0)
                .FirstOrDefault();
        }

        internal static void RememberVolume(Endpoint endpoint)
        {
            if (endpoint != null && endpoint.VolumePercent.HasValue && endpoint.VolumePercent.Value > 0.01f)
                RememberVolumeScalar(endpoint.VolumePercent.Value / 100f);
        }

        internal static void RememberVolumeScalar(float scalar)
        {
            if (scalar <= 0.0001f || scalar > 1.0001f) return;
            float clamped = Math.Max(0.001f, Math.Min(1f, scalar));
            string value = clamped.ToString("R", CultureInfo.InvariantCulture);
            try
            {
                if (!File.Exists(VolumeFile) || File.ReadAllText(VolumeFile) != value)
                    File.WriteAllText(VolumeFile, value);
            }
            catch { }
        }

        internal static float? RememberedVolume()
        {
            if (!File.Exists(VolumeFile)) return null;
            float scalar;
            if (!float.TryParse(File.ReadAllText(VolumeFile), NumberStyles.Float, CultureInfo.InvariantCulture, out scalar) || scalar <= 0 || scalar > 1) return null;
            return scalar;
        }

        internal static bool IsVolumeDrifted(float? currentPercent, float? rememberedScalar)
        {
            return rememberedScalar.HasValue && currentPercent.HasValue
                && Math.Abs(currentPercent.Value - rememberedScalar.Value * 100f) > VolumeDriftLimit;
        }

        private static bool VolumeDrifted(Endpoint endpoint)
        {
            return IsVolumeDrifted(endpoint.VolumePercent, RememberedVolume());
        }

        private static void RestoreRememberedVolume(Audio audio, Endpoint endpoint)
        {
            float? remembered = RememberedVolume();
            if (remembered.HasValue) SetVolume(audio, endpoint, remembered.Value);
        }

        private static void RestoreVolumeIfZero(Audio audio, Endpoint endpoint)
        {
            if (!endpoint.VolumePercent.HasValue || endpoint.VolumePercent.Value > 0.01f) return;
            RestoreRememberedVolume(audio, endpoint);
        }

        private static void SetVolume(Audio audio, Endpoint endpoint, float scalar)
        {
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

        internal static float? AdjustVolume(float deltaScalar, out bool unmutedNow)
        {
            unmutedNow = false;
            try
            {
                using (Audio audio = new Audio())
                {
                    Endpoint endpoint = Current(audio);
                    if (endpoint == null || endpoint.State != 1 || !endpoint.VolumePercent.HasValue) return null;
                    float current = endpoint.VolumePercent.Value / 100f;
                    float step = Math.Abs(deltaScalar);
                    float rawNext = Math.Max(0f, Math.Min(1f, current + deltaScalar));
                    float snapped = step > 0.005f ? (float)(Math.Round(rawNext / step) * step) : rawNext;
                    snapped = Math.Max(0f, Math.Min(1f, snapped));
                    SetVolume(audio, endpoint, snapped);
                    if (deltaScalar > 0 && endpoint.Muted == true)
                    {
                        audio.Unmute(endpoint.Id);
                        unmutedNow = true;
                    }
                    if (snapped > 0.005f) RememberVolumeScalar(snapped);
                    return snapped * 100f;
                }
            }
            catch (Exception error)
            {
                Program.Log("VOLUME WHEEL " + error.Message);
                return null;
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

        private static Endpoint FixAudio(Audio audio, Stopwatch timer, List<string> timeline, out float? volumeFrom, out bool defaultForced)
        {
            volumeFrom = null;
            defaultForced = false;
            long connectedAt = timer.ElapsedMilliseconds;
            long giveUpAt = connectedAt + 2500;
            long lastFixAt = connectedAt;
            Endpoint endpoint = null;
            bool unmutedOnce = false;
            bool defaultChecked = false;

            string simulate = Environment.GetEnvironmentVariable("AIRPODS_SIMULATE_VOLUME");
            float simulated;
            if (!string.IsNullOrEmpty(simulate) && float.TryParse(simulate, NumberStyles.Float, CultureInfo.InvariantCulture, out simulated) && simulated > 0 && simulated <= 100)
            {
                Endpoint first = Current(audio);
                if (first != null && first.State == 1) { SetVolume(audio, first, simulated / 100f); timeline.Add("sim-vol" + simulate + "@" + timer.ElapsedMilliseconds); }
            }

            while (true)
            {
                endpoint = Current(audio);
                if (endpoint == null || endpoint.State != 1) return null;

                if (!defaultChecked)
                {
                    defaultChecked = true;
                    bool isHandsFree = endpoint.Name != null && endpoint.Name.IndexOf("Hands-Free", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isHandsFree && !IsDefault(audio, endpoint))
                    {
                        if (audio.SetDefaultOutput(endpoint.Id))
                        {
                            defaultForced = true;
                            timeline.Add("default@" + timer.ElapsedMilliseconds);
                        }
                    }
                }

                bool zero = endpoint.VolumePercent.HasValue && endpoint.VolumePercent.Value <= 0.01f;
                bool drifted = !zero && VolumeDrifted(endpoint);
                if (!unmutedOnce || endpoint.Muted != false || zero || drifted)
                {
                    try
                    {
                        if (drifted)
                        {
                            if (!volumeFrom.HasValue) volumeFrom = endpoint.VolumePercent;
                            RestoreRememberedVolume(audio, endpoint);
                            timeline.Add("vol" + endpoint.VolumePercent.Value.ToString("0") + "->back@" + timer.ElapsedMilliseconds);
                        }
                        else RestoreVolumeIfZero(audio, endpoint);

                        if (!unmutedOnce || endpoint.Muted != false)
                        {
                            audio.Unmute(endpoint.Id);
                            timeline.Add((unmutedOnce ? "refix@" : "unmute@") + timer.ElapsedMilliseconds);
                        }
                        unmutedOnce = true;
                        lastFixAt = timer.ElapsedMilliseconds;
                    }
                    catch (Exception error) { timeline.Add("unmute-err@" + timer.ElapsedMilliseconds + ":" + error.GetType().Name); }
                }
                else if (timer.ElapsedMilliseconds - connectedAt >= AudioGuardMs && timer.ElapsedMilliseconds - lastFixAt >= 300) return endpoint;

                if (timer.ElapsedMilliseconds >= giveUpAt)
                    return unmutedOnce && endpoint.Muted == false ? endpoint : null;
                Thread.Sleep(PollMs);
            }
        }

        internal static SwitchResult Run(string action)
        {
            Stopwatch timer = Stopwatch.StartNew();
            using (Mutex single = new Mutex(false, ActionMutexName))
            {
                bool acquired = false;
                int attempts = 0;
                int lastResult = 0;
                bool reconnectRequested = false;
                bool connectedBefore = false;
                int polls = 0;
                float? volumeFrom = null;
                bool defaultForced = false;
                List<string> timeline = new List<string>();
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
                        if (initial.State == 1) RememberVolume(initial);
                        connectedBefore = initial.State == 1;
                        bool connect = action == "connect" || (action == "toggle" && initial.State != 1);
                        timeline.Add("start " + (connectedBefore ? "on" : "off") + " wprog");
                        for (int attempt = 1; attempt <= AttemptWindowsMs.Length; attempt++)
                        {
                            attempts = attempt;
                            Endpoint endpoint = Current(audio);
                            if (endpoint == null) { Thread.Sleep(500); continue; }
                            if (connect || (endpoint.State == 1) != connect)
                            {
                                long sent = timer.ElapsedMilliseconds;
                                lastResult = NativeBluetoothAudio.Request(audio, endpoint.Id, connect, false);
                                reconnectRequested = connect;
                                timeline.Add("req" + attempt + "@" + sent + "+" + (timer.ElapsedMilliseconds - sent) + "=" + lastResult.ToString("X8"));
                                if (lastResult < 0) { Thread.Sleep(500); continue; }
                                long windowEnd = timer.ElapsedMilliseconds + AttemptWindowsMs[attempt - 1];
                                while (timer.ElapsedMilliseconds < windowEnd)
                                {
                                    Thread.Sleep(PollMs);
                                    endpoint = Current(audio);
                                    polls++;
                                    if (endpoint != null && (endpoint.State == 1) == connect) break;
                                }
                            }
                            if (endpoint == null || (endpoint.State == 1) != connect) continue;
                            timeline.Add((connect ? "on@" : "off@") + timer.ElapsedMilliseconds);
                            if (connect)
                            {
                                endpoint = FixAudio(audio, timer, timeline, out volumeFrom, out defaultForced);
                                if (endpoint == null) continue;
                                endpoint = Current(audio) ?? endpoint;
                                RememberVolume(endpoint);
                            }
                            timeline.Add("done@" + timer.ElapsedMilliseconds + " polls=" + polls);
                            SwitchResult result = new SwitchResult
                            {
                                Timeline = string.Join(" ", timeline.ToArray()),
                                VolumeRestoredFrom = volumeFrom,
                                DefaultOutputForced = defaultForced,
                                Action = action,
                                Success = true,
                                Connected = connect,
                                Muted = endpoint.Muted,
                                ConnectedBefore = connectedBefore,
                                ReconnectRequested = reconnectRequested,
                                DriverResult = lastResult.ToString("X8"),
                                VolumePercent = endpoint.VolumePercent,
                                Attempts = attempt,
                                Milliseconds = timer.ElapsedMilliseconds,
                                DefaultOutput = connect && IsDefault(audio, endpoint),
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
                    timeline.Add("fail@" + timer.ElapsedMilliseconds + " polls=" + polls);
                    SwitchResult result = new SwitchResult
                    {
                        Timeline = string.Join(" ", timeline.ToArray()),
                        Action = action,
                        Message = error.Message,
                        Attempts = attempts,
                        ConnectedBefore = connectedBefore,
                        ReconnectRequested = reconnectRequested,
                        DriverResult = lastResult.ToString("X8"),
                        Milliseconds = timer.ElapsedMilliseconds
                    };
                    Save(result);
                    return result;
                }
                finally { if (acquired) single.ReleaseMutex(); }
            }
        }

        internal static bool SendGentleRetry()
        {
            if (SimulateFailure) return false;
            using (Mutex single = new Mutex(false, ActionMutexName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = single.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return false;
                    using (Audio audio = new Audio())
                    {
                        Endpoint endpoint = Current(audio);
                        if (endpoint == null || endpoint.State == 1) return false;
                        int hr = NativeBluetoothAudio.Request(audio, endpoint.Id, true, false);
                        Program.Log("LATE RETRY hr=" + hr.ToString("X8"));
                        return hr >= 0;
                    }
                }
                catch (Exception error) { Program.Log("LATE RETRY " + error.Message); return false; }
                finally { if (acquired) single.ReleaseMutex(); }
            }
        }

        internal static SwitchResult FinishLateConnect(long waitedMilliseconds)
        {
            return CompleteAutoFix("connect-late", waitedMilliseconds, "耳機較晚連上，已連回電腦並解除靜音。");
        }

        internal static SwitchResult GuardPassiveConnect()
        {
            return CompleteAutoFix("connect-auto", 0, "偵測到 AirPods 自動連上，已完成音量與靜音保護。");
        }

        private static SwitchResult CompleteAutoFix(string actionName, long waitedMilliseconds, string message)
        {
            Stopwatch watch = Stopwatch.StartNew();
            using (Mutex single = new Mutex(false, ActionMutexName))
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
                        List<string> timeline = new List<string>();
                        float? volumeFrom;
                        bool defaultForced;
                        endpoint = FixAudio(audio, watch, timeline, out volumeFrom, out defaultForced);
                        if (endpoint == null) return null;
                        endpoint = Current(audio);
                        if (endpoint == null || endpoint.State != 1) return null;
                        RememberVolume(endpoint);
                        SwitchResult result = new SwitchResult
                        {
                            Timeline = string.Join(" ", timeline.ToArray()),
                            VolumeRestoredFrom = volumeFrom,
                            DefaultOutputForced = defaultForced,
                            Action = actionName,
                            Success = true,
                            Connected = true,
                            Muted = endpoint.Muted,
                            VolumePercent = endpoint.VolumePercent,
                            Milliseconds = waitedMilliseconds > 0 ? waitedMilliseconds : watch.ElapsedMilliseconds,
                            DefaultOutput = IsDefault(audio, endpoint),
                            DriverResult = "00000000",
                            Message = message
                        };
                        Save(result);
                        return result;
                    }
                }
                catch (Exception error) { Program.Log(actionName.ToUpperInvariant() + " " + error.Message); return null; }
                finally { if (acquired) single.ReleaseMutex(); }
            }
        }

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
            try { File.WriteAllText(Path.Combine(Root, "last-result.json"), json, System.Text.Encoding.UTF8); } catch { }
            LogStore.AppendAndTrim(Path.Combine(Root, "switch.log"), DateTimeOffset.Now.ToString("o") + " " + json, 1000);
        }
    }

    internal static class SelfTestRunner
    {
        internal static int Run()
        {
            List<string> passed = new List<string>();
            List<string> failed = new List<string>();
            Action<string, Action> test = (name, body) =>
            {
                try { body(); passed.Add(name); }
                catch (Exception ex) { failed.Add(name + ": " + ex.Message); }
            };
            Action<bool, string> assert = (ok, msg) => { if (!ok) throw new Exception(msg); };

            test("ProgressivePacing", () =>
            {
                int[] w = SwitchOperations.AttemptWindowsMs;
                assert(w.Length == 6, "expected 6 progressive attempts");
                assert(w[0] == 2500 && w[1] == 2500 && w[2] == 2500, "first 3 attempts should be 2500ms");
                assert(w[3] >= 4000 && w[4] >= 4000 && w[5] >= 4000, "later attempts should be >= 4000ms");
                int total = w.Sum();
                assert(total >= 19000 && total <= 22000, "total window should be ~20.5s");
            });

            test("VolumeDriftGuard", () =>
            {
                assert(!SwitchOperations.IsVolumeDrifted(46f, 0.45f), "1% diff should not drift");
                assert(!SwitchOperations.IsVolumeDrifted(55f, 0.45f), "10% diff should not drift");
                assert(SwitchOperations.IsVolumeDrifted(80f, 0.45f), "35% jump must trigger drift");
                assert(SwitchOperations.IsVolumeDrifted(10f, 0.45f), "35% drop must trigger drift");
                assert(!SwitchOperations.IsVolumeDrifted(80f, null), "null remembered volume should not drift");
            });

            test("PassiveConnectDetector", () =>
            {
                PassiveConnectDetector det = new PassiveConnectDetector();
                assert(!det.Observe(true), "startup already connected must not fire");
                assert(!det.Observe(true), "staying connected must not fire");
                assert(!det.Observe(false), "disconnect must not fire");
                assert(det.Observe(true), "off -> on must fire");
                assert(!det.Observe(true), "subsequent on must not fire again");
                det.Sync(false);
                assert(det.Observe(true), "after Sync(false), on must fire");
            });

            test("LogRotationTrim", () =>
            {
                string[] input = Enumerable.Range(1, 1500).Select(i => "line-" + i).ToArray();
                string[] trimmed = LogStore.TrimLines(input, 1000);
                assert(trimmed.Length == 1000, "expected 1000 lines");
                assert(trimmed[0] == "line-501" && trimmed[999] == "line-1500", "must keep latest 1000 lines");
            });

            test("FallbackDeviceRebind", () =>
            {
                List<string> keys = SwitchOperations.ExtractBaseKeywords(new string[] {
                    "Headphones (My AirPods Pro - Find My)",
                    "Headphones (My AirPods Pro Hands-Free)"
                });
                assert(SwitchOperations.MatchFallbackName("Headphones (My AirPods Pro Stereo)", keys), "should match renamed stereo endpoint");
                assert(SwitchOperations.MatchFallbackName("Headphones (AirPods Max)", keys), "should match AirPods keyword");
                assert(!SwitchOperations.MatchFallbackName("Speakers (Realtek High Definition Audio)", keys), "must not match unrelated speaker");
            });

            test("PolicyConfigActivation", () =>
            {
                assert(Audio.CanActivatePolicyConfig(), "IPolicyConfig COM activation failed");
            });

            test("LayeredIconRendering", () =>
            {
                foreach (string mode in new string[] { "on", "off", "muted", "failed", "busy" })
                {
                    using (Bitmap bmp = CompactSwitchWindow.RenderBitmap(48, mode == "on", mode == "muted", mode == "failed", mode == "busy", false, 60))
                    {
                        assert(bmp.Width == 48 && bmp.Height == 48, "wrong size for " + mode);
                        assert(bmp.GetPixel(0, 0).A == 0, "outer corner must be transparent for " + mode);
                        assert(bmp.GetPixel(24, 24).A > 200, "center must be opaque for " + mode);
                    }
                }
            });

            test("ZeroSizeRendering", () =>
            {
                // 視窗被最小化時大小為 0×0，畫圖示不可丟「參數無效」。
                using (Bitmap bmp = CompactSwitchWindow.RenderBitmap(0, true, false, false, false, false, 0))
                    assert(bmp.Width >= 1 && bmp.Height >= 1, "zero size must be clamped to at least 1px");
            });

            var report = new { Passed = failed.Count == 0, PassedCount = passed.Count, FailedCount = failed.Count, PassedTests = passed, FailedTests = failed };
            string json = new JavaScriptSerializer().Serialize(report);
            File.WriteAllText(Path.Combine(SwitchOperations.Root, "self-test-report.json"), json, System.Text.Encoding.UTF8);
            return failed.Count == 0 ? 0 : 1;
        }
    }

    internal static class SwitchMain
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Contains("--self-test")) return SelfTestRunner.Run();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 錯誤不再跳「Microsoft .NET Framework 未處理的例外狀況」視窗，改成把完整錯誤寫進 activity.log，程式繼續執行。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) => Program.Log("UNHANDLED (UI) " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => Program.Log("UNHANDLED (background) " + e.ExceptionObject);
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
