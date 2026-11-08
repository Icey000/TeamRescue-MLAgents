using UnityEngine;

namespace TeamRescue.Coop
{
    /// <summary>
    /// A scripted stand-in for a human, used only to test the co-op logic in batch runs.
    /// It usually heads for the cell nearest to itself, sometimes the other one, sometimes
    /// changes its mind early and wobbles while walking. It never steps onto a live fence: it waits
    /// there and asks the robot for the breaker after a second. When the robot is stuck at the fence
    /// it walks to the breaker on its own side and stays until the robot is through. Once inside the
    /// generator room it waits next to the inner breaker. It is NOT a model of real people.
    /// </summary>
    public sealed class SimulatedPartner : IHumanBrain
    {
        public CoopCoordinator coord;
        private readonly System.Random rng;
        private readonly float pNearest, pChangeMind;
        private CellPart goal;
        private float roundTime, fenceWait, padDuty;
        private bool changeMindPlanned, mindChanged, asked, wasInside;
        private float noiseSeed;

        public CellColor? TrueGoal => goal != null ? goal.color : (CellColor?)null;

        public SimulatedPartner(int seed, float pNearest = 0.7f, float pChangeMind = 0.2f)
        {
            rng = new System.Random(seed);
            this.pNearest = pNearest;
            this.pChangeMind = pChangeMind;
        }

        public void NewRound(HumanAvatar self)
        {
            roundTime = 0; fenceWait = 0; padDuty = 0;
            mindChanged = false; asked = false; wasInside = false;
            noiseSeed = (float)rng.NextDouble() * 100f;
            var a = self.arena;
            var b = a.Cell(CellColor.Blue); var o = a.Cell(CellColor.Orange);
            float db = HumanAvatar.Flat(b.transform.localPosition - self.transform.localPosition);
            float dO = HumanAvatar.Flat(o.transform.localPosition - self.transform.localPosition);
            var nearest = db <= dO ? b : o; var other = nearest == b ? o : b;
            goal = rng.NextDouble() < pNearest ? nearest : other;
            changeMindPlanned = rng.NextDouble() < pChangeMind;
        }

        public Vector2 Move(HumanAvatar self)
        {
            float dt = Time.fixedDeltaTime;
            roundTime += dt;
            var a = self.arena;
            Vector3 p = self.transform.localPosition;
            bool inside = coord != null && coord.InRoom(p);
            if (inside != wasInside) { wasInside = inside; asked = false; fenceWait = 0; }

            // 1. The robot is stuck at the fence: hold the breaker on my side until it is through.
            if (coord != null)
            {
                if (coord.RobotNeedsDoor) padDuty = 0.5f; else padDuty -= dt;
                if (padDuty > 0f) return Steer(self, coord.Route(p, coord.PadOnSide(inside).localPosition), 0.3f, false);
            }

            Vector3 target;
            if (self.Carried != null) target = a.generator.localPosition;
            else if (inside)
            {
                // Inside with empty hands: wait beside the inner breaker, ready to open the fence.
                return Steer(self, coord.PadOnSide(true).localPosition, 1.6f, false);
            }
            else
            {
                if (changeMindPlanned && !mindChanged && roundTime > 1.2f)
                {
                    mindChanged = true;
                    var alt = goal == a.Cell(CellColor.Blue) ? a.Cell(CellColor.Orange) : a.Cell(CellColor.Blue);
                    if (alt.Available) goal = alt;
                }
                if (goal == null || !goal.Available)
                {
                    goal = null;
                    foreach (var c in a.cells) if (c.Available) { goal = c; break; }
                }
                if (goal == null) return Vector2.zero;
                target = coord != null ? coord.Route(p, goal.transform.localPosition) : goal.transform.localPosition;
            }

            var m = Steer(self, target, 0.05f, true);
            bool blocked = m == Vector2.zero && FenceAhead(self, Toward(self, target));
            if (blocked && self.Carried != null && coord != null)
            {
                fenceWait += dt;
                if (fenceWait > 1f && !asked && !coord.BreakerOn && !coord.RobotHelping) { coord.RequestHelp(); asked = true; }
            }
            else fenceWait = 0f;
            return m;
        }

        private static Vector3 Toward(HumanAvatar self, Vector3 target)
        {
            Vector3 d = target - self.transform.localPosition; d.y = 0;
            return d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.zero;
        }

        /// <summary>A cautious person never steps onto a cable that is live or about to relock.</summary>
        private static bool FenceAhead(HumanAvatar self, Vector3 localDir)
        {
            var a = self.arena;
            Vector3 ahead = self.transform.position + a.transform.TransformDirection(localDir) * 0.9f;
            foreach (var h in a.hazards)
            {
                if (!h.gameObject.activeSelf) continue;
                bool dangerous = h.IsActive || (h.forcedOn && !h.forcedOff);
                if (dangerous && h.Covers(ahead, 0.35f)) return true;
            }
            return false;
        }

        private Vector2 Steer(HumanAvatar self, Vector3 target, float stopRadius, bool wobble)
        {
            Vector3 d = target - self.transform.localPosition; d.y = 0;
            if (d.magnitude < stopRadius) return Vector2.zero;
            Vector3 dir = d.normalized;
            Vector3 m = dir;
            if (wobble && d.magnitude > 1.5f)
            {
                Vector3 side = new Vector3(-dir.z, 0, dir.x);
                float w = (Mathf.PerlinNoise(noiseSeed, roundTime * 0.8f) - 0.5f) * 0.9f;
                m = (dir + side * w).normalized;
            }
            if (FenceAhead(self, m)) return Vector2.zero;
            return new Vector2(m.x, m.z);
        }
    }
}
