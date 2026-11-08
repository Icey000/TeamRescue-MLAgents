using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TeamRescue
{
    /// <summary>
    /// Goal-conditioned rescue robot. It is told WHICH cell is needed (blue/orange),
    /// must fetch that cell, avoid live cables and deliver it to the generator.
    /// The goal is an input, so a human (or later a language interface) can command it.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class RescueBotAgent : Agent
    {
        public RescueArena arena;
        public Transform carrySocket;
        public Transform visual;
        public float moveSpeed = 5f;
        public float acceleration = 30f;
        public float pickupRadius = 1.1f;
        public float installRadius = 1.5f;
        public float shaping = 0.1f;
        public float timePenaltyTotal = 0.5f;
        public float stunDuration = 1.5f;
        public float shockPenalty = 1f;

        [Tooltip("Demo mode: the target is set by a human/command instead of randomly.")]
        public bool externalTarget;

        [Tooltip("Heuristic mode uses a scripted go-straight controller instead of the keyboard.")]
        public bool scriptedBaseline;

        [Tooltip("Language layer: the robot waits in place until it receives a new order.")]
        public bool holdPosition;

        public CellColor Target { get; private set; }
        public CellPart Carried { get; private set; }
        public float StunRemaining => stun;
        public int Installs { get; private set; }
        public int WrongInstalls { get; private set; }
        public int HazardHits { get; private set; }
        public string LastEvent { get; private set; } = "";

        private Rigidbody body;
        private float stun;
        private float prevDist;
        private bool pickupBonusGiven;
        private bool resolved = true;
        private int episodeHazardHits;
        private int episodeWrongTouches;
        private bool wrongTouchGiven;
        private int episodeSteps;
        private CellColor pendingTarget;
        private bool hasPendingTarget;

        public override void Initialize()
        {
            body = GetComponent<Rigidbody>();
        }

        /// <summary>Command the robot to fetch a different cell (applies immediately).</summary>
        public void SetTarget(CellColor c)
        {
            Target = c;
            pendingTarget = c;
            if (Carried != null && Carried.color != c)
            {
                var drop = Carried;
                Carried = null;
                drop.Release(transform.localPosition + new Vector3(0, 0, -1.2f));
            }
            hasPendingTarget = true;
            prevDist = SubgoalDistance();
            LastEvent = $"command: fetch {c}";
        }

        public override void OnEpisodeBegin()
        {
            if (!resolved) Record(0f, false); // timed out
            resolved = false;
            episodeHazardHits = 0;
            episodeWrongTouches = 0;
            wrongTouchGiven = false;
            episodeSteps = 0;

            if (Carried != null) { Carried.Release(Carried.transform.localPosition); Carried = null; }
            int hz = Mathf.RoundToInt(Academy.Instance.EnvironmentParameters
                .GetWithDefault("hazard_count", arena.defaultHazards));
            arena.ResetLayout(hz, transform);

            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            stun = 0f;
            pickupBonusGiven = false;

            if (externalTarget && hasPendingTarget) Target = pendingTarget;
            else if (!externalTarget) Target = (CellColor)Random.Range(0, 2);
            prevDist = SubgoalDistance();
            EpisodeBegan?.Invoke();
        }

        /// <summary>Raised after a new layout is generated.</summary>
        public event System.Action EpisodeBegan;
        /// <summary>Co-op mode: raised when the robot locks a cell into the generator.</summary>
        public event System.Action<CellColor> CoopInstalled;

        [Tooltip("Co-op mode: installing locks the cell into a socket instead of ending the episode.")]
        public bool coopMode;

        public override void CollectObservations(VectorSensor sensor)
        {
            float s = arena.halfSize * 2f;
            Vector3 p = transform.localPosition;

            sensor.AddObservation(Target == CellColor.Blue ? 1f : 0f);
            sensor.AddObservation(Target == CellColor.Orange ? 1f : 0f);
            sensor.AddObservation(Carried != null && Carried.color == CellColor.Blue ? 1f : 0f);
            sensor.AddObservation(Carried != null && Carried.color == CellColor.Orange ? 1f : 0f);

            foreach (var c in arena.cells)
            {
                if (!c.Available) { sensor.AddObservation(0f); sensor.AddObservation(0f); continue; }
                Vector3 d = c.transform.localPosition - p;
                sensor.AddObservation(d.x / s);
                sensor.AddObservation(d.z / s);
            }

            Vector3 g = arena.generator.localPosition - p;
            sensor.AddObservation(g.x / s);
            sensor.AddObservation(g.z / s);

            Vector3 v = arena.transform.InverseTransformDirection(body.linearVelocity);
            sensor.AddObservation(v.x / moveSpeed);
            sensor.AddObservation(v.z / moveSpeed);
            sensor.AddObservation(stun > 0f ? 1f : 0f);

            // Cables: present, relative position, orientation, live, time until toggle (6 each).
            for (int i = 0; i < 2; i++)
            {
                var h = i < arena.hazards.Length ? arena.hazards[i] : null;
                if (h == null || !h.gameObject.activeSelf)
                {
                    for (int k = 0; k < 6; k++) sensor.AddObservation(0f);
                    continue;
                }
                Vector3 d = h.transform.localPosition - p;
                sensor.AddObservation(1f);
                sensor.AddObservation(d.x / s);
                sensor.AddObservation(d.z / s);
                sensor.AddObservation(Mathf.Abs(Mathf.DeltaAngle(h.transform.localEulerAngles.y, 0f)) > 45f ? 1f : 0f);
                sensor.AddObservation(h.IsActive ? 1f : 0f);
                sensor.AddObservation(h.TimeToToggle01);
            }
        }

        public const int ObservationSize = 25;

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (celebrateRemaining > 0f) return;
            var ca = actions.ContinuousActions;
            var move = new Vector2(Mathf.Clamp(ca[0], -1f, 1f), Mathf.Clamp(ca[1], -1f, 1f));
            float dt = Time.fixedDeltaTime;
            episodeSteps++;
            if (stun > 0f) { stun -= dt; move = Vector2.zero; }
            if (holdPosition) move = Vector2.zero;

            Vector3 desired = arena.transform.TransformDirection(new Vector3(move.x, 0, move.y));
            desired = Vector3.ClampMagnitude(desired, 1f) * moveSpeed;
            Vector3 v = body.linearVelocity;
            var planar = Vector3.MoveTowards(new Vector3(v.x, 0, v.z), desired, acceleration * dt);
            body.linearVelocity = new Vector3(planar.x, 0f, planar.z);
            if (visual != null && planar.sqrMagnitude > 0.2f)
                visual.rotation = Quaternion.Slerp(visual.rotation, Quaternion.LookRotation(planar), 0.25f);

            if (MaxStep > 0) AddReward(-timePenaltyTotal / MaxStep);

            Vector3 p = transform.localPosition;
            if (Carried == null)
            {
                foreach (var c in arena.cells)
                {
                    if (!c.Available) continue;
                    if (Flat(c.transform.localPosition - p) > pickupRadius) continue;
                    if (c.color != Target)
                    {
                        // Scanning the wrong cell costs time and a penalty, but the robot
                        // does not pick it up, so it can still recover.
                        if (!wrongTouchGiven) { AddReward(-0.2f); wrongTouchGiven = true; episodeWrongTouches++; LastEvent = $"rejected {c.color} cell (wrong type)"; }
                        continue;
                    }
                    c.Attach(carrySocket, this);
                    Carried = c;
                    if (!pickupBonusGiven) AddReward(0.2f);
                    pickupBonusGiven = true;
                    LastEvent = $"picked {c.color} cell";
                    prevDist = SubgoalDistance();
                    break;
                }
            }
            else if (Flat(arena.generator.localPosition - p) < installRadius)
            {
                if (coopMode)
                {
                    // Co-op: the generator needs both cells; lock this one into its socket.
                    var installed = Carried;
                    Carried = null;
                    installed.Install(arena.SocketFor(installed.color));
                    Installs++;
                    LastEvent = $"installed {installed.color} cell";
                    CoopInstalled?.Invoke(installed.color);
                    return;
                }
                bool ok = Carried.color == Target;
                var cell = Carried;
                Carried = null;
                cell.Release(p);
                if (ok)
                {
                    AddReward(1f);
                    Installs++;
                    arena.SetPowered(true);
                    LastEvent = "power restored";
                    Finish(1f);
                }
                else
                {
                    AddReward(-0.5f);
                    WrongInstalls++;
                    LastEvent = "wrong cell installed";
                    Finish(0f, true);
                }
                return;
            }

            float dist = SubgoalDistance();
            AddReward(shaping * (prevDist - dist));
            prevDist = dist;
        }

        private void OnTriggerStay(Collider other)
        {
            if (stun > 0f) return;
            var h = other.GetComponent<HazardStrip>();
            if (h == null || !h.IsActive) return;
            AddReward(-shockPenalty);
            stun = stunDuration;
            HazardHits++;
            episodeHazardHits++;
            LastEvent = "shocked by live cable";
            ShockFX.Play(transform, visual, h, "Robot", false, stunDuration, Carried != null);
            if (Carried != null)
            {
                var c = Carried;
                Carried = null;
                c.Release(arena.ClampInside(arena.transform.InverseTransformPoint(h.SafeDropPoint(transform.position))));
            }
            body.linearVelocity = Vector3.zero;
            prevDist = SubgoalDistance();
        }

        [Tooltip("Demo only: seconds to show the powered generator before the next layout.")]
        public float celebrateSeconds = 0f;
        public bool IsCelebrating => celebrateRemaining > 0f;
        private float celebrateRemaining;

        private void Finish(float success, bool wrong = false)
        {
            Record(success, wrong);
            resolved = true;
            if (celebrateSeconds > 0f && success > 0.5f)
            {
                celebrateRemaining = celebrateSeconds;
                body.linearVelocity = Vector3.zero;
                return;
            }
            EndEpisode();
        }

        private void FixedUpdate()
        {
            if (celebrateRemaining <= 0f) return;
            body.linearVelocity = Vector3.zero;
            celebrateRemaining -= Time.fixedDeltaTime;
            if (celebrateRemaining <= 0f) EndEpisode();
        }

        /// <summary>(success, hazardHits, wrongTouches, seconds) for every finished episode.</summary>
        public event System.Action<bool, int, int, float> EpisodeFinished;

        private void Record(float success, bool wrong)
        {
            EpisodeFinished?.Invoke(success > 0.5f, episodeHazardHits, episodeWrongTouches, episodeSteps * Time.fixedDeltaTime);
            var stats = Academy.Instance.StatsRecorder;
            stats.Add("Rescue/SuccessRate", success);
            stats.Add("Rescue/WrongInstallRate", wrong ? 1f : 0f);
            stats.Add("Rescue/WrongCellTouchRate", episodeWrongTouches > 0 ? 1f : 0f);
            stats.Add("Rescue/EpisodeSeconds", episodeSteps * Time.fixedDeltaTime);
            stats.Add("Rescue/HazardHitsPerEpisode", episodeHazardHits);
        }

        private float SubgoalDistance()
        {
            if (arena == null) return 0f;
            Vector3 p = transform.localPosition;
            if (Carried != null) return Flat(arena.generator.localPosition - p);
            return Flat(arena.Cell(Target).transform.localPosition - p);
        }

        private static float Flat(Vector3 d) => new Vector2(d.x, d.z).magnitude;

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var ca = actionsOut.ContinuousActions;
            if (scriptedBaseline)
            {
                // Hand-written baseline: drive straight at the current subgoal, ignore cables.
                Vector3 p = transform.localPosition;
                Vector3 goal = Carried != null ? arena.generator.localPosition : arena.Cell(Target).transform.localPosition;
                Vector3 d = goal - p; d.y = 0;
                d = d.normalized;
                ca[0] = d.x;
                ca[1] = d.z;
                return;
            }
            var kb = Keyboard.current;
            float x = 0, z = 0;
            if (kb != null)
            {
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) z += 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) z -= 1;
            }
            ca[0] = x;
            ca[1] = z;
        }
    }
}
