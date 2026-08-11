using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketStats.Data;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private List<MarketBuyerStat> _marketBuyers = new();
        private bool _marketBuyersDirty = true;
        private string _marketBuyerSearch = string.Empty;
        private int _marketBuyerPeriod = 2;      // 既定は 1 週間
        private int _marketBuyerSort;
        private string? _selectedMarketBuyer;
        private bool _onlyMyCustomers;

        private static readonly string[] MarketPeriodLabels = { "24時間", "3日", "1週間", "1ヶ月", "すべて" };
        private static readonly int[] MarketPeriodDays = { 1, 3, 7, 30, 0 };

        private static readonly string[] MarketSortLabels =
        {
            "購入数が多い順",
            "購入回数が多い順",
            "支払額が多い順",
            "1回あたりの量が多い順",
            "最近買った順",
        };

        /// <summary>
        /// マーケット全体の購入者を集計するタブ。
        ///
        /// 出品者は隠されているが購入者は公開されているので、
        /// 「誰が買い占めているか」はここで確実に分かる。
        /// </summary>
        private void DrawMarketBuyersTab()
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "マーケットの購入履歴には買い手の名前が公開されています。自分の店で売れた分だけでなく、" +
                "他の人の店での購入も含めて集計するので、「特定の商品を大量に買っている人」を確実に把握できます。");

            ImGui.Spacing();

            ImGui.SetNextItemWidth(220);
            var search = _marketBuyerSearch;
            if (ImGui.InputTextWithHint("##mb_search", "購入者 / アイテム名で絞り込み", ref search, 64))
                _marketBuyerSearch = search;

            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "期間");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(110);
            var period = _marketBuyerPeriod;
            if (ImGui.Combo("##mb_period", ref period, MarketPeriodLabels, MarketPeriodLabels.Length))
            {
                _marketBuyerPeriod = period;
                _marketBuyersDirty = true;
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(190);
            var sort = _marketBuyerSort;
            if (ImGui.Combo("##mb_sort", ref sort, MarketSortLabels, MarketSortLabels.Length))
                _marketBuyerSort = sort;

            ImGui.SameLine();
            if (ImGui.Checkbox("自分の客のみ", ref _onlyMyCustomers))
                _marketBuyersDirty = true;
            AttachTooltip("自分のリテイナーから買ったことのある人だけに絞ります。");

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted,
                $"購入履歴 {Plugin.Purchases.Count:N0} 件（{Plugin.Purchases.ItemCount} 種）" +
                (Plugin.Purchases.LastObservedLocal == DateTime.MinValue
                    ? string.Empty
                    : $" / 最終取得 {Plugin.Purchases.LastObservedLocal:M/d HH:mm}"));
            AttachTooltip(
                "マーケットボードで購入履歴を開くと集まります。\n" +
                "設定で Universalis 連携を有効にすると、他ワールドの分も自動で集められます。");

            ImGui.Separator();

            EnsureMarketBuyers();

            if (_marketBuyers.Count == 0)
            {
                ImGui.TextColored(ColorAccent, "まだ購入履歴がありません。");
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "ゲーム内でマーケットボードのアイテムを開き、購入履歴のタブを見ると集まります。\n" +
                    "設定 → Universalis 連携を有効にすると、そちらからもまとめて取得できます。");
                return;
            }

            var listHeight = MathF.Max(140f, ImGui.GetContentRegionAvail().Y * 0.5f);
            if (ImGui.BeginChild("##mb_list", new System.Numerics.Vector2(0, listHeight), true))
                DrawMarketBuyerTable();
            ImGui.EndChild();

            ImGui.Spacing();
            if (ImGui.BeginChild("##mb_detail", new System.Numerics.Vector2(0, 0), true))
                DrawMarketBuyerDetail();
            ImGui.EndChild();
        }

        private void EnsureMarketBuyers()
        {
            if (!_marketBuyersDirty) return;
            _marketBuyersDirty = false;

            var days = MarketPeriodDays[Math.Clamp(_marketBuyerPeriod, 0, MarketPeriodDays.Length - 1)];
            var since = days > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)days * 86400L
                : 0;

            _marketBuyers = BuyerAnalytics.Build(
                Plugin.Purchases, Plugin.Store, Plugin.Retainers,
                Plugin.Config.SessionWindowSeconds, since);

            if (_onlyMyCustomers)
                _marketBuyers = _marketBuyers.Where(b => b.FromMeQuantity > 0).ToList();
        }

        private IEnumerable<MarketBuyerStat> SortedMarketBuyers()
        {
            var list = _marketBuyers.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(_marketBuyerSearch))
            {
                var q = _marketBuyerSearch.Trim();
                list = list.Where(b =>
                    b.BuyerName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    b.Items.Any(i => i.ItemName.Contains(q, StringComparison.OrdinalIgnoreCase)));
            }

            return _marketBuyerSort switch
            {
                1 => list.OrderByDescending(b => b.SessionCount),
                2 => list.OrderByDescending(b => b.TotalGil),
                3 => list.OrderByDescending(b => b.AveragePerSession),
                4 => list.OrderByDescending(b => b.LastUnix),
                _ => list.OrderByDescending(b => b.TotalQuantity),
            };
        }

        private void DrawMarketBuyerTable()
        {
            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##market_buyers", 8, flags)) return;

            ImGui.TableSetupColumn("購入者", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn("購入数", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("回数", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("1回平均", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("支払額", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("品目", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("自分から", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("最終購入", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var buyer in SortedMarketBuyers())
            {
                ImGui.TableNextRow();
                ImGui.PushID(buyer.BuyerName);

                ImGui.TableNextColumn();
                var selected = _selectedMarketBuyer == buyer.BuyerName;
                if (ImGui.Selectable("##row", selected, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedMarketBuyer = buyer.BuyerName;
                ImGui.SameLine(0, 0);
                ImGui.TextColored(ColorLink, buyer.BuyerName);
                if (ImGui.IsItemClicked()) LodestoneOpen(buyer.BuyerName);

                if (buyer.LinkedRetainers.Count > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ColorFavorite, "★");
                    AttachTooltip($"この人が持ち主と分かっている／推定されるリテイナー: {string.Join(", ", buyer.LinkedRetainers)}");
                }

                ImGui.TableNextColumn();
                ImGui.Text($"{buyer.TotalQuantity:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{buyer.SessionCount}");
                AttachTooltip($"取引 {buyer.TransactionCount} 件をまとめ買い単位で数えた回数です。");

                ImGui.TableNextColumn();
                ImGui.Text($"{buyer.AveragePerSession:N0}");
                AttachTooltip("1 回のまとめ買いあたりの平均個数。買い占めの規模感です。");

                ImGui.TableNextColumn();
                ImGui.Text($"{buyer.TotalGil:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{buyer.DistinctItems}");

                ImGui.TableNextColumn();
                if (buyer.FromMeQuantity > 0)
                    ImGui.TextColored(ColorFavorite, $"{buyer.FromMeQuantity:N0}");
                else
                    ImGui.TextColored(ColorMuted, "-");
                AttachTooltip("このうち、自分のリテイナーから買った数です。");

                ImGui.TableNextColumn();
                ImGui.Text(buyer.LastLocal.ToString("M/d HH:mm"));

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        private void DrawMarketBuyerDetail()
        {
            var buyer = _marketBuyers.FirstOrDefault(b => b.BuyerName == _selectedMarketBuyer);
            if (buyer == null)
            {
                ImGui.TextColored(ColorMuted, "購入者を選ぶと、買っているアイテムの内訳が表示されます。");
                return;
            }

            ImGui.TextColored(ColorAccent, buyer.BuyerName);
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted,
                $"— {buyer.TotalQuantity:N0}個 / {buyer.SessionCount}回 / {buyer.TotalGil:N0} ギル" +
                (string.IsNullOrEmpty(buyer.WorldName) ? string.Empty : $" / {buyer.WorldName}"));

            ImGui.SameLine();
            if (ImGui.SmallButton("Lodestone"))
                LodestoneOpen(buyer.BuyerName);

            if (buyer.LinkedRetainers.Count > 0)
            {
                ImGui.TextColored(ColorFavorite,
                    $"この人物のリテイナー（判明・推定）: {string.Join(", ", buyer.LinkedRetainers)}");
            }

            ImGui.Separator();

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##mb_items", 6, flags)) return;

            ImGui.TableSetupColumn("アイテム", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableSetupColumn("購入数", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("件数", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("支払額", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("自分から", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("最終購入", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var item in buyer.Items)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(item.ItemName + (item.Hq ? " (HQ)" : string.Empty));

                ImGui.TableNextColumn();
                ImGui.Text($"{item.TotalQuantity:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{item.TransactionCount}");

                ImGui.TableNextColumn();
                ImGui.Text($"{item.TotalGil:N0}");

                ImGui.TableNextColumn();
                if (item.FromMeQuantity > 0)
                    ImGui.TextColored(ColorFavorite, $"{item.FromMeQuantity:N0}");
                else
                    ImGui.TextColored(ColorMuted, "-");

                ImGui.TableNextColumn();
                ImGui.Text(item.LastLocal.ToString("M/d HH:mm"));
            }

            ImGui.EndTable();
        }
    }
}
