using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.MLAgents.Policies;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TeamRescue
{
    /// <summary>
    /// Play scene: the human commands the robot (1 = blue cell, 2 = orange cell) and the
    /// trained policy carries the command out. Also supports batch modes for the blog:
    ///   -autoshot DIR                 capture a scripted sequence of screenshots, then quit
    ///   -eval N OUT.json [-baseline]  run N random episodes fast and write statistics
    ///   -manual                       keyboard drives the robot (to compare with the AI)
    /// </summary>
    public sealed class DemoController : MonoBehaviour
    {
        public RescueBotAgent agent;
        public bool showHud = true;
        private GUIStyle style;
        private string mode = "AI policy (trained PPO)";
        private string banner = "";

        private void Start()
        {
            var args = new List<string>(System.Environment.GetCommandLineArgs());
            ShockFX.Enabled = !Application.isBatchMode && !args.Contains("-eval");
            if (agent == null) return;
            int i;
            if ((i = args.IndexOf("-manual")) >= 0)
            {
                agent.GetComponent<BehaviorParameters>().BehaviorType = BehaviorType.HeuristicOnly;
                mode = "Human keyboard (WASD)";
            }
            if ((i = args.IndexOf("-eval")) >= 0 && i + 2 < args.Count)
            {
                bool baseline = args.Contains("-baseline");
                StartCoroutine(Evaluate(int.Parse(args[i + 1]), args[i + 2], baseline));
                return;
            }
            agent.SetTarget(CellColor.Blue);
            if ((i = args.IndexOf("-autoshot")) >= 0 && i + 1 < args.Count)
                StartCoroutine(AutoShot(args[i + 1]));
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || agent == null || Language.LanguageCommander.IsTyping) return;
            if (kb.digit1Key.wasPressedThisFrame) { agent.holdPosition = false; agent.SetTarget(CellColor.Blue); }
            if (kb.digit2Key.wasPressedThisFrame) { agent.holdPosition = false; agent.SetTarget(CellColor.Orange); }
            if (kb.rKey.wasPressedThisFrame) agent.EndEpisode();
            if (kb.hKey.wasPressedThisFrame) showHud = !showHud;
            if (kb.escapeKey.wasPressedThisFrame) Application.Quit();
        }

        private IEnumerator AutoShot(string dir)
        {
            Directory.CreateDirectory(dir);
            int n = 0;
            void Shot(string name) => ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{++n:00}-{name}.png"));
            banner = "Command: fetch the BLUE cell";
            yield return new WaitForSeconds(1.0f); Shot("command-blue");
            yield return new WaitForSeconds(1.2f); Shot("moving");
            yield return new WaitForSeconds(1.5f); Shot("carrying");
            yield return new WaitForSeconds(2.5f); Shot("later");
            agent.SetTarget(CellColor.Orange);
            banner = "Command changed: fetch the ORANGE cell";
            yield return new WaitForSeconds(0.8f); Shot("command-orange");
            yield return new WaitForSeconds(1.5f); Shot("orange-moving");
            yield return new WaitForSeconds(2.5f); Shot("orange-later");
            yield return new WaitForSeconds(1.0f);
            Application.Quit();
        }

        private IEnumerator Evaluate(int episodes, string outPath, bool baseline)
        {
            var bp = agent.GetComponent<BehaviorParameters>();
            if (baseline)
            {
                agent.scriptedBaseline = true;
                bp.BehaviorType = BehaviorType.HeuristicOnly;
                mode = "Scripted baseline (straight line)";
            }
            agent.externalTarget = false;
            agent.celebrateSeconds = 0f;
            agent.MaxStep = 2000;
            showHud = true;
            Time.timeScale = 8f;
            int done = 0, success = 0, shocks = 0, wrongTouch = 0;
            float secs = 0f;
            agent.EpisodeFinished += (ok, hz, wt, s) =>
            {
                done++;
                if (ok) { success++; secs += s; }
                shocks += hz;
                if (wt > 0) wrongTouch++;
            };
            agent.EndEpisode();
            done = 0; success = 0; shocks = 0; wrongTouch = 0; secs = 0f;
            while (done < episodes)
            {
                banner = $"Evaluating {done}/{episodes}";
                yield return null;
            }
            string json = "{\n" +
                $"  \"policy\": \"{(baseline ? "scripted_straight_line" : "trained_ppo_onnx")}\",\n" +
                $"  \"episodes\": {done},\n" +
                $"  \"success_rate\": {success / (float)done:0.000},\n" +
                $"  \"mean_seconds_when_successful\": {(success > 0 ? secs / success : 0):0.00},\n" +
                $"  \"shocks_per_episode\": {shocks / (float)done:0.000},\n" +
                $"  \"wrong_cell_touch_rate\": {wrongTouch / (float)done:0.000},\n" +
                $"  \"hazard_cables\": 2,\n  \"timeout_seconds\": 40\n}}\n";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            File.WriteAllText(outPath, json);
            Application.Quit();
        }

        private void OnGUI()
        {
            if (!showHud || agent == null) return;
            GUI.depth = 0;
            style ??=new GUIStyle(GUI.skin.box) { fontSize = 18, alignment = TextAnchor.UpperLeft, richText = true, padding = new RectOffset(12, 12, 10, 10) };
            string carried = agent.Carried == null ? "nothing" : agent.Carried.color.ToString();
            string text =
                $"<b>Team Rescue · Lab 3</b>   <color=#9fe>{mode}</color>\n" +
                (agent.holdPosition ? "Task: <b>waiting for orders</b>   (type below, or 1 = Blue · 2 = Orange)\n"
                                    : $"Task: fetch the <b>{agent.Target}</b> cell   (1 = Blue · 2 = Orange · R = new layout)\n") +
                $"Carrying: {carried}{(agent.StunRemaining > 0 ? "   <color=#f88>SHOCKED</color>" : "")}\n" +
                $"Power restored: {agent.Installs}   Shocks: {agent.HazardHits}\n" +
                $"Last event: {agent.LastEvent}";
            if (Language.LanguageCommander.Active)
            {
                var s = new GUIStyle(style) { fontSize = 15, wordWrap = true };
                GUI.Box(new Rect(10, 10, Screen.width * Language.LanguageCommander.ColumnFraction - 20, Language.LanguageCommander.HudHeight), text, s);
            }
            else GUI.Box(new Rect(14, 14, 700, 140), text, style);
            if (agent.IsCelebrating)
            {
                var c = new GUIStyle(style) { fontSize = 30, alignment = TextAnchor.MiddleCenter };
                GUI.Box(new Rect(Screen.width / 2f - 230, Screen.height / 2f - 40, 460, 64), "<color=#7f9>POWER RESTORED</color>", c);
            }
            if (!string.IsNullOrEmpty(banner))
            {
                var b = new GUIStyle(style) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
                GUI.Box(new Rect(Screen.width / 2f - 260, Screen.height - 70, 520, 48), banner, b);
            }
        }
    }
}
