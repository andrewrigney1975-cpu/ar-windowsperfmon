using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Detective.Core.Native;

/// <summary>
/// Minimal WMI query client over raw COM vtables (IWbemLocator → IWbemServices → IEnumWbemClassObject →
/// IWbemClassObject). Replaces System.Management, which relies on built-in COM interop and so doesn't work
/// under Native AOT. Only explicit "SELECT a, b FROM ..." queries are supported: the select list names the
/// properties to read.
/// </summary>
internal static unsafe partial class WmiClient
{
    private static readonly Guid ClsidWbemLocator = new("4590F811-1D3A-11D0-891F-00AA004B2E24");
    private static readonly Guid IidWbemLocator = new("DC12A687-737F-11CF-884D-00AA004B2E24");

    private const uint CLSCTX_INPROC_SERVER = 1;
    private const uint COINIT_MULTITHREADED = 0;
    private const int WBEM_FLAG_RETURN_IMMEDIATELY = 0x10;
    private const int WBEM_FLAG_FORWARD_ONLY = 0x20;
    private const int WBEM_INFINITE = -1;

    public static List<Dictionary<string, object?>> Query(string scope, string wql)
    {
        var rows = new List<Dictionary<string, object?>>();
        var properties = SelectList(wql);
        if (properties.Length == 0) return rows;

        // Thread-pool threads may not have COM initialised; S_FALSE/RPC_E_CHANGED_MODE are both fine to proceed.
        int init = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);

        IntPtr locator = IntPtr.Zero, services = IntPtr.Zero, enumerator = IntPtr.Zero;
        try
        {
            var clsid = ClsidWbemLocator;
            var iid = IidWbemLocator;
            if (CoCreateInstance(in clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, in iid, out locator) < 0) return rows;

            IntPtr ns = SysAllocString(scope);
            try
            {
                // IWbemLocator::ConnectServer (slot 3)
                var connect = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr, IntPtr*, int>)Vtbl(locator)[3];
                IntPtr svc;
                if (connect(locator, ns, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, &svc) < 0) return rows;
                services = svc;
            }
            finally
            {
                SysFreeString(ns);
            }

            // Impersonation is required by several providers (e.g. storage) to answer at all.
            CoSetProxyBlanket(services, 10 /* WINNT */, 0, IntPtr.Zero, 3 /* CALL */, 3 /* IMPERSONATE */, IntPtr.Zero, 0);

            IntPtr language = SysAllocString("WQL"), query = SysAllocString(wql);
            try
            {
                // IWbemServices::ExecQuery (slot 20)
                var exec = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int, IntPtr, IntPtr*, int>)Vtbl(services)[20];
                IntPtr e;
                if (exec(services, language, query, WBEM_FLAG_RETURN_IMMEDIATELY | WBEM_FLAG_FORWARD_ONLY, IntPtr.Zero, &e) < 0) return rows;
                enumerator = e;
            }
            finally
            {
                SysFreeString(language);
                SysFreeString(query);
            }
            CoSetProxyBlanket(enumerator, 10, 0, IntPtr.Zero, 3, 3, IntPtr.Zero, 0);

            // IEnumWbemClassObject::Next (slot 4)
            var next = (delegate* unmanaged[Stdcall]<IntPtr, int, uint, IntPtr*, uint*, int>)Vtbl(enumerator)[4];
            while (true)
            {
                IntPtr obj;
                uint returned;
                if (next(enumerator, WBEM_INFINITE, 1, &obj, &returned) != 0 || returned == 0) break;
                try
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in properties) row[name] = GetProperty(obj, name);
                    rows.Add(row);
                }
                finally
                {
                    Release(obj);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or SEHException) { }
        finally
        {
            Release(enumerator);
            Release(services);
            Release(locator);
            if (init >= 0) CoUninitialize();
        }
        return rows;
    }

    /// <summary>IWbemClassObject::Get (slot 4), converted from VARIANT. WMI returns uint64 values as strings.</summary>
    private static object? GetProperty(IntPtr obj, string name)
    {
        var get = (delegate* unmanaged[Stdcall]<IntPtr, char*, int, Variant*, int*, int*, int>)Vtbl(obj)[4];
        Variant v = default;
        fixed (char* n = name)
        {
            if (get(obj, n, 0, &v, null, null) < 0) return null;
        }
        try
        {
            return v.Vt switch
            {
                2 => (long)*(short*)&v.Data,        // VT_I2
                3 => (long)*(int*)&v.Data,          // VT_I4
                8 => Marshal.PtrToStringBSTR(v.Data), // VT_BSTR
                11 => *(short*)&v.Data != 0,        // VT_BOOL
                16 => (long)*(sbyte*)&v.Data,       // VT_I1
                17 => (long)*(byte*)&v.Data,        // VT_UI1
                18 => (long)*(ushort*)&v.Data,      // VT_UI2
                19 => (long)*(uint*)&v.Data,        // VT_UI4
                20 => *(long*)&v.Data,              // VT_I8
                21 => (long)*(ulong*)&v.Data,       // VT_UI8
                _ => null,                          // VT_EMPTY, VT_NULL, arrays: not needed here
            };
        }
        finally
        {
            VariantClear(&v);
        }
    }

    /// <summary>Property names from "SELECT a, b, c FROM ...".</summary>
    internal static string[] SelectList(string wql)
    {
        var m = SelectRegex().Match(wql);
        return m.Success
            ? m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }

    [GeneratedRegex(@"^\s*SELECT\s+(.+?)\s+FROM\s", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SelectRegex();

    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort Vt;
        public ushort R1, R2, R3;
        public IntPtr Data;
        public IntPtr Data2;
    }

    private static IntPtr* Vtbl(IntPtr unknown) => *(IntPtr**)unknown;

    private static void Release(IntPtr unknown)
    {
        if (unknown != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vtbl(unknown)[2])(unknown);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [DllImport("ole32.dll")]
    private static extern int CoSetProxyBlanket(IntPtr proxy, uint authnSvc, uint authzSvc, IntPtr serverPrincName,
        uint authnLevel, uint impLevel, IntPtr authInfo, uint capabilities);

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SysAllocString(string value);

    [DllImport("oleaut32.dll")]
    private static extern void SysFreeString(IntPtr bstr);

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(Variant* variant);
}
