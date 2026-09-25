using System.Runtime.InteropServices;

namespace Detective.Core.Native;

/// <summary>DXGI adapter enumeration through raw vtable calls (no COM interop marshalling).</summary>
internal static unsafe class Dxgi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    public static List<GpuAdapter> EnumerateAdapters()
    {
        var result = new List<GpuAdapter>();
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(in iid, out var factory) < 0 || factory == IntPtr.Zero)
            return result;

        try
        {
            var fvt = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)fvt[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0) break;
                try
                {
                    var avt = *(IntPtr**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>)avt[10];
                    DXGI_ADAPTER_DESC1 d;
                    if (getDesc1(adapter, &d) < 0) continue;
                    if ((d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                    if (d.VendorId == 0x1414 && d.DeviceId == 0x8C) continue; // Microsoft Basic Render Driver

                    string name = new string(d.Description);
                    result.Add(new GpuAdapter(
                        result.Count, name.Trim(), d.VendorId, d.DeviceId, d.SubSysId, d.Revision,
                        d.LuidLow, d.LuidHigh, GpuInstanceName.LuidKey(d.LuidHigh, d.LuidLow),
                        d.DedicatedVideoMemory, d.SharedSystemMemory));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        return result;
    }

    private static void Release(IntPtr unknown)
    {
        if (unknown == IntPtr.Zero) return;
        var vt = *(IntPtr**)unknown;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vt[2])(unknown);
    }
}

/// <summary>Kernel-mode thunk queries (gdi32 D3DKMT*) for bus location, WDDM version and temperature.</summary>
internal static class D3dkmt
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OPENADAPTERFROMLUID
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ADAPTERADDRESS
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    private const int KMTQAITYPE_ADAPTERADDRESS = 6;
    private const int KMTQAITYPE_DRIVERVERSION = 13;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref OPENADAPTERFROMLUID args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER args);

    public static uint Open(uint luidLow, int luidHigh)
    {
        try
        {
            var a = new OPENADAPTERFROMLUID { LuidLow = luidLow, LuidHigh = luidHigh };
            return D3DKMTOpenAdapterFromLuid(ref a) == 0 ? a.hAdapter : 0;
        }
        catch (EntryPointNotFoundException) { return 0; }
    }

    public static void Close(uint handle)
    {
        if (handle == 0) return;
        var c = new CLOSEADAPTER { hAdapter = handle };
        D3DKMTCloseAdapter(ref c);
    }

    private static unsafe bool Query<T>(uint handle, int type, ref T data) where T : unmanaged
    {
        if (handle == 0) return false;
        fixed (T* p = &data)
        {
            var q = new QUERYADAPTERINFO
            {
                hAdapter = handle,
                Type = type,
                pPrivateDriverData = (IntPtr)p,
                PrivateDriverDataSize = (uint)sizeof(T),
            };
            return D3DKMTQueryAdapterInfo(ref q) == 0;
        }
    }

    public static ADAPTERADDRESS? Address(uint handle)
    {
        var a = new ADAPTERADDRESS();
        return Query(handle, KMTQAITYPE_ADAPTERADDRESS, ref a) ? a : null;
    }

    /// <summary>WDDM version as reported by the kernel, e.g. 3100 for WDDM 3.1.</summary>
    public static int? WddmVersion(uint handle)
    {
        int v = 0;
        return Query(handle, KMTQAITYPE_DRIVERVERSION, ref v) && v > 0 ? v : null;
    }

    /// <summary>GPU temperature in °C when the driver reports it.</summary>
    public static double? Temperature(uint handle)
    {
        var p = new ADAPTER_PERFDATA();
        if (!Query(handle, KMTQAITYPE_ADAPTERPERFDATA, ref p)) return null;
        return p.Temperature is > 0 and < 2000 ? p.Temperature / 10.0 : null;
    }
}
