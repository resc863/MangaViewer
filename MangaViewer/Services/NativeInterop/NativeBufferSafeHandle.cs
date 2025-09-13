using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;

namespace MangaViewer.Services.NativeInterop
{
    /// <summary>
    /// 네이티브에서 할당된 버퍼를 안전하게 해제하기 위한 SafeHandle 스텁. 실제 해제는 후속에 연결.
    /// </summary>
    public sealed class NativeBufferSafeHandle : SafeHandle
    {
        public NativeBufferSafeHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;

        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.MayFail)]
        protected override bool ReleaseHandle()
        {
            try
            {
                if (!IsInvalid)
                {
                    // TODO: call into native FreeBuffer when available
                }
            }
            catch { }
            handle = IntPtr.Zero;
            return true;
        }
    }
}
