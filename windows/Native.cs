using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WipeKit
{
    internal static unsafe class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint CREATE_ALWAYS = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        public const uint IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES = 0x002D9404;

        public const uint ES_CONTINUOUS = 0x80000000;
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;

        public const int WM_DEVICECHANGE = 0x0219;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle handle, byte* buffer, int count, out int written,
            NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(SafeFileHandle handle, byte* buffer, int count, out int read,
            NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FlushFileBuffers(SafeFileHandle handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte* inBuffer, int inSize,
            byte* outBuffer, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint flags);
    }
}
