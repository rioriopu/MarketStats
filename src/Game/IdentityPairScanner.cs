using System.Linq;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace MarketStats.Game
{
    /// <summary>メモリ上で見つかった「識別子と名前の組」。</summary>
    public sealed class IdentityPair
    {
        public ulong ContentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public int IdOffset { get; set; }
        public int NameOffset { get; set; }

        /// <summary>識別子と名前の距離（近いほど同じレコードの一部らしい）。</summary>
        public int Distance => Math.Abs(NameOffset - IdOffset);

        public bool AlreadyKnown { get; set; }
    }

    /// <summary>走査対象の領域。</summary>
    public sealed class ScanRegion
    {
        public string Name { get; set; } = string.Empty;
        public nint Address { get; set; }
        public int Size { get; set; }
    }

    /// <summary>
    /// メモリ上から「キャラクター識別子と名前が隣り合って置かれている場所」を探す。
    ///
    /// ゲームが内部で人物の情報を持つとき、識別子と名前は同じレコードに入っていることが多い。
    /// 構造体の定義を知らなくても、識別子らしい値の近くにある名前らしい文字列を拾えば、
    /// 「この識別子はこの人」という対応をまとめて回収できる。
    /// </summary>
    public static unsafe class IdentityPairScanner
    {
        /// <summary>識別子と名前が同じレコードとみなせる最大の距離。</summary>
        private const int PairRadius = 0x80;

        /// <summary>1 回の走査で拾う上限。</summary>
        private const int MaxPairs = 400;

        /// <summary>ポインタを辿る先で読む最大サイズ。</summary>
        private const int PointerTargetSize = 0x2000;

        /// <summary>1 領域あたりに辿るポインタの上限。</summary>
        private const int MaxPointerFollows = 96;

        /// <summary>走査できる領域を列挙する。</summary>
        public static List<ScanRegion> EnumerateRegions()
        {
            var regions = new List<ScanRegion>();

            try
            {
                var infoModule = InfoModule.Instance();
                if (infoModule != null)
                {
                    Add(regions, "出品データ", (nint)infoModule->GetInfoProxyById(InfoProxyId.ItemSearch), 0x5B98);
                    Add(regions, "フレンドリスト", (nint)infoModule->GetInfoProxyById(InfoProxyId.FriendList), 0xD0);
                    Add(regions, "FC メンバー", (nint)infoModule->GetInfoProxyById(InfoProxyId.FreeCompanyMember), 0xD0);
                    Add(regions, "リンクシェル", (nint)infoModule->GetInfoProxyById(InfoProxyId.LinkshellMember), 0xD0);
                    Add(regions, "パーティ", (nint)infoModule->GetInfoProxyById(InfoProxyId.PartyMember), 0xD0);
                    Add(regions, "コンテンツ同行者", (nint)infoModule->GetInfoProxyById(InfoProxyId.ContentMember), 0xD0);
                    Add(regions, "手紙", (nint)infoModule->GetInfoProxyById(InfoProxyId.Letter), 0xD0);
                }

                var agentModule = AgentModule.Instance();
                if (agentModule != null)
                {
                    Add(regions, "検索エージェント",
                        (nint)agentModule->GetAgentByInternalId(AgentId.ItemSearch), 0x3888);
                    Add(regions, "冒険者名刺",
                        (nint)agentModule->GetAgentByInternalId(AgentId.CharaCard), 0x38);
                    Add(regions, "リテイナー一覧",
                        (nint)agentModule->GetAgentByInternalId(AgentId.Retainer), 0x400);
                }

                var retainerManager = RetainerManager.Instance();
                if (retainerManager != null)
                    Add(regions, "リテイナー管理", (nint)retainerManager, 0x310);
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"走査領域の列挙に失敗しました: {e.Message}");
            }

            return regions;
        }

        private static void Add(List<ScanRegion> regions, string name, nint address, int size)
        {
            if (address == nint.Zero) return;

            // 実際に読める分だけを対象にする（構造体定義より領域が短いこともある）。
            var readable = SafeMemory.GetReadableSize(address, size);
            if (readable < 16) return;

            regions.Add(new ScanRegion { Name = name, Address = address, Size = readable });
        }

        /// <summary>
        /// 指定した領域から、識別子と名前の組を拾い集める。
        /// リストの実体が別の場所にある場合（ポインタ先）も 1 段だけ追いかける。
        /// </summary>
        public static List<IdentityPair> Scan(IEnumerable<ScanRegion> regions)
        {
            var pairs = new List<IdentityPair>();
            var seen = new HashSet<ulong>();

            foreach (var region in regions)
            {
                if (pairs.Count >= MaxPairs) break;

                try
                {
                    ScanRegion(region.Name, (byte*)region.Address, region.Size, pairs, seen);

                    // リスト系は本体がポインタ先にあるので、そこも見る。
                    ScanPointerTargets(region, pairs, seen);
                }
                catch (Exception e)
                {
                    Plugin.PluginLog.Debug($"{region.Name} の走査で例外: {e.Message}");
                }
            }

            return pairs;
        }

        /// <summary>
        /// 領域内のポインタを 1 段だけ辿って、その先も走査する。
        ///
        /// 辿る前に必ず OS へ読み取り可否を問い合わせる。
        /// 無効なアドレスを読むとアクセス違反でゲームごと落ちるため、ここは省略できない。
        /// </summary>
        private static void ScanPointerTargets(
            ScanRegion region, List<IdentityPair> pairs, HashSet<ulong> seen)
        {
            if (!Plugin.Config.ScanPointerTargets) return;

            var start = (byte*)region.Address;
            var followed = 0;

            for (var offset = 0; offset + 8 <= region.Size; offset += 8)
            {
                if (pairs.Count >= MaxPairs || followed >= MaxPointerFollows) return;

                var pointer = *(nint*)(start + offset);
                if (pointer == nint.Zero || ((ulong)pointer & 0x7) != 0) continue;

                // 読める分だけを、読める長さで走査する。
                var readable = SafeMemory.GetReadableSize(pointer, PointerTargetSize);
                if (readable < 64) continue;

                followed++;
                ScanRegion($"{region.Name} の参照先", (byte*)pointer, readable, pairs, seen);
            }
        }

        private static void ScanRegion(
            string label, byte* start, int size, List<IdentityPair> pairs, HashSet<ulong> seen)
        {
            if (start == null || size < 16) return;

            // 呼び出し元で確認済みでも、ここでもう一度だけ念を入れる。
            size = SafeMemory.GetReadableSize((nint)start, size);
            if (size < 16) return;

            for (var offset = 0; offset + 8 <= size; offset += 4)
            {
                if (pairs.Count >= MaxPairs) return;

                var value = *(ulong*)(start + offset);
                if (!MemoryScanner.LooksLikeContentId(value)) continue;
                if (!seen.Add(value)) continue;

                var name = FindNameNear(start, size, offset, out var nameOffset);
                if (string.IsNullOrEmpty(name)) continue;

                var known = Plugin.Identities.Resolve(value);

                pairs.Add(new IdentityPair
                {
                    ContentId = value,
                    Name = name,
                    Region = label,
                    IdOffset = offset,
                    NameOffset = nameOffset,
                    AlreadyKnown = known != null && known.Source != Data.IdentitySource.Inferred,
                });
            }
        }

        /// <summary>識別子の近くにあるキャラクター名らしき文字列を探す。</summary>
        private static string FindNameNear(byte* start, int size, int idOffset, out int nameOffset)
        {
            nameOffset = -1;

            var from = Math.Max(0, idOffset - PairRadius);
            var to = Math.Min(size - 1, idOffset + PairRadius);

            var best = string.Empty;
            var bestDistance = int.MaxValue;

            var builder = new StringBuilder();
            var runStart = -1;

            for (var i = from; i <= to + 1; i++)
            {
                var c = i <= to ? (char)start[i] : '\0';
                var isNameChar = char.IsAsciiLetter(c) || c == ' ' || c == '\'' || c == '-';

                if (isNameChar)
                {
                    if (builder.Length == 0) runStart = i;
                    builder.Append(c);
                    continue;
                }

                var candidate = builder.ToString().Trim();
                builder.Clear();

                if (!IsCharacterName(candidate)) continue;

                var distance = Math.Abs(runStart - idOffset);
                if (distance >= bestDistance) continue;

                best = candidate;
                bestDistance = distance;
                nameOffset = runStart;
            }

            return best;
        }

        /// <summary>FFXIV のキャラクター名として妥当な形か。</summary>
        private static bool IsCharacterName(string value)
        {
            if (value.Length is < 5 or > 32) return false;
            if (!char.IsAsciiLetterUpper(value[0])) return false;

            var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 2) return false;

            return words.All(w =>
                w.Length >= 2 && char.IsAsciiLetterUpper(w[0]) &&
                w.All(c => char.IsAsciiLetter(c) || c is '\'' or '-'));
        }
    }
}
