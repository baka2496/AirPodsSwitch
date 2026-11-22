using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AirPodsSwitch
{
    internal static class NativeBluetoothAudio
    {
        private static readonly Guid PropertySet = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

        internal static int Request(Audio audio, string endpointId, bool connect, bool probe)
        {
            IMMDevice endpoint;
            Marshal.ThrowExceptionForHR(audio.Enumerator.GetDevice(endpointId, out endpoint));
            object topologyObject = null;
            try
            {
                Guid topologyId = typeof(IDeviceTopology).GUID;
                Marshal.ThrowExceptionForHR(endpoint.Activate(ref topologyId, 23, IntPtr.Zero, out topologyObject));
                IDeviceTopology topology = (IDeviceTopology)topologyObject;
                uint count;
                Marshal.ThrowExceptionForHR(topology.GetConnectorCount(out count));
                int lastError = unchecked((int)0x80070490);
                for (uint index = 0; index < count; index++)
                {
                    IConnector connector;
                    Marshal.ThrowExceptionForHR(topology.GetConnector(index, out connector));
                    IConnector adapterConnector = null;
                    IDeviceTopology adapterTopology = null;
                    IMMDevice adapter = null;
                    object controlObject = null;
                    try
                    {
                        bool connected;
                        Marshal.ThrowExceptionForHR(connector.IsConnected(out connected));
                        if (!connected) continue;
                        Marshal.ThrowExceptionForHR(connector.GetConnectedTo(out adapterConnector));
                        Marshal.ThrowExceptionForHR(((IPart)adapterConnector).GetTopologyObject(out adapterTopology));
                        string adapterId;
                        Marshal.ThrowExceptionForHR(adapterTopology.GetDeviceId(out adapterId));
                        Marshal.ThrowExceptionForHR(audio.Enumerator.GetDevice(adapterId, out adapter));
                        Guid controlId = typeof(IKsControl).GUID;
                        lastError = adapter.Activate(ref controlId, 23, IntPtr.Zero, out controlObject);
                        if (lastError < 0) continue;
                        IKsControl control = (IKsControl)controlObject;
                        KsProperty property = new KsProperty { Set = PropertySet, Id = connect ? 0u : 1u, Flags = probe ? 0x200u : 1u };
                        uint returned;
                        IntPtr data = probe ? Marshal.AllocHGlobal(64) : IntPtr.Zero;
                        try
                        {
                            lastError = control.KsProperty(ref property, (uint)Marshal.SizeOf(typeof(KsProperty)), data, probe ? 64u : 0u, out returned);
                            if (lastError >= 0) return lastError;
                        }
                        finally { if (data != IntPtr.Zero) Marshal.FreeHGlobal(data); }
                    }
                    catch (COMException error) { lastError = error.ErrorCode; }
                    finally
                    {
                        if (controlObject != null) Marshal.ReleaseComObject(controlObject);
                        if (adapter != null) Marshal.ReleaseComObject(adapter);
                        if (adapterTopology != null) Marshal.ReleaseComObject(adapterTopology);
                        if (adapterConnector != null) Marshal.ReleaseComObject(adapterConnector);
                        Marshal.ReleaseComObject(connector);
                    }
                }
                return lastError;
            }
            finally
            {
                if (topologyObject != null) Marshal.ReleaseComObject(topologyObject);
                Marshal.ReleaseComObject(endpoint);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] internal struct KsProperty { internal Guid Set; internal uint Id; internal uint Flags; }
    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr unit);
        [PreserveSig] int GetPartById(uint id, out IntPtr part);
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector connector);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector connector);
    }
    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPart
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetPartType(out int type);
        [PreserveSig] int GetSubType(out Guid type);
        [PreserveSig] int GetControlInterfaceCount(out uint count);
        [PreserveSig] int GetControlInterface(uint index, out IntPtr control);
        [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
        [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
        [PreserveSig] int GetTopologyObject(out IDeviceTopology topology);
    }
    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KsProperty property, uint size, IntPtr data, uint dataSize, out uint returned);
        [PreserveSig] int KsMethod(IntPtr method, uint size, IntPtr data, uint dataSize, out uint returned);
        [PreserveSig] int KsEvent(IntPtr evt, uint size, IntPtr data, uint dataSize, out uint returned);
    }

    internal static class BluetoothBattery
    {
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfAllClasses = 0x00000004;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);
        private static DevPropKey KeyBattery = new DevPropKey { FormatId = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), Pid = 2 };
        private static DevPropKey KeyFriendlyName = new DevPropKey { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Pid = 14 };
        private static DevPropKey KeyName = new DevPropKey { FormatId = new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), Pid = 10 };

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey { internal Guid FormatId; internal uint Pid; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevInfoData
        {
            internal uint Size;
            internal Guid ClassGuid;
            internal uint DevInst;
            internal IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDevicePropertyW(IntPtr deviceInfoSet, ref SpDevInfoData deviceInfoData, ref DevPropKey propertyKey, out uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        internal static int? TryReadPercent(IEnumerable<string> keywords)
        {
            List<string> keys = new List<string>();
            if (keywords != null)
            {
                foreach (string raw in keywords)
                    if (!string.IsNullOrEmpty(raw) && raw.Length >= 2) keys.Add(raw);
            }
            if (keys.Count == 0) keys.Add("AirPods");

            foreach (string enumerator in new string[] { "BTHHFENUM", "BTHENUM" })
            {
                int? found = ScanEnumerator(enumerator, keys);
                if (found.HasValue) return found;
            }
            return null;
        }

        private static int? ScanEnumerator(string enumerator, List<string> keys)
        {
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
            if (set == IntPtr.Zero || set == InvalidHandle) return null;
            try
            {
                SpDevInfoData info = new SpDevInfoData { Size = (uint)Marshal.SizeOf(typeof(SpDevInfoData)) };
                byte[] batBuf = new byte[4];
                byte[] nameBuf = new byte[512];
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    uint propType, reqSize;
                    if (!SetupDiGetDevicePropertyW(set, ref info, ref KeyBattery, out propType, batBuf, (uint)batBuf.Length, out reqSize, 0))
                        continue;
                    if (reqSize == 0) continue;
                    int percent = batBuf[0];
                    if (percent < 0 || percent > 100) continue;

                    string name = ReadStringProp(set, ref info, KeyFriendlyName, nameBuf);
                    if (string.IsNullOrEmpty(name)) name = ReadStringProp(set, ref info, KeyName, nameBuf);
                    if (string.IsNullOrEmpty(name)) continue;

                    foreach (string key in keys)
                        if (name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return percent;
                }
                return null;
            }
            catch { return null; }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static string ReadStringProp(IntPtr set, ref SpDevInfoData info, DevPropKey key, byte[] buffer)
        {
            uint propType, reqSize;
            if (!SetupDiGetDevicePropertyW(set, ref info, ref key, out propType, buffer, (uint)buffer.Length, out reqSize, 0))
                return null;
            if (reqSize < 2) return null;
            int len = Math.Min((int)reqSize, buffer.Length);
            return Encoding.Unicode.GetString(buffer, 0, len).TrimEnd('\0');
        }
    }
}
