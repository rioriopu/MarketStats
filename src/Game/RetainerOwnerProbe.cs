using System.Linq;
using MarketStats.Data;

namespace MarketStats.Game
{
    /// <summary>
    /// リテイナー ID を起点に、持ち主へ辿り着けないかを片端から試す。
    ///
    /// 出品データのオーナー識別子は届いていないことが分かっているので、
    /// 「リテイナー ID そのものから持ち主を引けないか」を、考えられる経路すべてで試す。
    /// 成功したものがあれば、それが正式な取得手段になる。
    /// </summary>
    public static unsafe class RetainerOwnerProbe
    {
        /// <summary>キャラクター識別子の上位 16 ビット（0x0040_0000_XXXX_XXXX 形式）。</summary>
        private const ulong ContentIdPrefix = 0x0040;

        /// <summary>リテイナー識別子の上位 16 ビット（0x0078_0000_XXXX_XXXX 形式）。</summary>
        private const ulong RetainerIdPrefix = 0x0078;

        public sealed class Attempt
        {
            public string Method { get; set; } = string.Empty;
            public string Result { get; set; } = string.Empty;
            public bool Success { get; set; }
        }

        public sealed class ProbeResult
        {
            public ulong RetainerId { get; set; }
            public string RetainerName { get; set; } = string.Empty;
            public List<Attempt> Attempts { get; } = new();
            public string? OwnerName { get; set; }
            public ulong OwnerContentId { get; set; }

            /// <summary>名刺で確かめる価値のある候補（識別子の形をしているもの）。</summary>
            public List<ulong> ContentIdCandidates { get; } = new();
        }

        public static ProbeResult TryResolve(ulong retainerId)
        {
            var result = new ProbeResult { RetainerId = retainerId };

            if (retainerId == 0)
            {
                Add(result, "入力", "リテイナー ID が 0 です。", false);
                return result;
            }

            var profile = Plugin.Retainers.Resolve(retainerId);
            if (profile != null) result.RetainerName = profile.RetainerName;

            TryRegistry(result, profile);
            TryIdentityTable(result, retainerId);
            TryGameLists(result, retainerId);
            TryPrefixConversion(result, retainerId);
            TryNeighbourScan(result, retainerId);
            TryArtisanRoute(result, profile);

            return result;
        }

        /// <summary>1. すでに台帳で持ち主が分かっていないか。</summary>
        private static void TryRegistry(ProbeResult result, RetainerProfile? profile)
        {
            if (profile == null)
            {
                Add(result, "台帳を引く", "このリテイナーは未観測です。", false);
                return;
            }

            if (profile.IsMine)
            {
                result.OwnerName = profile.OwnerName;
                Add(result, "台帳を引く", $"自分のリテイナーです（{profile.OwnerName}）。", true);
                return;
            }

            if (profile.HasOwner)
            {
                result.OwnerName = profile.OwnerName;
                Add(result, "台帳を引く", $"確定済み: {profile.OwnerName}", true);
                return;
            }

            Add(result, "台帳を引く",
                string.IsNullOrEmpty(profile.GuessedOwnerName)
                    ? "持ち主は未判明です。"
                    : $"推定のみ: {profile.GuessedOwnerName}（確度 {profile.Confidence}）",
                false);
        }

        /// <summary>2. リテイナー ID をそのままキャラクター識別子として対応表に問い合わせる。</summary>
        private static void TryIdentityTable(ProbeResult result, ulong retainerId)
        {
            var identity = Plugin.Identities.Resolve(retainerId);
            if (identity != null)
            {
                result.OwnerName = identity.Name;
                result.OwnerContentId = retainerId;
                Add(result, "対応表を引く", $"一致しました: {identity.Name}", true);
                return;
            }

            Add(result, "対応表を引く", "リテイナー ID はキャラクター識別子として登録されていません。", false);
        }

        /// <summary>3. ゲームが持つ各リスト（フレンド / FC / LS など）に問い合わせる。</summary>
        private static void TryGameLists(ProbeResult result, ulong retainerId)
        {
            if (IdentityCollector.TryLookupByContentId(retainerId, out var name, out _))
            {
                result.OwnerName = name;
                result.OwnerContentId = retainerId;
                Add(result, "ゲーム内のリストを引く", $"一致しました: {name}", true);
                return;
            }

            Add(result, "ゲーム内のリストを引く",
                "フレンド / FC / リンクシェル等のどのリストにも該当しませんでした。", false);
        }

