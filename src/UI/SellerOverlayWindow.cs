using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MarketStats.Data;

namespace MarketStats.UI
{
    /// <summary>
    /// マーケットボードでアイテムの出品一覧（購入する画面）を開いている間だけ、
    /// その横に並べて表示する小窓。出品ごとの出品者を表示し、
    /// 分からない相手はその場で特定できるようにする。
    ///
    /// ゲーム側の右クリックメニューに頼らないので、こちらは確実に使える。
    /// </summary>
    public sealed class SellerOverlayWindow : Window
    {
        private const string AddonName = "ItemSearchResult";

        private static readonly Vector4 ColorMuted = new(0.65f, 0.65f, 0.65f, 1f);
        private static readonly Vector4 ColorLink = new(0.55f, 0.78f, 1f, 1f);
        private static readonly Vector4 ColorAccent = new(1f, 0.85f, 0.45f, 1f);

        private List<ListingRecord> _listings = new();
        private DateTime _nextRefreshUtc = DateTime.MinValue;

        public SellerOverlayWindow() : base("出品者##MarketStatsSellerOverlay",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing)
        {
            Size = new Vector2(340, 320);
            SizeCondition = ImGuiCond.FirstUseEver;
            RespectCloseHotkey = false;
        }

        public override bool DrawConditions()
        {
            if (!Plugin.Config.ShowSellerOverlay) return false;
            if (!Plugin.Config.EnableResaleTracking) return false;
            return IsAddonVisible();
        }

        public override void PreDraw()
        {
            // 出品一覧のウィンドウの右隣に添える。
            var pos = GetAddonPosition();
            if (pos.HasValue)
                ImGui.SetNextWindowPos(pos.Value, ImGuiCond.Always);
        }

        public override void Draw()
        {
            if (DateTime.UtcNow >= _nextRefreshUtc)
            {
                _nextRefreshUtc = DateTime.UtcNow.AddMilliseconds(500);
                _listings = Plugin.MarketWatcher.CurrentListings();
            }

            if (_listings.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "出品を読み取れませんでした。");
                return;
            }

            ImGui.TextColored(ColorAccent, Plugin.Items.GetName(_listings[0].ItemId));
            ImGui.SameLine();
            ImGui.TextColored(ColorMuted, $"({_listings.Count} 件)");

            if (!string.IsNullOrEmpty(Plugin.CharaCard.LastResult))
                ImGui.TextColored(ColorAccent, Plugin.CharaCard.LastResult);

            ImGui.Separator();

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##overlay_sellers", 3, flags)) return;

            ImGui.TableSetupColumn("単価", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("リテイナー", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("出品者", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            foreach (var listing in _listings)
            {
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.Text($"{listing.UnitPrice:N0}");
                if (listing.Quantity > 1)
                    AttachTooltip($"{listing.Quantity:N0}個 / 合計 {listing.Total:N0} ギル");

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted,
                    string.IsNullOrEmpty(listing.RetainerName) ? "-" : listing.RetainerName);

                ImGui.TableNextColumn();
                DrawSeller(listing);
            }

            ImGui.EndTable();
        }

        private static void DrawSeller(ListingRecord listing)
        {
            if (listing.OwnerContentId == 0)
            {
                ImGui.TextColored(ColorMuted, "情報なし");
                AttachTooltip(
                    "この出品にはオーナーの識別子が含まれていませんでした。\n" +
                    "ゲームのアップデートで読み取り位置がずれている可能性があります。");
                return;
            }

            var identity = Plugin.Identities.Resolve(listing.OwnerContentId);

            if (identity is { Source: not IdentitySource.Inferred })
            {
                ImGui.TextColored(ColorLink, identity.Name);
                if (ImGui.IsItemClicked()) Game.LodestoneLink.OpenSearch(identity.Name);
                AttachTooltip("クリックで Lodestone のキャラクター検索を開きます。");
                return;
            }

            var label = identity == null ? "特定" : $"{identity.Name}?";

            var busy = Plugin.CharaCard.IsBusy;
            if (busy) ImGui.BeginDisabled();
            if (ImGui.SmallButton($"{label}##ov_{listing.ListingId}"))
                Plugin.CharaCard.Request(listing.OwnerContentId);
            if (busy) ImGui.EndDisabled();

            AttachTooltip(identity == null
                ? "冒険者名刺を開いて、この出品者のキャラクター名を調べます。"
                : $"推定では {identity.Name} です。冒険者名刺で確認します。");
        }

        private static unsafe bool IsAddonVisible()
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

        private static unsafe Vector2? GetAddonPosition()
        {
            try
            {
                var ptr = Plugin.GameGui.GetAddonByName(AddonName, 1);
                if (ptr.IsNull || !ptr.IsVisible) return null;
                return new Vector2(ptr.X + ptr.ScaledWidth + 6, ptr.Y);
            }
            catch
            {
                return null;
            }
        }

        private static void AttachTooltip(string text)
        {
            if (!ImGui.IsItemHovered()) return;
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(360);
            ImGui.TextWrapped(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
