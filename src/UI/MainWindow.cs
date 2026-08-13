using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketStats.Data;

namespace MarketStats.UI
{
    /// <summary>
    /// メインウィンドウ。タブごとの描画は partial で別ファイルに分けている。
    ///   購入者別 : MainWindow.BuyersTab.cs
    ///   取引履歴 : MainWindow.HistoryTab.cs
    ///   相場     : MainWindow.MarketTab.cs
    ///   設定     : MainWindow.SettingsTab.cs
    ///   ご支援   : MainWindow.DonationTab.cs
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        public enum Tab
        {
            Search,
            Buyers,
            History,
            OwnListings,
            Sellers,
            Players,
            MarketBuyers,
            Retainers,
            Market,
            Probe,
            Settings,
            Donation,
        }

        // 表示フィルタ
        private string _search = string.Empty;
        private int _periodIndex;          // 0=すべて 1=24時間 2=3日 3=7日
        private bool _favoritesOnly;

        // 集計キャッシュ
        private List<BuyerStat> _stats = new();
        private List<SaleRecord> _filtered = new();
        private Dictionary<(string Buyer, uint ItemId, bool Hq), long> _cumulative = new();
        private bool _statsDirty = true;

        // 選択状態
        private string? _selectedBuyer;
        private Tab? _requestedTab;

        /// <summary>表示するキャラクター（0 ならすべて）。複数キャラを使い分けるための絞り込み。</summary>
        private ulong _characterFilter;

        private static readonly string[] PeriodLabels = { "すべて", "24時間", "3日", "1週間" };
        private static readonly int[] PeriodDays = { 0, 1, 3, 7 };

        private static readonly Vector4 ColorAccent = new(1f, 0.85f, 0.45f, 1f);
        private static readonly Vector4 ColorMuted = new(0.65f, 0.65f, 0.65f, 1f);
        private static readonly Vector4 ColorLink = new(0.55f, 0.78f, 1f, 1f);
        private static readonly Vector4 ColorFavorite = new(1f, 0.82f, 0.3f, 1f);

