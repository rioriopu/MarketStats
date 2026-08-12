using System.Linq;
using Dalamud.Bindings.ImGui;
using MarketStats.Game;

namespace MarketStats.UI
{
    public sealed partial class MainWindow
    {
        private string _unifiedInput = string.Empty;
        private SearchOutcome? _unifiedResult;

        /// <summary>
        /// 何を入れても受け付ける検索タブ。
        /// 識別子・キャラクター名・リテイナー名・アイテム名・Lodestone の URL を
        /// 入力の形から判別し、持っている記録すべてに横断で問い合わせる。
        /// </summary>
        private void DrawSearchTab()
        {
            ImGui.Spacing();
            ImGui.TextWrapped(
                "識別子でも、キャラクター名でも、リテイナー名でも、アイテム名でも構いません。" +
                "入力の形から判別して、持っている記録すべてを横断で探します。");

            ImGui.Spacing();

            ImGui.SetNextItemWidth(420);
            var input = _unifiedInput;
            var submitted = ImGui.InputTextWithHint("##unified_search",
                "例: Omu Anko / 0x40000002105B17 / 33776997236783377 / 剛力の宝薬",
                ref input, 128, ImGuiInputTextFlags.EnterReturnsTrue);
            _unifiedInput = input;

            ImGui.SameLine();
            if (ImGui.Button("検索") || submitted)
                _unifiedResult = UnifiedSearch.Search(_unifiedInput);

            ImGui.SameLine();
            if (ImGui.Button("消去"))
            {
                _unifiedInput = string.Empty;
                _unifiedResult = null;
            }

            ImGui.Spacing();
            ImGui.TextColored(ColorMuted,
                $"対応表 {Plugin.Identities.Count:N0} 件 / リテイナー {Plugin.Retainers.Count:N0} 体 / " +
                $"購入履歴 {Plugin.Purchases.Count:N0} 件 / 販売ログ {Plugin.Store.Count:N0} 件");

            ImGui.Separator();

            if (_unifiedResult == null)
            {
                ImGui.TextColored(ColorMuted, "検索したい語句を入力してください。");
                return;
            }

            var result = _unifiedResult;

            if (!string.IsNullOrEmpty(result.Interpretation))
            {
                ImGui.TextColored(ColorAccent, result.Interpretation);
                ImGui.Spacing();
            }

            if (result.Hits.Count == 0)
            {
                ImGui.TextColored(ColorMuted, "見つかりませんでした。");

                foreach (var suggestion in result.Suggestions)
                {
                    ImGui.Spacing();
                    ImGui.TextWrapped("・" + suggestion);
                }

                return;
            }

            ImGui.TextColored(ColorMuted, $"{result.Hits.Count} 件見つかりました。");
            ImGui.Spacing();

            const ImGuiTableFlags flags =
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV |
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;

            if (!ImGui.BeginTable("##unified_hits", 4, flags)) return;

            ImGui.TableSetupColumn("見つかった場所", ImGuiTableColumnFlags.WidthFixed, 160);
            ImGui.TableSetupColumn("対象", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("内容", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("辿る", ImGuiTableColumnFlags.WidthFixed, 190);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            var index = 0;
            foreach (var hit in result.Hits)
            {
                index++;
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.TextColored(ColorMuted, hit.Source);

                ImGui.TableNextColumn();
                if (!string.IsNullOrEmpty(hit.CharacterName))
                {
                    ImGui.TextColored(ColorLink, hit.Title);
                    if (ImGui.IsItemClicked()) LodestoneOpen(hit.CharacterName);
                    AttachTooltip("クリックで Lodestone のキャラクター検索を開きます。");
                }
                else
                {
                    ImGui.Text(hit.Title);
                }

                ImGui.TableNextColumn();
                ImGui.TextWrapped(hit.Detail);

                ImGui.TableNextColumn();
                if (hit.RetainerId != 0)
                {
                    if (ImGui.SmallButton($"リテイナー##r{index}"))
                    {
                        _selectedRetainerId = hit.RetainerId;
                        _requestedTab = Tab.Retainers;
                    }
                    ImGui.SameLine();
                }

                if (hit.ContentId != 0)
                {
                    if (ImGui.SmallButton($"識別子##c{index}"))
                        OpenProbeForContentId(hit.ContentId);
                    ImGui.SameLine();
                }

                if (!string.IsNullOrEmpty(hit.CharacterName))
                {
                    if (ImGui.SmallButton($"購入##b{index}"))
                    {
                        _marketBuyerSearch = hit.CharacterName;
                        _marketBuyersDirty = true;
                        _requestedTab = Tab.MarketBuyers;
                    }
                    AttachTooltip("買い占めタブで、この人物の購入状況を見ます。");
                }
            }

            ImGui.EndTable();
        }
    }
}
