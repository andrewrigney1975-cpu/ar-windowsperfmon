using System.Runtime.InteropServices;
using System.Text;

namespace Detective.Core.Native;

/// <summary>Current Wi-Fi connection state for one interface.</summary>
public sealed record WlanStatus(
    string? Ssid,
    bool AccessDenied,
    int SignalQuality,
    string PhyType,
    uint RxRateKbps,
    uint TxRateKbps,
    int Channel);

/// <summary>Minimal Native Wifi API wrapper. Windows 11 24H2+ denies SSID access unless location is allowed.</summary>
public sealed class WlanClient : IDisposable
{
    private IntPtr _handle;

    public WlanClient()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out _handle) != 0)
            _handle = IntPtr.Zero;
    }

    public WlanStatus? Query(Guid interfaceGuid)
    {
        if (_handle == IntPtr.Zero) return null;

        uint rc = WlanQueryInterface(_handle, ref interfaceGuid, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, IntPtr.Zero);
        if (rc == ErrorAccessDenied)
            return new WlanStatus(null, true, 0, "", 0, 0, 0);
        if (rc != 0 || data == IntPtr.Zero)
            return null;

        try
        {
            // WLAN_CONNECTION_ATTRIBUTES: isState @0, mode @4, profile WCHAR[256] @8,
            // WLAN_ASSOCIATION_ATTRIBUTES @520: SSID {len @520, bytes @524}, bssType @556,
            // bssid @560, phyType @568, phyIndex @572, signal @576, rx @580, tx @584.
            int ssidLen = Math.Clamp(Marshal.ReadInt32(data, 520), 0, 32);
            var ssidBytes = new byte[ssidLen];
            Marshal.Copy(data + 524, ssidBytes, 0, ssidLen);
            string ssid = Encoding.UTF8.GetString(ssidBytes);
            int phy = Marshal.ReadInt32(data, 568);
            int signal = Marshal.ReadInt32(data, 576);
            uint rx = (uint)Marshal.ReadInt32(data, 580);
            uint tx = (uint)Marshal.ReadInt32(data, 584);

            int channel = 0;
            if (WlanQueryInterface(_handle, ref interfaceGuid, OpcodeChannelNumber, IntPtr.Zero, out _, out var ch, IntPtr.Zero) == 0 && ch != IntPtr.Zero)
            {
                channel = Marshal.ReadInt32(ch);
                WlanFreeMemory(ch);
            }

            // An empty SSID with success is what 24H2 returns when location access is off.
            bool denied = ssidLen == 0;
            return new WlanStatus(denied ? null : ssid, denied, signal, PhyName(phy), rx, tx, channel);
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    internal static string PhyName(int phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "802.11n (Wi-Fi 4)",
        8 => "802.11ac (Wi-Fi 5)",
        9 => "802.11ad",
        10 => "802.11ax (Wi-Fi 6/6E)",
        11 => "802.11be (Wi-Fi 7)",
        _ => "Unknown",
    };

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            WlanCloseHandle(_handle, IntPtr.Zero);
            _handle = IntPtr.Zero;
        }
    }

    private const int OpcodeCurrentConnection = 7;
    private const int OpcodeChannelNumber = 8;
    private const uint ErrorAccessDenied = 5;

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode, IntPtr reserved,
        out uint dataSize, out IntPtr data, IntPtr opcodeValueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
