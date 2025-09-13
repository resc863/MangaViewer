using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;

namespace MangaViewer.Services.NativeInterop
{
    /// <summary>
    /// ����Ƽ�꿡�� �Ҵ�� ���۸� �����ϰ� �����ϱ� ���� SafeHandle ����. ���� ������ �ļӿ� ����.
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
