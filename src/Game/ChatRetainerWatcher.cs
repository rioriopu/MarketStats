using System.Linq;
using System.Reflection;
using Dalamud.Game.Chat;

namespace MarketStats.Game
{
    /// <summary>
    /// チャットでリテイナー名に言及した発言を拾う。
    ///
    /// マーケット関連の告知で「○○（リテイナー名）に出しています」と案内する人がいる。
    /// 発言者とリテイナー名が同じ発言に出てくれば、その人が持ち主である可能性がある。
    /// 他人のリテイナーの話をしている場合もあるので、あくまで手がかりの 1 つとして扱う。
    ///
    /// 判定に使うのは「すでに観測済みのリテイナー名が含まれるか」だけで、
    /// チャットの本文を保存することはしない。
    /// </summary>
    public sealed class ChatRetainerWatcher : IDisposable
    {
        private bool _subscribed;

        // Dalamud の型が変わっても動くよう、文字列化はプロパティを探して行う。
        private static PropertyInfo? _textValueProperty;
        private static MethodInfo? _extractTextMethod;
        private static bool _textAccessorResolved;

        public int MentionCount { get; private set; }
        public string LastMention { get; private set; } = "なし";

        public void Initialize()
        {
            try
            {
                Plugin.ChatGui.ChatMessage += OnChatMessage;
                _subscribed = true;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"チャットの監視を開始できませんでした: {e.Message}");
            }
        }

        private void OnChatMessage(IHandleableChatMessage message)
        {
            if (!Plugin.Config.EnableChatRetainerWatch) return;

            try
            {
                var speaker = NormalizeName(ToText(message.Sender));
                if (string.IsNullOrEmpty(speaker)) return;   // 発言者のいないシステムメッセージは対象外

                var text = ToText(message.Message);
                if (string.IsNullOrWhiteSpace(text) || text.Length < 4) return;

                var channel = message.LogKind.ToString() ?? string.Empty;

                foreach (var (id, name) in Plugin.Retainers.AllNames())
                {
                    // 短い名前は普通の単語と衝突するため対象外にする。
                    if (name.Length < 4) continue;
                    if (!text.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Plugin.Retainers.AddChatMention(id, speaker, channel)) continue;

                    MentionCount++;
                    LastMention = $"{speaker} → {name} ({DateTime.Now:HH:mm})";
                    Plugin.PluginLog.Information(
                        $"チャットでリテイナー名への言及を検出: {speaker} が「{name}」に言及");
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"チャットの解析に失敗しました: {e.Message}");
            }
        }

        /// <summary>SeString 系のオブジェクトから表示文字列を取り出す。</summary>
        private static string ToText(object? value)
        {
            if (value == null) return string.Empty;

            if (!_textAccessorResolved)
            {
                _textAccessorResolved = true;
                var type = value.GetType();
                _textValueProperty = type.GetProperty("TextValue");
                _extractTextMethod = type.GetMethod("ExtractText", Type.EmptyTypes);
            }

            try
            {
                if (_textValueProperty != null)
                    return _textValueProperty.GetValue(value) as string ?? string.Empty;
                if (_extractTextMethod != null)
                    return _extractTextMethod.Invoke(value, null) as string ?? string.Empty;
            }
            catch
            {
                // 取り出せなければ諦める。
            }

            return value.ToString() ?? string.Empty;
        }

        /// <summary>発言者名から装飾（パーティ番号やワールド名）を落とす。</summary>
        private static string NormalizeName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var cleaned = new string(raw.Where(c =>
                char.IsAsciiLetter(c) || c == ' ' || c == '\'' || c == '-').ToArray()).Trim();

            // 「Firstname Lastname」の形になっていなければ採用しない。
            return cleaned.Count(char.IsWhiteSpace) == 1 && cleaned.Length >= 5
                ? cleaned
                : string.Empty;
        }

        public void Dispose()
        {
            if (!_subscribed) return;
            try
            {
                Plugin.ChatGui.ChatMessage -= OnChatMessage;
            }
            catch
            {
                // 破棄時の失敗は握りつぶす。
            }
        }
    }
}
