using System.Linq;
using System.Text;

namespace MarketStats.Data
{
    /// <summary>取り込みの結果。</summary>
    public sealed class ImportResult
    {
        public int Read { get; set; }
        public int Added { get; set; }
        public int Skipped { get; set; }
        public List<string> Problems { get; } = new();

        public string Summary =>
            Read == 0
                ? "読み取れる行がありませんでした。"
                : $"{Read} 行を読み取り、{Added} 件を取り込みました（{Skipped} 件は対象外）。";
    }

    /// <summary>
    /// 識別子と名前の対応を、外から取り込む・外へ書き出す。
    ///
    /// 他のプラグイン（PlayerTrack など）が貯めた記録や、
    /// 自分で別のキャラクターに移すときに使う。
    ///
    /// 受け付ける形は「1 行 1 人」で、区切りはカンマ・タブのどちらでも良い。
    ///   識別子, 名前, ワールドID(省略可), LodestoneID(省略可)
    /// 先頭が数字でない行（見出しなど）は読み飛ばす。
    /// </summary>
    public static class IdentityImporter
    {
        public static ImportResult Import(string text, IdentitySource source = IdentitySource.ObjectTable)
        {
            var result = new ImportResult();
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim().TrimEnd('\r');
                if (line.Length == 0) continue;
                if (line.StartsWith('#') || line.StartsWith("//")) continue;

                var parts = line.Split(new[] { ',', '\t' }, StringSplitOptions.TrimEntries);
                if (parts.Length < 2) continue;

                result.Read++;

                if (!TryParseId(parts[0], out var contentId) || contentId == 0)
                {
                    // 見出し行はここで落ちるので、問題としては数えない。
                    result.Skipped++;
                    continue;
                }

                var name = parts[1].Trim().Trim('"');
                if (!LooksLikeCharacterName(name))
                {
                    result.Skipped++;
                    if (result.Problems.Count < 5)
                        result.Problems.Add($"名前として扱えませんでした: 「{name}」");
                    continue;
                }

                ushort worldId = 0;
                if (parts.Length >= 3 && ushort.TryParse(parts[2], out var parsedWorld)) worldId = parsedWorld;

                long lodestoneId = 0;
                if (parts.Length >= 4 && long.TryParse(parts[3], out var parsedLodestone))
                    lodestoneId = parsedLodestone;

                Plugin.Identities.Record(contentId, name, worldId, source);

                if (lodestoneId > 0)
                {
                    var identity = Plugin.Identities.Resolve(contentId);
                    if (identity != null) identity.LodestoneId = lodestoneId;
                }

                result.Added++;
            }

            if (result.Added > 0) Plugin.Identities.Save(force: true);
            return result;
        }

        /// <summary>いま持っている対応表を、取り込みと同じ形で書き出す。</summary>
        public static string Export(bool confirmedOnly = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 識別子, 名前, ワールドID, LodestoneID");

            foreach (var identity in Plugin.Identities.All
                         .Where(i => !confirmedOnly || i.IsConfirmed)
                         .OrderBy(i => i.Name))
            {
                sb.Append(identity.ContentId).Append(',')
                  .Append(identity.Name).Append(',')
                  .Append(identity.WorldId).Append(',')
                  .Append(identity.LodestoneId)
                  .AppendLine();
            }

            return sb.ToString();
        }

        private static bool TryParseId(string text, out ulong id)
        {
            text = text.Trim().Trim('"');

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ulong.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out id);

            return ulong.TryParse(text, out id);
        }

        private static bool LooksLikeCharacterName(string value)
        {
            if (value.Length is < 3 or > 32) return false;
            if (!char.IsAsciiLetterUpper(value[0])) return false;

            return value.All(c => char.IsAsciiLetter(c) || c is ' ' or '\'' or '-');
        }
    }
}
