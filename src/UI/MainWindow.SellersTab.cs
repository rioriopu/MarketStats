using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketStats.Data;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private bool _sellersGroupByOwner = true;

        // 出品一覧はゲームのメモリから読み直すため、毎フレームではなく一定間隔で更新する。
        private List<ListingRecord> _sellerListings = new();
        private DateTime _nextSellerRefreshUtc = DateTime.MinValue;

        /// <summary>
        /// マーケットボードで開いているアイテムの出品一覧に、出品者（オーナー）を並べて表示するタブ。
        /// 名前が分かるのは対応表に載っている場合だけで、それ以外は ContentId ベースの識別になる。
        /// </summary>
        private void DrawSellersTab()
        {
            ImGui.Spacing();

            if (!Plugin.Config.EnableResaleTracking)
            {
                ImGui.TextColored(ColorAccent, "出品の記録が無効になっています。");
                ImGui.TextWrapped("設定タブの「再出品の追跡」を有効にすると、マーケットボードで見た出品を記録します。");
                return;
            }

            if (DateTime.UtcNow >= _nextSellerRefreshUtc)
            {
                _nextSellerRefreshUtc = DateTime.UtcNow.AddMilliseconds(500);
                _sellerListings = Plugin.MarketWatcher.CurrentListings();
            }

            var listings = _sellerListings;

            ImGui.TextWrapped(
                "ゲーム内でマーケットボードのアイテムを開くと、その出品一覧がここに表示されます。" +
                "マーケットの出品情報に含まれるのはリテイナー名までですが、" +
                "内部的にはオーナーごとの識別子が付いているため、同じ人物の出品はまとめて表示できます。");

            if (!string.IsNullOrEmpty(Plugin.CharaCard.LastResult))
            {
                ImGui.Spacing();
                ImGui.TextColored(ColorAccent, $"名刺照会: {Plugin.CharaCard.LastResult}");
            }

            ImGui.Spacing();
            ImGui.Checkbox("出品者ごとにまとめる", ref _sellersGroupByOwner);
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted,
                $"対応表: {Plugin.Identities.ConfirmedCount:N0} 人（推定含め {Plugin.Identities.Count:N0} 件）");
            AttachTooltip(
                "周囲に見えたプレイヤーやフレンド / FC / リンクシェルのメンバーから集めた、" +
                "識別子とキャラクター名の対応表です。ここに載っている人なら出品者名を表示できます。");

            ImGui.Separator();

            if (listings.Count == 0)
            {
                ImGui.TextColored(ColorMuted,
                    "表示できる出品がありません。ゲーム内でマーケットボードのアイテムを開いてください。");
                return;
            }

            var itemName = Plugin.Items.GetName(listings[0].ItemId);
            ImGui.TextColored(ColorAccent, itemName);
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, $"— 出品 {listings.Count} 件");

            ImGui.Spacing();

            if (_sellersGroupByOwner)
                DrawSellersGrouped(listings);
            else
                DrawSellersFlat(listings);
        }

        private void DrawSellersGrouped(List<ListingRecord> listings)
        {
            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##sellers_grouped", 6, flags)) return;

            ImGui.TableSetupColumn("出品者", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("リテイナー", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("枠数", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("総数", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("最安単価", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("あなたの取引", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            var groups = listings
                .GroupBy(l => l.OwnerContentId != 0 ? $"o:{l.OwnerContentId}" : $"r:{l.RetainerId}")
                .OrderBy(g => g.Min(l => l.UnitPrice));

            foreach (var group in groups)
            {
                var first = group.First();
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                DrawOwnerName(first.OwnerContentId, first.RetainerName, first.RetainerId);

                ImGui.TableNextColumn();
                var retainers = group.Select(l => l.RetainerName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .ToList();
                ImGui.TextColored(ColorMuted, retainers.Count == 0 ? "-" : string.Join(", ", retainers));

                ImGui.TableNextColumn();
                ImGui.Text($"{group.Count()}");

                ImGui.TableNextColumn();
                ImGui.Text($"{group.Sum(l => l.Quantity):N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{group.Min(l => l.UnitPrice):N0}");

                ImGui.TableNextColumn();
                DrawKnownRelation(first.OwnerContentId);
            }

            ImGui.EndTable();
        }

        private void DrawSellersFlat(List<ListingRecord> listings)
        {
            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##sellers_flat", 6, flags)) return;

            ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("個数", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("合計", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("リテイナー", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("出品者", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var listing in listings)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.UnitPrice:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.Quantity:N0}");

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.Total:N0}");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, listing.RetainerName);

                ImGui.TableNextColumn();
                DrawOwnerName(listing.OwnerContentId, listing.RetainerName, listing.RetainerId);

                ImGui.TableNextColumn();
                ImGui.Text(listing.Hq ? "HQ" : string.Empty);
            }

            ImGui.EndTable();
        }

        /// <summary>出品者（オーナー）の名前を、判明している範囲で描画する。</summary>
        private static void DrawOwnerName(ulong contentId, string retainerName, ulong retainerId = 0)
        {
            if (contentId == 0)
            {
                // 出品データに識別子が入らないため、リテイナー台帳の判定を使う。
                var profile = retainerId != 0
                    ? Plugin.Retainers.Resolve(retainerId)
                    : Plugin.Retainers.ResolveByName(retainerName);

                if (profile != null && profile.IsMine)
                {
                    ImGui.TextColored(ColorFavorite, $"{profile.OwnerName}（自分）");
                    return;
                }

                if (profile is { HasOwner: true })
                {
                    ImGui.TextColored(ColorLink, profile.OwnerName!);
                    if (ImGui.IsItemClicked()) LodestoneOpen(profile.OwnerName!);
                    AttachTooltip(
                        $"確度 {profile.Confidence}。\n" +
                        string.Join("\n", profile.GuessReasons.Take(4)) +
                        "\nクリックで Lodestone のキャラクター検索を開きます。");
                    return;
                }

                if (profile != null && !string.IsNullOrEmpty(profile.GuessedOwnerName))
                {
                    ImGui.TextColored(ColorAccent, $"{profile.GuessedOwnerName}（推定）");
                    AttachTooltip(
                        $"確度 {profile.Confidence}。\n" + string.Join("\n", profile.GuessReasons.Take(4)));
                    return;
                }

                ImGui.TextColored(ColorMuted, "不明");
                AttachTooltip(
                    "出品データに出品者の識別子が含まれていないため、直接は分かりません。\n" +
                    "製作者署名・購入履歴・関連するリテイナーから推定を試みています。\n" +
                    "「リテイナー」タブで手動設定もできます。");
                return;
            }

            var identity = Plugin.Identities.Resolve(contentId);

            if (identity == null)
            {
                ImGui.TextColored(ColorMuted, $"不明 (ID:{contentId & 0xFFFFFF:X6})");
                AttachTooltip(
                    "この出品者の名前はまだ分かっていません。\n" +
                    "同じ識別子の出品は同一人物としてまとめられます。\n" +
                    "その人物を街などで見かけるか、あなたの購入履歴と結びつくと名前が判明します。\n" +
                    "右の「名刺」ボタンで、冒険者名刺から直接調べることもできます。");

                ImGui.SameLine();
                DrawCharaCardButton(contentId);
                return;
            }

            if (identity.Source == IdentitySource.Inferred)
            {
                ImGui.TextColored(ColorAccent, $"{identity.Name}（推定）");
                AttachTooltip(
                    $"あなたの販売履歴と出品タイミングの一致から推定した名前です（確信度スコア {identity.InferenceScore}）。\n" +
                    "確定情報ではありません。「名刺」ボタンで確認できます。");

                ImGui.SameLine();
                DrawCharaCardButton(contentId);
                return;
            }

            ImGui.TextColored(ColorLink, identity.Name);
            if (ImGui.IsItemClicked()) LodestoneOpen(identity.Name);
            AttachTooltip(
                $"出所: {DescribeSource(identity.Source)}\n" +
                "クリックで Lodestone のキャラクター検索を開きます。");
        }

        /// <summary>
        /// 冒険者名刺で出品者を調べるボタン。
        /// サーバーへの問い合わせを伴うため、押した 1 件だけを照会する。
        /// </summary>
        private static void DrawCharaCardButton(ulong contentId)
        {
            var busy = Plugin.CharaCard.IsBusy;
            if (busy) ImGui.BeginDisabled();

            if (ImGui.SmallButton($"名刺##card_{contentId}"))
                Plugin.CharaCard.Request(contentId);

            if (busy) ImGui.EndDisabled();

            AttachTooltip(
                "この出品者の冒険者名刺を開いて、名前とワールドを調べます。\n" +
                "フレンドリストから名刺を開くのと同じ操作を 1 回行います（サーバーへの問い合わせが発生します）。\n" +
                "一度調べた相手は対応表に残るので、次からは自動で名前が表示されます。");
        }

        /// <summary>その出品者と自分との取引実績（買われたことがあるか）を表示する。</summary>
        private void DrawKnownRelation(ulong contentId)
        {
            if (contentId == 0)
            {
                ImGui.TextColored(ColorMuted, "-");
                return;
            }

            var identity = Plugin.Identities.Resolve(contentId);
            if (identity == null)
            {
                ImGui.TextColored(ColorMuted, "-");
                return;
            }

            var buyer = _stats.FirstOrDefault(
                s => string.Equals(s.BuyerName, identity.Name, StringComparison.OrdinalIgnoreCase));

            if (buyer == null)
            {
                ImGui.TextColored(ColorMuted, "-");
                return;
            }

            ImGui.TextColored(ColorFavorite,
                $"あなたから {buyer.TotalQuantity:N0}個 購入");
            AttachTooltip(
                $"{buyer.BuyerName} は、あなたのリテイナーから {buyer.SessionCount} 回・" +
                $"合計 {buyer.TotalQuantity:N0}個 購入しています。");
        }

        private static string DescribeSource(IdentitySource source) => source switch
        {
            IdentitySource.Self => "自分",
            IdentitySource.Friend => "フレンドリスト",
            IdentitySource.FreeCompany => "フリーカンパニー",
            IdentitySource.Linkshell => "リンクシェル",
            IdentitySource.Party => "パーティ",
            IdentitySource.ObjectTable => "周囲で見かけた",
            IdentitySource.Inferred => "推定",
            _ => "不明",
        };
    }
}
