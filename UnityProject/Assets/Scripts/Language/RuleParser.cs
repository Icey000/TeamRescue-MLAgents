using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace TeamRescue.Language
{
    /// <summary>
    /// Baseline: a typical keyword parser (English / Chinese / French). It knows colour and
    /// shape synonyms and keeps the order in which they appear, but has no notion of
    /// negation, space or distance. Also used as the offline fallback.
    /// </summary>
    public sealed class RuleParser : ICommandParser
    {
        public string Name => "rules";

        private static readonly string[] Blue = { "blue", "蓝", "bleu", "bleue", "cylinder", "round", "圆", "cylindre", "rond" };
        private static readonly string[] Orange = { "orange", "橙", "橘", "square", "box", "cube", "方", "carré", "carree", "boîte", "boite" };
        private static readonly string[] Cancel = { "stop", "cancel", "wait", "hold", "停", "等", "取消", "别动", "arrête", "arrete", "attends" };
        private static readonly string[] Layout = { "new layout", "reset", "shuffle", "new map", "换", "重置", "新地图", "nouvelle carte", "réinitialise" };
        private static readonly string[] Status = { "what are you doing", "status", "在干", "在做", "状态", "que fais", "tu fais quoi" };

        public IEnumerator Parse(string utterance, SceneFacts facts, List<(string player, string robot)> history, Action<ParsedCommand> done)
        {
            done(ParseNow(utterance));
            yield break;
        }

        public static ParsedCommand ParseNow(string utterance)
        {
            string s = (utterance ?? "").ToLowerInvariant();
            bool zh = s.Any(c => c >= 0x4e00 && c <= 0x9fff);
            var cmd = new ParsedCommand { backend = "rules" };

            // Colour mentions in order of appearance.
            var hits = new List<(int pos, string color)>();
            foreach (var w in Blue) for (int i = s.IndexOf(w, StringComparison.Ordinal); i >= 0; i = s.IndexOf(w, i + 1, StringComparison.Ordinal)) hits.Add((i, "blue"));
            foreach (var w in Orange) for (int i = s.IndexOf(w, StringComparison.Ordinal); i >= 0; i = s.IndexOf(w, i + 1, StringComparison.Ordinal)) hits.Add((i, "orange"));
            var ordered = new List<string>();
            foreach (var h in hits.OrderBy(h => h.pos))
                if (ordered.Count == 0 || ordered[ordered.Count - 1] != h.color) ordered.Add(h.color);

            if (Cancel.Any(w => s.Contains(w)) && ordered.Count == 0) { cmd.intent = "cancel"; cmd.reply = zh ? "好的，我原地待命。" : "OK, holding position."; }
            else if (Layout.Any(w => s.Contains(w))) { cmd.intent = "new_layout"; cmd.reply = zh ? "好的，换一个布局。" : "OK, new layout."; }
            else if (Status.Any(w => s.Contains(w))) { cmd.intent = "status"; cmd.reply = zh ? "我正在执行当前任务。" : "I am working on my current task."; }
            else if (ordered.Count > 0)
            {
                cmd.intent = "fetch";
                cmd.targets = ordered.ToArray();
                cmd.reply = zh ? $"收到，去拿{string.Join("、", ordered.Select(c => c == "blue" ? "蓝色" : "橙色"))}电芯。"
                               : $"Got it: fetching {string.Join(" then ", ordered)}.";
            }
            else { cmd.intent = "clarify"; cmd.reply = zh ? "我没听懂，要拿蓝色还是橙色电芯？" : "Sorry, blue or orange cell?"; }
            return cmd;
        }
    }
}
