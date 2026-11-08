using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TeamRescue
{
    /// <summary>Owns one training/play arena and randomizes its layout each episode.</summary>
    public sealed class RescueArena : MonoBehaviour
    {
        public float halfSize = 7f;
        public int defaultHazards = 2;
        public CellPart[] cells;          // index == (int)CellColor
        public Transform generator;
        public HazardStrip[] hazards;
        public Renderer generatorLight;
        public Material generatorIdle;
        public Material generatorPowered;

        public int ActiveHazards { get; private set; }

        [Tooltip("Co-op: one socket per cell colour on the generator.")]
        public Transform[] sockets;

        public CellPart Cell(CellColor c) => cells[(int)c];

        public Transform SocketFor(CellColor c) =>
            sockets != null && sockets.Length > (int)c && sockets[(int)c] != null ? sockets[(int)c] : generator;

        public void SetHazardsForcedOff(bool off)
        {
            foreach (var h in hazards) if (h != null) h.forcedOff = off;
        }

        public bool BothInstalled => cells.All(c => c.Installed);

        /// <summary>Keep an arena-local floor point away from the walls.</summary>
        public Vector3 ClampInside(Vector3 p, float margin = 0.8f)
        {
            float m = halfSize - margin;
            return new Vector3(Mathf.Clamp(p.x, -m, m), 0f, Mathf.Clamp(p.z, -m, m));
        }

        /// <summary>Random free floor point (arena-local) away from given points and live cables.</summary>
        public Vector3 FreePoint(IEnumerable<Vector3> avoid, float minDist)
        {
            var used = new List<Vector3>(avoid);
            return Sample(used, minDist);
        }

        public void ResetLayout(int hazardCount, Transform agent)
        {
            var used = new List<Vector3>();
            SetPowered(false);

            // Generator against a random wall.
            int side = Random.Range(0, 4);
            float along = Random.Range(-halfSize + 2f, halfSize - 2f);
            float inset = halfSize - 1.3f;
            Vector3 g = side switch
            {
                0 => new Vector3(along, 0, inset),
                1 => new Vector3(along, 0, -inset),
                2 => new Vector3(inset, 0, along),
                _ => new Vector3(-inset, 0, along),
            };
            generator.localPosition = g;
            used.Add(g);

            // Hazard strips somewhere in the middle region.
            ActiveHazards = Mathf.Clamp(hazardCount, 0, hazards.Length);
            for (int i = 0; i < hazards.Length; i++)
            {
                bool on = i < ActiveHazards;
                hazards[i].gameObject.SetActive(on);
                if (!on) continue;
                float r = halfSize - 3.5f;
                hazards[i].transform.localPosition = new Vector3(Random.Range(-r, r), 0, Random.Range(-r, r));
                hazards[i].transform.localRotation = Quaternion.Euler(0, Random.value < 0.5f ? 0 : 90, 0);
                hazards[i].Randomize();
            }

            foreach (var c in cells)
            {
                Vector3 p = Sample(used, 3f);
                c.Release(p);
                used.Add(p);
            }

            Vector3 a = Sample(used, 2.5f);
            agent.localPosition = new Vector3(a.x, 0, a.z);
        }

        public void SetPowered(bool powered)
        {
            if (generatorLight != null)
                generatorLight.sharedMaterial = powered ? generatorPowered : generatorIdle;
        }

        private Vector3 Sample(List<Vector3> used, float minDist)
        {
            float m = halfSize - 1.2f;
            Vector3 best = Vector3.zero;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var p = new Vector3(Random.Range(-m, m), 0, Random.Range(-m, m));
                best = p;
                bool ok = true;
                foreach (var u in used)
                    if ((u - p).sqrMagnitude < minDist * minDist) { ok = false; break; }
                if (!ok) continue;
                for (int i = 0; i < ActiveHazards && ok; i++)
                    if (hazards[i].Covers(transform.TransformPoint(p), 1.0f)) ok = false;
                if (ok) return p;
            }
            return best;
        }
    }
}
