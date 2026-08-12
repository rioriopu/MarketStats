using System.Linq;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketStats.Data;

namespace MarketStats.Game
{
    /// <summary>
    /// アイテムの説明（ツールチップ）から製作者名を拾い、識別子との対応を集める。
    ///
    /// ゲームは製作品の「製作者」をツールチップに表示できる。
    /// つまりゲーム自身は識別子から名前を解決している。
    /// カーソルを合わせた瞬間に「アイテムに刻まれた製作者の識別子」と
    /// 「表示されている製作者名」を突き合わせれば、名刺を使わずに対応表を増やせる。
    ///
    /// マーケットで製作品を眺めているだけで対応が貯まっていく。
    /// </summary>
    public sealed unsafe class CrafterNameHarvester : IDisposable
    {
        private const string TooltipAddon = "ItemDetail";

        private bool _registered;
        private ulong _lastContentId;
        private string _lastName = string.Empty;

        public int HarvestCount { get; private set; }
        public string LastHarvest { get; private set; } = "なし";

        public void Initialize()
        {
            try
            {
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, TooltipAddon, OnTooltip);
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, TooltipAddon, OnTooltip);
                _registered = true;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"アイテム説明の監視を登録できませんでした: {e.Message}");
            }
        }

        private void OnTooltip(AddonEvent type, AddonArgs args)
        {
            if (!Plugin.Config.HarvestCrafterNames) return;

            try
            {
                var contentId = GetHoveredCrafterContentId();
                if (contentId == 0) return;

                // 同じアイテムを見続けている間は繰り返さない。
                if (contentId == _lastContentId) return;

                var name = ExtractCrafterName(args.Addon);
                if (string.IsNullOrEmpty(name)) return;

                _lastContentId = contentId;
                _lastName = name;

                var known = Plugin.Identities.Resolve(contentId);
                if (known != null && known.Source != IdentitySource.Inferred) return;

                Plugin.Identities.Record(contentId, name, 0, IdentitySource.MarketBoard);
                Plugin.Identities.Save();

                HarvestCount++;
                LastHarvest = $"{name} = 0x{contentId:X}（{DateTime.Now:HH:mm}）";
                Plugin.PluginLog.Information($"製作者名を取得しました: {name} = 0x{contentId:X}");
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Debug($"製作者名の取得に失敗: {e.Message}");
            }
        }

        /// <summary>いまカーソルを合わせているマーケットの出品の、製作者の識別子。</summary>
        private static ulong GetHoveredCrafterContentId()
        {
            var module = AgentModule.Instance();
            if (module == null) return 0;

            var agent = (AgentItemSearch*)module->GetAgentByInternalId(AgentId.ItemSearch);
            if (agent == null) return 0;

            var crafter = agent->ResultHoveredItem.CrafterContentId;
            if (crafter != 0) return crafter;

            // ホバー情報が無い場合は、選択中の行の製作者署名を使う。
            var proxy = agent->InfoProxyItemSearch;
            if (proxy == null) return 0;

            var index = (int)agent->ResultSelectedIndex;
            if (index < 0 || index >= (int)proxy->ListingCount) return 0;

            return proxy->Listings[index].ArtisanId;
        }

        /// <summary>
        /// ツールチップの表示文字列からキャラクター名らしきものを取り出す。
        /// 「製作者」の行に入っている名前を拾うのが狙い。
        /// </summary>
        private static string ExtractCrafterName(nint addonAddress)
        {
            if (addonAddress == nint.Zero) return string.Empty;
            if (!SafeMemory.IsFullyReadable(addonAddress, 0x30)) return string.Empty;

            var addon = (AtkUnitBase*)addonAddress;
            if (addon == null || !addon->IsVisible) return string.Empty;

            var candidates = new List<string>();
            CollectText(addon->RootNode, candidates, 0);

            // ツールチップには説明文も入るので、キャラクター名の形をしたものだけを拾う。
            return candidates.FirstOrDefault(IsCharacterName) ?? string.Empty;
        }

        /// <summary>ノードを辿って表示文字列を集める。</summary>
        private static void CollectText(AtkResNode* node, List<string> output, int depth)
        {
            if (node == null || depth > 12 || output.Count > 64) return;

            if (node->Type == NodeType.Text)
            {
                var textNode = (AtkTextNode*)node;
                var text = textNode->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(text)) output.Add(text.Trim());
            }
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    CollectText(component->UldManager.RootNode, output, depth + 1);
            }

            CollectText(node->ChildNode, output, depth + 1);
            CollectText(node->PrevSiblingNode, output, depth + 1);
        }

        private static bool IsCharacterName(string value)
        {
            var trimmed = value.Trim();
            if (trimmed.Length is < 5 or > 32) return false;
            if (!char.IsAsciiLetterUpper(trimmed[0])) return false;

            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 2) return false;

            return words.All(w =>
                w.Length >= 2 && char.IsAsciiLetterUpper(w[0]) &&
                w.All(c => char.IsAsciiLetter(c) || c is '\'' or '-'));
        }

        public void Dispose()
        {
            if (!_registered) return;
            try
            {
                Plugin.AddonLifecycle.UnregisterListener(OnTooltip);
            }
            catch
            {
                // 破棄時の失敗は握りつぶす。
            }
        }
    }
}
