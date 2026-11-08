using UnityEngine;

namespace TeamRescue
{
    public enum CellColor { Blue = 0, Orange = 1 }

    /// <summary>A power cell that the robot or the human player can carry.</summary>
    public sealed class CellPart : MonoBehaviour
    {
        public CellColor color;
        public bool IsCarried { get; private set; }
        /// <summary>Co-op mode: the cell sits in a generator socket and is no longer available.</summary>
        public bool Installed { get; private set; }
        /// <summary>Who is carrying it: null, the robot, or the human avatar.</summary>
        public Object Carrier { get; private set; }
        public bool Available => !IsCarried && !Installed;

        private Collider col;
        private Transform home;

        private void Awake()
        {
            col = GetComponent<Collider>();
            home = transform.parent;
        }

        public void Attach(Transform socket, Object carrier = null)
        {
            IsCarried = true;
            Installed = false;
            Carrier = carrier;
            if (col != null) col.enabled = false;
            transform.SetParent(socket, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
        }

        /// <summary>Lock the cell into a generator socket (co-op mode).</summary>
        public void Install(Transform socket)
        {
            IsCarried = false;
            Installed = true;
            Carrier = null;
            if (col != null) col.enabled = false;
            transform.SetParent(socket, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
        }

        /// <summary>Put the cell back on the floor at a position local to its arena.</summary>
        public void Release(Vector3 arenaLocalPosition)
        {
            IsCarried = false;
            Installed = false;
            Carrier = null;
            if (home == null) home = transform.parent;
            transform.SetParent(home, false);
            transform.localPosition = new Vector3(arenaLocalPosition.x, 0f, arenaLocalPosition.z);
            transform.localRotation = Quaternion.identity;
            if (col != null) col.enabled = true;
        }
    }
}
