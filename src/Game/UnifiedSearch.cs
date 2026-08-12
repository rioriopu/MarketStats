using System.Linq;
using System.Text.RegularExpressions;
using MarketStats.Data;

namespace MarketStats.Game
{
    /// <summary>入力をどう解釈したか。</summary>
    public enum SearchInputKind
    {
        Unknown,
        ContentId,
        RetainerId,
        Name,
        LodestoneUrl,
    }

    /// <summary>検索で見つかった 1 件。</summary>
    public sealed class SearchHit
    {
        /// <summary>どこで見つかったか（対応表 / リテイナー台帳 / 購入履歴 …）。</summary>
        public string Source { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        /// <summary>この結果から辿れる識別子。</summary>
        public ulong ContentId { get; set; }
        public ulong RetainerId { get; set; }

        /// <summary>この結果に紐づくキャラクター名。</summary>
        public string? CharacterName { get; set; }

        /// <summary>並べ替え用の重み（大きいほど上）。</summary>
        public int Rank { get; set; }
    }

    public sealed class SearchOutcome
    {
        public string Input { get; set; } = string.Empty;
        public SearchInputKind Kind { get; set; }
        public string Interpretation { get; set; } = string.Empty;
        public List<SearchHit> Hits { get; } = new();
        public List<string> Suggestions { get; } = new();
    }

    /// <summary>
    /// 何を入れても受け付ける検索。
    ///
    /// 識別子・キャラクター名・リテイナー名・アイテム名・Lodestone の URL を
    /// 入力の形から自動で判別し、持っているデータすべてに横断で問い合わせる。
    /// どこに何があるかを利用者が覚えておく必要をなくすのが狙い。
    /// </summary>
    public static class UnifiedSearch
    {
        public static SearchOutcome Search(string input)
        {
            var outcome = new SearchOutcome { Input = input.Trim() };
            if (string.IsNullOrWhiteSpace(outcome.Input)) return outcome;

            var text = outcome.Input;

            // Lodestone の URL なら ID だけ抜き出して案内する。
            var lodestone = Regex.Match(text, @"lodestone/character/(\d+)");
            if (lodestone.Success)
            {
                outcome.Kind = SearchInputKind.LodestoneUrl;
                outcome.Interpretation =
                    $"Lodestone のキャラクターページ（ID {lodestone.Groups[1].Value}）と解釈しました。" +
                    "Lodestone の ID はゲーム内の識別子とは別物なので、" +
                    "ページに表示されているキャラクター名で検索してください。";
                outcome.Suggestions.Add("ページの名前をコピーして、もう一度検索してください。");
                return outcome;
            }

            if (TryParseId(text, out var id))
            {
                outcome.Kind = (id >> 48) == 0x0078 ? SearchInputKind.RetainerId : SearchInputKind.ContentId;
                outcome.Interpretation = outcome.Kind == SearchInputKind.RetainerId
                    ? $"リテイナーの識別子（0x{id:X}）と解釈しました。"
                    : $"キャラクターの識別子（0x{id:X}）と解釈しました。";

                SearchById(outcome, id);
            }
            else
            {
                outcome.Kind = SearchInputKind.Name;
                outcome.Interpretation = "名前として、すべての記録を横断で探しました。";
                SearchByText(outcome, text);
            }

            outcome.Hits.Sort((a, b) => b.Rank.CompareTo(a.Rank));

            if (outcome.Hits.Count == 0)
                AddEmptySuggestions(outcome);

            return outcome;
        }

        // ---- 識別子で探す ----

        private static void SearchById(SearchOutcome outcome, ulong id)
        {
            var identity = Plugin.Identities.Resolve(id);
            if (identity != null)
                outcome.Hits.Add(new SearchHit
                {
                    Source = "対応表",
                    Title = identity.Name,
                    Detail = $"出所: {identity.Source}" +
                             (identity.Source == IdentitySource.Inferred ? "（推定）" : "（確定）"),
                    ContentId = id,
                    CharacterName = identity.Name,
                    Rank = identity.Source == IdentitySource.Inferred ? 70 : 100,
                });

            foreach (var profile in Plugin.Retainers.Snapshot())
            {
                if (profile.RetainerId == id)
                    outcome.Hits.Add(new SearchHit
                    {
                        Source = "リテイナー台帳",
                        Title = profile.RetainerName,
                        Detail = $"持ち主 {profile.DisplayOwner} / 出品 {profile.ObservedListings} 件",
                        RetainerId = profile.RetainerId,
                        ContentId = profile.OwnerContentId,
                        CharacterName = profile.OwnerName ?? profile.GuessedOwnerName,
                        Rank = 95,
                    });

                if (profile.OwnerContentId == id)
                    outcome.Hits.Add(new SearchHit
                    {
                        Source = "リテイナー台帳（オーナー）",
                        Title = profile.RetainerName,
                        Detail = "この識別子がオーナーとして記録されています",
                        RetainerId = profile.RetainerId,
                        ContentId = id,
                        Rank = 90,
                    });

                if (profile.ArtisanCounts.TryGetValue(id, out var count))
                    outcome.Hits.Add(new SearchHit
                    {
                        Source = "製作者署名",
                        Title = profile.RetainerName,
                        Detail = $"この製作者の品を {count} 件扱っています" +
                                 $"（署名の {profile.MainArtisanRatio * 100:F0}% / 持ち主 {profile.DisplayOwner}）",
                        RetainerId = profile.RetainerId,
                        ContentId = id,
                        Rank = profile.MainArtisanId == id ? 85 : 60,
                    });
            }

            // メモリ上にあるかどうかも見る。
            var memoryHits = MemoryScanner.ScanForValue(id);
            if (memoryHits.Count > 0)
                outcome.Hits.Add(new SearchHit
                {
                    Source = "メモリ",
                    Title = $"{memoryHits.Count} 箇所で発見",
                    Detail = string.Join(" / ", memoryHits.Take(3).Select(h => h.ToString())),
                    ContentId = id,
                    Rank = 40,
                });

            if (identity == null)
                outcome.Suggestions.Add("名刺で調べると名前が判明することがあります。");
        }

