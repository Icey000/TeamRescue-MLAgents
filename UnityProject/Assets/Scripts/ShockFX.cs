using System.Collections.Generic;
using UnityEngine;

namespace TeamRescue
{
    /// <summary>
    /// Presentation-only feedback for a cable shock: electric arcs, a light flash, a crackle,
    /// a blinking body, a floating "SHOCKED" label with a stun bar and, for the player, a screen
    /// flash and camera shake. It never touches game state, so the policy and evaluations are unaffected.
    /// </summary>
    public sealed class ShockFX : MonoBehaviour
    {
        /// <summary>Set by the interactive front-ends; stays off in training players.</summary>
        public static bool Enabled;

        private static ShockFX instance;

        private sealed class Hit
        {
            public Transform target;
            public Renderer[] body;
            public LineRenderer[] arcs;
            public Light flash;
            public float age, stun;
            public bool isPlayer, dropped, fence;
            public string who;
        }

        private readonly List<Hit> hits = new List<Hit>();
        private AudioSource audioSource;
        private AudioClip crackle;
        private float screenFlash, shake;
        private Vector3 camBase;
        private Transform shakenCam;
        private GUIStyle labelStyle, subStyle;
        private Texture2D white;

        private const float ArcTime = 0.4f;

        public static void Play(Transform target, Transform visual, HazardStrip cable, string who, bool isPlayer, float stun, bool dropped)
        {
            if (!Enabled || target == null) return;
            if (instance == null) instance = new GameObject("ShockFX").AddComponent<ShockFX>();
            instance.Spawn(target, visual, cable, who, isPlayer, stun, dropped);
        }

        private void Awake()
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake = false;
            audioSource.spatialBlend = 0f;
            audioSource.volume = 0.55f;
            crackle = BuildCrackle();
            white = Texture2D.whiteTexture;
        }

        private void Spawn(Transform target, Transform visual, HazardStrip cable, string who, bool isPlayer, float stun, bool dropped)
        {
            if (FindAnyObjectByType<AudioListener>() == null && Camera.main != null)
                Camera.main.gameObject.AddComponent<AudioListener>();

            var hit = new Hit
            {
                target = target,
                body = (visual != null ? visual : target).GetComponentsInChildren<Renderer>(),
                arcs = new LineRenderer[4],
                age = 0f,
                stun = stun,
                isPlayer = isPlayer,
                dropped = dropped,
                fence = cable != null && cable.forcedOn,
                who = who,
            };

            Material arcMat = cable != null && cable.onMaterial != null ? new Material(cable.onMaterial) : null;
            if (arcMat != null && arcMat.HasProperty("_EmissionColor"))
            {
                arcMat.EnableKeyword("_EMISSION");
                arcMat.SetColor("_EmissionColor", new Color(0.6f, 0.9f, 1f) * 6f);
            }
            for (int i = 0; i < hit.arcs.Length; i++)
            {
                var lr = new GameObject("Arc").AddComponent<LineRenderer>();
                lr.transform.SetParent(transform, false);
                lr.positionCount = 7;
                lr.widthMultiplier = 0.07f;
                lr.numCapVertices = 2;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                if (arcMat != null) lr.sharedMaterial = arcMat;
                lr.startColor = lr.endColor = new Color(0.75f, 0.95f, 1f);
                hit.arcs[i] = lr;
            }

            hit.flash = new GameObject("ShockFlash").AddComponent<Light>();
            hit.flash.transform.SetParent(transform, false);
            hit.flash.type = LightType.Point;
            hit.flash.color = new Color(0.55f, 0.85f, 1f);
            hit.flash.range = 6f;
            hit.flash.intensity = 12f;
            hit.flash.transform.position = target.position + Vector3.up;

            hits.Add(hit);
            audioSource.pitch = isPlayer ? 1f : 1.2f;
            audioSource.PlayOneShot(crackle, isPlayer ? 1f : 0.7f);

            if (isPlayer)
            {
                screenFlash = 1f;
                shake = 0.3f;
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            for (int i = hits.Count - 1; i >= 0; i--)
            {
                var h = hits[i];
                h.age += dt;
                bool alive = h.target != null && h.age < Mathf.Max(h.stun, ArcTime) + 0.25f;
                if (!alive)
                {
                    Cleanup(h);
                    hits.RemoveAt(i);
                    continue;
                }

                Vector3 c = h.target.position + Vector3.up * 0.6f;
                bool arcsOn = h.age < ArcTime || (h.age < h.stun && Random.value < 0.08f);
                foreach (var lr in h.arcs)
                {
                    lr.enabled = arcsOn;
                    if (!arcsOn) continue;
                    Vector3 a = c + Random.insideUnitSphere * 0.3f;
                    Vector3 b = c + Random.onUnitSphere * Random.Range(0.8f, 1.3f);
                    b.y = Mathf.Max(b.y, h.target.position.y + 0.05f);
                    for (int k = 0; k < lr.positionCount; k++)
                    {
                        float t = k / (float)(lr.positionCount - 1);
                        Vector3 p = Vector3.Lerp(a, b, t);
                        if (k > 0 && k < lr.positionCount - 1) p += Random.insideUnitSphere * 0.18f;
                        lr.SetPosition(k, p);
                    }
                }

                if (h.flash != null)
                {
                    h.flash.transform.position = c + Vector3.up * 0.5f;
                    h.flash.intensity = Mathf.Max(0f, 12f * (1f - h.age / ArcTime)) + (arcsOn ? 4f : 0f);
                }

                // Blink the stunned body so it reads as "disabled", not just "stopped".
                bool visible = h.age >= h.stun || Mathf.Repeat(h.age, 0.16f) < 0.09f;
                foreach (var r in h.body) if (r != null) r.enabled = visible;
            }

            screenFlash = Mathf.Max(0f, screenFlash - dt * 2.5f);
            var cam = Camera.main;
            if (shake > 0f && cam != null)
            {
                if (shakenCam != cam.transform) { shakenCam = cam.transform; camBase = shakenCam.localPosition; }
                shake -= dt;
                shakenCam.localPosition = camBase + (shake > 0f ? Random.insideUnitSphere * 0.18f * (shake / 0.3f) : Vector3.zero);
                if (shake <= 0f) shakenCam = null;
            }
        }

        private static void Cleanup(Hit h)
        {
            foreach (var lr in h.arcs) if (lr != null) Destroy(lr.gameObject);
            if (h.flash != null) Destroy(h.flash.gameObject);
            foreach (var r in h.body) if (r != null) r.enabled = true;
        }

        private void OnGUI()
        {
            GUI.depth = -5;
            if (labelStyle == null)
            {
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                labelStyle.normal.textColor = new Color(1f, 0.92f, 0.3f);
                subStyle = new GUIStyle(labelStyle) { fontSize = 15, fontStyle = FontStyle.Normal };
                subStyle.normal.textColor = Color.white;
            }

            if (screenFlash > 0f)
            {
                GUI.color = new Color(0.5f, 0.85f, 1f, 0.35f * screenFlash);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), white);
                GUI.color = Color.white;
            }

