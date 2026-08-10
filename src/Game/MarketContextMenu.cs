using System.Linq;
using Dalamud.Game.Gui.ContextMenu;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using MarketStats.Data;
using Sheets = Lumina.Excel.Sheets;

namespace MarketStats.Game
{
    /// <summary>
    /// マーケットボードの出品一覧を右クリックしたときに「出品者を特定する」を追加する。
    ///
    /// 出品データにはオーナーの ContentId が入っているので、
    ///   ・対応表にあればその場で名前を表示
    ///   ・無ければ冒険者名刺で 1 件だけ照会
    /// という流れで出品者を割り出す。
    /// </summary>
    public sealed unsafe class MarketContextMenu : IDisposable
    {
        private const string AddonName = "ItemSearchResult";

        private bool _attached;

        /// <summary>名刺照会の結果をチャットに出すために、照会した出品を覚えておく。</summary>
        private ulong _awaitingContentId;
        private uint _awaitingItemId;

        public void Attach()
        {
            if (_attached) return;

            try
            {
                Plugin.ContextMenu.OnMenuOpened += OnMenuOpened;
                Plugin.CharaCard.Resolved += OnCharaCardResolved;
                Plugin.CharaCard.Failed += OnCharaCardFailed;
                _attached = true;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"マーケットの右クリックメニューを登録できませんでした: {e.Message}");
            }
        }

        /// <summary>直近にコンテキストメニューが開いたアドオン名（診断用）。</summary>
        public string LastMenuAddon { get; private set; } = "（まだ開いていません）";

        public DateTime LastMenuLocal { get; private set; } = DateTime.MinValue;

