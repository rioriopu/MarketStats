using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketStats.Data;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private const int HistoryDisplayLimit = 1000;

        private void DrawHistoryTab()
        {
            ImGui.Spacing();
            DrawFilterRow();

            ImGui.SameLine();
            if (ImGui.Button("CSV をコピー"))
            {
                ImGui.SetClipboardText(Plugin.Store.ExportCsv());
                Plugin.ChatGui.Print("[Market Stats] 売却ログを CSV 形式でクリップボードにコピーしました。");
            }
            AttachTooltip("保存されている全ログ（絞り込み前）を CSV 形式でクリップボードへコピーします。");

            ImGui.Spacing();

            var records = _filtered.OrderByDescending(r => r.UnixTime).ToList();
            if (records.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "表示できる取引がありません。");
                return;
            }

            if (records.Count > HistoryDisplayLimit)
            {
                ImGui.TextColored(ColorMuted,
                    $"{records.Count:N0} 件中、新しい {HistoryDisplayLimit:N0} 件を表示しています。");
                records = records.Take(HistoryDisplayLimit).ToList();
            }

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##history", 8, flags)) return;

            ImGui.TableSetupColumn("購入日時", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableSetupColumn("購入者", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("アイテム", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableSetupColumn("数量", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("合計", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("リテイナー", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("累計", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var rec in records)
            {
                ImGui.TableNextRow();
                var buyerName = rec.HasBuyer ? rec.BuyerName : SaleAggregator.UnknownBuyer;

                ImGui.TableNextColumn();
                ImGui.Text(rec.LocalTime.ToString("M/d HH:mm"));

                ImGui.TableNextColumn();
                if (rec.HasBuyer)
                {
                    ImGui.TextColored(ColorLink, buyerName);
                    if (ImGui.IsItemClicked()) LodestoneOpen(buyerName);
                    AttachTooltip("クリックで Lodestone のキャラクター検索を開きます。");
                }
                else
                {
                    ImGui.TextColored(ColorMuted, buyerName);
                }

                ImGui.TableNextColumn();
                ImGui.Text(Plugin.Items.GetName(rec.ItemId) + (rec.Hq ? " (HQ)" : string.Empty));

                ImGui.TableNextColumn();
                ImGui.Text($"{rec.Quantity:N0}");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, $"{rec.UnitPrice:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{rec.TotalGil:N0}");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, rec.RetainerName);
                if (!string.IsNullOrEmpty(rec.OwnerName))
                    AttachTooltip($"所有キャラ: {rec.OwnerName}" +
                                  (string.IsNullOrEmpty(rec.OwnerWorld) ? string.Empty : $" @ {rec.OwnerWorld}"));

                // 同一購入者 × 同一アイテムの累計（この一覧内での参考値）
                ImGui.TableNextColumn();
                var cumulative = CumulativeFor(buyerName, rec.ItemId, rec.Hq);
                ImGui.TextColored(ColorAccent, $"{cumulative:N0} 個");
                AttachTooltip($"{buyerName} がこのアイテムを購入した累計数（現在の絞り込み範囲内）です。");
            }

            ImGui.EndTable();
        }

        /// <summary>購入者 × アイテムの累計購入数を集計結果から引く。</summary>
        private long CumulativeFor(string buyerName, uint itemId, bool hq) =>
            _cumulative.TryGetValue((buyerName, itemId, hq), out var total) ? total : 0;
    }
}
