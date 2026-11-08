using UnityEngine;
using UnityEngine.InputSystem;

namespace TeamRescue.Coop
{
    /// <summary>Who drives the human avatar: the keyboard, or a simulated partner for batch tests.</summary>
    public interface IHumanBrain
    {
        Vector2 Move(HumanAvatar self);
    }

    /// <summary>
    /// The human player's character. Same rules as the robot: walks into a cell to pick it up,
    /// walks to the generator to install it, and is stunned by live cables.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public sealed class HumanAvatar : MonoBehaviour
    {
        public RescueArena arena;
        public Transform carrySocket;
        public Transform visual;
        public float moveSpeed = 4.5f;
        public float acceleration = 30f;
        public float pickupRadius = 1.1f;
        public float installRadius = 1.5f;
        public float stunDuration = 1.5f;

        public IHumanBrain brain; // null = keyboard
        public CellPart Carried { get; private set; }
        public float StunRemaining { get; private set; }
        public int Shocks { get; private set; }
        public Vector3 Velocity => body != null ? body.linearVelocity : Vector3.zero;
        public event System.Action<CellPart> PickedUp;
        public event System.Action<CellColor> Installed;

        private Rigidbody body;

        private void Awake() => body = GetComponent<Rigidbody>();

        public void ResetForRound(Vector3 arenaLocal)
        {
            Carried = null; // the arena already released every cell
            StunRemaining = 0;
            Shocks = 0;
            transform.localPosition = new Vector3(arenaLocal.x, 0, arenaLocal.z);
            if (body == null) body = GetComponent<Rigidbody>();
            body.linearVelocity = Vector3.zero;
        }

        private Vector2 KeyboardMove()
        {
            var kb = Keyboard.current;
            if (kb == null || Language.LanguageCommander.IsTyping) return Vector2.zero;
            float x = 0, z = 0;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) z += 1;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) z -= 1;
            return new Vector2(x, z);
        }

        private void FixedUpdate()
        {
            float dt = Time.fixedDeltaTime;
            Vector2 move = brain != null ? brain.Move(this) : KeyboardMove();
            if (StunRemaining > 0) { StunRemaining -= dt; move = Vector2.zero; }
            Vector3 desired = arena.transform.TransformDirection(new Vector3(move.x, 0, move.y));
            desired = Vector3.ClampMagnitude(desired, 1f) * moveSpeed;
            Vector3 v = body.linearVelocity;
            var planar = Vector3.MoveTowards(new Vector3(v.x, 0, v.z), desired, acceleration * dt);
            body.linearVelocity = new Vector3(planar.x, 0, planar.z);
            if (visual != null && planar.sqrMagnitude > 0.2f)
                visual.rotation = Quaternion.Slerp(visual.rotation, Quaternion.LookRotation(planar), 0.3f);

            Vector3 p = transform.localPosition;
            if (Carried == null)
            {
                foreach (var c in arena.cells)
                {
                    if (!c.Available || Flat(c.transform.localPosition - p) > pickupRadius) continue;
                    c.Attach(carrySocket, this);
                    Carried = c;
                    PickedUp?.Invoke(c);
                    break;
                }
            }
            else if (Flat(arena.generator.localPosition - p) < installRadius)
            {
                var c = Carried;
                Carried = null;
                c.Install(arena.SocketFor(c.color));
                Installed?.Invoke(c.color);
            }
        }

        private void OnTriggerStay(Collider other)
        {
            if (StunRemaining > 0) return;
            var h = other.GetComponent<HazardStrip>();
            if (h == null || !h.IsActive) return;
            StunRemaining = stunDuration;
            Shocks++;
            bool dropped = Carried != null;
            if (dropped)
            {
                var c = Carried; Carried = null;
                c.Release(arena.ClampInside(arena.transform.InverseTransformPoint(h.SafeDropPoint(transform.position))));
            }
            body.linearVelocity = Vector3.zero;
            if (h.forcedOn)
            {
                // A fence never switches off by itself: knock the victim back to its own side,
                // otherwise it would be shocked again the moment the stun ends.
                Vector3 k = arena.ClampInside(arena.transform.InverseTransformPoint(h.SafeDropPoint(transform.position, 1.5f)));
                Vector3 w = arena.transform.TransformPoint(new Vector3(k.x, transform.localPosition.y, k.z));
                body.position = w; transform.position = w;
            }
            ShockFX.Play(transform, visual, h, brain == null ? "You" : "Partner", brain == null, stunDuration, dropped);
        }

        public static float Flat(Vector3 d) => new Vector2(d.x, d.z).magnitude;
    }
}