        private void OnMenuOpened(IMenuOpenedArgs args)
        {
            LastMenuAddon = string.IsNullOrEmpty(args.AddonName) ? "(名前なし)" : args.AddonName;
            LastMenuLocal = DateTime.Now;

            if (Plugin.Config.DebugMode)
                Plugin.PluginLog.Information(
                    $"コンテキストメニューが開きました: addon='{LastMenuAddon}' type={args.MenuType}");

            if (!Plugin.Config.EnableSellerContextMenu) return;

            // アドオン名が期待どおりに入らないことがあるため、
            // 「出品一覧が開いている間の右クリック」なら対象とみなす。
            if (args.AddonName != AddonName && !IsListingAddonVisible()) return;

            // 出品が取れなくても項目自体は出す。取れない理由はクリック時に案内する
            // （項目ごと消えると、機能が無いのか失敗しているのか判別できないため）。
            var listing = ResolveSelectedListing();

            var identity = listing is { OwnerContentId: not 0 }
                ? Plugin.Identities.Resolve(listing.OwnerContentId)
                : null;

            string label;
            if (identity == null)
                label = "出品者を特定する (Market Stats)";
            else if (identity.Source == IdentitySource.Inferred)
                label = $"出品者: {identity.Name}? を確認 (Market Stats)";
            else
                label = $"出品者: {identity.Name} (Market Stats)";

            try
            {
                args.AddMenuItem(new MenuItem
                {
                    Name = label,
                    PrefixChar = 'M',
                    PrefixColor = 539,
                    OnClicked = _ => OnClicked(listing),
                });
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"メニュー項目を追加できませんでした: {e.Message}");
            }
        }

        private void OnClicked(ListingRecord? listing)
        {
            if (listing == null)
            {
                Plugin.ChatGui.PrintError(
                    "[Market Stats] 選択されている出品を特定できませんでした。" +
                    "行を一度クリックして選んでから、もう一度お試しください。");
                return;
            }

            if (listing.OwnerContentId == 0)
            {
                Plugin.ChatGui.Print("[Market Stats] この出品にはオーナーの情報が含まれていませんでした。");
                return;
            }

            var identity = Plugin.Identities.Resolve(listing.OwnerContentId);

            // 確定情報が既にあるなら、その場で結果を出す。
            if (identity is { Source: not IdentitySource.Inferred })
            {
                ReportSeller(identity.Name, identity.WorldId, listing, "対応表");
                return;
            }

            // 推定しかない、または全く分からない場合は名刺で確認する。
            if (Plugin.CharaCard.IsBusy)
            {
                Plugin.ChatGui.Print("[Market Stats] 別の照会を実行中です。少し待ってからお試しください。");
                return;
            }

            _awaitingContentId = listing.OwnerContentId;
            _awaitingItemId = listing.ItemId;

            if (Plugin.CharaCard.Request(listing.OwnerContentId))
            {
                Plugin.ChatGui.Print(
                    $"[Market Stats] 出品リテイナー「{listing.RetainerName}」のオーナーを冒険者名刺で照会しています…");
            }
            else
            {
                Plugin.ChatGui.PrintError("[Market Stats] 冒険者名刺を開けませんでした。");
                _awaitingContentId = 0;
            }
        }

        private void OnCharaCardResolved(ulong contentId, string name, ushort worldId)
        {
            if (_awaitingContentId != contentId) return;

            var itemId = _awaitingItemId;
            _awaitingContentId = 0;
            _awaitingItemId = 0;

            ReportSeller(name, worldId, null, "冒険者名刺", itemId);
        }

        private void OnCharaCardFailed(ulong contentId, string reason)
        {
            if (_awaitingContentId != contentId) return;
            _awaitingContentId = 0;
            _awaitingItemId = 0;
            Plugin.ChatGui.PrintError($"[Market Stats] 出品者を特定できませんでした: {reason}");
        }

        /// <summary>判明した出品者の情報と、自分との取引実績をチャットに出す。</summary>
        private static void ReportSeller(
            string name, ushort worldId, ListingRecord? listing, string source, uint itemId = 0)
        {
            var world = ResolveWorldName(worldId);
            var suffix = string.IsNullOrEmpty(world) ? string.Empty : $" @ {world}";
            Plugin.ChatGui.Print($"[Market Stats] 出品者: {name}{suffix}（{source}）");

            var targetItemId = listing?.ItemId ?? itemId;
            ReportPurchaseHistory(name, targetItemId);
        }

        /// <summary>その相手が自分のリテイナーから買っていれば、その実績を出す。</summary>
        private static void ReportPurchaseHistory(string name, uint itemId)
        {
            var records = Plugin.Store.Snapshot()
                .Where(r => string.Equals(r.BuyerName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (records.Count == 0) return;

            var total = records.Sum(r => (long)r.Quantity);
            var gil = records.Aggregate(0UL, (acc, r) => acc + r.TotalGil);
            Plugin.ChatGui.Print(
                $"[Market Stats] {name} はあなたのリテイナーから 合計 {total:N0}個（{gil:N0} ギル）購入しています。");

            if (itemId == 0) return;

            var forItem = records.Where(r => r.ItemId == itemId).ToList();
            if (forItem.Count == 0) return;

            var itemTotal = forItem.Sum(r => (long)r.Quantity);
            Plugin.ChatGui.Print(
                $"[Market Stats] └ うち {Plugin.Items.GetName(itemId)}: {itemTotal:N0}個 / {forItem.Count} 件");
        }

        private static string ResolveWorldName(ushort worldId)
        {
            if (worldId == 0) return string.Empty;
            try
            {
                var world = Plugin.DataManager.GetExcelSheet<Sheets.World>()?.GetRowOrDefault(worldId);
                return world?.Name.ExtractText() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>マーケットの出品一覧（購入する画面）が表示されているか。</summary>
        private static bool IsListingAddonVisible()
        {
            try
            {
                var ptr = Plugin.GameGui.GetAddonByName(AddonName, 1);
                return !ptr.IsNull && ptr.IsVisible;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>いま出品一覧で選択されている行の出品を取り出す。</summary>
        private static ListingRecord? ResolveSelectedListing()
        {
            try
            {
                var module = AgentModule.Instance();
                if (module == null) return null;

                var agent = (AgentItemSearch*)module->GetAgentByInternalId(AgentId.ItemSearch);
                if (agent == null) return null;

                var proxy = agent->InfoProxyItemSearch;
                if (proxy == null) return null;

                var index = (int)agent->ResultSelectedIndex;
                if (index < 0 || index >= (int)proxy->ListingCount) return null;

                ref var listing = ref proxy->Listings[index];
                if (listing.ItemId == 0) return null;

                return new ListingRecord
                {
                    ItemId = listing.ItemId,
                    Hq = listing.IsHqItem,
                    ListingId = listing.ListingId,
                    RetainerId = listing.RetainerId,
                    OwnerContentId = listing.ContentId,
                    RetainerName = listing.CharacterName.ToString(),
                    UnitPrice = listing.UnitPrice,
                    Quantity = listing.Quantity,
                    TownId = listing.TownId,
                    Source = "game",
                };
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"選択中の出品を取得できませんでした: {e.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            if (!_attached) return;

            try
            {
                Plugin.ContextMenu.OnMenuOpened -= OnMenuOpened;
                Plugin.CharaCard.Resolved -= OnCharaCardResolved;
                Plugin.CharaCard.Failed -= OnCharaCardFailed;
            }
            catch
            {
                // 破棄時の失敗は握りつぶす。
            }
        }
    }
}
