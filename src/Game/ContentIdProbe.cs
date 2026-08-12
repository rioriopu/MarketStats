using System.Linq;
using MarketStats.Data;

namespace MarketStats.Game
{
    /// <summary>
    /// キャラクター識別子 1 つについて、集められる情報をすべて集める。
    ///
    /// 名前が分かるか、どのリテイナーと関係があるか、メモリのどこに現れるか、
    /// 購入者として何を買っているか──取れるものを一箇所にまとめて、
    /// その人物の正体に近づくための材料にする。
    /// </summary>
    public static class ContentIdProbe
    {
        public sealed class Finding
        {
            public string Category { get; set; } = string.Empty;
            public string Detail { get; set; } = string.Empty;
            public bool Positive { get; set; }
        }

        public sealed class Report
        {
            public ulong ContentId { get; set; }
            public string? Name { get; set; }
            public string NameSource { get; set; } = string.Empty;
            public ushort WorldId { get; set; }
            public string WorldName { get; set; } = string.Empty;

            public List<Finding> Findings { get; } = new();

            /// <summary>この人物が製作した品を扱っているリテイナー。</summary>
            public List<RetainerProfile> AsArtisan { get; } = new();

            /// <summary>この人物がオーナーとして記録されているリテイナー。</summary>
            public List<RetainerProfile> AsOwner { get; } = new();

            /// <summary>名前が判明している場合の、購入者としての実績。</summary>
            public MarketBuyerStat? AsBuyer { get; set; }

            /// <summary>メモリ上で見つかった場所。</summary>
            public List<ScanHit> MemoryHits { get; } = new();

            /// <summary>メモリ上でこの識別子の近くにあった名前。</summary>
            public List<string> NearbyNames { get; } = new();
        }

        public static Report Investigate(ulong contentId)
        {
            var report = new Report { ContentId = contentId };

            if (contentId == 0)
            {
                Add(report, "入力", "識別子が 0 です。", false);
                return report;
            }

            AnalyzeStructure(report);
            ResolveName(report);
            SearchRetainers(report);
            SearchAsBuyer(report);
            SearchMemory(report);

            return report;
        }

        /// <summary>識別子そのものの形を調べる。</summary>
        private static void AnalyzeStructure(Report report)
        {
            var id = report.ContentId;
            var high = id >> 48;
            var low = (uint)(id & 0xFFFF_FFFF);

            if (MemoryScanner.LooksLikeContentId(id))
                Add(report, "識別子の形",
                    $"キャラクター識別子の形です（上位 0x{high:X4} / 下位 0x{low:X8} = {low:N0}）。", true);
            else if (high == 0x0078)
                Add(report, "識別子の形",
                    $"これはリテイナーの識別子です（上位 0x{high:X4}）。キャラクター識別子ではありません。", false);
            else
                Add(report, "識別子の形",
                    $"見慣れない形です（上位 0x{high:X4}）。キャラクター識別子ではないかもしれません。", false);

            // 自分の識別子と比べると、作られた時期の前後が分かる。
            var self = SelfRetainerProbe.Read();
            if (self.ContentId == 0) return;

            var selfLow = (uint)(self.ContentId & 0xFFFF_FFFF);
            var difference = (long)low - selfLow;

            Add(report, "自分との比較",
                difference == 0
                    ? "自分自身の識別子です。"
                    : $"自分（0x{selfLow:X8}）との差は {Math.Abs(difference):N0}。" +
                      (difference < 0 ? "自分より早く作られたキャラクターです。" : "自分より後に作られたキャラクターです。"),
                difference == 0);
        }

        /// <summary>名前が分かるか、あらゆる経路で調べる。</summary>
        private static void ResolveName(Report report)
        {
            var identity = Plugin.Identities.Resolve(report.ContentId);
            if (identity != null)
            {
                report.Name = identity.Name;
                report.NameSource = identity.Source == IdentitySource.Inferred ? "推定" : "対応表";
                report.WorldId = identity.WorldId;

                Add(report, "対応表",
                    identity.Source == IdentitySource.Inferred
                        ? $"推定で {identity.Name}（確信度 {identity.InferenceScore}）"
                        : $"{identity.Name}（{DescribeSource(identity.Source)}）",
                    identity.Source != IdentitySource.Inferred);
            }
            else
            {
                Add(report, "対応表", "登録されていません。", false);
            }

            // ゲームが持つ各種リストに直接問い合わせる。
            if (IdentityCollector.TryLookupByContentId(report.ContentId, out var name, out var worldId))
            {
                report.Name = name;
                report.NameSource = "ゲーム内のリスト";
                report.WorldId = worldId;
                Add(report, "ゲーム内のリスト", $"見つかりました: {name}", true);
            }
            else
            {
                Add(report, "ゲーム内のリスト",
                    "フレンド / FC / リンクシェル / パーティ / コンテンツ同行者 / 手紙 / " +
                    "ブラックリスト / サークル のいずれにも居ません。", false);
            }

            if (report.WorldId != 0)
                report.WorldName = ResolveWorldName(report.WorldId);
        }

