using System.Linq;

namespace MarketStats.Data
{
    /// <summary>
    /// 「自分」を確実に見分ける。
    ///
    /// 他人のリテイナーの持ち主が自分である、ということはあり得ない。
    /// にもかかわらず自分の名前が候補に出てしまうのは、
    ///   ・自分が作った物を、買った誰かが売っている（製作者署名が自分になる）
    ///   ・自分がマーケットで買った直後に、別の誰かが同じ物を出品した
    /// といった場合に、手がかりが自分を指してしまうため。
    ///
    /// ログイン中のキャラクターだけでなく、これまでに確認したすべての自キャラを対象にする。
    /// </summary>
    public static class SelfIdentity
    {
        private static HashSet<string>? _names;
        private static HashSet<ulong>? _contentIds;
        private static DateTime _builtUtc = DateTime.MinValue;

        /// <summary>作り直す間隔。頻繁に変わるものではないので、しばらく使い回す。</summary>
        private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

        /// <summary>この名前は自分（自分のいずれかのキャラクター）か。</summary>
        public static bool IsSelf(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            Build();
            return _names!.Contains(name.Trim());
        }

        /// <summary>この識別子は自分のものか。</summary>
        public static bool IsSelf(ulong contentId)
        {
            if (contentId == 0) return false;
            Build();
            return _contentIds!.Contains(contentId);
        }

        /// <summary>把握している自分のキャラクター名。</summary>
        public static IReadOnlyCollection<string> Names
        {
            get { Build(); return _names!; }
        }

        /// <summary>記録が変わったときに呼ぶ。次回の判定で作り直す。</summary>
        public static void Invalidate() => _builtUtc = DateTime.MinValue;

        private static void Build()
        {
            if (_names != null && DateTime.UtcNow - _builtUtc < Lifetime) return;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<ulong>();

            try
            {
                // 1. いまログインしているキャラクター
                if (Plugin.PlayerState.IsLoaded)
                {
                    var name = Plugin.PlayerState.CharacterName;
                    if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                    if (Plugin.PlayerState.ContentId != 0) ids.Add(Plugin.PlayerState.ContentId);
                }

                // 2. リテイナーを確認したことのあるキャラクター（他のキャラも含む）
                foreach (var character in Plugin.OwnListings.ByCharacter())
                {
                    if (character.ContentId != 0) ids.Add(character.ContentId);
                    if (!string.IsNullOrWhiteSpace(character.Name)) names.Add(character.Name);
                }

                // 3. 自分のリテイナーとして登録済みのものの持ち主
                foreach (var profile in Plugin.Retainers.Snapshot().Where(p => p.IsMine))
                {
                    if (!string.IsNullOrWhiteSpace(profile.OwnerName)) names.Add(profile.OwnerName!);
                    if (profile.OwnerContentId != 0) ids.Add(profile.OwnerContentId);
                }

                // 4. 売却ログに残っている「売った側」のキャラクター
                foreach (var record in Plugin.Store.Snapshot())
                {
                    if (record.OwnerContentId != 0) ids.Add(record.OwnerContentId);
                    if (!string.IsNullOrWhiteSpace(record.OwnerName)) names.Add(record.OwnerName);
                }

                // 5. 対応表で自分と記録されているもの
                foreach (var identity in Plugin.Identities.All.Where(i => i.Source == IdentitySource.Self))
                {
                    names.Add(identity.Name);
                    ids.Add(identity.ContentId);
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"自分のキャラクターを把握できませんでした: {e.Message}");
            }

            _names = names;
            _contentIds = ids;
            _builtUtc = DateTime.UtcNow;
        }
    }
}
