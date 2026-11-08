using UnityEngine;

namespace TeamRescue
{
    /// <summary>
    /// An exposed cable that discharges periodically. Its tag switches between
    /// HazardOn / HazardOff so the robot's ray sensor can see whether it is live.
    /// </summary>
    public sealed class HazardStrip : MonoBehaviour
    {
        public float period = 3f;
        public float activeDuration = 1.2f;
        public float length = 6f;
        public float width = 0.8f;
        public Material onMaterial;
        public Material offMaterial;
        public Renderer[] renderers;

        private float phase;
        public bool IsActive { get; private set; }

        /// <summary>Seconds until the cable switches on/off, normalized by the period.</summary>
        public float TimeToToggle01
        {
            get
            {
                float t = Mathf.Repeat(Time.fixedTime + phase, period);
                float remaining = t < activeDuration ? activeDuration - t : period - t;
                return remaining / period;
            }
        }

        private void Awake() => Apply(false);

        public void Randomize()
        {
            phase = Random.value * period;
            Tick();
        }

        private void FixedUpdate() => Tick();

        /// <summary>Co-op: while someone stands on the breaker pad, the cable is dead.</summary>
        public bool forcedOff;

        /// <summary>Seconds before switching on during which the cable flickers as a warning (visual only).</summary>
        public const float WarnTime = 0.45f;
        private bool shownOn;

        private void Tick()
        {
            float t = Mathf.Repeat(Time.fixedTime + phase, period);
            bool active = !forcedOff && t < activeDuration;
            if (active != IsActive) Apply(active);

            // Telegraph the next discharge so players can read the hazard. Only the look changes:
            // the tag (what the robot's sensor sees) and IsActive stay "off" until it really is live.
            bool warn = !active && !forcedOff && period - t < WarnTime;
            bool show = active || (warn && Mathf.Repeat(Time.fixedTime, 0.15f) < 0.075f);
            if (show != shownOn) Show(show);
        }

        private void Apply(bool active)
        {
            IsActive = active;
            gameObject.tag = active ? "HazardOn" : "HazardOff";
            Show(active);
        }

        private void Show(bool on)
        {
            shownOn = on;
            if (renderers == null) return;
            foreach (var r in renderers)
                if (r != null) r.sharedMaterial = on ? onMaterial : offMaterial;
        }

        /// <summary>Where a cell dropped by a shocked carrier lands: just beside the cable, on the carrier's side,
        /// so it never ends up lying on a live strip where nobody can pick it up.</summary>
        public Vector3 SafeDropPoint(Vector3 worldPoint)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            float side = local.z >= 0f ? 1f : -1f;
            local.z = side * (width * 0.5f + 0.8f);
            local.y = 0f;
            return transform.TransformPoint(local);
        }

        /// <summary>True if an arena-local point is within clearance of this strip.</summary>
        public bool Covers(Vector3 worldPoint, float clearance)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint);
            return Mathf.Abs(local.x) < length * 0.5f + clearance
                && Mathf.Abs(local.z) < width * 0.5f + clearance;
        }
    }
}
