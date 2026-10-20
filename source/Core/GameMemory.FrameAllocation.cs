using System.Runtime.InteropServices;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal sealed class FrameAllocation(IntPtr process, IntPtr page) : IDisposable
    {
        private bool published, disposed;

        internal void MarkPublished() => published = true;

        public void Dispose()
        {
            if (disposed) return;
            if (!published && !VirtualFreeEx(process, page, UIntPtr.Zero, MemRelease))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "VirtualFreeEx");
            disposed = true;
        }
    }
}
