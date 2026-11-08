using UnityEngine;

namespace TeamRescue.Coop
{
    /// <summary>
    /// A scripted stand-in for a human, used only to test the co-op logic in batch runs.
    /// It usually heads for the cell nearest to itself, sometimes the other one, sometimes
    /// changes its mind early, wobbles while walking and ignores cables. It is NOT a model of
    /// real people; real participants are needed for any claim about humans.
    /// </summary>
    public sealed class SimulatedPartner : IHumanBrain
    {
        private readonly System.Random rng;
        private readonly float pNearest, pChangeMind;
        private CellPart goal;
        private float roundTime;
        private bool changeMindPlanned, mindChanged;
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
            roundTime = 0;
            mindChanged = false;
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
            roundTime += Time.fixedDeltaTime;
            var a = self.arena;
            Vector3 p = self.transform.localPosition;
            Vector3 target;
            if (self.Carried != null) target = a.generator.localPosition;
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
                target = goal.transform.localPosition;
            }
            Vector3 d = target - p; d.y = 0;
            if (d.magnitude < 0.05f) return Vector2.zero;
            Vector3 dir = d.normalized;
            Vector3 side = new Vector3(-dir.z, 0, dir.x);
            float wobble = (Mathf.PerlinNoise(noiseSeed, roundTime * 0.8f) - 0.5f) * 0.9f;
            Vector3 m = (dir + side * wobble).normalized;
            // A cautious person: stop in front of a live cable and wait for it to switch off.
            Vector3 ahead = self.transform.position + a.transform.TransformDirection(m) * 0.9f;
            foreach (var h in a.hazards)
                if (h.gameObject.activeSelf && h.IsActive && h.Covers(ahead, 0.35f)) return Vector2.zero;
            return new Vector2(m.x, m.z);
        }
    }
}
