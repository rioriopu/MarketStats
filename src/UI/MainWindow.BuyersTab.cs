using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketStats.Data;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private static readonly string[] SortLabels =
        {
            "最終購入が新しい順",
            "累計購入数が多い順",
            "購入回数が多い順",
            "累計金額が多い順",
            "購入者名順",
        };

        private int _sortIndex;

        private void DrawBuyersTab()
        {
            ImGui.Spacing();
            DrawFilterRow();

            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "並び順");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            var sort = _sortIndex;
            if (ImGui.Combo("##sort", ref sort, SortLabels, SortLabels.Length))
                _sortIndex = sort;

            ImGui.Spacing();

            if (_stats.Count == 0)
            {
                DrawEmptyState();
                return;
            }

            var listHeight = MathF.Max(140f, ImGui.GetContentRegionAvail().Y * 0.45f);
            if (ImGui.BeginChild("##buyerlist", new Vector2(0, listHeight), true))
                DrawBuyerTable();
            ImGui.EndChild();

            ImGui.Spacing();

            if (ImGui.BeginChild("##buyerdetail", new Vector2(0, 0), true))
                DrawBuyerDetail();
            ImGui.EndChild();
        }

        private void DrawEmptyState()
        {
            ImGui.Spacing();
            ImGui.TextColored(ColorAccent, "まだ売却ログがありません。");
            ImGui.Spacing();
            ImGui.TextWrapped(
                "リテイナーに話しかけて「マーケットに出品する」→「売却履歴」を開くと、そのリテイナーの履歴が取り込まれます。\n" +
                "ゲーム側の履歴はリテイナーごとに最新 20 件しか残らないため、こまめに開くほど取りこぼしが減ります。");
            ImGui.Spacing();

            if (!string.IsNullOrWhiteSpace(_search) || _periodIndex != 0 || _favoritesOnly)
            {
                ImGui.TextColored(ColorMuted, "※ 絞り込みが有効です。条件を外すと表示されるログがあるかもしれません。");
                if (ImGui.Button("絞り込みを解除"))
                {
                    _search = string.Empty;
                    _periodIndex = 0;
                    _favoritesOnly = false;
                    _statsDirty = true;
                }
            }
        }

        private IEnumerable<BuyerStat> SortedStats() => _sortIndex switch
        {
            1 => _stats.OrderByDescending(s => s.TotalQuantity),
            2 => _stats.OrderByDescending(s => s.SessionCount).ThenByDescending(s => s.TransactionCount),
            3 => _stats.OrderByDescending(s => s.TotalGil),
            4 => _stats.OrderBy(s => s.BuyerName, StringComparer.OrdinalIgnoreCase),
            _ => _stats.OrderByDescending(s => s.LastUnix),
        };

        private void DrawBuyerTable()
        {
            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##buyers", 8, flags)) return;

            ImGui.TableSetupColumn("★", ImGuiTableColumnFlags.WidthFixed, 26);
            ImGui.TableSetupColumn("購入者", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("購入回数", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("枠数", ImGuiTableColumnFlags.WidthFixed, 54);
            ImGui.TableSetupColumn("累計購入数", ImGuiTableColumnFlags.WidthFixed, 92);
            ImGui.TableSetupColumn("累計金額", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("品目", ImGuiTableColumnFlags.WidthFixed, 48);
            ImGui.TableSetupColumn("最終購入", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var buyer in SortedStats())
            {
                ImGui.TableNextRow();
                ImGui.PushID(buyer.BuyerName);

                // ★ お気に入り
                ImGui.TableNextColumn();
                var star = buyer.IsFavorite ? "★" : "☆";
                ImGui.PushStyleColor(ImGuiCol.Text, buyer.IsFavorite ? ColorFavorite : ColorMuted);
                if (ImGui.Selectable(star, false, ImGuiSelectableFlags.None, new Vector2(18, 0))
                    && !buyer.IsMannequin)
                {
                    Plugin.Favorites.Toggle(buyer.BuyerName);
                    _statsDirty = true;
                }
                ImGui.PopStyleColor();
                if (!buyer.IsMannequin)
                    AttachTooltip(buyer.IsFavorite
                        ? "お気に入りを解除します。解除すると通常の保持期間が適用されます。"
                        : "お気に入りに登録します。登録した購入者のログは長めに保持されます。");

                // 購入者名（行選択）
                ImGui.TableNextColumn();
                var selected = _selectedBuyer == buyer.BuyerName;
                if (ImGui.Selectable($"##row_{buyer.BuyerName}", selected,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                    _selectedBuyer = buyer.BuyerName;

                ImGui.SameLine(0, 0);
                DrawBuyerLink(buyer);

                ImGui.TableNextColumn();
                ImGui.Text($"{buyer.SessionCount:N0} 回");
                AttachTooltip("まとめ買い（近い時刻の連続購入）を 1 回として数えた回数です。");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, $"{buyer.TransactionCount:N0}");
                AttachTooltip("実際に売れた出品枠の数です。");

                ImGui.TableNextColumn();
                ImGui.Text($"{buyer.TotalQuantity:N0} 個");

                ImGui.TableNextColumn();
                ImGui.Text($"{buyer.TotalGil:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{buyer.DistinctItemCount}");

                ImGui.TableNextColumn();
                ImGui.Text(FormatDateTime(buyer.LastUnix));
                ImGui.SameLine();
                ImGui.TextColored(ColorMuted, $"({RelativeTime(buyer.LastUnix)})");

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        private void DrawBuyerDetail()
        {
            var buyer = _stats.FirstOrDefault(s => s.BuyerName == _selectedBuyer);
            if (buyer == null)
            {
                ImGui.TextColored(ColorMuted, "上の一覧から購入者を選ぶと、アイテムごとの内訳が表示されます。");
                return;
            }

            ImGui.TextColored(ColorAccent, buyer.BuyerName);
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted,
                $"— 累計 {buyer.TotalQuantity:N0}個 / {buyer.SessionCount}回 / {buyer.TotalGil:N0} ギル");

            if (!buyer.IsMannequin)
            {
                ImGui.SameLine();
                var avail = ImGui.GetContentRegionAvail().X;
                if (avail > 260) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - 250);

                if (ImGui.SmallButton("Lodestone で検索"))
                    LodestoneOpen(buyer.BuyerName);
                AttachTooltip(Game.LodestoneLink.BuildSearchUrl(buyer.BuyerName));

                ImGui.SameLine();
                if (ImGui.SmallButton(buyer.IsFavorite ? "お気に入り解除" : "お気に入り登録"))
                {
                    Plugin.Favorites.Toggle(buyer.BuyerName);
                    _statsDirty = true;
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("名前をコピー"))
                    ImGui.SetClipboardText(buyer.BuyerName);
            }

            var fav = Plugin.Favorites.Get(buyer.BuyerName);
            if (fav != null)
            {
                ImGui.SetNextItemWidth(320);
                var note = fav.Note;
                if (ImGui.InputTextWithHint("##fav_note", "メモ（お気に入り登録者のみ）", ref note, 200))
                    Plugin.Favorites.SetNote(buyer.BuyerName, note);
            }

            ImGui.Separator();

            foreach (var item in buyer.Items)
                DrawBuyerItem(buyer, item);
        }

        private void DrawBuyerItem(BuyerStat buyer, BuyerItemStat item)
        {
            var hq = item.Hq ? " (HQ)" : string.Empty;
            var header =
                $"{item.ItemName}{hq}   累計 {item.TotalQuantity:N0}個 / {item.SessionCount}回 " +
                $"(枠 {item.TransactionCount}) / 平均 {item.AvgUnitPrice:N0} ギル" +
                $"###item_{buyer.BuyerName}_{item.ItemId}_{(item.Hq ? 1 : 0)}";

            if (!ImGui.CollapsingHeader(header)) return;

            ImGui.Indent(12);

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable($"##sessions_{item.ItemId}_{(item.Hq ? 1 : 0)}", 5, flags))
            {
                ImGui.TableSetupColumn("購入日時", ImGuiTableColumnFlags.WidthFixed, 130);
                ImGui.TableSetupColumn("内訳", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("購入数", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("金額", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableHeadersRow();

                foreach (var session in item.Sessions)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Text(session.StartLocal.ToString("M/d HH:mm"));

                    ImGui.TableNextColumn();
                    ImGui.Text(session.QuantityText);
                    if (session.Slots > 1)
                        AttachTooltip(
                            $"{session.StartLocal:M/d HH:mm} ～ " +
                            $"{DateTimeOffset.FromUnixTimeSeconds(session.EndUnix).LocalDateTime:HH:mm} の " +
                            $"{session.Slots} 件をまとめて表示しています。\n" +
                            "まとめる時間幅は設定タブで変更できます。");

                    ImGui.TableNextColumn();
                    ImGui.Text($"{session.TotalQuantity:N0} 個");

                    ImGui.TableNextColumn();
                    var unit = session.TotalQuantity == 0
                        ? 0
                        : (double)session.TotalGil / session.TotalQuantity;
                    ImGui.TextColored(ColorMuted, $"{unit:N0}");

                    ImGui.TableNextColumn();
                    ImGui.Text($"{session.TotalGil:N0}");
                }

                ImGui.EndTable();
            }

            if (Plugin.Config.EnableUniversalis)
            {
                if (ImGui.SmallButton($"このアイテムの相場を見る##market_{item.ItemId}"))
                    OpenMarketFor(item.ItemId, buyer.BuyerName);
                AttachTooltip("Universalis から、このアイテムの現在の出品状況を取得します。");
            }

            ImGui.Unindent(12);
            ImGui.Spacing();
        }
    }
}
