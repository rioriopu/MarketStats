using System.Runtime.InteropServices;

namespace MarketStats.Game
{
    /// <summary>
    /// メモリを読む前に、その領域が本当に読めるかを OS に確認する。
    ///
    /// 無効なアドレスを読むとアクセス違反でゲームごと落ちる。
    /// これは try/catch では捕まえられないため、読む前に確認するしかない。
    /// </summary>
    public static class SafeMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public nint BaseAddress;
            public nint AllocationBase;
            public uint AllocationProtect;
            private readonly uint _alignment1;
            public nuint RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            private readonly uint _alignment2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint VirtualQuery(
            nint address, out MemoryBasicInformation buffer, nuint length);

        private const uint MemCommit = 0x1000;

        private const uint PageNoAccess = 0x01;
        private const uint PageReadonly = 0x02;
        private const uint PageReadWrite = 0x04;
        private const uint PageWriteCopy = 0x08;
        private const uint PageExecuteRead = 0x20;
        private const uint PageExecuteReadWrite = 0x40;
        private const uint PageExecuteWriteCopy = 0x80;
        private const uint PageGuard = 0x100;

        /// <summary>
        /// 指定アドレスから何バイトまで安全に読めるかを返す。
        /// 読めない場合は 0。
        /// </summary>
        public static int GetReadableSize(nint address, int desired)
        {
            if (address == nint.Zero || desired <= 0) return 0;

            // 明らかに不正な範囲は問い合わせるまでもなく弾く。
            var value = (ulong)address;
            if (value < 0x10000 || value > 0x7FFF_FFFF_FFFF) return 0;

            try
            {
                var infoSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
                if (VirtualQuery(address, out var info, infoSize) == 0) return 0;

                if (info.State != MemCommit) return 0;
                if (!IsReadable(info.Protect)) return 0;

                // 領域の末尾を超えないように切り詰める。
                var offsetInRegion = (long)address - info.BaseAddress;
                var available = (long)info.RegionSize - offsetInRegion;
                if (available <= 0) return 0;

                return (int)Math.Min(desired, available);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>指定アドレスから size バイトすべてが読めるか。</summary>
        public static bool IsFullyReadable(nint address, int size) =>
            GetReadableSize(address, size) >= size;

        private static bool IsReadable(uint protect)
        {
            if ((protect & PageGuard) != 0) return false;
            if ((protect & PageNoAccess) != 0) return false;

            return (protect & (PageReadonly | PageReadWrite | PageWriteCopy |
                               PageExecuteRead | PageExecuteReadWrite | PageExecuteWriteCopy)) != 0;
        }
    }
}