        /// <summary>
        /// 4. 識別子の作りが似ているので、上位を入れ替えたら持ち主になるかを試す。
        /// リテイナー ID は 0x0078_0000_XXXX_XXXX、キャラクター ID は 0x0040_0000_XXXX_XXXX の形。
        /// </summary>
        private static void TryPrefixConversion(ProbeResult result, ulong retainerId)
        {
            var high = retainerId >> 48;
            if (high != RetainerIdPrefix)
            {
                Add(result, "識別子の変換",
                    $"上位が想定と違います（0x{high:X}）。変換は試しません。", false);
                return;
            }

            var converted = (ContentIdPrefix << 48) | (retainerId & 0x0000_FFFF_FFFF_FFFFUL);

            var identity = Plugin.Identities.Resolve(converted);
            if (identity != null)
            {
                result.OwnerName = identity.Name;
                result.OwnerContentId = converted;
                Add(result, "識別子の変換",
                    $"上位を差し替えた 0x{converted:X} が {identity.Name} と一致しました。", true);
                return;
            }

            if (IdentityCollector.TryLookupByContentId(converted, out var name, out _))
            {
                result.OwnerName = name;
                result.OwnerContentId = converted;
                Add(result, "識別子の変換",
                    $"上位を差し替えた 0x{converted:X} が {name} と一致しました。", true);
                return;
            }

            result.ContentIdCandidates.Add(converted);
            Add(result, "識別子の変換",
                $"上位を差し替えると 0x{converted:X}。該当者は見つかりませんでしたが、名刺で確認できます。",
                false);
        }

        /// <summary>
        /// 5. メモリ上でリテイナー ID を探し、その近くにキャラクター識別子が置かれていないかを見る。
        /// ゲームが内部で対応を持っていれば、隣接して格納されている可能性がある。
        /// </summary>
        private static void TryNeighbourScan(ProbeResult result, ulong retainerId)
        {
            var hits = MemoryScanner.ScanForValue(retainerId);
            if (hits.Count == 0)
            {
                Add(result, "近くのメモリを調べる",
                    "このリテイナー ID はマーケット関連のメモリに見つかりませんでした。", false);
                return;
            }

            var candidates = MemoryScanner.FindContentIdsNear(retainerId, 0xC0);

            if (candidates.Count == 0)
            {
                Add(result, "近くのメモリを調べる",
                    $"{hits.Count} 箇所で見つかりましたが、周辺にキャラクター識別子はありませんでした。", false);
                return;
            }

            foreach (var candidate in candidates.Take(5))
            {
                var identity = Plugin.Identities.Resolve(candidate);
                if (identity != null)
                {
                    result.OwnerName = identity.Name;
                    result.OwnerContentId = candidate;
                    Add(result, "近くのメモリを調べる",
                        $"隣接する 0x{candidate:X} が {identity.Name} と一致しました。", true);
                    return;
                }

                result.ContentIdCandidates.Add(candidate);
            }

            Add(result, "近くのメモリを調べる",
                $"周辺に識別子らしき値が {candidates.Count} 件ありました（名刺で確認できます）: " +
                string.Join(", ", candidates.Take(3).Select(c => $"0x{c:X}")),
                false);
        }

        /// <summary>6. 製作者署名から辿る。自作品を並べているなら製作者が持ち主の可能性が高い。</summary>
        private static void TryArtisanRoute(ProbeResult result, RetainerProfile? profile)
        {
            if (profile == null || profile.SignedListingCount == 0)
            {
                Add(result, "製作者から辿る", "製作者署名のある出品を観測していません。", false);
                return;
            }

            var artisanId = profile.MainArtisanId;
            var ratio = profile.MainArtisanRatio;
            var identity = Plugin.Identities.Resolve(artisanId);

            if (identity != null && ratio >= 0.8)
            {
                result.OwnerName ??= identity.Name;
                result.OwnerContentId = result.OwnerContentId == 0 ? artisanId : result.OwnerContentId;
                Add(result, "製作者から辿る",
                    $"出品の {ratio * 100:F0}% が {identity.Name} の製作品です。持ち主の可能性が高いです。", true);
                return;
            }

            if (!result.ContentIdCandidates.Contains(artisanId))
                result.ContentIdCandidates.Add(artisanId);

            Add(result, "製作者から辿る",
                identity == null
                    ? $"主な製作者は 0x{artisanId:X}（署名の {ratio * 100:F0}%）。名刺で名前を確認できます。"
                    : $"{identity.Name} の品が {ratio * 100:F0}%。偏りが小さいため断定はできません。",
                false);
        }

        private static void Add(ProbeResult result, string method, string text, bool success) =>
            result.Attempts.Add(new Attempt { Method = method, Result = text, Success = success });
    }
}
