using System.Runtime.InteropServices;

namespace Detective.Core.Native;

/// <summary>One process as reported by <c>NtQuerySystemInformation(SystemProcessInformation)</c>.</summary>
public readonly record struct ProcessEntry(
    int Pid,
    int ParentPid,
    long CreateTime,
    string ImageName,
    int SessionId,
    long CpuTime,
    ulong PrivateWorkingSet,
    ulong IoReadBytes,
    ulong IoWriteBytes);

/// <summary>
/// Reads every process in one system call. Unlike OpenProcess-based APIs this works as a standard user for
/// every process (services and other sessions included), which is how Task Manager avoids access-denied gaps.
/// </summary>
internal static unsafe class Ntdll
{
    private const int SystemProcessInformation = 5;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    /// <summary>x64 layout of SYSTEM_PROCESS_INFORMATION up to the I/O counters (thread entries follow it).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_INFORMATION
    {
        public uint NextEntryOffset;
        public uint NumberOfThreads;
        public long WorkingSetPrivateSize;
        public uint HardFaultCount;
        public uint NumberOfThreadsHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public ushort ImageNameLength;
        public ushort ImageNameMaximumLength;
        public char* ImageNameBuffer;
        public int BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
        public uint HandleCount;
        public uint SessionId;
        public nuint UniqueProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivatePageCount;
        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;
    }

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, void* buffer, uint length, out uint returnLength);

    // The process list is ~100 KB on a busy machine; the buffer is kept and grown as needed.
    private static byte[] _buffer = new byte[256 * 1024];
    private static readonly Lock BufferLock = new();

    /// <summary>All processes, or an empty list if the call fails.</summary>
    public static List<ProcessEntry> ReadProcesses()
    {
        lock (BufferLock)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                uint status, needed;
                fixed (byte* p = _buffer)
                    status = NtQuerySystemInformation(SystemProcessInformation, p, (uint)_buffer.Length, out needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    // Processes can start between the size query and the read, so leave headroom.
                    _buffer = new byte[Math.Max(_buffer.Length * 2, (int)needed + 64 * 1024)];
                    continue;
                }
                return status == 0 ? Parse(_buffer) : [];
            }
            return [];
        }
    }

    private static List<ProcessEntry> Parse(byte[] buffer)
    {
        var list = new List<ProcessEntry>(512);
        fixed (byte* start = buffer)
        {
            byte* p = start;
            while (true)
            {
                var info = (SYSTEM_PROCESS_INFORMATION*)p;
                int pid = (int)info->UniqueProcessId;
                string name = info->ImageNameBuffer is null || info->ImageNameLength == 0
                    ? (pid == 0 ? "System Idle Process" : "System")
                    : new string(info->ImageNameBuffer, 0, info->ImageNameLength / 2);
                list.Add(new ProcessEntry(
                    pid,
                    (int)info->InheritedFromUniqueProcessId,
                    info->CreateTime,
                    name,
                    (int)info->SessionId,
                    info->UserTime + info->KernelTime,
                    (ulong)Math.Max(0, info->WorkingSetPrivateSize),
                    (ulong)Math.Max(0, info->ReadTransferCount),
                    (ulong)Math.Max(0, info->WriteTransferCount)));
                if (info->NextEntryOffset == 0) break;
                p += info->NextEntryOffset;
            }
        }
        return list;
    }
}
