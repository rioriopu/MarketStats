using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketStats.Game
{
    /// <summary>メモリ走査で値が見つかった場所。</summary>
    public sealed class ScanHit
    {
        public string Region { get; set; } = string.Empty;
        public int Offset { get; set; }
        public int Size { get; set; }

        /// <summary>出品一覧の中なら、何番目の出品のどのオフセットか。</summary>
        public int ListingIndex { get; set; } = -1;
        public int OffsetInListing { get; set; } = -1;

        public override string ToString() =>
            ListingIndex >= 0
                ? $"{Region} +0x{Offset:X} （出品 #{ListingIndex} の +0x{OffsetInListing:X2}, {Size}バイト）"
                : $"{Region} +0x{Offset:X} （{Size}バイト）";
    }

    /// <summary>
    /// マーケット関連のメモリ領域を直接走査する診断機能。
    ///
    /// 「出品データにオーナーの ContentId が入っていない」ように見えても、
    /// 構造体の定義が古くて読む場所がずれているだけ、という可能性がある。
    /// 自分の出品なら答え（自分の ContentId）が分かっているので、
    /// その値がメモリ上に実在するかを総当たりで探せば白黒がつく。
    /// </summary>
    public static unsafe class MemoryScanner
    {
        /// <summary>1 出品あたりのサイズ（MarketBoardListing）。</summary>
        private const int ListingSize = 0xB8;

        /// <summary>指定した値が、マーケット関連のメモリに存在するかを探す。</summary>
        public static List<ScanHit> ScanForValue(ulong needle)
        {
            var hits = new List<ScanHit>();
            if (needle == 0) return hits;

            try
            {
                var module = InfoModule.Instance();
                if (module != null)
                {
                    var proxy = module->GetInfoProxyById(InfoProxyId.ItemSearch);
                    if (proxy != null)
                        ScanRegion((byte*)proxy, 0x5B98, "出品データ (InfoProxyItemSearch)", needle, hits, true);
                }

                var agentModule = AgentModule.Instance();
                if (agentModule != null)
                {
                    var agent = agentModule->GetAgentByInternalId(AgentId.ItemSearch);
                    if (agent != null)
                        ScanRegion((byte*)agent, 0x3888, "検索エージェント (AgentItemSearch)", needle, hits, false);
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"メモリ走査に失敗しました: {e.Message}");
            }

            return hits;
        }

        private static void ScanRegion(
            byte* start, int size, string label, ulong needle, List<ScanHit> hits, bool isListingArea)
        {
            if (start == null) return;

            // 出品配列の先頭（InfoProxyItemSearch._listings は 0x30 から）
            const int listingsOffset = 0x30;
            const int listingsCount = 100;

            for (var offset = 0; offset + 8 <= size; offset += 4)
            {
                var value64 = *(ulong*)(start + offset);
                var value32 = *(uint*)(start + offset);

                var matched64 = value64 == needle;
                var matched32 = needle <= uint.MaxValue && value32 == (uint)needle;
                if (!matched64 && !matched32) continue;

                var hit = new ScanHit
                {
                    Region = label,
                    Offset = offset,
                    Size = matched64 ? 8 : 4,
                };

                if (isListingArea &&
                    offset >= listingsOffset &&
                    offset < listingsOffset + ListingSize * listingsCount)
                {
                    var relative = offset - listingsOffset;
                    hit.ListingIndex = relative / ListingSize;
                    hit.OffsetInListing = relative % ListingSize;
                }

                hits.Add(hit);
                if (hits.Count >= 64) return;
            }
        }

        /// <summary>指定した出品の生バイトを 16 進でダンプする。</summary>
        public static string DumpListingBytes(int index)
        {
            try
            {
                var module = InfoModule.Instance();
                if (module == null) return "情報を取得できませんでした。";

                var proxy = (InfoProxyItemSearch*)module->GetInfoProxyById(InfoProxyId.ItemSearch);
                if (proxy == null) return "出品データを取得できませんでした。";

                if (index < 0 || index >= (int)proxy->ListingCount)
                    return $"出品 #{index} は範囲外です（{proxy->ListingCount} 件）。";

                var start = (byte*)proxy + 0x30 + ListingSize * index;
                var sb = new StringBuilder();

                for (var row = 0; row < ListingSize; row += 16)
                {
                    sb.Append($"+{row:X2}  ");
                    for (var col = 0; col < 16 && row + col < ListingSize; col++)
                        sb.Append($"{start[row + col]:X2} ");
                    sb.AppendLine();
                }

                return sb.ToString();
            }
            catch (Exception e)
            {
                return $"ダンプに失敗しました: {e.Message}";
            }
        }

        /// <summary>
        /// 出品ごとに、8 バイト境界で 0 でない値を並べる。
        /// 識別子らしき値がどこに入っているかを見つけるための一覧。
        /// </summary>
        public static string DescribeNonZeroFields(int index)
        {
            try
            {
                var module = InfoModule.Instance();
                if (module == null) return string.Empty;

                var proxy = (InfoProxyItemSearch*)module->GetInfoProxyById(InfoProxyId.ItemSearch);
                if (proxy == null || index < 0 || index >= (int)proxy->ListingCount) return string.Empty;

                var start = (byte*)proxy + 0x30 + ListingSize * index;
                var parts = new List<string>();

                for (var offset = 0; offset + 8 <= ListingSize; offset += 8)
                {
                    var value = *(ulong*)(start + offset);
                    if (value == 0) continue;

                    // 識別子は 0x1000_0000_0000_0000 前後の大きな値になる。
                    var looksLikeId = value > 0x0100_0000_0000_0000UL;
                    parts.Add($"+0x{offset:X2}=0x{value:X}{(looksLikeId ? " ←識別子らしい" : string.Empty)}");
                }

                return string.Join("\n", parts);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
