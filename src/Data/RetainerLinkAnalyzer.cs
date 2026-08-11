using System.Linq;

namespace MarketStats.Data
{
    /// <summary>同じ持ち主だと思われるリテイナーの組。</summary>
    public sealed class RetainerLink
    {
        public ulong RetainerId { get; set; }
        public string RetainerName { get; set; } = string.Empty;
        public int Score { get; set; }
        public List<string> Reasons { get; } = new();

        /// <summary>相手側で持ち主が分かっている場合の名前。</summary>
        public string? KnownOwner { get; set; }

        public string ConfidenceLabel => Score switch
        {
            >= 120 => "高",
            >= 70 => "中",
            _ => "低",
        };
    }

    /// <summary>
    /// リテイナー同士の関連を調べ、「同じ人が使っているリテイナー」を割り出す。
    ///
    /// 持ち主の名前が分からなくても、同一人物のリテイナーを束ねられれば、
    /// そのうち 1 つで名前が判明したときに、まとめて全部に適用できる。
    ///
    /// 手がかり:
    ///   ・同じ製作者の品を扱っている（自作品を複数のリテイナーで売っている）
    ///   ・出品の時刻が揃っている（1 人が続けて操作すると、更新時刻が近くなる）
    ///   ・リテイナー ID が近い（まとめて作られた可能性）
    ///   ・扱っている商品の傾向が同じ
    /// </summary>
    public static class RetainerLinkAnalyzer
    {
        /// <summary>同時操作とみなす時間差（秒）。</summary>
        private const long SimultaneousWindow = 600;

        /// <summary>ID が近いとみなす差。</summary>
        private const ulong NearbyIdDistance = 64;

        public static List<RetainerLink> FindLinks(
            RetainerProfile target, ListingStore listings, RetainerRegistry registry)
        {
            var links = new Dictionary<ulong, RetainerLink>();

            var all = listings.Snapshot().Where(l => l.RetainerId != 0).ToList();
            var mine = all.Where(l => l.RetainerId == target.RetainerId).ToList();

            AddArtisanLinks(target, registry, links);
            AddTimingLinks(target, mine, all, registry, links);
            AddNearbyIdLinks(target, registry, links);
            AddItemOverlapLinks(target, mine, all, registry, links);

            foreach (var link in links.Values)
            {
                var profile = registry.Resolve(link.RetainerId);
                if (profile == null) continue;

                link.RetainerName = profile.RetainerName;
                link.KnownOwner = profile.IsMine || profile.HasOwner
                    ? profile.OwnerName
                    : profile.GuessedOwnerName;
            }

            return links.Values
                .Where(l => l.Score >= 40)
                .OrderByDescending(l => l.Score)
                .ToList();
        }

        /// <summary>同じ製作者の品を扱っている。</summary>
        private static void AddArtisanLinks(
            RetainerProfile target, RetainerRegistry registry, Dictionary<ulong, RetainerLink> links)
        {
            foreach (var (artisanId, count) in target.ArtisanCounts)
            {
                if (count < 1) continue;

                foreach (var other in registry.WithSameArtisan(artisanId, target.RetainerId))
                {
                    var shared = other.ArtisanCounts[artisanId];

                    // 自作品を並べている同士なら、同じ人である可能性が高い。
                    var bothDominant = target.MainArtisanId == artisanId && other.MainArtisanId == artisanId;
                    var score = bothDominant ? 70 : 35;

                    Bump(links, other.RetainerId, score,
                        bothDominant
                            ? $"どちらも同じ製作者（0x{artisanId:X}）の品が主力です"
                            : $"同じ製作者（0x{artisanId:X}）の品を扱っています（相手側 {shared} 件）");
                }
            }
        }

        /// <summary>出品の時刻が揃っている（1 人が続けて操作した形跡）。</summary>
        private static void AddTimingLinks(
            RetainerProfile target,
            List<ListingRecord> mine,
            List<ListingRecord> all,
            RetainerRegistry registry,
            Dictionary<ulong, RetainerLink> links)
        {
            if (mine.Count == 0) return;

            var myTimes = mine.Select(l => l.EffectiveListedUnix).Where(t => t > 0).ToList();
            if (myTimes.Count == 0) return;

            foreach (var group in all.Where(l => l.RetainerId != target.RetainerId).GroupBy(l => l.RetainerId))
            {
                var matches = 0;

                foreach (var listing in group)
                {
                    var listedAt = listing.EffectiveListedUnix;
                    if (listedAt <= 0) continue;
                    if (myTimes.Any(t => Math.Abs(t - listedAt) <= SimultaneousWindow)) matches++;
                }

                if (matches < 2) continue;

                var score = Math.Min(60, matches * 15);
                Bump(links, group.Key, score,
                    $"出品の更新時刻が {matches} 件で揃っています（同じ人が続けて操作した形跡）");
            }
        }

        /// <summary>リテイナー ID が近い。</summary>
        private static void AddNearbyIdLinks(
            RetainerProfile target, RetainerRegistry registry, Dictionary<ulong, RetainerLink> links)
        {
            foreach (var (other, distance) in registry.WithNearbyId(target.RetainerId, NearbyIdDistance))
            {
                var score = distance switch
                {
                    <= 4 => 50,
                    <= 16 => 30,
                    _ => 15,
                };

                Bump(links, other.RetainerId, score,
                    $"リテイナー ID が近い（差 {distance}。まとめて作られた可能性）");
            }
        }

        /// <summary>扱っている商品が重なっている。</summary>
        private static void AddItemOverlapLinks(
            RetainerProfile target,
            List<ListingRecord> mine,
            List<ListingRecord> all,
            RetainerRegistry registry,
            Dictionary<ulong, RetainerLink> links)
        {
            var myItems = mine.Select(l => l.ItemId).ToHashSet();
            if (myItems.Count == 0) return;

            foreach (var group in all.Where(l => l.RetainerId != target.RetainerId).GroupBy(l => l.RetainerId))
            {
                var otherItems = group.Select(l => l.ItemId).ToHashSet();
                var overlap = myItems.Intersect(otherItems).Count();
                if (overlap == 0) continue;

                // 品揃えが被っているだけなら弱い手がかり。単価まで一致すると強くなる。
                var samePrice = group.Any(o => mine.Any(m =>
                    m.ItemId == o.ItemId && m.UnitPrice == o.UnitPrice));

                var score = samePrice ? 35 : Math.Min(25, overlap * 8);
                Bump(links, group.Key, score,
                    samePrice
                        ? $"同じ商品を同じ単価で出しています（{overlap} 品目が重複）"
                        : $"扱っている商品が {overlap} 品目重なっています");
            }
        }

        private static void Bump(
            Dictionary<ulong, RetainerLink> links, ulong retainerId, int score, string reason)
        {
            if (!links.TryGetValue(retainerId, out var link))
            {
                link = new RetainerLink { RetainerId = retainerId };
                links[retainerId] = link;
            }

            link.Score += score;
            if (!link.Reasons.Contains(reason)) link.Reasons.Add(reason);
        }
    }
}
