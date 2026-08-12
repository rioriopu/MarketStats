using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketStats.Data;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private string _ownSearch = string.Empty;
        private bool _ownOnlyCurrentCharacter;

        /// <summary>
        /// 自分のリテイナーが今なにを出品しているかを、キャラクターごとに一覧する。
        ///
        /// ログインしていないキャラクターの分はゲームから読めないため、
        /// 一度確認した内容を記録して表示する。
        /// </summary>
        private void DrawOwnListingsTab()
        {
            ImGui.Spacing();

            var characters = Plugin.OwnListings.ByCharacter();

            if (characters.Count == 0)
            {
                ImGui.TextColored(ColorAccent, "まだリテイナーの情報がありません。");
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "リテイナーに話しかけると、そのキャラクターのリテイナー一覧が記録されます。\n" +
                    "さらに「マーケットに出品する」を開くと、出品している品の明細まで分かります。\n" +
                    "複数のキャラクターを使っている場合は、それぞれで一度開いてください。");
                return;
            }

            ImGui.SetNextItemWidth(240);
            var search = _ownSearch;
            if (ImGui.InputTextWithHint("##own_search", "アイテム / リテイナー名で絞り込み", ref search, 64))
                _ownSearch = search;

            ImGui.SameLine();
            ImGui.Checkbox("ログイン中のキャラのみ", ref _ownOnlyCurrentCharacter);

            var currentId = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0;
            if (_ownOnlyCurrentCharacter && currentId != 0)
                characters = characters.Where(c => c.ContentId == currentId).ToList();

            // 全体の合計
            var totalValue = characters.Sum(c => c.TotalValue);
            var totalListings = characters.Sum(c => c.TotalListings);
            var totalGil = characters.Sum(c => c.TotalGil);

            ImGui.Spacing();
            ImGui.TextColored(ColorAccent, $"キャラクター {characters.Count} 人");
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "/");
            ImGui.SameLine();
            ImGui.Text($"出品 {totalListings:N0} 件");
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "/");
            ImGui.SameLine();
            ImGui.Text($"出品総額 {totalValue:N0} ギル");
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, "/");
            ImGui.SameLine();
            ImGui.Text($"預かりギル {totalGil:N0}");

            ImGui.Separator();

            foreach (var character in characters)
                DrawCharacter(character, currentId);
        }

        private void DrawCharacter(OwnCharacter character, ulong currentId)
        {
            var isCurrent = character.ContentId == currentId && currentId != 0;

            var header =
                $"{character.Display}   出品 {character.TotalListings} 件 / " +
                $"{character.TotalValue:N0} ギル / リテイナー {character.Retainers.Count} 体" +
                (isCurrent ? "  ★ログイン中" : string.Empty) +
                $"###char_{character.ContentId}";

            if (!ImGui.CollapsingHeader(header, isCurrent ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                return;

            ImGui.Indent(12);

            foreach (var retainer in character.Retainers)
                DrawRetainer(retainer);

            ImGui.Unindent(12);
            ImGui.Spacing();
        }

        private void DrawRetainer(OwnRetainer retainer)
        {
            var listings = FilterListings(retainer);

            // 絞り込み中に該当しないリテイナーは畳んでおく。
            if (!string.IsNullOrWhiteSpace(_ownSearch) && listings.Count == 0 &&
                !retainer.Name.Contains(_ownSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                return;

            var expire = retainer.MarketExpireIn;
            var expireText = retainer.MarketExpireUnix == 0
                ? string.Empty
                : expire.TotalSeconds <= 0
                    ? " / 出品期限切れ"
                    : $" / 残り {FormatRemaining(expire)}";

            var header =
                $"{retainer.Name}   出品 {retainer.Listings.Count} 件 / " +
                $"{retainer.TotalValue:N0} ギル / 預かり {retainer.Gil:N0} ギル{expireText}" +
                $"###ret_{retainer.RetainerId}";

            if (!ImGui.CollapsingHeader(header)) return;

            ImGui.Indent(12);

            if (retainer.ListingsUpdatedUnix == 0)
            {
                ImGui.TextColored(ColorMuted,
                    "出品の明細はまだ読み取っていません。" +
                    "このリテイナーの「マーケットに出品する」を開くと記録されます。");
                ImGui.Unindent(12);
                return;
            }

            ImGui.TextColored(ColorMuted, $"最終確認: {retainer.ListingsUpdatedLocal:M/d HH:mm}");

            if (retainer.ListingsLookStale)
            {
                ImGui.SameLine();
                ImGui.TextColored(ColorAccent,
                    $"（一覧では {retainer.MarketItemCount} 件。内容が変わっている可能性があります）");
            }

            if (expire.TotalSeconds > 0 && expire.TotalDays < 1)
            {
                ImGui.TextColored(ColorAccent,
                    "出品期限が近づいています。リテイナーに話しかけると延長されます。");
            }

            if (listings.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "出品はありません。");
                ImGui.Unindent(12);
                return;
            }

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (ImGui.BeginTable($"##own_{retainer.RetainerId}", 6, flags))
            {
                ImGui.TableSetupColumn("アイテム", ImGuiTableColumnFlags.WidthStretch, 1.8f);
                ImGui.TableSetupColumn("個数", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("合計", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("相場", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("売れ行き", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableHeadersRow();

                foreach (var listing in listings.OrderByDescending(l => l.Total))
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Text(Plugin.Items.GetName(listing.ItemId) + (listing.Hq ? " (HQ)" : string.Empty));

                    ImGui.TableNextColumn();
                    ImGui.Text($"{listing.Quantity:N0}");

                    ImGui.TableNextColumn();
                    ImGui.Text($"{listing.UnitPrice:N0}");

                    ImGui.TableNextColumn();
                    ImGui.Text($"{listing.Total:N0}");

                    // 観測済みの他人の出品と比べて、自分が高いか安いかを出す。
                    ImGui.TableNextColumn();
                    DrawMarketComparison(listing);

                    // このアイテムが自分の店でどれくらい売れているか。
                    ImGui.TableNextColumn();
                    DrawSalesPace(listing.ItemId, listing.Hq);
                }

                ImGui.EndTable();
            }

            ImGui.Unindent(12);
            ImGui.Spacing();
        }

        /// <summary>観測済みの出品と比べて、自分の値付けの位置を示す。</summary>
        private static void DrawMarketComparison(OwnListing listing)
        {
            var others = Plugin.Listings.Snapshot()
                .Where(l => l.ItemId == listing.ItemId && l.Hq == listing.Hq && l.UnitPrice > 0)
                .Where(l => !Plugin.OwnListings.IsMine(l.RetainerId))
                .ToList();

            if (others.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "-");
                AttachTooltip("このアイテムの他の出品をまだ観測していません。");
                return;
            }

            var lowest = others.Min(l => l.UnitPrice);

            if (listing.UnitPrice <= lowest)
            {
                ImGui.TextColored(ColorFavorite, "最安");
                AttachTooltip($"観測した中で最も安い出品です（他の最安 {lowest:N0}）。");
                return;
            }

            var difference = listing.UnitPrice - lowest;
            ImGui.TextColored(ColorAccent, $"+{difference:N0}");
            AttachTooltip(
                $"他の最安は {lowest:N0} です（観測 {others.Count} 件）。\n" +
                $"あなたの出品はそれより {difference:N0} 高い値付けです。");
        }

        /// <summary>そのアイテムが自分の店でどれだけ売れているか。</summary>
        private void DrawSalesPace(uint itemId, bool hq)
        {
            var sales = Plugin.Store.Snapshot()
                .Where(r => r.ItemId == itemId && r.Hq == hq)
                .ToList();

            if (sales.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "-");
                return;
            }

            var quantity = sales.Sum(r => (long)r.Quantity);
            var latest = sales.Max(r => r.UnixTime);

            ImGui.Text($"{quantity:N0}個 / {sales.Count}件");
            AttachTooltip(
                $"記録している範囲で、このアイテムは {sales.Count} 件・{quantity:N0}個 売れています。\n" +
                $"直近の売却: {DateTimeOffset.FromUnixTimeSeconds(latest).LocalDateTime:M/d HH:mm}");
        }

        private List<OwnListing> FilterListings(OwnRetainer retainer)
        {
            if (string.IsNullOrWhiteSpace(_ownSearch)) return retainer.Listings;

            var q = _ownSearch.Trim();

            // リテイナー名で一致した場合は、そのリテイナーの出品をすべて出す。
            if (retainer.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return retainer.Listings;

            return retainer.Listings
                .Where(l => Plugin.Items.GetName(l.ItemId).Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static string FormatRemaining(TimeSpan span) =>
            span.TotalDays >= 1 ? $"{(int)span.TotalDays}日"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}時間"
            : $"{(int)span.TotalMinutes}分";
    }
}
