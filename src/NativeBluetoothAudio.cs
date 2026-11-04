using System;
using System.Runtime.InteropServices;

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
}
