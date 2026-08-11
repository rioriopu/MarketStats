using System.Linq;
using System.Text;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketStats.Game
{
    /// <summary>フックした地点の状態（診断表示用）。</summary>
    public sealed class TapStatus
    {
        public string Name { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
        public bool Active { get; set; }
        public string Detail { get; set; } = "未設置";
        public int HitCount { get; set; }
        public DateTime LastHitLocal { get; set; } = DateTime.MinValue;
        public string LastFinding { get; set; } = string.Empty;
    }

    /// <summary>フックで捕まえた生データ。</summary>
    public sealed class CapturedPacket
    {
        public string Source { get; set; } = string.Empty;
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
        public DateTime Local { get; set; }
        public List<string> Findings { get; } = new();

        public string ToHex(int maxBytes = 512)
        {
            var sb = new StringBuilder();
            var length = Math.Min(Bytes.Length, maxBytes);

            for (var row = 0; row < length; row += 16)
            {
                sb.Append($"+{row:X3}  ");
                for (var col = 0; col < 16 && row + col < length; col++)
                    sb.Append($"{Bytes[row + col]:X2} ");

                sb.Append("  ");
                for (var col = 0; col < 16 && row + col < length; col++)
                {
                    var b = Bytes[row + col];
                    sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// マーケット関連の処理を複数の地点でフックし、それぞれから何が取れるかを観測する。
    ///
    /// 出品データの構造体（ClientStructs の定義）から読めない情報でも、
    /// サーバーから届いた生のデータには含まれている可能性がある。
    /// そこで「データがゲーム内部に取り込まれる瞬間」を複数の地点で捕まえ、
    /// 中身を自動解析して、識別子や名前が入っていないかを調べる。
    ///
    /// 関数のアドレスは ClientStructs が解決したものを使うので、
    /// 自前でシグネチャを抱えずに済み、パッチ後の追従も比較的楽になる。
    ///
    /// 生データの取り込みは診断目的のため、既定では無効。
    /// </summary>
    public sealed unsafe class MarketTaps : IDisposable
    {
        /// <summary>1 回に取り込む最大バイト数。</summary>
        private const int CaptureSize = 512;

        /// <summary>保持する取り込み結果の数。</summary>
        private const int MaxCaptures = 12;

        private delegate void ProcessItemHistoryDelegate(InfoProxyItemSearch* self, nint packet);
        private delegate nint ProcessPlayerRetainerInfoDelegate(
            InfoProxyItemSearch* self, nint packetData, uint retainerCount);
        private delegate void ProcessRequestResultDelegate(InfoProxyItemSearch* self, byte a2, int a3);

        private Hook<ProcessItemHistoryDelegate>? _itemHistoryHook;
        private Hook<ProcessPlayerRetainerInfoDelegate>? _retainerInfoHook;
        private Hook<ProcessRequestResultDelegate>? _requestResultHook;

        private readonly TapStatus _itemHistoryTap = new()
        {
            Name = "購入履歴の受信",
            Purpose = "買い手の名前と一緒に識別子が届いていないかを見る",
        };

        private readonly TapStatus _retainerInfoTap = new()
        {
            Name = "リテイナー情報の受信",
            Purpose = "自分のリテイナー情報の並びから、出品データの構造を推測する",
        };

        private readonly TapStatus _requestResultTap = new()
        {
            Name = "検索結果の確定",
            Purpose = "出品一覧が確定した瞬間を捉える",
        };

        private readonly List<CapturedPacket> _captures = new();
        private readonly object _lock = new();

        public IReadOnlyList<TapStatus> Statuses => new[]
        {
            _itemHistoryTap, _retainerInfoTap, _requestResultTap,
        };

        public List<CapturedPacket> Captures
        {
            get { lock (_lock) return _captures.ToList(); }
        }

        public void Initialize()
        {
            _itemHistoryHook = Install<ProcessItemHistoryDelegate>(
                (nint)InfoProxyItemSearch.MemberFunctionPointers.ProcessItemHistory,
                ItemHistoryDetour, _itemHistoryTap);

            _retainerInfoHook = Install<ProcessPlayerRetainerInfoDelegate>(
                (nint)InfoProxyItemSearch.MemberFunctionPointers.ProcessPlayerRetainerInfo,
                RetainerInfoDetour, _retainerInfoTap);

            _requestResultHook = Install<ProcessRequestResultDelegate>(
                (nint)InfoProxyItemSearch.MemberFunctionPointers.ProcessRequestResult,
                RequestResultDetour, _requestResultTap);
        }

        private Hook<T>? Install<T>(nint address, T detour, TapStatus status) where T : Delegate
        {
            if (address == nint.Zero)
            {
                status.Detail = "関数の位置を解決できませんでした";
                return null;
            }

            try
            {
                var hook = Plugin.GameInterop.HookFromAddress(address, detour);
                hook.Enable();
                status.Active = true;
                status.Detail = $"設置済み (0x{address:X})";
                return hook;
            }
            catch (Exception e)
            {
                status.Detail = $"設置失敗: {e.Message}";
                Plugin.PluginLog.Warning($"{status.Name} のフックを設置できませんでした: {e.Message}");
                return null;
            }
        }

        // ---- 各フック地点 ----

        private void ItemHistoryDetour(InfoProxyItemSearch* self, nint packet)
        {
            Observe(_itemHistoryTap, "購入履歴", packet);
            _itemHistoryHook!.Original(self, packet);
        }

        private nint RetainerInfoDetour(InfoProxyItemSearch* self, nint packetData, uint retainerCount)
        {
            Observe(_retainerInfoTap, "リテイナー情報", packetData);
            return _retainerInfoHook!.Original(self, packetData, retainerCount);
        }

        private void RequestResultDetour(InfoProxyItemSearch* self, byte a2, int a3)
        {
            _requestResultTap.HitCount++;
            _requestResultTap.LastHitLocal = DateTime.Now;

            try
            {
                _requestResultTap.LastFinding = $"出品 {self->ListingCount} 件が確定";
            }
            catch
            {
                // 読めなくても本来の処理は続行する。
            }

            _requestResultHook!.Original(self, a2, a3);
        }

        // ---- 取り込みと解析 ----

        private void Observe(TapStatus status, string label, nint address)
        {
            status.HitCount++;
            status.LastHitLocal = DateTime.Now;

            if (!Plugin.Config.EnablePacketCapture || address == nint.Zero) return;

            try
            {
                var bytes = new byte[CaptureSize];
                fixed (byte* destination = bytes)
                    Buffer.MemoryCopy((void*)address, destination, CaptureSize, CaptureSize);

                var capture = new CapturedPacket
                {
                    Source = label,
                    Bytes = bytes,
                    Local = DateTime.Now,
                };

                Analyze(capture);
                status.LastFinding = capture.Findings.Count > 0
                    ? string.Join(" / ", capture.Findings.Take(3))
                    : "識別子らしき値は見つかりませんでした";

                lock (_lock)
                {
                    _captures.Insert(0, capture);
                    if (_captures.Count > MaxCaptures) _captures.RemoveRange(MaxCaptures, _captures.Count - MaxCaptures);
                }
            }
            catch (Exception e)
            {
                status.LastFinding = $"取り込み失敗: {e.Message}";
            }
        }

        /// <summary>
        /// 取り込んだ生データから、識別子や名前らしきものを探す。
        /// 答えが分かっている自分の値が見つかれば、そこが読むべき場所だと分かる。
        /// </summary>
        private static void Analyze(CapturedPacket capture)
        {
            var self = SelfRetainerProbe.Read();
            var data = capture.Bytes;

            for (var offset = 0; offset + 8 <= data.Length; offset += 4)
            {
                var value = BitConverter.ToUInt64(data, offset);
                if (value == 0) continue;

                if (self.ContentId != 0 && value == self.ContentId)
                {
                    capture.Findings.Add($"+0x{offset:X3} に自分の ContentId");
                    continue;
                }

                var retainer = self.Retainers.FirstOrDefault(r => r.RetainerId == value);
                if (retainer != null)
                {
                    capture.Findings.Add($"+0x{offset:X3} にリテイナー {retainer.Name} の ID");
                    continue;
                }

                // ContentId は 0x0100_0000_0000_0000 以上の大きな値になる。
                if (value > 0x0100_0000_0000_0000UL && value < 0xF000_0000_0000_0000UL)
                    capture.Findings.Add($"+0x{offset:X3} に識別子らしき値 0x{value:X}");
            }

            // 名前らしき ASCII 文字列を探す（買い手の名前など）。
            var text = new StringBuilder();
            var start = 0;

            for (var i = 0; i <= data.Length; i++)
            {
                var c = i < data.Length ? (char)data[i] : '\0';
                var isNameChar = char.IsAsciiLetter(c) || c == ' ' || c == '\'' || c == '-';

                if (isNameChar)
                {
                    if (text.Length == 0) start = i;
                    text.Append(c);
                    continue;
                }

                if (text.Length >= 5 && text.ToString().Trim().Count(char.IsWhiteSpace) == 1)
                    capture.Findings.Add($"+0x{start:X3} に名前らしき文字列「{text.ToString().Trim()}」");

                text.Clear();
            }
        }

        private static void DisposeHook<T>(Hook<T>? hook) where T : Delegate
        {
            try
            {
                hook?.Disable();
                hook?.Dispose();
            }
            catch
            {
                // 破棄時の失敗は握りつぶす。
            }
        }

        public void Dispose()
        {
            DisposeHook(_itemHistoryHook);
            DisposeHook(_retainerInfoHook);
            DisposeHook(_requestResultHook);
        }
    }
}