        public MainWindow() : base("Market Stats##MarketStatsMain")
        {
            Size = new Vector2(940, 640);
            SizeCondition = ImGuiCond.FirstUseEver;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(620, 400),
                MaximumSize = new Vector2(4000, 3000),
            };
        }

        /// <summary>売却ログが変化したときに呼ぶ。次の描画で集計を作り直す。</summary>
        public void InvalidateCache()
        {
            _statsDirty = true;
            _resaleCacheBuyer = null;
        }

        /// <summary>外部（コマンド等）から開くタブを指定する。</summary>
        public void RequestTab(Tab tab) => _requestedTab = tab;

        public override void Draw()
        {
            EnsureStats();
            DrawSummaryBar();

            if (!ImGui.BeginTabBar("##marketstats_tabs")) return;

            DrawTab("検索", Tab.Search, DrawSearchTab);
            DrawTab("購入者別", Tab.Buyers, DrawBuyersTab);
            DrawTab("取引履歴", Tab.History, DrawHistoryTab);
            DrawTab("自分の出品", Tab.OwnListings, DrawOwnListingsTab);
            DrawTab("出品者", Tab.Sellers, DrawSellersTab);
            DrawTab("プレイヤー", Tab.Players, DrawPlayersTab);
            DrawTab("買い占め", Tab.MarketBuyers, DrawMarketBuyersTab);
            DrawTab("リテイナー", Tab.Retainers, DrawRetainersTab);
            DrawTab("相場", Tab.Market, DrawMarketTab);
            DrawTab("検証", Tab.Probe, DrawProbeTab);
            DrawTab("設定", Tab.Settings, DrawSettingsTab);
            DrawTab("ご支援", Tab.Donation, DrawDonationTab);

            ImGui.EndTabBar();
        }

        private void DrawTab(string label, Tab tab, Action body)
        {
            var flags = _requestedTab == tab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (!ImGui.BeginTabItem(label, flags)) return;

            if (_requestedTab == tab) _requestedTab = null;
            body();
            ImGui.EndTabItem();
        }

        /// <summary>ウィンドウ上部の概況表示。</summary>
        private void DrawSummaryBar()
        {
            var totalGil = _filtered.Aggregate(0UL, (acc, r) => acc + r.TotalGil);
            var buyerCount = _stats.Count(s => !s.IsMannequin);

            ImGui.TextColored(ColorAccent, $"購入者 {buyerCount} 人");
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "/");
            ImGui.SameLine();
            ImGui.Text($"取引 {_filtered.Count:N0} 件");
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "/");
            ImGui.SameLine();
            ImGui.Text($"売上 {totalGil:N0} ギル");

            ImGui.SameLine();
            var status = Plugin.Capture.HookActive ? "自動取り込み: 有効" : "自動取り込み: 履歴ウィンドウ経由";
            var width = ImGui.GetContentRegionAvail().X;
            var textWidth = ImGui.CalcTextSize(status).X;
            if (width > textWidth + 20) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + width - textWidth - 8);
            ImGui.TextColored(Plugin.Capture.HookActive ? ColorMuted : ColorAccent, status);

            ImGui.Separator();
        }

        /// <summary>フィルタ済みレコードと集計を必要に応じて作り直す。</summary>
        private void EnsureStats()
        {
            if (!_statsDirty) return;
            _statsDirty = false;

            var records = Plugin.Store.Snapshot();

            // 表示するキャラクターの絞り込み（0 = すべて）
            if (_characterFilter != 0)
                records = records.Where(r => r.OwnerContentId == _characterFilter).ToList();
            else if (Plugin.Config.FilterCurrentCharacterOnly)
            {
                var cid = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0;
                if (cid != 0) records = records.Where(r => r.OwnerContentId == cid).ToList();
            }

            if (!Plugin.Config.IncludeMannequinSales)
                records = records.Where(r => r.HasBuyer).ToList();

            var days = PeriodDays[Math.Clamp(_periodIndex, 0, PeriodDays.Length - 1)];
            if (days > 0)
            {
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)days * 86400L;
                records = records.Where(r => r.UnixTime >= cutoff).ToList();
            }

            if (!string.IsNullOrWhiteSpace(_search))
            {
                var q = _search.Trim();
                records = records.Where(r =>
                    r.BuyerName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    Plugin.Items.GetName(r.ItemId).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    r.RetainerName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            _filtered = records;
            _stats = SaleAggregator.Build(
                records, Plugin.Favorites.IsFavorite, Plugin.Config.SessionWindowSeconds);

            // 取引履歴タブで「同一購入者 × 同一アイテムの累計」を毎行引けるようにしておく。
            _cumulative = new Dictionary<(string, uint, bool), long>();
            foreach (var buyer in _stats)
            foreach (var item in buyer.Items)
                _cumulative[(buyer.BuyerName, item.ItemId, item.Hq)] = item.TotalQuantity;

            if (_favoritesOnly)
                _stats = _stats.Where(s => s.IsFavorite).ToList();

            if (_selectedBuyer != null && _stats.All(s => s.BuyerName != _selectedBuyer))
                _selectedBuyer = null;
        }

        /// <summary>フィルタ行（検索 / 期間 / お気に入りのみ）。</summary>
        private void DrawFilterRow()
        {
            ImGui.SetNextItemWidth(220);
            var search = _search;
            if (ImGui.InputTextWithHint("##search", "購入者 / アイテム / リテイナー名で絞り込み", ref search, 64))
            {
                _search = search;
                _statsDirty = true;
            }

            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "期間");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110);
            var period = _periodIndex;
            if (ImGui.Combo("##period", ref period, PeriodLabels, PeriodLabels.Length))
            {
                _periodIndex = period;
                _statsDirty = true;
            }
            AttachTooltip("表示する期間の絞り込みです。保持期間（設定タブ）とは別で、ログ自体は削除されません。");

            ImGui.SameLine();
            DrawCharacterFilter();

            ImGui.SameLine();
            var favOnly = _favoritesOnly;
            if (ImGui.Checkbox("お気に入りのみ", ref favOnly))
            {
                _favoritesOnly = favOnly;
                _statsDirty = true;
            }
        }

        /// <summary>
        /// 表示するキャラクターを選ぶ。複数のキャラクターでリテイナーを運用している場合に、
        /// キャラごとの売上を切り替えて見られるようにする。
        /// </summary>
        private void DrawCharacterFilter()
        {
            var characters = CollectCharacters();
            if (characters.Count <= 1) return;   // 1 人しか居なければ出す意味がない

            var current = _characterFilter == 0
                ? "すべてのキャラ"
                : characters.FirstOrDefault(c => c.ContentId == _characterFilter).Display ?? "すべてのキャラ";

            ImGui.SetNextItemWidth(180);
            if (!ImGui.BeginCombo("##charfilter", current)) return;

            if (ImGui.Selectable("すべてのキャラ", _characterFilter == 0))
            {
                _characterFilter = 0;
                _statsDirty = true;
            }

            foreach (var character in characters)
            {
                if (!ImGui.Selectable(character.Display, _characterFilter == character.ContentId)) continue;
                _characterFilter = character.ContentId;
                _statsDirty = true;
            }

            ImGui.EndCombo();
        }

        /// <summary>売却ログとリテイナー情報から、これまでに確認したキャラクターを集める。</summary>
        private static List<(ulong ContentId, string Display)> CollectCharacters()
        {
            var map = new Dictionary<ulong, string>();

            foreach (var record in Plugin.Store.Snapshot())
            {
                if (record.OwnerContentId == 0 || string.IsNullOrEmpty(record.OwnerName)) continue;
                map[record.OwnerContentId] = string.IsNullOrEmpty(record.OwnerWorld)
                    ? record.OwnerName
                    : $"{record.OwnerName} @ {record.OwnerWorld}";
            }

            foreach (var character in Plugin.OwnListings.ByCharacter())
                if (character.ContentId != 0) map[character.ContentId] = character.Display;

            return map.Select(kv => (kv.Key, kv.Value)).OrderBy(c => c.Value).ToList();
        }

        // ---- 共通ヘルパー ----

        private static void AttachTooltip(string text)
        {
            if (!ImGui.IsItemHovered()) return;
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(420);
            ImGui.TextWrapped(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        /// <summary>「3分前」「2時間前」のような相対表記。</summary>
        private static string RelativeTime(long unixTime)
        {
            var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixTime);
            if (span.TotalMinutes < 1) return "たった今";
            if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}分前";
            if (span.TotalDays < 1) return $"{(int)span.TotalHours}時間前";
            return $"{(int)span.TotalDays}日前";
        }

        private static string FormatDateTime(long unixTime) =>
            DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime.ToString("M/d HH:mm");

        /// <summary>購入者名を Lodestone 検索リンクとして描画する。</summary>
        private static void DrawBuyerLink(BuyerStat buyer)
        {
            if (buyer.IsMannequin)
            {
                ImGui.TextColored(ColorMuted, buyer.BuyerName);
                AttachTooltip("マネキン販売など、購入者名が記録されていない取引です。");
                return;
            }

            ImGui.TextColored(ColorLink, buyer.BuyerName);
            if (ImGui.IsItemClicked()) LodestoneOpen(buyer.BuyerName);
            AttachTooltip($"クリックで Lodestone のキャラクター検索を開きます。\n{Game.LodestoneLink.BuildSearchUrl(buyer.BuyerName)}");
        }

        private static void LodestoneOpen(string name) => Game.LodestoneLink.OpenSearch(name);
    }
}
