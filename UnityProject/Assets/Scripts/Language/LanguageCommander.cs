using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TeamRescue.Language
{
    /// <summary>
    /// Natural-language command layer: player text -> local LLM (or rule fallback) ->
    /// structured command -> goal for the trained RL policy. Also hosts batch modes:
    ///   -langshot DIR                 scripted conversation with screenshots
    ///   -evalnlu TESTS.json OUT.json  parser accuracy on a labelled test set (add -rules for baseline)
    ///   -model NAME                   Ollama model (default qwen2.5:3b)
    /// </summary>
    public sealed class LanguageCommander : MonoBehaviour
    {
        public RescueBotAgent agent;
        public static bool IsTyping { get; private set; }
        /// <summary>Co-op scene: orders go to the coordinator instead (empty list = hold).</summary>
        public static Func<List<CellColor>, bool> CoopOverride;
        /// <summary>True when the dialogue column is shown (the game view uses the rest of the screen).</summary>
        public static bool Active { get; private set; }
        public const float ColumnFraction = 0.34f;
        public const float HudHeight = 150f;
        private Vector2 scroll;
        private int lastLogCount;

        private ICommandParser parser;
        private readonly List<CellColor> queue = new();
        private readonly List<(string who, string text, string meta)> log = new();
        private readonly List<(string player, string robot)> history = new();
        private string input = "";
        private bool busy;
        private GUIStyle box, line, meta, field;

        private void Start()
        {
            var args = Environment.GetCommandLineArgs().ToList();
            int i = args.IndexOf("-model");
            var ollama = new OllamaParser();
            if (i >= 0 && i + 1 < args.Count) ollama.Model = args[i + 1];
            parser = args.Contains("-rules") ? new RuleParser() : ollama;

            if ((i = args.IndexOf("-evalnlu")) >= 0 && i + 2 < args.Count)
            {
                StartCoroutine(EvaluateParser(args[i + 1], args[i + 2]));
                return;
            }
            if (agent == null || args.Contains("-eval") || args.Contains("-autoshot") || args.Contains("-coopsim") || args.Contains("-coopshot")) { enabled = false; return; }
            Active = true;
            if (Camera.main != null) Camera.main.rect = new Rect(ColumnFraction, 0f, 1f - ColumnFraction, 1f);
            if (CoopOverride == null) agent.holdPosition = true;
            agent.EpisodeFinished += OnEpisodeFinished;
            Say("robot", "Systems online. Tell me what to do. / 请下达指令。", parser.Name);
            if ((i = args.IndexOf("-langshot")) >= 0 && i + 1 < args.Count)
                StartCoroutine(LangShot(args[i + 1]));
        }

        // ---------------- command execution ----------------
        public void Submit(string text)
        {
            if (busy || string.IsNullOrWhiteSpace(text)) return;
            StartCoroutine(Handle(text.Trim()));
        }

        private IEnumerator Handle(string text)
        {
            busy = true;
            Say("player", text, "");
            var facts = SceneFacts.From(agent, agent.holdPosition);
            ParsedCommand cmd = null;
            yield return parser.Parse(text, facts, history.TakeLast(3).ToList(), c => cmd = c);
            Execute(cmd);
            string metaText = $"{cmd.backend} · {cmd.latency:0.0}s · intent={cmd.intent}" +
                              (cmd.targets != null && cmd.targets.Length > 0 ? $" [{string.Join(", ", cmd.targets)}]" : "");
            Say("robot", string.IsNullOrEmpty(cmd.reply) ? "OK." : cmd.reply, metaText);
            history.Add((text, $"{{\"intent\":\"{cmd.intent}\",\"targets\":[{string.Join(",", (cmd.targets ?? Array.Empty<string>()).Select(t => $"\"{t}\""))}],\"reply\":\"{cmd.reply?.Replace("\"", "'")}\"}}"));
            busy = false;
        }

        private void Execute(ParsedCommand cmd)
        {
            switch (cmd.intent)
            {
                case "fetch":
                    var targets = cmd.TargetColors();
                    if (targets.Count == 0) return;
                    if (CoopOverride != null && CoopOverride(targets)) return;
                    queue.Clear();
                    queue.AddRange(targets);
                    agent.holdPosition = false;
                    agent.SetTarget(queue[0]);
                    break;
                case "cancel":
                    if (CoopOverride != null && CoopOverride(new List<CellColor>())) return;
                    queue.Clear();
                    agent.holdPosition = true;
                    break;
                case "new_layout":
                    agent.EndEpisode();
                    break;
            }
        }

        private void OnEpisodeFinished(bool success, int shocks, int wrong, float seconds)
        {
            if (!success || queue.Count == 0) return;
            queue.RemoveAt(0);
            if (queue.Count > 0)
            {
                agent.SetTarget(queue[0]);
                Say("robot", $"Power restored. Next: {queue[0].ToString().ToLowerInvariant()} cell.", "queue");
            }
            else
            {
                agent.holdPosition = true;
                Say("robot", "Power restored. Waiting for the next order. / 供电恢复，等待指令。", "queue empty");
            }
        }

        private void Say(string who, string text, string metaText)
        {
            log.Add((who, text, metaText));
            if (log.Count > 40) log.RemoveAt(0);
        }

        // ---------------- UI ----------------
        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && !IsTyping && (kb.enterKey.wasPressedThisFrame || kb.tKey.wasPressedThisFrame))
                focusRequested = true;
        }

        private bool focusRequested;

        private void OnGUI()
        {
            if (agent == null) return;
            GUI.depth = 10; // behind the status HUD drawn by DemoController
            box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(12, 12, 10, 10) };
            line ??= new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true, richText = true };
            meta ??= new GUIStyle(GUI.skin.label) { fontSize = 12, richText = true };
            field ??= new GUIStyle(GUI.skin.textField) { fontSize = 18, padding = new RectOffset(10, 10, 8, 8) };

            float colW = Screen.width * ColumnFraction;
            GUI.color = new Color(0.04f, 0.05f, 0.07f, 1f);
            GUI.DrawTexture(new Rect(0, 0, colW, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            var area = new Rect(10, HudHeight + 20, colW - 20, Screen.height - HudHeight - 30);
            GUI.Box(area, "", box);
            GUI.Label(new Rect(area.x + 12, area.y + 6, area.width, 22), "<color=#8a96a8>CONVERSATION · local LLM → goal → trained policy</color>", meta);
            if (log.Count != lastLogCount || busy) { scroll.y = float.MaxValue; lastLogCount = log.Count; }
            GUILayout.BeginArea(new Rect(area.x + 8, area.y + 28, area.width - 16, area.height - 86));
            scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            foreach (var (who, text, m) in log)
            {
                string name = who == "player" ? "<color=#ffd27a><b>You</b></color>" : "<color=#7fe8ff><b>Robot</b></color>";
                GUILayout.Label($"{name}  {text}", line);
                if (!string.IsNullOrEmpty(m)) GUILayout.Label($"<color=#8a96a8>{m}</color>", meta);
                GUILayout.Space(4);
            }
            if (busy) GUILayout.Label("<color=#8a96a8><i>Robot is thinking…</i></color>", meta);
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            var fieldRect = new Rect(area.x + 12, area.yMax - 50, area.width - 24, 38);
            GUI.SetNextControlName("cmd");
            var e = Event.current;
            bool submit = e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "cmd";
            input = GUI.TextField(fieldRect, input, 200, field);
            if (string.IsNullOrEmpty(input) && GUI.GetNameOfFocusedControl() != "cmd")
                GUI.Label(new Rect(fieldRect.x + 12, fieldRect.y + 9, fieldRect.width, 24), "<color=#8a96a8>Type an order + Enter · “拿那个方的” · “get the closer one”</color>", meta);
            if (submit) { Submit(input); input = ""; e.Use(); }
            if (focusRequested) { GUI.FocusControl("cmd"); focusRequested = false; }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && GUI.GetNameOfFocusedControl() == "cmd") { GUI.FocusControl(null); e.Use(); }
            IsTyping = GUI.GetNameOfFocusedControl() == "cmd";
        }

        // ---------------- batch: scripted conversation ----------------
        private IEnumerator LangShot(string dir)
        {
            Directory.CreateDirectory(dir);
            int n = 0;
            void Shot(string name) => ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{++n:00}-{name}.png"));
            // Warm the model up so the first visible reply is not a cold start.
            if (parser is OllamaParser) { ParsedCommand warm = null; yield return parser.Parse("hello", SceneFacts.From(agent, true), null, c => warm = c); }
            yield return new WaitForSeconds(1f); Shot("waiting");

            string[] script =
            {
                "帮我把那个方形的电池拿过来",
                "Now get the one that is closer to you",
                "先拿蓝色的，再拿橙色的",
                "拿电池",
                "你现在在干什么？",
                "ne prends pas la bleue",
            };
            foreach (var s in script)
            {
                Submit(s);
                while (busy) yield return null;
                yield return new WaitForSeconds(0.4f); Shot("reply");
                yield return new WaitForSeconds(2.5f); Shot("acting");
                float t = 0;
                while (!agent.holdPosition && t < 14f) { t += Time.deltaTime; yield return null; }
                yield return new WaitForSeconds(0.6f);
            }
            Shot("end");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // ---------------- batch: parser evaluation ----------------
        [Serializable] private class TestItem { public string id, category, utterance, expect_intent; public string[] expect_targets; public SceneFacts facts; }
        [Serializable] private class TestSet { public TestItem[] items; }

        private IEnumerator EvaluateParser(string testsPath, string outPath)
        {
            var set = JsonUtility.FromJson<TestSet>(File.ReadAllText(testsPath));
            if (parser is OllamaParser) { ParsedCommand warm = null; yield return parser.Parse("hello", new SceneFacts(), null, c => warm = c); }
            var rows = new StringBuilder();
            var perCat = new Dictionary<string, (int ok, int n)>();
            int ok = 0, fallbacks = 0; float lat = 0;
            foreach (var it in set.items)
            {
                ParsedCommand cmd = null;
                yield return parser.Parse(it.utterance, it.facts ?? new SceneFacts(), null, c => cmd = c);
                var exp = (it.expect_targets ?? Array.Empty<string>()).Select(x => x.ToLowerInvariant()).ToArray();
                var got = (cmd.targets ?? Array.Empty<string>()).Select(x => x.ToLowerInvariant()).ToArray();
                bool pass = cmd.intent == it.expect_intent && (it.expect_intent != "fetch" || exp.SequenceEqual(got));
                if (pass) ok++;
                if (cmd.backend.StartsWith("rules (")) fallbacks++;
                lat += cmd.latency;
                perCat.TryGetValue(it.category, out var c0);
                perCat[it.category] = (c0.ok + (pass ? 1 : 0), c0.n + 1);
                if (rows.Length > 0) rows.Append(",\n");
                rows.Append($"    {{\"id\":\"{it.id}\",\"category\":\"{it.category}\",\"pass\":{(pass ? "true" : "false")},\"intent\":\"{cmd.intent}\",\"targets\":[{string.Join(",", got.Select(x => $"\"{x}\""))}],\"latency\":{cmd.latency.ToString("0.00", CultureInfo.InvariantCulture)},\"reply\":\"{Esc(cmd.reply)}\"}}");
            }
            var cats = string.Join(",\n", perCat.Select(kv => $"    \"{kv.Key}\": {{\"correct\": {kv.Value.ok}, \"total\": {kv.Value.n}}}"));
            string json = "{\n" +
                $"  \"parser\": \"{parser.Name}\",\n  \"items\": {set.items.Length},\n" +
                $"  \"accuracy\": {(ok / (float)set.items.Length).ToString("0.000", CultureInfo.InvariantCulture)},\n" +
                $"  \"mean_latency_s\": {(lat / set.items.Length).ToString("0.00", CultureInfo.InvariantCulture)},\n" +
                $"  \"fallbacks\": {fallbacks},\n  \"by_category\": {{\n{cats}\n  }},\n  \"results\": [\n{rows}\n  ]\n}}\n";
            File.WriteAllText(outPath, json, new UTF8Encoding(false));
            Application.Quit();
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
    }
}
