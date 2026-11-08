using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TeamRescue.Coop
{
    public enum CoopMode { Fixed, Adaptive, Language }

    /// <summary>
    /// Human–AI co-op round built around a real dependency: the generator stands in a corner room
    /// sealed by two cables that stay live unless someone holds a breaker. There is one breaker
    /// outside and one inside, so whoever is on one side has to open the "door" for the other.
    /// Nobody can deliver a cell alone. The trained policy is unchanged; this class decides what
    /// the robot does:
    ///   Fixed    – fetches blue first; holds a breaker only when the player asks (E key);
    ///   Adaptive – infers the player's goal, takes the other cell, and goes to a breaker on its
    ///              own when it sees the player carrying a cell towards the fence;
    ///   Language – waits for typed orders ("get blue", "hold the breaker").
    /// In every mode the robot asks for help when it is stuck at the fence.
    /// Batch modes: -coopsim N OUT.csv -mode fixed|adaptive [-seed S]   (simulated partner)
    ///              -study P01                                          (participant session)
    ///              -coopshot DIR                                       (screenshots)
    /// </summary>
    public sealed class CoopCoordinator : MonoBehaviour
    {
        public RescueBotAgent robot;
        public HumanAvatar human;
        public RescueArena arena;
        public Transform breakerPad; // the outer breaker
        public Renderer padRenderer;
        public Material padIdle, padActive;
        public CoopMode mode = CoopMode.Adaptive;
        public float roundTimeout = 90f;
        public float threeStars = 18f, twoStars = 30f;

        // goal inference
        public float beta = 2.5f, decay = 0.96f, switchThreshold = 0.72f;
        public readonly float[] GoalProb = { 0.5f, 0.5f };
        private readonly float[] logit = new float[2], prevDist = new float[2];
        private float inferTimer;

        // station geometry (arena-local frame rotated by quarter turns each round)
        private const float RoomCorner = 1f, PadRadius = 1.1f, FenceNear = 2.6f;
        private Quaternion frame = Quaternion.identity;
        private Transform innerPad;
        private Renderer innerPadRenderer;

        // round state
        private int round;
        private float roundTime, pauseTimer;
        private bool roundActive, waitingForSpace;
        private int lostTarget, switches, commands, humanCells, robotCells, robotShockStart;
        private int inferTicks, inferCorrect;
        private int robotHelpRequests, humanHelpRequests, robotAssists, humanAssists;
        private float humanFenceWait, robotFenceWait, humanFenceTime;
        private bool manualHold;
        private readonly List<CellColor> manualQueue = new();
        private bool targetLostFlag;
        private float approachGrace;
        private bool humanInside, robotInside, humanOnPad, robotOnPad, robotAsking;
        private string lastResult = "";
        private float bestTime = float.MaxValue;

        // help: the robot holding a breaker for the player
        private bool helpActive, helpRequested, helpStartInside;
        private float helpTimer, helpAfterCross;

        public string PlanText { get; private set; } = "";
        public string RobotSays { get; private set; } = "";
        public bool BreakerOn { get; private set; }
        /// <summary>The robot is at the fence and its goal is on the other side.</summary>
        public bool RobotNeedsDoor { get; private set; }
        public bool RobotHelping => helpActive;

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
        private GUIStyle box, small, big, bubble;
        private AudioSource chime;
        private AudioClip chimeClip;

        private const string CsvHeader = "participant,condition,mode,round,round_in_condition,seconds,completed,human_shocks,robot_shocks,robot_lost_target,robot_switches,commands,human_cells,robot_cells,inference_accuracy,robot_help_requests,human_help_requests,robot_assists,human_assists,human_fence_wait,robot_fence_wait";

        private void Awake()
        {
            Language.LanguageCommander.CoopOverride = OnLanguageOrder;
            Language.LanguageCommander.CoopTextHook = OnLanguageText;
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

            var ip = Instantiate(breakerPad.gameObject, breakerPad.parent);
            ip.name = "InnerBreakerPad";
            innerPad = ip.transform;
            innerPadRenderer = ip.GetComponentsInChildren<Renderer>().FirstOrDefault(r => r.name == "Disc");
            chime = gameObject.AddComponent<AudioSource>();
            chime.playOnAwake = false; chime.volume = 0.35f;
            chimeClip = BuildChime();

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
                sim = new SimulatedPartner(seed) { coord = this };
                human.brain = sim;
                Time.timeScale = 4f;
                participant = "sim";
            }
            else if ((i = args.IndexOf("-coopshot")) >= 0 && i + 1 < args.Count)
            {
                screenshotDir = args[i + 1];
                sim = new SimulatedPartner(seed, 0.7f, 0f) { coord = this };
                human.brain = sim;
                StartCoroutine(Shots());
            }
            else if ((i = args.IndexOf("-study")) >= 0 && i + 1 < args.Count)
            {
                participant = args[i + 1];
                SetupStudy(participant);
            }
            if ((i = args.IndexOf("-trace")) >= 0 && i + 1 < args.Count)
            {
                tracePath = args[i + 1];
                File.WriteAllText(tracePath, "round,t,hx,hz,h_in,h_carry,h_stun,h_pad,rx,rz,r_in,r_carry,r_override,r_hold,r_pause,r_pad,help,breaker,installed,padx,padz,gx,gz,target,tx,tz,plan\n");
            }
            Header();
            if (studyIndex < 0) BeginRound();
            if ((i = args.IndexOf("-snap")) >= 0 && i + 1 < args.Count) StartCoroutine(StudySmokeTest(args[i + 1]));
            if ((i = args.IndexOf("-shocktest")) >= 0 && i + 1 < args.Count) StartCoroutine(ShockTest(args[i + 1]));
        }

        // ---------------- geometry ----------------
        private Vector3 Local(Vector3 framePoint) => frame * framePoint;
        private Vector3 Frame(Vector3 local) => Quaternion.Inverse(frame) * local;

        /// <summary>True if an arena-local point is inside the sealed generator room.</summary>
        public bool InRoom(Vector3 local)
        {
            var f = Frame(local);
            return f.x > RoomCorner + 0.45f && f.z > RoomCorner + 0.45f;
        }

        public Transform PadOnSide(bool inside) => inside ? innerPad : breakerPad;

        /// <summary>Next point on a fence-free path between two points on the same side (arena-local).
        /// Inside the room every straight line is safe; outside, a line may clip the room's corner,
        /// in which case the path goes via a point just outside that corner.</summary>
        public Vector3 Route(Vector3 from, Vector3 to)
        {
            if (InRoom(from)) return to;
            Vector3 a = Frame(from), b = Frame(to);
            const float clear = RoomCorner - 0.9f;
            bool clips = false;
            for (int k = 1; k < 20 && !clips; k++)
            {
                var q = Vector3.Lerp(a, b, k / 20f);
                clips = q.x > clear && q.z > clear;
            }
            if (!clips) return to;
            var corner = new Vector3(RoomCorner - 1.6f, 0, RoomCorner - 1.6f);
            return (a - corner).sqrMagnitude < 0.36f ? to : Local(corner);
        }

        private float FenceDist(Vector3 world)
        {
            float best = float.MaxValue;
            foreach (var h in arena.hazards)
            {
                if (h == null || !h.gameObject.activeSelf) continue;
                var l = h.transform.InverseTransformPoint(world);
                float dx = Mathf.Max(Mathf.Abs(l.x) - h.length * 0.5f, 0f), dz = Mathf.Max(Mathf.Abs(l.z) - h.width * 0.5f, 0f);
                best = Mathf.Min(best, Mathf.Sqrt(dx * dx + dz * dz));
            }
            return best;
        }

        private static bool OnPad(Vector3 local, Transform pad) => HumanAvatar.Flat(local - pad.localPosition) < PadRadius;

        private Vector3 SampleOutside(List<Vector3> used, float minDist, float maxDiagonal)
        {
            float m = arena.halfSize - 1.3f;
            Vector3 best = new Vector3(-m, 0, -m);
            for (int k = 0; k < 200; k++)
            {
                var f = new Vector3(UnityEngine.Random.Range(-m, m), 0, UnityEngine.Random.Range(-m, m));
                if (f.x + f.z > maxDiagonal) continue; // stay on the far side so paths outside never cut through the room
                best = f;
                if (used.All(u => (u - f).sqrMagnitude >= minDist * minDist)) return f;
            }
            return best;
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
            inferTicks = 0; inferCorrect = 0; targetLostFlag = false; approachGrace = 0f;
            robotHelpRequests = 0; humanHelpRequests = 0; robotAssists = 0; humanAssists = 0;
            humanFenceWait = 0; robotFenceWait = 0; humanFenceTime = 0;
            helpActive = false; robotAsking = false; RobotNeedsDoor = false;
            manualHold = false; manualQueue.Clear();
            robotShockStart = robot.HazardHits;
            task = RobotTask.Idle; robot.goalOverride = false; robot.pauseForFence = false;
            BuildStation();

            for (int k = 0; k < 2; k++) { logit[k] = 0; prevDist[k] = DistHuman(arena.cells[k]); GoalProb[k] = 0.5f; }
            sim?.NewRound(human);
            if (waitingForSpace) { roundActive = false; Hold("waiting for the round to start"); return; }

            switch (mode)
            {
                case CoopMode.Fixed: SetRobotTarget(CellColor.Blue, "fixed plan: blue first"); break;
                case CoopMode.Adaptive: SetRobotTarget(BestAssignment(), "split by distance"); break;
                default: Hold("waiting for your orders"); RobotSays = "Tell me what to do."; break;
            }
            roundActive = !waitingForSpace;
        }

        /// <summary>Lay out the sealed generator room in a random corner, both breakers, cells and players.</summary>
        private void BuildStation()
        {
            frame = Quaternion.Euler(0, 90f * UnityEngine.Random.Range(0, 4), 0);
            float yaw = frame.eulerAngles.y;
            var h0 = arena.hazards[0]; var h1 = arena.hazards[1];
            foreach (var h in new[] { h0, h1 }) { h.gameObject.SetActive(true); h.forcedOn = true; }
            h0.transform.localPosition = Local(new Vector3(RoomCorner + 3f, 0, RoomCorner));
            h0.transform.localRotation = Quaternion.Euler(0, yaw, 0);
            h1.transform.localPosition = Local(new Vector3(RoomCorner, 0, RoomCorner + 3f));
            h1.transform.localRotation = Quaternion.Euler(0, yaw + 90f, 0);
            arena.SetHazardsForcedOff(false);
            foreach (var h in new[] { h0, h1 }) h.relockUntil = 0f;

            arena.generator.localPosition = Local(new Vector3(5.3f, 0, 5.3f));
            arena.generator.localRotation = Quaternion.Euler(0, yaw - 135f, 0);
            bool padOnX = UnityEngine.Random.value < 0.5f;
            innerPad.localPosition = Local(padOnX ? new Vector3(5.2f, 0, 2.6f) : new Vector3(2.6f, 0, 5.2f)) + Vector3.up * 0.02f;

            var used = new List<Vector3>();
            Vector3 pad = SampleOutside(used, 0f, -3.5f); used.Add(pad);
            breakerPad.localPosition = Local(pad) + Vector3.up * 0.02f;
            foreach (var c in arena.cells) { var p = SampleOutside(used, 3f, -0.5f); used.Add(p); c.Release(Local(p)); }
            var r = SampleOutside(used, 2.5f, -0.5f); used.Add(r);
            robot.transform.localPosition = Local(r);
            human.ResetForRound(Local(SampleOutside(used, 2.5f, -0.5f)));
            humanInside = robotInside = false;
            BreakerOn = false;
            UpdatePadLooks();
        }

        /// <summary>Choose the robot's cell so that robot + human walk the least in total.</summary>
        private CellColor BestAssignment()
        {
            float rb = DistRobot(arena.Cell(CellColor.Blue)), ro = DistRobot(arena.Cell(CellColor.Orange));
            float hb = DistHuman(arena.Cell(CellColor.Blue)), ho = DistHuman(arena.Cell(CellColor.Orange));
            return rb + ho <= ro + hb ? CellColor.Blue : CellColor.Orange;
        }

        // What the robot is doing; ApplyControl turns it into policy inputs every physics step.
        private enum RobotTask { Idle, Fetch, Deliver, HoldPad }
        private RobotTask task = RobotTask.Idle;

        private void SetRobotTarget(CellColor c, string why)
        {
            if (robot.Carried != null) { Deliver(); return; } // never swap while carrying
            if (task == RobotTask.Fetch && robot.Target == c) return; // keep the current plan text
            if (robot.Target != c) robot.SetTarget(c);
            task = RobotTask.Fetch;
            targetLostFlag = false;
            PlanText = $"fetching {c.ToString().ToUpperInvariant()} ({why})";
            RobotSays = $"Getting {c.ToString().ToLowerInvariant()}.";
            ApplyControl(0f);
        }

        private void Hold(string why)
        {
            task = RobotTask.Idle;
            PlanText = why;
            RobotSays = "Standing by.";
            ApplyControl(0f);
        }

        private void Deliver()
        {
            task = RobotTask.Deliver;
            PlanText = $"carrying {robot.Carried.color.ToString().ToUpperInvariant()} to the generator";
            RobotSays = $"Taking {robot.Carried.color.ToString().ToLowerInvariant()} to the generator.";
        }

        private void GoHoldBreaker()
        {
            task = RobotTask.HoldPad;
            PlanText = helpRequested ? "holding the breaker (you asked)" : "holding the breaker for you";
            RobotSays = robotOnPad ? "Breaker held — go!" : helpRequested ? "On my way to the breaker." : "I'll get the breaker for you.";
        }

        /// <summary>A spot on the robot's side, 1.6 m from the nearest stretch of fence (arena-local).</summary>
        private Vector3 WaitSpot(Vector3 local, bool inside)
        {
            var f = Frame(local);
            float c = RoomCorner, off = inside ? 1.6f : -1.6f, lo = c + (inside ? 1.2f : 0.6f), hi = arena.halfSize - 0.8f;
            var onH0 = new Vector3(Mathf.Clamp(f.x, lo, hi), 0, c + off);
            var onH1 = new Vector3(c + off, 0, Mathf.Clamp(f.z, lo, hi));
            return Local((onH0 - f).sqrMagnitude <= (onH1 - f).sqrMagnitude ? onH0 : onH1);
        }

        /// <summary>Turn the current task into inputs for the unchanged policy. The trained network fetches
        /// and delivers; walking up to the fence and stepping onto a breaker are scripted, because a fence
        /// that never switches off and an untagged spot on the floor are things it was never trained on.</summary>
        private void ApplyControl(float dt)
        {
            Vector3 rp = robot.transform.localPosition;
            bool asking = false;
            RobotNeedsDoor = false;
            robot.pauseForFence = false;
            robot.goalOverride = false;
            robot.holdPosition = task == RobotTask.Idle;

            if (task == RobotTask.HoldPad)
            {
                var pad = PadOnSide(robotInside);
                robot.goalOverride = true;
                robot.overrideGoalLocal = Route(rp, new Vector3(pad.localPosition.x, 0, pad.localPosition.z));
            }
            else if (task == RobotTask.Fetch || task == RobotTask.Deliver)
            {
                var tc = arena.Cell(robot.Target);
                bool delivering = task == RobotTask.Deliver && robot.Carried != null;
                if (delivering || tc.Available)
                {
                    Vector3 goal = delivering ? arena.generator.localPosition : tc.transform.localPosition;
                    if (InRoom(goal) != robotInside)
                    {
                        float fd = FenceDist(robot.transform.position);
                        RobotNeedsDoor = fd < (BreakerOn ? FenceNear + 1.5f : FenceNear);
                        if (BreakerOn)
                        {
                            // Cross straight to the other side while the fence is dark, then let the policy finish.
                            if (fd < FenceNear + 1.5f) { robot.goalOverride = true; robot.overrideGoalLocal = WaitSpot(rp, !robotInside); }
                        }
                        else if (fd < FenceNear) { robot.pauseForFence = true; asking = true; }
                        else
                        {
                            robot.goalOverride = true;
                            robot.overrideGoalLocal = Route(rp, WaitSpot(rp, robotInside));
                        }
                    }
                }
            }

            if (asking && !robotAsking)
            {
                robotHelpRequests++;
                if (chime != null && chimeClip != null && sim == null) chime.PlayOneShot(chimeClip);
            }
            robotAsking = asking;
            if (asking) robotFenceWait += dt;
        }

        private void OnInstalled(CellColor c, bool byRobot)
        {
            if (!roundActive) return;
            if (byRobot) robotCells++; else humanCells++;
            if (arena.BothInstalled) { arena.SetPowered(true); FinishRound(true); return; }
            if (byRobot) { task = RobotTask.Idle; ApplyControl(0f); }
            if (byRobot && manualQueue.Count > 0)
            {
                var next = manualQueue[0]; manualQueue.RemoveAt(0);
                if (arena.Cell(next).Available) SetRobotTarget(next, "your order");
            }
        }

        private void FinishRound(bool success)
        {
            roundActive = false;
            task = RobotTask.Idle;
            ApplyControl(0f);
            helpActive = false;
            arena.SetHazardsForcedOff(false);
            if (success) bestTime = Mathf.Min(bestTime, roundTime);
            int stars = !success ? 0 : roundTime <= threeStars ? 3 : roundTime <= twoStars ? 2 : 1;
            lastResult = success
                ? $"<color=#7f9>POWER RESTORED</color>  {roundTime:0.0} s  <color=#ffd84a>{new string('★', stars)}{new string('☆', 3 - stars)}</color>\n" +
                  $"<size=16>Robot opened the fence for you {robotAssists}× · you opened it for the robot {humanAssists}×</size>"
                : "<color=#f99>BLACKOUT — backup power ran out</color>";
            string condition = studyIndex >= 0 ? studyLabels[studyIndex] : mode.ToString();
            rows.Add(string.Join(",", new[]
            {
                participant, condition, mode.ToString(), round.ToString(), roundInCondition.ToString(),
                F(roundTime), success ? "1" : "0", human.Shocks.ToString(), (robot.HazardHits - robotShockStart).ToString(),
                lostTarget.ToString(), switches.ToString(), commands.ToString(), humanCells.ToString(), robotCells.ToString(),
                inferTicks > 0 ? F(inferCorrect / (float)inferTicks) : "",
                robotHelpRequests.ToString(), humanHelpRequests.ToString(), robotAssists.ToString(), humanAssists.ToString(),
                F(humanFenceWait), F(robotFenceWait),
            }));
            Flush();
            pauseTimer = sim != null && simRoundsLeft > 0 ? 0.05f : 3f;
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

            // Breakers: anyone standing on either pad switches the fence off.
            Vector3 hp = human.transform.localPosition, rp = robot.transform.localPosition;
            humanOnPad = OnPad(hp, breakerPad) || OnPad(hp, innerPad);
            robotOnPad = OnPad(rp, breakerPad) || OnPad(rp, innerPad);
            bool on = humanOnPad || robotOnPad;
            if (on != BreakerOn) { BreakerOn = on; arena.SetHazardsForcedOff(on); }
            UpdatePadLooks();

            // Who crossed the fence, and who held the door for them?
            bool hIn = InRoom(hp), rIn = InRoom(rp);
            if (hIn != humanInside) { if (robotOnPad) robotAssists++; humanInside = hIn; }
            if (rIn != robotInside) { if (humanOnPad) humanAssists++; robotInside = rIn; }

            bool humanAtFence = FenceDist(human.transform.position) < FenceNear;
            humanFenceTime = humanAtFence ? humanFenceTime + dt : 0f;
            if (humanAtFence && human.Carried != null && !humanInside && !BreakerOn) humanFenceWait += dt;

            inferTimer += dt;
            if (inferTimer >= 0.1f)
            {
                float step = inferTimer;
                inferTimer = 0;
                UpdateInference();
                DecideRobot(step);
            }
            ApplyControl(dt);
            if (tracePath != null && (traceTimer += dt) >= 0.25f) { traceTimer = 0f; Trace(); }
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

        /// <summary>Does the player need someone on a breaker to get through the fence?</summary>
        private bool HumanNeedsDoor(bool anticipate)
        {
            float fd = FenceDist(human.transform.position);
            bool wantsIn = human.Carried != null && !humanInside;
            bool wantsOut = humanInside && human.Carried == null && arena.cells.Any(c => c.Available) && fd < 2f && humanFenceTime > 0.6f;
            if (wantsOut) return true;
            if (!wantsIn) return false;
            if (fd < FenceNear) return true;
            if (!anticipate) return false;
            Vector3 toGen = arena.generator.position - human.transform.position; toGen.y = 0;
            Vector3 v = human.Velocity; v.y = 0;
            if (!(v.magnitude > 1f && Vector3.Dot(v.normalized, toGen.normalized) > 0.5f && fd < 6f)) return false;
            // Leave early only if the robot would reach the breaker about when the player reaches the fence;
            // otherwise keep working and come once the player is actually waiting.
            float robotEta = HumanAvatar.Flat(PadOnSide(robotInside).localPosition - robot.transform.localPosition) / robot.moveSpeed;
            float humanEta = fd / human.moveSpeed;
            return robotEta <= humanEta + 1f;
        }

        private void StartHelp(bool requested)
        {
            if (helpActive) { helpRequested |= requested; return; }
            helpActive = true;
            helpRequested = requested;
            helpTimer = 0f;
            helpAfterCross = -1f;
            helpStartInside = humanInside;
        }

        /// <summary>Player asked for the breaker (E key, typed order, or simulated partner).</summary>
        public void RequestHelp()
        {
            if (!roundActive) return;
            humanHelpRequests++;
            StartHelp(true);
        }

        private void UpdateHelp(float dt)
        {
            helpTimer += dt;
            if (humanInside != helpStartInside && helpAfterCross < 0f) helpAfterCross = 0.6f; // let them clear the fence
            if (helpAfterCross >= 0f && (helpAfterCross -= dt) <= 0f) { helpActive = false; return; }
            if (helpTimer > 12f) { helpActive = false; return; }
            if (!helpRequested && helpAfterCross < 0f && helpTimer > 1.5f && !HumanNeedsDoor(true)) helpActive = false;
        }

        private void DecideRobot(float dt)
        {
            if (helpActive) UpdateHelp(dt);
            if (!helpActive && mode == CoopMode.Adaptive && !humanOnPad && HumanNeedsDoor(true)) StartHelp(false);
            if (helpActive) { GoHoldBreaker(); return; }

            if (robot.Carried != null) { Deliver(); return; }
            var target = arena.Cell(robot.Target);
            var others = arena.cells.Where(c => c.Available).ToList();

            // The robot's cell was taken by the human.
            if (!target.Available && task == RobotTask.Fetch && !targetLostFlag)
            {
                if (target.Carrier == (UnityEngine.Object)human) lostTarget++;
                targetLostFlag = true;
            }
            if (mode == CoopMode.Language || manualHold)
            {
                if (task == RobotTask.HoldPad || task == RobotTask.Deliver)
                {
                    // Back from holding a breaker (or just delivered): resume the last order, or wait for a new one.
                    if (manualHold && target.Available) { task = RobotTask.Fetch; PlanText = $"fetching {robot.Target.ToString().ToUpperInvariant()} (your order)"; }
                    else Hold("waiting for your orders");
                }
                if (!target.Available && task == RobotTask.Fetch) Hold("its cell is gone — waiting for your orders");
                if (task == RobotTask.Idle) RobotSays = "Waiting for your orders.";
                else RobotSays = $"Getting {robot.Target.ToString().ToLowerInvariant()}.";
                return;
            }
            if (others.Count == 0) { Hold("nothing left to fetch"); return; }

            if (mode == CoopMode.Fixed)
            {
                var want = arena.Cell(CellColor.Blue).Available ? CellColor.Blue : others[0].color;
                SetRobotTarget(want, want == CellColor.Blue ? "fixed plan: blue first" : "only one left");
                return;
            }

            // Adaptive
            if (others.Count == 2 && human.Carried == null)
            {
                int h = GoalProb[0] >= GoalProb[1] ? 0 : 1;
                var want = (CellColor)(1 - h);
                if (GoalProb[h] > switchThreshold && human.Velocity.magnitude > 1f && robot.Target != want)
                {
                    switches++;
                    SetRobotTarget(want, $"you seem to go for {((CellColor)h).ToString().ToLowerInvariant()}");
                }
                else SetRobotTarget(robot.Target, "split by distance");
            }
            else if (others.Count == 1)
            {
                var last = others[0];
                // Only yield when the human is actually walking towards the last cell.
                Vector3 toCell = last.transform.position - human.transform.position; toCell.y = 0;
                Vector3 hv = human.Velocity; hv.y = 0;
                bool approaching = human.Carried == null && hv.magnitude > 1f
                                   && Vector3.Dot(hv.normalized, toCell.normalized) > 0.6f
                                   && DistHuman(last) < DistRobot(last);
                approachGrace = approaching ? 1.5f : approachGrace - dt;
                if (approachGrace > 0f) Hold($"you are heading for the {last.color.ToString().ToLowerInvariant()} cell — leaving it to you");
                else SetRobotTarget(last.color, "last cell");
            }
            else SetRobotTarget(BestAssignment(), "split by distance");
        }

        // ---------------- language hooks ----------------
        private bool OnLanguageOrder(List<CellColor> targets)
        {
            if (!roundActive) return true;
            commands++;
            helpActive = false;
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

        private static readonly string[] BreakerWords = { "breaker", "pad", "switch", "lever", "door", "开关", "踏板", "断电", "电闸", "闸", "开门", "interrupteur", "porte" };

        /// <summary>"Hold the breaker" is not a cell order, so a keyword rule handles it before the LLM.</summary>
        private string OnLanguageText(string text)
        {
            string t = text.ToLowerInvariant();
            if (!BreakerWords.Any(t.Contains)) return null;
            bool zh = text.Any(ch => ch >= 0x4e00 && ch <= 0x9fff);
            if (!roundActive) return zh ? "这一局还没开始。" : "The round has not started yet.";
            commands++;
            RequestHelp();
            return zh ? "好的，我去按住开关，你过去吧。" : "On my way to the breaker — go when the fence goes dark.";
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
            if (kb.eKey.wasPressedThisFrame && sim == null) RequestHelp();
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
            if (File.Exists(csvPath) && File.ReadLines(csvPath).FirstOrDefault() != CsvHeader)
                File.Move(csvPath, Path.ChangeExtension(csvPath, $".old-{DateTime.Now:yyyyMMddHHmmss}.csv"));
            if (!File.Exists(csvPath)) File.WriteAllText(csvPath, CsvHeader + "\n");
        }

        private void Flush()
        {
            if (rows.Count == 0) return;
            File.AppendAllText(csvPath, string.Join("\n", rows) + "\n");
            rows.Clear();
        }

        private void WriteSummary()
        {
            var lines = File.ReadAllLines(csvPath).Skip(1).Select(l => l.Split(',')).Where(c => c.Length > 20 && c[0] == "sim").ToList();
            float Avg(int col) => lines.Count == 0 ? 0 : lines.Average(c => float.Parse(c[col], CultureInfo.InvariantCulture));
            var acc = lines.Where(c => c[14] != "").Select(c => float.Parse(c[14], CultureInfo.InvariantCulture)).ToList();
            string json = "{\n" +
                $"  \"mode\": \"{mode}\",\n  \"rounds\": {lines.Count},\n" +
                $"  \"completion_rate\": {F(Avg(6))},\n  \"mean_seconds\": {F(Avg(5))},\n" +
                $"  \"robot_lost_target_per_round\": {F(Avg(9))},\n  \"robot_switches_per_round\": {F(Avg(10))},\n" +
                $"  \"human_shocks_per_round\": {F(Avg(7))},\n  \"robot_shocks_per_round\": {F(Avg(8))},\n" +
                $"  \"robot_cells_share\": {F(Avg(13) / Mathf.Max(0.001f, Avg(12) + Avg(13)))},\n" +
                $"  \"robot_help_requests\": {F(Avg(15))},\n  \"human_help_requests\": {F(Avg(16))},\n" +
                $"  \"robot_assists\": {F(Avg(17))},\n  \"human_assists\": {F(Avg(18))},\n" +
                $"  \"human_fence_wait_s\": {F(Avg(19))},\n  \"robot_fence_wait_s\": {F(Avg(20))},\n" +
                $"  \"inference_accuracy\": {(acc.Count > 0 ? F(acc.Average()) : "null")}\n}}\n";
            File.WriteAllText(Path.ChangeExtension(csvPath, ".summary.json"), json);
        }

        private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);
        private float DistHuman(CellPart c) => HumanAvatar.Flat(c.transform.position - human.transform.position);
        private float DistRobot(CellPart c) => HumanAvatar.Flat(c.transform.position - robot.transform.position);

        // ---------------- tests and screenshots ----------------
        private string tracePath;
        private float traceTimer;

        /// <summary>Debug: one line every 0.25 s of game time (positions in the room frame).</summary>
        private void Trace()
        {
            Vector3 h = Frame(human.transform.localPosition), r = Frame(robot.transform.localPosition);
            string line = string.Join(",", round, F(roundTime), F(h.x), F(h.z), humanInside ? 1 : 0, human.Carried != null ? human.Carried.color.ToString() : "-",
                human.StunRemaining > 0 ? 1 : 0, humanOnPad ? 1 : 0, F(r.x), F(r.z), robotInside ? 1 : 0, robot.Carried != null ? robot.Carried.color.ToString() : "-",
                robot.goalOverride ? 1 : 0, robot.holdPosition ? 1 : 0, robot.pauseForFence ? 1 : 0, robotOnPad ? 1 : 0, helpActive ? 1 : 0, BreakerOn ? 1 : 0,
                arena.cells.Count(c => c.Installed), F(Frame(breakerPad.localPosition).x), F(Frame(breakerPad.localPosition).z),
                F(Frame(robot.overrideGoalLocal).x), F(Frame(robot.overrideGoalLocal).z), robot.Target,
                F(Frame(arena.Cell(robot.Target).transform.position - arena.transform.position).x), F(Frame(arena.Cell(robot.Target).transform.position - arena.transform.position).z),
                "\"" + PlanText + "\"");
            File.AppendAllText(tracePath, line + "\n");
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

        /// <summary>Test only: walk the player into the fence, capture the shock feedback, quit.</summary>
        private System.Collections.IEnumerator ShockTest(string dir)
        {
            Directory.CreateDirectory(dir);
            yield return new WaitForSeconds(1.5f);
            var cable = arena.hazards[0];
            while (!cable.IsActive) yield return null;
            human.transform.position = cable.transform.position;
            yield return new WaitForSeconds(0.15f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "shock-1-zap.png"));
            yield return new WaitForSeconds(0.7f);
            ScreenCapture.CaptureScreenshot(Path.Combine(dir, "shock-2-stunned.png"));
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        /// <summary>Screenshots for the blog: a simulated partner plays; capture the key teamwork moments.</summary>
        private System.Collections.IEnumerator Shots()
        {
            Directory.CreateDirectory(screenshotDir);
            int n = 0;
            System.Collections.IEnumerator Until(Func<bool> cond, float limit)
            {
                float t = 0;
                while (!cond() && roundActive && t < limit) { t += Time.deltaTime; yield return null; }
            }
            void Shot(string name) => ScreenCapture.CaptureScreenshot(Path.Combine(screenshotDir, $"{++n:00}-{name}.png"));

            for (int attempt = 0; attempt < 3; attempt++)
            {
                mode = CoopMode.Adaptive;
                BeginRound();
                yield return new WaitForSeconds(0.8f); Shot("adaptive-start");
                yield return Until(() => helpActive && robotOnPad && human.Carried != null, 30f);
                if (helpActive && robotOnPad) { yield return new WaitForSeconds(0.25f); Shot("adaptive-robot-holds-breaker"); }
                yield return Until(() => robotAsking, 30f);
                if (robotAsking) { yield return null; Shot("adaptive-robot-asks"); }
                yield return Until(() => humanOnPad && RobotNeedsDoor, 20f);
                if (humanOnPad) { yield return new WaitForSeconds(0.3f); Shot("adaptive-player-holds-breaker"); }
                yield return Until(() => false, 40f);
                yield return new WaitForSeconds(0.4f); Shot("adaptive-done");
                if (n >= 5) break;
            }
            mode = CoopMode.Fixed;
            BeginRound();
            yield return Until(() => robotAsking, 30f);
            yield return null; Shot("fixed-robot-asks");
            yield return Until(() => human.Carried != null && humanFenceTime > 0.8f && !BreakerOn, 30f);
            yield return null; Shot("fixed-player-waits");
            yield return Until(() => false, 40f);
            yield return new WaitForSeconds(0.4f); Shot("fixed-done");
            yield return new WaitForSeconds(0.5f);
            Application.Quit();
        }

        // ---------------- presentation ----------------
        private void UpdatePadLooks()
        {
            bool outerUsed = OnPad(human.transform.localPosition, breakerPad) || OnPad(robot.transform.localPosition, breakerPad);
            bool innerUsed = OnPad(human.transform.localPosition, innerPad) || OnPad(robot.transform.localPosition, innerPad);
            if (padRenderer != null) padRenderer.sharedMaterial = outerUsed ? padActive : padIdle;
            if (innerPadRenderer != null) innerPadRenderer.sharedMaterial = innerUsed ? padActive : padIdle;
        }

        private static AudioClip BuildChime()
        {
            const int rate = 44100;
            int n = (int)(rate * 0.5f);
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float f = t < 0.18f ? 880f : 1320f;
                d[i] = Mathf.Sin(2 * Mathf.PI * f * t) * Mathf.Exp(-(t < 0.18f ? t : t - 0.18f) * 9f) * 0.5f;
            }
            var clip = AudioClip.Create("chime", n, 1, rate, false);
            clip.SetData(d, 0);
            return clip;
        }

        private void OnGUI()
        {
            GUI.depth = 0;
            box ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, richText = true, wordWrap = true, fontSize = 15, padding = new RectOffset(12, 12, 10, 10) };
            small ??= new GUIStyle(GUI.skin.label) { richText = true, fontSize = 14 };
            big ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = true, fontSize = 22, padding = new RectOffset(20, 20, 20, 20) };
            bubble ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleCenter, richText = true, wordWrap = false, fontSize = 15, padding = new RectOffset(10, 10, 6, 6) };

            bool col = Language.LanguageCommander.Active;
            float colW = col ? Screen.width * Language.LanguageCommander.ColumnFraction : 0;
            var r = col ? new Rect(10, 10, colW - 20, Language.LanguageCommander.HudHeight) : new Rect(14, 14, 500, 104);
            string modeName = mode switch { CoopMode.Fixed => "Fixed plan (helps only when asked)", CoopMode.Adaptive => "Adaptive (reads your movement)", _ => "Language orders" };
            string cond = studyIndex >= 0 ? $"Condition {studyLabels[Mathf.Min(studyIndex, studyLabels.Count - 1)]}" : modeName;
            float left = Mathf.Max(0f, roundTimeout - roundTime);
            string text = $"<b>CO-OP</b> · {cond} · Round {round}\n" +
                          $"Backup power: <color={(left < 20 ? "#f88" : "#ffd27a")}>{left:0} s</color>   Generator: {arena.cells.Count(c => c.Installed)}/2" +
                          $"{(human.StunRemaining > 0 ? "   <color=#f88>YOU ARE STUNNED</color>" : "")}\n" +
                          $"Robot: {PlanText}\n" +
                          (BreakerOn ? "<color=#8f8>Fence OFF — someone is holding a breaker</color>"
                                     : "<color=#9cf>Fence live. Stand on a yellow breaker to open it · <b>E</b> = ask the robot to hold one</color>");
            GUI.Box(r, text, box);

            if (mode == CoopMode.Adaptive)
            {
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

            var cam = Camera.main;
            if (cam != null && roundActive)
            {
                // The robot's speech bubble: what it is doing, or what it needs from you.
                string say = robotAsking ? (robotInside ? "Let me out — hold the breaker!" : "Hold the breaker for me!") : RobotSays;
                if (!string.IsNullOrEmpty(say))
                {
                    Vector3 sp = cam.WorldToScreenPoint(robot.transform.position + Vector3.up * 2.3f);
                    if (sp.z > 0)
                    {
                        var size = bubble.CalcSize(new GUIContent(say));
                        var br = new Rect(sp.x - size.x / 2, Screen.height - sp.y - size.y, size.x, size.y);
                        bool pulse = robotAsking && Mathf.Repeat(Time.time, 0.6f) < 0.3f;
                        GUI.color = robotAsking ? (pulse ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 0.6f, 0.1f)) : new Color(0.1f, 0.6f, 0.75f, 0.95f);
                        GUI.DrawTexture(br, Texture2D.whiteTexture);
                        GUI.color = Color.white;
                        bubble.normal.textColor = robotAsking ? Color.black : Color.white;
                        GUI.Label(br, say, bubble);
                    }
                }
                // Point at the breaker the player should use when the robot is waiting.
                if (robotAsking && !humanOnPad)
                {
                    var pad = PadOnSide(humanInside);
                    Vector3 pp = cam.WorldToScreenPoint(pad.position + Vector3.up * 1.4f);
                    if (pp.z > 0 && Mathf.Repeat(Time.time, 0.6f) < 0.4f)
                    {
                        var pr = new Rect(pp.x - 70, Screen.height - pp.y - 16, 140, 26);
                        GUI.color = new Color(1f, 0.85f, 0.2f);
                        GUI.DrawTexture(pr, Texture2D.whiteTexture);
                        GUI.color = Color.white;
                        bubble.normal.textColor = Color.black;
                        GUI.Label(pr, "▼ STAND HERE", bubble);
                    }
                }
            }

            if (studyIndex >= 0 && (waitingForSpace || studyDone))
            {
                string msg = studyDone
                    ? "<b>Session complete — thank you!</b>\n\nPlease fill in the questionnaire."
                    : studyLabels[studyIndex] == "practice"
                        ? "<b>Practice round</b>\n\nMove with WASD. Walk into a cell to pick it up, walk to the generator to install it.\nThe generator needs BOTH cells and sits behind a live fence.\nStanding on a yellow breaker switches the fence off — one of you holds it while the other crosses.\nPress E to ask the robot to hold a breaker for you.\n\nPress SPACE to start."
                        : $"<b>Condition {studyLabels[studyIndex]}</b> — 3 rounds\n\n" + (mode switch
                        {
                            CoopMode.Fixed => "The robot follows its own fixed plan. It helps when you press E.",
                            CoopMode.Adaptive => "The robot tries to work out what you are doing.",
                            _ => "The robot waits for your typed orders (type below and press Enter).",
                        }) + "\n\nPress SPACE to start.";
                var pr = new Rect(colW + (Screen.width - colW) / 2 - 340, Screen.height / 2 - 180, 680, 360);
                GUI.color = new Color(0.05f, 0.06f, 0.08f, 0.97f);
                GUI.DrawTexture(pr, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Box(pr, msg, big);
            }
            else if (!roundActive && round > 0 && sim == null && !string.IsNullOrEmpty(lastResult))
            {
                string best = bestTime < float.MaxValue ? $"\n<size=14>Best: {bestTime:0.0} s</size>" : "";
                GUI.Box(new Rect(colW + (Screen.width - colW) / 2 - 290, Screen.height / 2 - 70, 580, 130), lastResult + best, big);
            }
        }
    }
}
