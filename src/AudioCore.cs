using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AirPodsSwitch
{
    public sealed class Endpoint
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public uint State { get; set; }
        public bool Target { get; set; }
        public bool? Muted { get; set; }
        public float? VolumePercent { get; set; }
    }

    internal sealed class Audio : IDisposable
    {
        internal readonly IMMDeviceEnumerator Enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        private static readonly Guid VolumeId = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
        private readonly HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal IEnumerable<string> ConfiguredNames { get { return names; } }
        internal Audio()
        {
            string path = AppPaths.NamesFile;
            if (File.Exists(path))
                foreach (string line in File.ReadAllLines(path))
                    if (line.Trim().Length > 0 && !line.TrimStart().StartsWith("#")) names.Add(line.Trim());
        }
        internal List<Endpoint> Snapshot(HashSet<string> targets)
        {
            IMMDeviceCollection collection;
            Marshal.ThrowExceptionForHR(Enumerator.EnumAudioEndpoints(0, 15, out collection));
            try
            {
                uint count;
                Marshal.ThrowExceptionForHR(collection.GetCount(out count));
                List<Endpoint> result = new List<Endpoint>();
                for (uint index = 0; index < count; index++)
                {
                    IMMDevice device;
                    Marshal.ThrowExceptionForHR(collection.Item(index, out device));
                    try
                    {
                        string id;
                        uint state;
                        Marshal.ThrowExceptionForHR(device.GetId(out id));
                        Marshal.ThrowExceptionForHR(device.GetState(out state));
                        string name = "";
                        try { name = ReadName(device); } catch (COMException) { }
                        Endpoint entry = new Endpoint { Id = id, State = state, Name = name, Target = targets.Contains(id) || names.Contains(name) };
                        if (state == 1)
                        {
                            IAudioEndpointVolume volume = null;
                            try
                            {
                                volume = GetVolume(device);
                                bool mute;
                                float scalar;
                                Marshal.ThrowExceptionForHR(volume.GetMute(out mute));
                                Marshal.ThrowExceptionForHR(volume.GetMasterVolumeLevelScalar(out scalar));
                                entry.Muted = mute;
                                entry.VolumePercent = scalar * 100;
                            }
                            catch (COMException) { }
                            finally { if (volume != null) Marshal.ReleaseComObject(volume); }
                        }
                        result.Add(entry);
                    }
                    finally { Marshal.ReleaseComObject(device); }
                }
                return result;
            }
            finally { Marshal.ReleaseComObject(collection); }
        }

        internal bool Unmute(string id)
        {
            if (!id.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only playback outputs may be changed.");
            IMMDevice device;
            Marshal.ThrowExceptionForHR(Enumerator.GetDevice(id, out device));
            IAudioEndpointVolume volume = null;
            try
            {
                uint state;
                Marshal.ThrowExceptionForHR(device.GetState(out state));
                if (state != 1) throw new InvalidOperationException("Output is not connected.");
                volume = GetVolume(device);
                bool before;
                Marshal.ThrowExceptionForHR(volume.GetMute(out before));
                if (before)
                {
                    Guid context = Program.EventContext;
                    Marshal.ThrowExceptionForHR(volume.SetMute(false, ref context));
                    bool after;
                    Marshal.ThrowExceptionForHR(volume.GetMute(out after));
                    if (after) throw new InvalidOperationException("Output is still muted.");
                }
                return before;
            }
            finally
            {
                if (volume != null) Marshal.ReleaseComObject(volume);
                Marshal.ReleaseComObject(device);
            }
        }

        internal bool SetDefaultOutput(string id)
        {
            if (string.IsNullOrEmpty(id) || !id.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase)) return false;
            IPolicyConfig config = null;
            try
            {
                config = (IPolicyConfig)new PolicyConfigClient();
                int hr0 = config.SetDefaultEndpoint(id, 0);
                int hr1 = config.SetDefaultEndpoint(id, 1);
                return hr0 >= 0 && hr1 >= 0;
            }
            catch { return false; }
            finally { if (config != null) Marshal.ReleaseComObject(config); }
        }

        internal static bool CanActivatePolicyConfig()
        {
            IPolicyConfig config = null;
            try
            {
                config = (IPolicyConfig)new PolicyConfigClient();
                return config != null;
            }
            catch { return false; }
            finally { if (config != null) Marshal.ReleaseComObject(config); }
        }

        private static IAudioEndpointVolume GetVolume(IMMDevice device)
        {
            object result;
            Guid id = VolumeId;
            Marshal.ThrowExceptionForHR(device.Activate(ref id, 23, IntPtr.Zero, out result));
            return (IAudioEndpointVolume)result;
        }
        private static string ReadName(IMMDevice device)
        {
            IPropertyStore store;
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out store));
            try
            {
                PropertyKey key = new PropertyKey { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                PropVariant value;
                Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
                try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : ""; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        public void Dispose() { Marshal.ReleaseComObject(Enumerator); }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
    }

    [StructLayout(LayoutKind.Sequential)] public struct PropertyKey { public Guid FormatId; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] public struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumerator { }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, uint state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] internal class PolicyConfigClient { }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long defaultPeriodVal, out long minPeriodVal);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int store, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int store, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
