using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TeamRescue.Coop
{
    public enum CoopMode { Fixed, Adaptive, Language }

    /// <summary>
    /// Human–AI co-op round: the generator needs BOTH cells. The human and the trained robot
    /// split the work; a breaker pad switches the cables off while someone stands on it.
    /// The coordinator decides which cell the robot pursues, according to the mode:
    ///   Fixed    – robot always starts with blue and only reacts after a cell is taken;
    ///   Adaptive – robot infers the human's goal from their motion and takes the other cell;
    ///   Language – robot waits for typed orders (local LLM layer).
    /// Batch modes: -coopsim N OUT.csv -mode fixed|adaptive [-seed S]   (simulated partner)
    ///              -study P01                                          (participant session)
    ///              -coopshot DIR                                       (screenshots)
    /// </summary>
    public sealed class CoopCoordinator : MonoBehaviour
    {
        public RescueBotAgent robot;
        public HumanAvatar human;
        public RescueArena arena;
        public Transform breakerPad;
        public Renderer padRenderer;
        public Material padIdle, padActive;
        public CoopMode mode = CoopMode.Adaptive;
        public float roundTimeout = 60f;

        // goal inference
        public float beta = 2.5f, decay = 0.96f, switchThreshold = 0.72f;
        public readonly float[] GoalProb = { 0.5f, 0.5f };
        private readonly float[] logit = new float[2], prevDist = new float[2];
        private float inferTimer;

        // round state
        private int round;
        private float roundTime, pauseTimer;
        private bool roundActive, waitingForSpace;
        private int lostTarget, switches, commands, humanCells, robotCells, robotShockStart;
        private int inferTicks, inferCorrect;
        private bool manualHold;
        private readonly List<CellColor> manualQueue = new();
        private bool targetLostFlag;
        private float approachGrace;
        private bool robotOwnsLast;
        private int lastPhaseShockBase = -1;
        public string PlanText { get; private set; } = "";
        public bool BreakerOn { get; private set; }

        // batch / study
        private SimulatedPartner sim;
        private int simRoundsLeft = -1;
        private string csvPath, participant = "";
        private readonly List<CoopMode> studyPlan = new();
        private readonly List<string> studyLabels = new();
        private int studyIndex = -1, roundsPerCondition = 3, roundInCondition;
        private bool studyDone;
        private string screenshotDir;
        private readonly List<string> rows = new();
        private GUIStyle box, small, big;

        private void Awake()
        {
            Language.LanguageCommander.CoopOverride = OnLanguageOrder;
        }

        private void Start()
        {
            robot.coopMode = true;
            robot.externalTarget = true;
            robot.MaxStep = 0;
            robot.celebrateSeconds = 0f;
            robot.EpisodeBegan += OnRoundStart;
            robot.CoopInstalled += c => OnInstalled(c, byRobot: true);
            human.Installed += c => OnInstalled(c, byRobot: false);

            var args = Environment.GetCommandLineArgs().ToList();
            ShockFX.Enabled = !Application.isBatchMode && !args.Contains("-coopsim");
            int i;
            if ((i = args.IndexOf("-mode")) >= 0 && i + 1 < args.Count)
                mode = args[i + 1].ToLowerInvariant() switch { "fixed" => CoopMode.Fixed, "language" => CoopMode.Language, _ => CoopMode.Adaptive };
            int seed = (i = args.IndexOf("-seed")) >= 0 && i + 1 < args.Count ? int.Parse(args[i + 1]) : 1;

            if ((i = args.IndexOf("-coopsim")) >= 0 && i + 2 < args.Count)
            {
                simRoundsLeft = int.Parse(args[i + 1]);
                csvPath = args[i + 2];
                sim = new SimulatedPartner(seed);
                human.brain = sim;
                Time.timeScale = 4f;
                participant = "sim";
            }
            else if ((i = args.IndexOf("-coopshot")) >= 0 && i + 1 < args.Count)
            {
                screenshotDir = args[i + 1];
                sim = new SimulatedPartner(seed, 0.7f, 0f);
                human.brain = sim;
                StartCoroutine(Shots());
            }
            else if ((i = args.IndexOf("-study")) >= 0 && i + 1 < args.Count)
            {
                participant = args[i + 1];
                SetupStudy(participant);
            }
            Header();
            if (studyIndex < 0) BeginRound();
            if ((i = args.IndexOf("-snap")) >= 0 && i + 1 < args.Count) StartCoroutine(StudySmokeTest(args[i + 1]));
            if ((i = args.IndexOf("-shocktest")) >= 0 && i + 1 < args.Count) StartCoroutine(ShockTest(args[i + 1]));
        }

        /// <summary>Test only: walk the player into the nearest cable, capture the shock feedback, quit.</summary>
        private System.Collections.IEnumerator ShockTest(string dir)
        {
            Directory.CreateDirectory(dir);
            yield return new WaitForSeconds(1.5f);
            var cable = arena.GetComponentsInChildren<HazardStrip>().OrderBy(h => Vector3.Distance(h.transform.position, human.transform.position)).First();
            human.transform.position = cable.transform.position + Vector3.up * 0.05f + cable.transform.forward * 1.2f;
            arena.SetHazardsForcedOff(false);
            while (!cable.IsActive) yield return null;
            human.transform.position = cable.transform.position;
            yield return new WaitForSeconds(0.15f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "shock-1-zap.png"));
            yield return new WaitForSeconds(0.7f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "shock-2-stunned.png"));
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        /// <summary>Test only: capture the study start screen, press "SPACE" in code, capture a round, quit.</summary>
        private System.Collections.IEnumerator StudySmokeTest(string dir)
        {
            Directory.CreateDirectory(dir);
            yield return new WaitForSeconds(2f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "study-1-start.png"));
            yield return new WaitForSeconds(0.5f);
            if (waitingForSpace) BeginRound();
            yield return new WaitForSeconds(3f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "study-2-round.png"));
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // ---------------- rounds ----------------
        public void BeginRound()
        {
            waitingForSpace = false;
            robot.EndEpisode(); // generates a new layout -> OnRoundStart
        }

        private void OnRoundStart()
        {
            if (!waitingForSpace) round++;
            roundTime = 0; lostTarget = 0; switches = 0; commands = 0; humanCells = 0; robotCells = 0;
            inferTicks = 0; inferCorrect = 0; targetLostFlag = false; approachGrace = 0f; robotOwnsLast = false; lastPhaseShockBase = -1;
            manualHold = false; manualQueue.Clear();
            robotShockStart = robot.HazardHits;

            var avoid = arena.cells.Select(c => c.transform.localPosition).ToList();
            avoid.Add(arena.generator.localPosition);
            avoid.Add(robot.transform.localPosition);
            human.ResetForRound(arena.FreePoint(avoid, 3f));
            avoid.Add(human.transform.localPosition);
            Vector3 pad = arena.FreePoint(avoid, 3f);
            float edge = arena.halfSize - 1.1f;
            if (Mathf.Abs(pad.x) > Mathf.Abs(pad.z)) pad.x = Mathf.Sign(pad.x) * edge; else pad.z = Mathf.Sign(pad.z) * edge;
            breakerPad.localPosition = new Vector3(pad.x, 0.02f, pad.z);
            arena.SetHazardsForcedOff(false);
            BreakerOn = false;

            for (int k = 0; k < 2; k++) { logit[k] = 0; prevDist[k] = DistHuman(arena.cells[k]); GoalProb[k] = 0.5f; }
            sim?.NewRound(human);
            if (waitingForSpace) { roundActive = false; Hold("waiting for the round to start"); return; }

            switch (mode)
            {
                case CoopMode.Fixed: SetRobotTarget(CellColor.Blue, "fixed plan: blue first"); break;
                case CoopMode.Adaptive: SetRobotTarget(BestAssignment(), "split by distance"); break;
                default: robot.holdPosition = true; PlanText = "waiting for your orders"; break;
            }
            roundActive = !waitingForSpace;
        }

        /// <summary>Choose the robot's cell so that robot + human walk the least in total.</summary>
        private CellColor BestAssignment()
        {
            float rb = DistRobot(arena.Cell(CellColor.Blue)), ro = DistRobot(arena.Cell(CellColor.Orange));
            float hb = DistHuman(arena.Cell(CellColor.Blue)), ho = DistHuman(arena.Cell(CellColor.Orange));
            return rb + ho <= ro + hb ? CellColor.Blue : CellColor.Orange;
        }

        private void SetRobotTarget(CellColor c, string why)
        {
            if (robot.Carried != null) return; // never swap while carrying
            robot.holdPosition = false;
            robot.SetTarget(c);
            targetLostFlag = false;
            PlanText = $"fetching {c.ToString().ToUpperInvariant()} ({why})";
        }

        private void Hold(string why)
        {
            robot.holdPosition = true;
            PlanText = why;
        }

        private void OnInstalled(CellColor c, bool byRobot)
        {
            if (!roundActive) return;
            if (byRobot) robotCells++; else humanCells++;
            if (arena.BothInstalled) { arena.SetPowered(true); FinishRound(true); return; }
            if (byRobot) robot.holdPosition = true;
            if (byRobot && manualQueue.Count > 0)
            {
                var next = manualQueue[0]; manualQueue.RemoveAt(0);
                if (arena.Cell(next).Available) SetRobotTarget(next, "your order");
            }
        }

        private void FinishRound(bool success)
        {
            roundActive = false;
            robot.holdPosition = true;
            arena.SetHazardsForcedOff(false);
            string condition = studyIndex >= 0 ? studyLabels[studyIndex] : mode.ToString();
            rows.Add(string.Join(",", new[]
            {
                participant, condition, mode.ToString(), round.ToString(), roundInCondition.ToString(),
                F(roundTime), success ? "1" : "0", human.Shocks.ToString(), (robot.HazardHits - robotShockStart).ToString(),
                lostTarget.ToString(), switches.ToString(), commands.ToString(), humanCells.ToString(), robotCells.ToString(),
                inferTicks > 0 ? F(inferCorrect / (float)inferTicks) : "",
            }));
            Flush();
            pauseTimer = sim != null && simRoundsLeft > 0 ? 0.05f : 1.8f;
            if (simRoundsLeft > 0 && --simRoundsLeft == 0) { WriteSummary(); Application.Quit(); }
            if (studyIndex >= 0) AdvanceStudy();
        }

        private void FixedUpdate()
        {
            if (!roundActive)
            {
                if (pauseTimer > 0 && (pauseTimer -= Time.fixedDeltaTime) <= 0 && !waitingForSpace && !studyDone && screenshotDir == null) BeginRound();
                return;
            }
            float dt = Time.fixedDeltaTime;
            roundTime += dt;
            if (roundTime > roundTimeout) { FinishRound(false); return; }

            // Breaker pad: standing on it switches every cable off.
            bool on = HumanAvatar.Flat(human.transform.localPosition - breakerPad.localPosition) < 1.1f;
            if (on != BreakerOn)
            {
                BreakerOn = on;
                arena.SetHazardsForcedOff(on);
                if (padRenderer != null) padRenderer.sharedMaterial = on ? padActive : padIdle;
            }

            inferTimer += dt;
            if (inferTimer < 0.1f) return;
            inferTimer = 0;
            UpdateInference();
            DecideRobot();
        }

        // Bayesian-style goal inference: a cell becomes more likely when the human makes
        // progress towards it; old evidence decays so the robot follows changes of mind.
        private void UpdateInference()
        {
            for (int k = 0; k < 2; k++)
            {
                var c = arena.cells[k];
                float d = DistHuman(c);
                if (!c.Available) { logit[k] = -8f; prevDist[k] = d; continue; }
                logit[k] = logit[k] * decay + beta * (prevDist[k] - d);
                prevDist[k] = d;
            }
            float m = Mathf.Max(logit[0], logit[1]);
            float e0 = Mathf.Exp(logit[0] - m), e1 = Mathf.Exp(logit[1] - m);
            GoalProb[0] = e0 / (e0 + e1); GoalProb[1] = 1 - GoalProb[0];

            if (sim != null && sim.TrueGoal.HasValue && human.Carried == null && arena.cells.All(c => c.Available) && human.Velocity.magnitude > 1f)
            {
                inferTicks++;
                int guess = GoalProb[0] >= GoalProb[1] ? 0 : 1;
                if (guess == (int)sim.TrueGoal.Value) inferCorrect++;
            }
        }

        private void DecideRobot()
        {
            if (robot.Carried != null) { PlanText = $"carrying {robot.Carried.color.ToString().ToUpperInvariant()} to the generator"; return; }
            var target = arena.Cell(robot.Target);
            var others = arena.cells.Where(c => c.Available).ToList();

            // The robot's cell was taken by the human.
            if (!target.Available && !robot.holdPosition && !targetLostFlag)
            {
                if (target.Carrier == (UnityEngine.Object)human) lostTarget++;
                targetLostFlag = true;
            }
            if (mode == CoopMode.Language || manualHold)
            {
                if (!target.Available && !robot.holdPosition) Hold("its cell is gone — waiting for your orders");
                return;
            }
            if (others.Count == 0) { Hold("nothing left to fetch"); return; }

            if (mode == CoopMode.Fixed)
            {
                if (!target.Available || robot.holdPosition) SetRobotTarget(others[0].color, "only one left");
                return;
            }

            // Adaptive
            if (others.Count == 2 && human.Carried == null)
            {
                int h = GoalProb[0] >= GoalProb[1] ? 0 : 1;
                var want = (CellColor)(1 - h);
                if (GoalProb[h] > switchThreshold && human.Velocity.magnitude > 1f && (robot.Target != want || robot.holdPosition))
                {
                    if (!robot.holdPosition) switches++;
                    SetRobotTarget(want, $"you seem to go for {((CellColor)h).ToString().ToLowerInvariant()}");
                }
                else if (robot.holdPosition) SetRobotTarget(BestAssignment(), "split by distance");
            }
            else if (others.Count == 1)
            {
                var last = others[0];
                // Only yield when the human is actually walking towards the last cell. With one cell
                // left its probability is always ~100 %, so the belief alone would make the robot wait
                // forever for a human who is standing still (e.g. holding the breaker for it).
                Vector3 toCell = last.transform.position - human.transform.position; toCell.y = 0;
                Vector3 hv = human.Velocity; hv.y = 0;
                bool approaching = human.Carried == null && hv.magnitude > 1f
                                   && Vector3.Dot(hv.normalized, toCell.normalized) > 0.6f
                                   && DistHuman(last) < DistRobot(last);
                // A short pause (waiting for a cable to go dark) keeps the claim alive; standing still
                // for longer than the grace period, or holding the breaker, hands the cell to the robot.
                approachGrace = approaching ? 1.5f : approachGrace - 0.1f;
                if (human.StunRemaining > 0f) approachGrace = 0f; // stunned: the robot starts moving, but yields again if the human resumes
                if (lastPhaseShockBase < 0) lastPhaseShockBase = human.Shocks;
                // Holding the breaker, or being shocked twice going for this cell, is a clear hand-over.
                if (BreakerOn || human.Shocks - lastPhaseShockBase >= 2) robotOwnsLast = true;
                bool humanGoingThere = !robotOwnsLast && approachGrace > 0f && human.Carried == null;
                if (humanGoingThere) Hold($"you are heading for the {last.color.ToString().ToLowerInvariant()} cell — leaving it to you");
                else if (robot.Target != last.color || robot.holdPosition || !target.Available) SetRobotTarget(last.color, "last cell");
            }
            else if (others.Count == 2 && human.Carried != null)
            {
                // The human carries something already installed? Then both remain; pick the closer one.
                if (!target.Available || robot.holdPosition) SetRobotTarget(BestAssignment(), "split by distance");
            }
        }

        // ---------------- language hook ----------------
        private bool OnLanguageOrder(List<CellColor> targets)
        {
            if (!roundActive) return true;
            commands++;
            if (targets.Count == 0) { manualHold = true; manualQueue.Clear(); Hold("holding (your order)"); return true; }
            manualHold = true; // an explicit order replaces the automatic plan for this round
            manualQueue.Clear();
            manualQueue.AddRange(targets.Skip(1));
            var first = targets[0];
            if (!arena.Cell(first).Available)
            {
                if (manualQueue.Count > 0) { first = manualQueue[0]; manualQueue.RemoveAt(0); }
                else { Hold($"the {first.ToString().ToLowerInvariant()} cell is not available"); return true; }
            }
            SetRobotTarget(first, "your order");
            return true;
        }

        // ---------------- study session ----------------
        private void SetupStudy(string id)
        {
            int n = int.TryParse(new string(id.Where(char.IsDigit).ToArray()), out var v) ? v : 0;
            var orders = new[]
            {
                new[] { CoopMode.Fixed, CoopMode.Adaptive, CoopMode.Language },
                new[] { CoopMode.Adaptive, CoopMode.Language, CoopMode.Fixed },
                new[] { CoopMode.Language, CoopMode.Fixed, CoopMode.Adaptive },
            };
            studyPlan.Add(CoopMode.Adaptive); studyLabels.Add("practice");
            foreach (var m in orders[n % 3]) { studyPlan.Add(m); studyLabels.Add(Label(m)); }
            studyIndex = 0;
            mode = studyPlan[0];
            roundsPerCondition = 1; // practice: one round
            roundInCondition = 0;
            waitingForSpace = true;
            csvPath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "StudyData", $"{id}.csv");
        }

        private static string Label(CoopMode m) => m switch { CoopMode.Fixed => "A", CoopMode.Adaptive => "B", _ => "C" };

        private void AdvanceStudy()
        {
            roundInCondition++;
            if (roundInCondition < roundsPerCondition) return;
            studyIndex++;
            roundInCondition = 0;
            if (studyIndex >= studyPlan.Count) { studyDone = true; return; }
            mode = studyPlan[studyIndex];
            roundsPerCondition = 3;
            waitingForSpace = true;
        }

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null || Language.LanguageCommander.IsTyping) return;
            if (waitingForSpace && !roundActive && kb.spaceKey.wasPressedThisFrame) BeginRound();
            if (studyIndex < 0 && sim == null)
            {
                if (kb.f1Key.wasPressedThisFrame) { mode = CoopMode.Fixed; BeginRound(); }
                if (kb.f2Key.wasPressedThisFrame) { mode = CoopMode.Adaptive; BeginRound(); }
                if (kb.f3Key.wasPressedThisFrame) { mode = CoopMode.Language; BeginRound(); }
                if (kb.nKey.wasPressedThisFrame) BeginRound();
            }
            if (kb.escapeKey.wasPressedThisFrame) Application.Quit();
        }

        // ---------------- logging ----------------
        private void Header()
        {
            if (csvPath == null) csvPath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "StudyData", "free-play.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath)));
            if (!File.Exists(csvPath))
                File.WriteAllText(csvPath, "participant,condition,mode,round,round_in_condition,seconds,completed,human_shocks,robot_shocks,robot_lost_target,robot_switches,commands,human_cells,robot_cells,inference_accuracy\n");
        }

        private void Flush()
        {
            if (rows.Count == 0) return;
            File.AppendAllText(csvPath, string.Join("\n", rows) + "\n");
            rows.Clear();
        }

        private void WriteSummary()
        {
            var lines = File.ReadAllLines(csvPath).Skip(1).Select(l => l.Split(',')).Where(c => c.Length > 14 && c[0] == "sim").ToList();
            float Avg(int col) => lines.Count == 0 ? 0 : lines.Average(c => float.Parse(c[col], CultureInfo.InvariantCulture));
            var acc = lines.Where(c => c[14] != "").Select(c => float.Parse(c[14], CultureInfo.InvariantCulture)).ToList();
            string json = "{\n" +
                $"  \"mode\": \"{mode}\",\n  \"rounds\": {lines.Count},\n" +
                $"  \"completion_rate\": {F(Avg(6))},\n  \"mean_seconds\": {F(Avg(5))},\n" +
                $"  \"robot_lost_target_per_round\": {F(Avg(9))},\n  \"robot_switches_per_round\": {F(Avg(10))},\n" +
                $"  \"human_shocks_per_round\": {F(Avg(7))},\n  \"robot_shocks_per_round\": {F(Avg(8))},\n" +
                $"  \"robot_cells_share\": {F(Avg(13) / Mathf.Max(0.001f, Avg(12) + Avg(13)))},\n" +
                $"  \"inference_accuracy\": {(acc.Count > 0 ? F(acc.Average()) : "null")}\n}}\n";
            File.WriteAllText(Path.ChangeExtension(csvPath, ".summary.json"), json);
        }

        private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);
        private float DistHuman(CellPart c) => HumanAvatar.Flat(c.transform.position - human.transform.position);
        private float DistRobot(CellPart c) => HumanAvatar.Flat(c.transform.position - robot.transform.position);

        // ---------------- screenshots ----------------
        private System.Collections.IEnumerator Shots()
        {
            Directory.CreateDirectory(screenshotDir);
            int n = 0;
            foreach (var m in new[] { CoopMode.Adaptive, CoopMode.Adaptive, CoopMode.Fixed })
            {
                mode = m;
                BeginRound();
                yield return new WaitForSeconds(0.9f); ScreenCapture.CaptureScreenshot(Path.Combine(screenshotDir, $"{++n:00}-{m}-start.png"));
                yield return new WaitForSeconds(1.0f); ScreenCapture.CaptureScreenshot(Path.Combine(screenshotDir, $"{++n:00}-{m}-inferring.png"));
                float t = 0;
                while (roundActive && t < 25f) { t += Time.deltaTime; yield return null; }
                yield return new WaitForSeconds(0.3f); ScreenCapture.CaptureScreenshot(Path.Combine(screenshotDir, $"{++n:00}-{m}-done.png"));
                yield return new WaitForSeconds(0.5f);
            }
            Application.Quit();
        }

        // ---------------- HUD ----------------
        private void OnGUI()
        {
            GUI.depth = 0;
            box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, richText = true, wordWrap = true, fontSize = 15, padding = new RectOffset(12, 12, 10, 10) };
            small ??= new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 };
            big ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = true, fontSize = 22, padding = new RectOffset(20, 20, 20, 20) };

            bool col = Language.LanguageCommander.Active;
            float colW = col ? Screen.width * Language.LanguageCommander.ColumnFraction : 0;
            var r = col ? new Rect(10, 10, colW - 20, Language.LanguageCommander.HudHeight) : new Rect(14, 14, 470, 170);
            string modeName = mode switch { CoopMode.Fixed => "Fixed plan", CoopMode.Adaptive => "Adaptive (reads your movement)", _ => "Language orders" };
            string cond = studyIndex >= 0 ? $"Condition {studyLabels[Mathf.Min(studyIndex, studyLabels.Count - 1)]}" : modeName;
            string text = $"<b>CO-OP</b> · {cond} · Round {round} · {roundTime:0.0}s\n" +
                          $"You carry: {(human.Carried == null ? "nothing" : human.Carried.color.ToString())}{(human.StunRemaining > 0 ? " <color=#f88>SHOCKED</color>" : "")}" +
                          $"   Generator: {arena.cells.Count(c => c.Installed)}/2\n" +
                          $"Robot: {PlanText}\n" +
                          (BreakerOn ? "<color=#8f8>Cables OFF — you are on the breaker</color>" : "<color=#ffd27a>Yellow pad = cut the cables while you stand on it</color>");
            GUI.Box(r, text, box);

            if (mode == CoopMode.Adaptive)
            {
                // Make the robot's belief visible: what it thinks the human is going for.
                float w = 300, x = col ? colW + 14 : Screen.width - w - 14, y = 14;
                GUI.Box(new Rect(x, y, w, 74), "", box);
                GUI.Label(new Rect(x + 10, y + 6, w, 20), "<color=#9fe>Robot's guess of your goal</color>", small);
                for (int k = 0; k < 2; k++)
                {
                    float yy = y + 30 + k * 20;
                    GUI.Label(new Rect(x + 10, yy, 60, 20), k == 0 ? "Blue" : "Orange", small);
                    GUI.color = new Color(0.2f, 0.22f, 0.26f); GUI.DrawTexture(new Rect(x + 70, yy + 4, 170, 12), Texture2D.whiteTexture);
                    GUI.color = k == 0 ? new Color(0.25f, 0.6f, 1f) : new Color(1f, 0.55f, 0.15f);
                    GUI.DrawTexture(new Rect(x + 70, yy + 4, 170 * GoalProb[k], 12), Texture2D.whiteTexture);
                    GUI.color = Color.white;
                    GUI.Label(new Rect(x + 246, yy, 50, 20), $"{GoalProb[k] * 100:0}%", small);
                }
            }

            if (studyIndex >= 0 && (waitingForSpace || studyDone))
            {
                string msg = studyDone
                    ? "<b>Session complete — thank you!</b>\n\nPlease fill in the questionnaire."
                    : studyLabels[studyIndex] == "practice"
                        ? "<b>Practice round</b>\n\nMove with WASD. Walk into a cell to pick it up, walk to the generator to install it.\nThe generator needs BOTH cells. The robot helps you.\nStanding on the yellow pad switches the cables off.\n\nPress SPACE to start."
                        : $"<b>Condition {studyLabels[studyIndex]}</b> — 3 rounds\n\n" + (mode switch
                        {
                            CoopMode.Fixed => "The robot follows its own fixed plan.",
                            CoopMode.Adaptive => "The robot tries to work out what you are doing.",
                            _ => "The robot waits for your typed orders (type below and press Enter).",
                        }) + "\n\nPress SPACE to start.";
                var pr = new Rect(colW + (Screen.width - colW) / 2 - 320, Screen.height / 2 - 170, 640, 340);
                GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.97f);
                GUI.DrawTexture(pr, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Box(pr, msg, big);
            }
            else if (!roundActive && round > 0 && sim == null)
                GUI.Box(new Rect(colW + (Screen.width - colW) / 2 - 200, Screen.height / 2 - 40, 400, 70), arena.BothInstalled ? "<color=#7f9>POWER RESTORED</color>" : "<color=#f99>Time is up</color>", big);
        }
    }
}