            var cam = Camera.main;
            if (cam == null) return;
            foreach (var h in hits)
            {
                if (h.target == null || h.age >= h.stun) continue;
                Vector3 sp = cam.WorldToScreenPoint(h.target.position + Vector3.up * 2.1f);
                if (sp.z < 0f) continue;
                float x = sp.x, y = Screen.height - sp.y - h.age * 12f;
                string title = h.isPlayer ? "ZAP! You are stunned" : $"ZAP! {h.who} stunned";
                Shadowed(new Rect(x - 160, y - 30, 320, 30), title, labelStyle);
                string detail = h.dropped ? "dropped the cell"
                              : h.fence ? "live fence: someone must hold a breaker" : "live cable: wait for it to go dark";
                Shadowed(new Rect(x - 160, y - 4, 320, 22), detail, subStyle);

                // Stun bar: how long until it can move again.
                float left = 1f - h.age / h.stun;
                var bar = new Rect(x - 50, y + 20, 100, 8);
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.DrawTexture(bar, white);
                GUI.color = new Color(1f, 0.85f, 0.25f);
                GUI.DrawTexture(new Rect(bar.x + 1, bar.y + 1, (bar.width - 2) * left, bar.height - 2), white);
                GUI.color = Color.white;
            }
        }

        private static void Shadowed(Rect r, string text, GUIStyle style)
        {
            var c = style.normal.textColor;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), text, style);
            style.normal.textColor = c;
            GUI.Label(r, text, style);
        }

        /// <summary>A short electric crackle: buzzing square wave plus noise bursts, decaying.</summary>
        private static AudioClip BuildCrackle()
        {
            const int rate = 44100;
            int n = (int)(rate * 0.45f);
            var data = new float[n];
            var rng = new System.Random(7);
            float burst = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)rate;
                float env = Mathf.Exp(-t * 7f);
                if (rng.NextDouble() < 0.004) burst = 1f;
                burst *= 0.9985f;
                float buzz = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * 120f * t)) * 0.35f;
                float noise = (float)(rng.NextDouble() * 2 - 1);
                data[i] = Mathf.Clamp((buzz + noise * (0.4f + burst)) * env * 0.6f, -1f, 1f);
            }
            var clip = AudioClip.Create("crackle", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