        /// <summary>この識別子と関係のあるリテイナーを探す。</summary>
        private static void SearchRetainers(Report report)
        {
            foreach (var profile in Plugin.Retainers.Snapshot())
            {
                if (profile.OwnerContentId == report.ContentId)
                    report.AsOwner.Add(profile);

                if (profile.ArtisanCounts.ContainsKey(report.ContentId))
                    report.AsArtisan.Add(profile);
            }

            Add(report, "オーナーとして",
                report.AsOwner.Count == 0
                    ? "この識別子をオーナーとするリテイナーは記録にありません。"
                    : $"{report.AsOwner.Count} 体のリテイナーのオーナーとして記録されています。",
                report.AsOwner.Count > 0);

            if (report.AsArtisan.Count == 0)
            {
                Add(report, "製作者として", "この識別子の製作品は観測していません。", false);
                return;
            }

            var total = report.AsArtisan.Sum(p => p.ArtisanCounts[report.ContentId]);
            var dominant = report.AsArtisan
                .Where(p => p.MainArtisanId == report.ContentId && p.MainArtisanRatio >= 0.8)
                .ToList();

            Add(report, "製作者として",
                $"{report.AsArtisan.Count} 体のリテイナーが、この人物の製作品を計 {total} 件扱っています。" +
                (dominant.Count > 0
                    ? $" うち {dominant.Count} 体は出品の大半がこの人物の製作品で、持ち主である可能性が高いです（{string.Join(", ", dominant.Select(p => p.RetainerName))}）。"
                    : string.Empty),
                dominant.Count > 0);
        }

        /// <summary>名前が分かっている場合、購入者としての実績を調べる。</summary>
        private static void SearchAsBuyer(Report report)
        {
            if (string.IsNullOrEmpty(report.Name))
            {
                Add(report, "購入者として", "名前が判明していないため照合できません。", false);
                return;
            }

            var buyers = BuyerAnalytics.Build(
                Plugin.Purchases, Plugin.Store, Plugin.Retainers, Plugin.Config.SessionWindowSeconds);

            report.AsBuyer = buyers.FirstOrDefault(b =>
                string.Equals(b.BuyerName, report.Name, StringComparison.OrdinalIgnoreCase));

            if (report.AsBuyer == null)
            {
                Add(report, "購入者として", $"{report.Name} の購入履歴は記録にありません。", false);
                return;
            }

            Add(report, "購入者として",
                $"{report.AsBuyer.TotalQuantity:N0}個を {report.AsBuyer.SessionCount} 回で購入" +
                $"（支払 {report.AsBuyer.TotalGil:N0} ギル / 品目 {report.AsBuyer.DistinctItems}）" +
                (report.AsBuyer.FromMeQuantity > 0
                    ? $"。うち {report.AsBuyer.FromMeQuantity:N0}個 はあなたから購入しています。"
                    : "。"),
                true);
        }

        /// <summary>メモリ上のどこに現れるか、その近くに何があるかを調べる。</summary>
        private static void SearchMemory(Report report)
        {
            var hits = MemoryScanner.ScanForValue(report.ContentId);
            report.MemoryHits.AddRange(hits);

            if (hits.Count == 0)
            {
                Add(report, "メモリ上の出現", "マーケット関連のメモリには現れていません。", false);
            }
            else
            {
                var inListing = hits.Where(h => h.ListingIndex >= 0).ToList();
                Add(report, "メモリ上の出現",
                    $"{hits.Count} 箇所で見つかりました。" +
                    (inListing.Count > 0
                        ? $"うち {inListing.Count} 件は出品データの中です（出品 #{string.Join(", #", inListing.Take(5).Select(h => h.ListingIndex))}）。"
                        : string.Empty),
                    true);
            }

            // 近くに名前が置かれていれば、それがこの識別子の持ち主かもしれない。
            try
            {
                var regions = IdentityPairScanner.EnumerateRegions();
                foreach (var pair in IdentityPairScanner.Scan(regions))
                {
                    if (pair.ContentId != report.ContentId) continue;
                    if (report.NearbyNames.Contains(pair.Name)) continue;
                    report.NearbyNames.Add(pair.Name);
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Debug($"近傍走査に失敗: {e.Message}");
            }

            if (report.NearbyNames.Count > 0)
            {
                Add(report, "近くにあった名前",
                    string.Join(", ", report.NearbyNames) + " — この識別子と同じレコードに置かれている可能性があります。",
                    true);

                report.Name ??= report.NearbyNames[0];
                if (string.IsNullOrEmpty(report.NameSource)) report.NameSource = "メモリ上の近接";
            }
        }

        private static string ResolveWorldName(ushort worldId)
        {
            try
            {
                var world = Plugin.DataManager
                    .GetExcelSheet<Lumina.Excel.Sheets.World>()?.GetRowOrDefault(worldId);
                return world?.Name.ExtractText() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string DescribeSource(IdentitySource source) => source switch
        {
            IdentitySource.Self => "自分",
            IdentitySource.CharaCard => "冒険者名刺",
            IdentitySource.Friend => "フレンド",
            IdentitySource.FreeCompany => "フリーカンパニー",
            IdentitySource.Linkshell => "リンクシェル",
            IdentitySource.Party => "パーティ",
            IdentitySource.ObjectTable => "周囲で見かけた",
            IdentitySource.MarketBoard => "マーケットの出品",
            _ => "不明",
        };

        private static void Add(Report report, string category, string detail, bool positive) =>
            report.Findings.Add(new Finding { Category = category, Detail = detail, Positive = positive });
    }
}
