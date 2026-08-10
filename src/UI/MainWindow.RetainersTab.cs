using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketStats.Data;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private string _retainerSearch = string.Empty;
        private bool _retainerIdentifiedOnly;
        private ulong _selectedRetainerId;

        /// <summary>
        /// マーケットで見かけたリテイナーの台帳。持ち主が判明／推定できたものを一覧する。
        /// </summary>
        private void DrawRetainersTab()
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "マーケットで見かけたリテイナーの一覧です。リテイナー名から持ち主を直接引く手段はゲームに無いため、" +
                "「そのリテイナーが商品を出す直前に、同じ商品を買っていた人は誰か」を突き合わせて持ち主を推定します。");

            ImGui.Spacing();

            ImGui.SetNextItemWidth(220);
            var search = _retainerSearch;
            if (ImGui.InputTextWithHint("##retainer_search", "リテイナー名 / 持ち主で絞り込み", ref search, 64))
                _retainerSearch = search;

            ImGui.SameLine();
            ImGui.Checkbox("判明・推定できたものだけ", ref _retainerIdentifiedOnly);

            ImGui.SameLine();
            if (ImGui.Button("いま推定し直す"))
            {
                var updated = RetainerOwnerGuesser.Update(
                    Plugin.Listings, Plugin.Purchases, Plugin.Store,
                    Plugin.Retainers, Plugin.Config.ResaleWindowHours);
                Plugin.Retainers.Save(force: true);
                Plugin.ChatGui.Print($"[Market Stats] リテイナー {updated} 件の持ち主を推定しました。");
            }
            AttachTooltip("蓄積済みの出品と購入履歴をもとに、その場で推定をやり直します。");

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted,
                $"リテイナー {Plugin.Retainers.Count:N0} 件 / 持ち主が分かったもの {Plugin.Retainers.IdentifiedCount:N0} 件 / " +
                $"購入履歴 {Plugin.Purchases.Count:N0} 件（{Plugin.Purchases.ItemCount} 種）");
            ImGui.Separator();

            var profiles = Plugin.Retainers.Snapshot();

            if (_retainerIdentifiedOnly)
                profiles = profiles.Where(p => p.HasOwner || !string.IsNullOrEmpty(p.GuessedOwnerName)).ToList();

            if (!string.IsNullOrWhiteSpace(_retainerSearch))
            {
                var q = _retainerSearch.Trim();
                profiles = profiles.Where(p =>
                    p.RetainerName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    (p.OwnerName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (p.GuessedOwnerName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            }

            if (profiles.Count == 0)
            {
                ImGui.TextColored(ColorMuted,
                    "まだ記録がありません。マーケットボードでアイテムの出品一覧と購入履歴を開くと集まります。");
                return;
            }

            profiles = profiles
                .OrderByDescending(p => p.IsMine)
                .ThenByDescending(p => p.HasOwner)
                .ThenByDescending(p => p.GuessScore)
                .ThenByDescending(p => p.LastSeenUnix)
                .ToList();

            var listHeight = MathF.Max(140f, ImGui.GetContentRegionAvail().Y * 0.55f);
            if (ImGui.BeginChild("##retainer_list", new System.Numerics.Vector2(0, listHeight), true))
                DrawRetainerTable(profiles);
            ImGui.EndChild();

            ImGui.Spacing();
            DrawRetainerDetail();
        }

        private void DrawRetainerTable(List<RetainerProfile> profiles)
        {
            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##retainers", 6, flags)) return;

            ImGui.TableSetupColumn("リテイナー", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("持ち主", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupColumn("確度", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("出品", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("品目", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableSetupColumn("最終確認", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var profile in profiles)
            {
                ImGui.TableNextRow();
                ImGui.PushID(profile.RetainerId.ToString());

                ImGui.TableNextColumn();
                var selected = _selectedRetainerId == profile.RetainerId;
                if (ImGui.Selectable($"##row", selected, ImGuiSelectableFlags.SpanAllColumns))
                    _selectedRetainerId = profile.RetainerId;
                ImGui.SameLine(0, 0);
                ImGui.Text(string.IsNullOrEmpty(profile.RetainerName) ? "(名前不明)" : profile.RetainerName);

                ImGui.TableNextColumn();
                if (profile.IsMine)
                    ImGui.TextColored(ColorFavorite, $"{profile.OwnerName}（自分）");
                else if (profile.HasOwner)
                {
                    ImGui.TextColored(ColorLink, profile.OwnerName!);
                    if (ImGui.IsItemClicked()) LodestoneOpen(profile.OwnerName!);
                    AttachTooltip("クリックで Lodestone のキャラクター検索を開きます。");
                }
                else if (!string.IsNullOrEmpty(profile.GuessedOwnerName))
                    ImGui.TextColored(ColorAccent, $"{profile.GuessedOwnerName}（推定）");
                else
                    ImGui.TextColored(ColorMuted, "不明");

                ImGui.TableNextColumn();
                if (profile.IsMine || profile.HasOwner)
                    ImGui.TextColored(ColorMuted, "確定");
                else if (profile.GuessScore > 0)
                {
                    var text = profile.GuessScore switch
                    {
                        >= 200 => "高",
                        >= 120 => "中",
                        _ => "低",
                    };
                    ImGui.TextColored(profile.GuessScore >= 200 ? ColorFavorite : ColorMuted, text);
                    AttachTooltip($"スコア {profile.GuessScore}");
                }
                else
                    ImGui.TextColored(ColorMuted, "-");

                ImGui.TableNextColumn();
                ImGui.Text($"{profile.ObservedListings}");

                ImGui.TableNextColumn();
                ImGui.Text($"{profile.ObservedItems.Count}");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, profile.LastSeenLocal.ToString("M/d HH:mm"));

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        private void DrawRetainerDetail()
        {
            if (_selectedRetainerId == 0)
            {
                ImGui.TextColored(ColorMuted, "リテイナーを選ぶと、推定の根拠と取り扱っている商品が表示されます。");
                return;
            }

            var profile = Plugin.Retainers.Resolve(_selectedRetainerId);
            if (profile == null)
            {
                ImGui.TextColored(ColorMuted, "選択したリテイナーの情報が見つかりませんでした。");
                return;
            }

            ImGui.TextColored(ColorAccent, profile.RetainerName);
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, $"— 持ち主: {profile.DisplayOwner}");

            if (profile.OwnerContentId != 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("名刺で確認"))
                    Plugin.CharaCard.Request(profile.OwnerContentId);
                AttachTooltip("この出品者の冒険者名刺を開いて、名前を確定させます。");
            }

            if (profile.GuessReasons.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorMuted, "推定の根拠:");
                foreach (var reason in profile.GuessReasons)
                    ImGui.BulletText(reason);
            }

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted, "取り扱っている商品:");

            var listings = Plugin.Listings.Snapshot()
                .Where(l => l.RetainerId == profile.RetainerId)
                .OrderByDescending(l => l.EffectiveListedUnix)
                .Take(50)
                .ToList();

            if (listings.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "記録がありません。");
                return;
            }

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##retainer_items", 4, flags)) return;

            ImGui.TableSetupColumn("アイテム", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("個数", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("出品時刻", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableHeadersRow();

            foreach (var listing in listings)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text(Plugin.Items.GetName(listing.ItemId) + (listing.Hq ? " (HQ)" : string.Empty));

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.Quantity:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.UnitPrice:N0}");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted,
                    DateTimeOffset.FromUnixTimeSeconds(listing.EffectiveListedUnix).LocalDateTime.ToString("M/d HH:mm"));
            }

            ImGui.EndTable();
        }
    }
}