        // ---- 名前・語句で探す ----

        private static void SearchByText(SearchOutcome outcome, string text)
        {
            // 対応表
            foreach (var identity in Plugin.Identities.SearchByName(text).Take(20))
                outcome.Hits.Add(new SearchHit
                {
                    Source = "対応表",
                    Title = identity.Name,
                    Detail = $"識別子 0x{identity.ContentId:X} / 出所 {identity.Source}",
                    ContentId = identity.ContentId,
                    CharacterName = identity.Name,
                    Rank = identity.Source == IdentitySource.Inferred ? 70 : 100,
                });

            // リテイナー台帳（リテイナー名・持ち主名）
            foreach (var profile in Plugin.Retainers.Snapshot())
            {
                var matchesRetainer = profile.RetainerName.Contains(text, StringComparison.OrdinalIgnoreCase);
                var matchesOwner =
                    (profile.OwnerName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (profile.GuessedOwnerName?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false);

                if (!matchesRetainer && !matchesOwner) continue;

                outcome.Hits.Add(new SearchHit
                {
                    Source = matchesRetainer ? "リテイナー名" : "リテイナーの持ち主",
                    Title = profile.RetainerName,
                    Detail = $"持ち主 {profile.DisplayOwner} / 出品 {profile.ObservedListings} 件 / " +
                             $"ID 0x{profile.RetainerId:X}",
                    RetainerId = profile.RetainerId,
                    ContentId = profile.OwnerContentId,
                    CharacterName = profile.OwnerName ?? profile.GuessedOwnerName,
                    Rank = matchesRetainer ? 90 : 80,
                });
            }

            // 購入履歴（買い手）
            var buyers = BuyerAnalytics.Build(
                Plugin.Purchases, Plugin.Store, Plugin.Retainers, Plugin.Config.SessionWindowSeconds);

            foreach (var buyer in buyers
                         .Where(b => b.BuyerName.Contains(text, StringComparison.OrdinalIgnoreCase))
                         .Take(20))
                outcome.Hits.Add(new SearchHit
                {
                    Source = "購入履歴",
                    Title = buyer.BuyerName,
                    Detail = $"{buyer.TotalQuantity:N0}個 / {buyer.SessionCount}回 / " +
                             $"{buyer.TotalGil:N0} ギル" +
                             (buyer.FromMeQuantity > 0 ? $"（うちあなたから {buyer.FromMeQuantity:N0}個）" : string.Empty),
                    CharacterName = buyer.BuyerName,
                    Rank = 95,
                });

            // 自分の販売履歴（購入者）
            foreach (var group in Plugin.Store.Snapshot()
                         .Where(r => r.HasBuyer &&
                                     r.BuyerName.Contains(text, StringComparison.OrdinalIgnoreCase))
                         .GroupBy(r => r.BuyerName)
                         .Take(10))
                outcome.Hits.Add(new SearchHit
                {
                    Source = "あなたの販売履歴",
                    Title = group.Key,
                    Detail = $"あなたから {group.Sum(r => (long)r.Quantity):N0}個 購入（{group.Count()} 件）",
                    CharacterName = group.Key,
                    Rank = 98,
                });

            // アイテム名でも探す（そのアイテムを扱っているリテイナー）
            var itemIds = Plugin.Listings.Snapshot()
                .Select(l => l.ItemId)
                .Distinct()
                .Where(id => Plugin.Items.GetName(id).Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(5)
                .ToList();

            foreach (var itemId in itemIds)
            {
                var sellers = Plugin.Listings.Snapshot()
                    .Where(l => l.ItemId == itemId && l.RetainerId != 0)
                    .Select(l => l.RetainerName)
                    .Distinct()
                    .Take(8)
                    .ToList();

                outcome.Hits.Add(new SearchHit
                {
                    Source = "アイテム",
                    Title = Plugin.Items.GetName(itemId),
                    Detail = sellers.Count == 0
                        ? "出品を観測していません"
                        : $"扱っているリテイナー: {string.Join(", ", sellers)}",
                    Rank = 50,
                });
            }
        }

        private static void AddEmptySuggestions(SearchOutcome outcome)
        {
            outcome.Suggestions.Add(
                $"手持ちの記録: 対応表 {Plugin.Identities.Count:N0} 件 / " +
                $"リテイナー {Plugin.Retainers.Count:N0} 体 / " +
                $"購入履歴 {Plugin.Purchases.Count:N0} 件 / " +
                $"販売ログ {Plugin.Store.Count:N0} 件");

            if (Plugin.Purchases.Count < 500)
                outcome.Suggestions.Add(
                    "購入履歴が少なめです。「買い占め」タブの「マーケット全体から集める」でまとめて増やせます。");

            if (Plugin.Identities.ConfirmedCount < 50)
                outcome.Suggestions.Add(
                    "対応表が少なめです。マーケットで製作品にカーソルを合わせると自動で増えます。");
        }

        private static bool TryParseId(string text, out ulong id)
        {
            id = 0;
            text = text.Trim();

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out id);

            return text.Length >= 6 && text.All(char.IsAsciiDigit) && ulong.TryParse(text, out id);
        }
    }
}
