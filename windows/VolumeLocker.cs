using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WipeKit
{
    /// <summary>
    /// Sperrt und hängt alle Volumes eines Datenträgers aus. Solange die Sperre gehalten wird,
    /// erlaubt Windows das direkte Beschreiben der Sektoren (sonst »Zugriff verweigert«).
    /// </summary>
    internal sealed class VolumeLocker : IDisposable
    {
        readonly List<SafeFileHandle> _handles = new List<SafeFileHandle>();
        public List<string> Locked { get; } = new List<string>();
        public List<string> Problems { get; } = new List<string>();

        public static VolumeLocker LockAllOn(int diskNumber, Func<bool> cancelled)
        {
            var locker = new VolumeLocker();
            foreach (string volume in EnumerateVolumes())
            {
                if (cancelled()) break;
                string path = volume.TrimEnd('\\');
                var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
                    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) { h.Dispose(); continue; }
                if (!IsOnDisk(h, diskNumber)) { h.Dispose(); continue; }

                bool locked = TryLock(h);
                // Erzwingt das Aushängen, auch wenn noch Dateien geöffnet sind
                Ioctl(h, Native.FSCTL_DISMOUNT_VOLUME);
                if (!locked) locked = TryLock(h);
                if (!locked)
                    locker.Problems.Add($"{volume}: konnte nicht gesperrt werden ({new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message})");
                locker._handles.Add(h);
                locker.Locked.Add(volume);
            }
            return locker;
        }

        static bool TryLock(SafeFileHandle h)
        {
            for (int i = 0; i < 10; i++)
            {
                if (Ioctl(h, Native.FSCTL_LOCK_VOLUME)) return true;
                Thread.Sleep(300);
            }
            return false;
        }

        static unsafe bool Ioctl(SafeFileHandle h, uint code) =>
            Native.DeviceIoControl(h, code, null, 0, null, 0, out _, IntPtr.Zero);

        static unsafe bool IsOnDisk(SafeFileHandle h, int diskNumber)
        {
            int size = 8 + 24 * 32;
            byte* buf = stackalloc byte[size];
            if (!Native.DeviceIoControl(h, Native.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0, buf, size, out int returned, IntPtr.Zero))
                return false;
            int count = *(int*)buf;
            for (int i = 0; i < count && 8 + i * 24 + 4 <= returned; i++)
            {
                int disk = *(int*)(buf + 8 + i * 24);
                if (disk == diskNumber) return true;
            }
            return false;
        }

        static IEnumerable<string> EnumerateVolumes()
        {
            var name = new StringBuilder(1024);
            IntPtr find = Native.FindFirstVolume(name, name.Capacity);
            if (find == new IntPtr(-1)) yield break;
            try
            {
                do
                {
                    yield return name.ToString();
                    name.Clear();
                } while (Native.FindNextVolume(find, name, name.Capacity));
            }
            finally
            {
                Native.FindVolumeClose(find);
            }
        }

        public void Dispose()
        {
            foreach (var h in _handles) h.Dispose();   // Schließen hebt die Sperre auf
            _handles.Clear();
        }
    }
}
