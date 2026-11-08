using System;
using System.Collections.Generic;
using System.Linq;

namespace TeamRescue.Language
{
    /// <summary>One referring expression extracted by the language model.</summary>
    [Serializable]
    public sealed class CellRef
    {
        public string by = "unspecified"; // blue|orange|round|square|closer_to_robot|farther_from_robot|closer_to_generator|farther_from_generator|left|right|unspecified
        public bool negated;
    }

    /// <summary>Raw output of the language model (semantic parse, not yet grounded).</summary>
    [Serializable]
    public sealed class SemanticParse
    {
        public string intent = "other"; // fetch | cancel | new_layout | status | other
        public CellRef[] refs = Array.Empty<CellRef>();
        public string reply = "";
    }

    /// <summary>
    /// Symbolic grounding: turns referring expressions into concrete cells using the real
    /// scene geometry. The language model never compares distances itself.
    /// </summary>
    public static class Grounding
    {
        public static ParsedCommand Ground(SemanticParse p, SceneFacts f, string utterance)
        {
            string lang = DetectLanguage(utterance);
            var cmd = new ParsedCommand { intent = p.intent ?? "other", reply = p.reply ?? "" };
            if (cmd.intent != "fetch") return cmd;

            var targets = new List<string>();
            foreach (var r in p.refs ?? Array.Empty<CellRef>())
            {
                string c = Resolve(r?.by, f);
                if (c == null) { targets.Clear(); break; }
                if (r.negated) c = c == "blue" ? "orange" : "blue";
                if (targets.Count == 0 || targets[^1] != c) targets.Add(c);
            }
            if (targets.Count == 0)
            {
                cmd.intent = "clarify";
                cmd.reply = lang switch { "zh" => "要拿哪一个：蓝色还是橙色？", "fr" => "Laquelle : la bleue ou l'orange ?", _ => "Which one: blue or orange?" };
                return cmd;
            }
            cmd.targets = targets.ToArray();
            cmd.reply = Confirm(targets, lang);
            return cmd;
        }

        public static string Resolve(string by, SceneFacts f)
        {
            switch ((by ?? "").Trim().ToLowerInvariant())
            {
                case "blue": case "round": return "blue";
                case "orange": case "square": return "orange";
                case "closer_to_robot": return f.blueToRobot <= f.orangeToRobot ? "blue" : "orange";
                case "farther_from_robot": return f.blueToRobot > f.orangeToRobot ? "blue" : "orange";
                case "closer_to_generator": return f.blueToGenerator <= f.orangeToGenerator ? "blue" : "orange";
                case "farther_from_generator": return f.blueToGenerator > f.orangeToGenerator ? "blue" : "orange";
                case "left": return f.blueSide == "left" ? "blue" : "orange";
                case "right": return f.blueSide == "right" ? "blue" : "orange";
                default: return null; // unspecified -> ask the player
            }
        }

        public static string DetectLanguage(string s)
        {
            s ??= "";
            if (s.Any(c => c >= 0x4e00 && c <= 0x9fff)) return "zh";
            string l = " " + s.ToLowerInvariant() + " ";
            string[] fr = { " la ", " le ", " les ", " pas ", " moi", " va ", " tu ", " une ", " celle", " puis ", "d'abord", "arrête", "ramène", "prends", "apporte", "é", "è", "ê", "à" };
            return fr.Any(w => l.Contains(w)) ? "fr" : "en";
        }

        private static string Confirm(List<string> t, string lang)
        {
            string Name(string c, string l) => l switch
            {
                "zh" => c == "blue" ? "蓝色（圆柱）" : "橙色（方块）",
                "fr" => c == "blue" ? "la bleue" : "l'orange",
                _ => c == "blue" ? "the blue cell" : "the orange cell",
            };
            return lang switch
            {
                "zh" => "收到，" + (t.Count > 1 ? "依次去拿" : "去拿") + string.Join("，再拿", t.Select(c => Name(c, "zh"))) + "电芯。",
                "fr" => "D'accord, je vais chercher " + string.Join(", puis ", t.Select(c => Name(c, "fr"))) + ".",
                _ => "Got it: fetching " + string.Join(", then ", t.Select(c => Name(c, "en"))) + ".",
            };
        }
    }
}
