using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using MarketStats.Game;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private uint _marketItemId;
        private string _marketBuyer = string.Empty;
        private Task<MarketSnapshot>? _marketTask;
        private MarketSnapshot? _marketSnapshot;

        /// <summary>購入者タブから「相場を見る」で呼ばれる。</summary>
        private void OpenMarketFor(uint itemId, string buyerName)
        {
            _marketItemId = itemId;
            _marketBuyer = buyerName;
            _requestedTab = Tab.Market;
            StartMarketFetch(force: false);
        }

        private void StartMarketFetch(bool force)
        {
            if (_marketItemId == 0 || !Plugin.Config.EnableUniversalis) return;
            if (_marketTask is { IsCompleted: false }) return;

            _marketTask = Plugin.Universalis.FetchAsync(_marketItemId, force);
        }

        private void DrawMarketTab()
        {
            ImGui.Spacing();

            if (!Plugin.Config.EnableUniversalis)
            {
                ImGui.TextColored(ColorAccent, "Universalis 連携は無効です。");
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "設定タブで有効にすると、売れたアイテムが「今どのワールドでいくらで出品されているか」を " +
                    "Universalis（有志が運営するマーケット情報サイト）から取得して表示できます。\n" +
                    "有効にするとプラグインから外部サイトへの通信が発生します。");
                return;
            }

            DrawMarketItemPicker();

            if (_marketTask is { IsCompletedSuccessfully: true })
            {
                _marketSnapshot = _marketTask.Result;
                _marketTask = null;

                // 手動で取得した分も、購入者の分析材料として取り込む。
                if (_marketSnapshot.Error == null)
                {
                    var purchases = UniversalisClient.ToPurchases(_marketSnapshot);
                    if (purchases.Count > 0)
                    {
                        Plugin.Purchases.Add(purchases);
                        Plugin.Purchases.Save();
                    }

                    var records = UniversalisClient.ToListingRecords(_marketSnapshot);
                    if (records.Count > 0)
                    {
                        Plugin.Listings.Observe(records);
                        foreach (var record in records) Plugin.Retainers.Observe(record);
                    }
                }
            }

            if (_marketTask is { IsFaulted: true })
            {
                ImGui.TextColored(ColorAccent, "取得に失敗しました。");
                _marketTask = null;
            }

            if (_marketTask != null)
            {
                ImGui.TextColored(ColorMuted, "取得中…");
                return;
            }

            if (_marketSnapshot == null)
            {
                ImGui.TextColored(ColorMuted, "アイテムを選んで「取得」を押してください。");
                return;
            }

            DrawMarketSnapshot(_marketSnapshot);
        }

        private void DrawMarketItemPicker()
        {
            // ログに出てくるアイテムだけを選択肢にする。
            var items = _filtered
                .Select(r => r.ItemId)
                .Distinct()
                .OrderBy(id => Plugin.Items.GetName(id), StringComparer.CurrentCulture)
                .ToList();

            var current = _marketItemId == 0 ? "（アイテムを選択）" : Plugin.Items.GetName(_marketItemId);

            ImGui.SetNextItemWidth(300);
            if (ImGui.BeginCombo("##marketitem", current))
            {
                foreach (var id in items)
                {
                    if (!ImGui.Selectable(Plugin.Items.GetName(id), id == _marketItemId)) continue;
                    _marketItemId = id;
                    _marketSnapshot = null;
                    StartMarketFetch(force: false);
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.Button("取得")) StartMarketFetch(force: true);

            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, $"照会範囲: {Plugin.Universalis.ResolveScope()}");

            if (!string.IsNullOrEmpty(_marketBuyer))
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorAccent, $"／ 注目: {_marketBuyer}");
            }

            ImGui.Spacing();
            ImGui.TextWrapped(
                "※ マーケットの出品情報に含まれるのはリテイナー名だけで、キャラクター名との対応は公開されていません。" +
                "そのため「この購入者が再出品しているか」を確定することはできません。以下は参考情報です。");
            ImGui.Separator();
        }

        private void DrawMarketSnapshot(MarketSnapshot snapshot)
        {
            if (snapshot.Error != null)
            {
                ImGui.TextColored(ColorAccent, $"取得に失敗しました: {snapshot.Error}");
                return;
            }

            ImGui.TextColored(ColorMuted,
                $"取得時刻 {snapshot.FetchedLocal:HH:mm:ss} / 出品 {snapshot.Listings.Count} 件 / 履歴 {snapshot.History.Count} 件");
            ImGui.Spacing();

            DrawListings(snapshot);
            ImGui.Spacing();
            DrawBuyerHistory(snapshot);
        }

        private void DrawListings(MarketSnapshot snapshot)
        {
            if (!ImGui.CollapsingHeader($"現在の出品 ({snapshot.Listings.Count} 件)###listings",
                    ImGuiTreeNodeFlags.DefaultOpen))
                return;

            if (snapshot.Listings.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "出品されていません。");
                return;
            }

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##listings_table", 6, flags, new System.Numerics.Vector2(0, 220)))
                return;

            ImGui.TableSetupColumn("ワールド", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("個数", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("合計", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("リテイナー", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var listing in snapshot.Listings)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(listing.WorldName);

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.PricePerUnit:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.Quantity:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.Total:N0}");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, listing.RetainerName);
                if (listing.OnMannequin) AttachTooltip("マネキン展示です。");

                ImGui.TableNextColumn();
                ImGui.Text(listing.Hq ? "HQ" : string.Empty);
            }

            ImGui.EndTable();
        }

        private void DrawBuyerHistory(MarketSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(_marketBuyer)) return;

            var matches = UniversalisClient.FilterByBuyer(snapshot, _marketBuyer);

            if (!ImGui.CollapsingHeader(
                    $"{_marketBuyer} の購入履歴（Universalis 収集分・{matches.Count} 件）###buyerhistory",
                    ImGuiTreeNodeFlags.DefaultOpen))
                return;

            if (matches.Count == 0)
            {
                ImGui.TextColored(ColorMuted,
                    "この照会範囲では、同名の購入者による購入履歴は見つかりませんでした。\n" +
                    "Universalis の履歴は誰かがそのワールドのデータをアップロードした分のみです。");
                return;
            }

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##buyerhistory_table", 5, flags)) return;

            ImGui.TableSetupColumn("購入日時", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("ワールド", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("個数", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableHeadersRow();

            foreach (var entry in matches)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(entry.LocalTime.ToString("M/d HH:mm"));

                ImGui.TableNextColumn();
                ImGui.Text(entry.WorldName);

                ImGui.TableNextColumn();
                ImGui.Text($"{entry.Quantity:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{entry.PricePerUnit:N0}");

                ImGui.TableNextColumn();
                ImGui.Text(entry.Hq ? "HQ" : string.Empty);
            }

            ImGui.EndTable();
        }
    }
}
