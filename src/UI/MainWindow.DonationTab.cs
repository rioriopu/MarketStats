using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MarketStats.UI
{
    // ご支援（寄付）タブ。自己完結した描画のため partial で分離。
    public sealed partial class MainWindow
    {
        private const string PatreonUrl = "https://www.patreon.com/c/SuppotToEstell";

        private void DrawDonationTab()
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.9f, 0.5f, 1f));
            ImGui.TextWrapped("Market Stats をご利用いただき、誠にありがとうございます");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            ImGui.TextWrapped(
                "皆さまの温かいご支援が、本プラグインの開発・メンテナンスを支える大きな力となっております。\n" +
                "頂いたサポートは新機能の開発、不具合修正、FFXIV のメジャーパッチへの追従に大切に使わせていただきます。\n" +
                "今後ともどうぞよろしくお願いいたします。");
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.85f, 1f, 0.85f, 1f));
            ImGui.TextWrapped("いつもご支援くださり、心より感謝申し上げます。");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Patreon で支援する##openpatreon", new Vector2(240, 36)))
                Game.LodestoneLink.OpenUrl(PatreonUrl);
            AttachTooltip("ブラウザで Patreon ページを開きます。");

            ImGui.SameLine();
            if (ImGui.Button("URL をコピー##copypatreon", new Vector2(160, 36)))
                ImGui.SetClipboardText(PatreonUrl);
            AttachTooltip("Patreon の URL をクリップボードにコピーします。");

            ImGui.Spacing();
            ImGui.TextDisabled(PatreonUrl);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextDisabled(
                "※ Patreon サイトの利用は外部サービスとして行われます。Market Stats は寄付処理には一切関与しません。");
        }
    }
}
