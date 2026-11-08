using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace TeamRescue.EditorTools
{
    /// <summary>Builds prefabs, scenes and player builds from code (batch-mode friendly).</summary>
    public static class ArenaBuilder
    {
        private const string Root = "Assets/TeamRescue";
        private const string PrefabPath = Root + "/RescueArena.prefab";
        private const string TrainScene = "Assets/Scenes/Training.unity";
        private const string DemoScene = "Assets/Scenes/Demo.unity";
        public const string BehaviorName = "RescueBot";
        public static readonly string[] Tags = { "Wall", "CellBlue", "CellOrange", "Generator", "HazardOn", "HazardOff" };

        [MenuItem("Team Rescue/Build Everything")]
        public static void BuildAll()
        {
            EnsureTags();
            BuildPrefab();
            BuildTrainingScene();
            BuildDemoScene(null);
            AssetDatabase.SaveAssets();
            Debug.Log("[TeamRescue] scenes built");
        }

        // ---------- batch entry points ----------
        public static void BatchBuildTrainingPlayer()
        {
            BuildAll();
            ConfigurePlayer();
            BuildPlayer(new[] { TrainScene }, "../Build/Train/TeamRescueTrain.exe");
        }

        public static void BatchBuildDemoPlayer()
        {
            string model = System.Environment.GetEnvironmentVariable("TEAMRESCUE_MODEL");
            EnsureTags();
            if (!File.Exists(PrefabPath)) BuildPrefab();
            BuildDemoScene(model);
            ConfigurePlayer();
            BuildPlayer(new[] { DemoScene }, "../Build/Demo/TeamRescueDemo.exe");
        }

        private const string CoopScene = "Assets/Scenes/Coop.unity";

        public static void BatchBuildCoopPlayer()
        {
            string model = System.Environment.GetEnvironmentVariable("TEAMRESCUE_MODEL");
            EnsureTags();
            BuildPrefab(); // prefab now carries the generator sockets
            BuildCoopScene(model);
            ConfigurePlayer();
            PlayerSettings.productName = "Team Rescue Coop";
            BuildPlayer(new[] { CoopScene }, "../Build/Coop/TeamRescueCoop.exe");
        }

        private static void BuildCoopScene(string modelPath)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddLighting();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var arena = go.GetComponent<RescueArena>();
            var agent = go.GetComponentInChildren<RescueBotAgent>();
            agent.externalTarget = true;
            agent.coopMode = true;
            agent.MaxStep = 0;
            agent.visual.localScale = Vector3.one * 1.25f;
            var bp = agent.GetComponent<BehaviorParameters>();
            var prop = typeof(BehaviorParameters).GetProperty("Model");
            var model = string.IsNullOrEmpty(modelPath) ? null : AssetDatabase.LoadAssetAtPath(modelPath, prop.PropertyType);
            if (model == null) Debug.LogError($"[TeamRescue] model not found at {modelPath}");
            prop.SetValue(bp, model);
            bp.BehaviorType = BehaviorType.InferenceOnly;

            // Human avatar: hi-vis jacket, helmet; same physics as the robot.
            var skin = Mat("HumanSkin", new Color(0.93f, 0.76f, 0.62f), 0.3f);
            var jacket = Mat("HumanJacket", new Color(0.1f, 0.85f, 0.6f), 0.4f, 0f, new Color(0.02f, 0.35f, 0.22f));
            var helmet = Mat("HumanHelmet", new Color(1f, 1f, 1f), 0.6f, 0.1f);
            var h = new GameObject("HumanPlayer");
            h.transform.SetParent(go.transform, false);
            var rb = h.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionY;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var sc = h.AddComponent<SphereCollider>();
            sc.radius = 0.4f; sc.center = new Vector3(0, 0.5f, 0);
            sc.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(Root + "/Frictionless.physicMaterial");
            var vis = new GameObject("Visual").transform;
            vis.SetParent(h.transform, false);
            Prim(PrimitiveType.Capsule, "Body", vis, new Vector3(0, 0.75f, 0), new Vector3(0.6f, 0.55f, 0.45f), jacket);
            Prim(PrimitiveType.Sphere, "Head", vis, new Vector3(0, 1.48f, 0), Vector3.one * 0.38f, skin);
            Prim(PrimitiveType.Sphere, "Helmet", vis, new Vector3(0, 1.58f, 0), new Vector3(0.42f, 0.25f, 0.42f), helmet);
            var hs = new GameObject("CarrySocket").transform;
            hs.SetParent(vis, false); hs.localPosition = new Vector3(0, 0.55f, 0.5f); hs.localScale = Vector3.one * 0.7f;
            var avatar = h.AddComponent<Coop.HumanAvatar>();
            avatar.arena = arena; avatar.carrySocket = hs; avatar.visual = vis;

            // Breaker pad
            var padIdle = Mat("PadIdle", new Color(1f, 0.8f, 0.1f), 0.5f, 0f, new Color(1.2f, 0.8f, 0.05f));
            var padActive = Mat("PadActive", new Color(0.3f, 1f, 0.45f), 0.5f, 0f, new Color(0.3f, 2.2f, 0.6f));
            var pad = new GameObject("BreakerPad");
            pad.transform.SetParent(go.transform, false);
            var disc = Prim(PrimitiveType.Cylinder, "Disc", pad.transform, Vector3.zero, new Vector3(1.6f, 0.02f, 1.6f), padIdle);
            Prim(PrimitiveType.Cube, "Lever", pad.transform, new Vector3(0, 0.25f, 0), new Vector3(0.15f, 0.5f, 0.15f), Mat("Wall", new Color(0.32f, 0.35f, 0.4f), 0.35f, 0.5f));

            var ctrlGo = new GameObject("CoopController");
            var coord = ctrlGo.AddComponent<Coop.CoopCoordinator>();
            coord.robot = agent; coord.human = avatar; coord.arena = arena;
            coord.breakerPad = pad.transform; coord.padRenderer = disc.GetComponent<Renderer>();
            coord.padIdle = padIdle; coord.padActive = padActive;
            var lang = ctrlGo.AddComponent<TeamRescue.Language.LanguageCommander>();
            lang.agent = agent;

            var cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.transform.position = new Vector3(0, 13f, -11.2f);
            cam.transform.rotation = Quaternion.Euler(52, 0, 0);
            cam.fieldOfView = 48;
            cam.backgroundColor = new Color(0.02f, 0.025f, 0.035f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var camData = cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            DressBlackoutStation();
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, CoopScene);
        }

        private static void ConfigurePlayer()
        {
            PlayerSettings.companyName = "MingyuZhang";
            PlayerSettings.productName = "Team Rescue Lab3";
            PlayerSettings.runInBackground = true;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1280;
            PlayerSettings.defaultScreenHeight = 720;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed; // local Ollama on 127.0.0.1
        }

        private static void BuildPlayer(string[] scenes, string path)
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = path,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            Debug.Log($"[TeamRescue] build {path}: {report.summary.result} errors={report.summary.totalErrors}");
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
                EditorApplication.Exit(2);
        }

        // ---------- tags ----------
        private static void EnsureTags()
        {
            var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var tags = tm.FindProperty("tags");
            var existing = new HashSet<string>();
            for (int i = 0; i < tags.arraySize; i++) existing.Add(tags.GetArrayElementAtIndex(i).stringValue);
            foreach (var t in Tags)
            {
                if (existing.Contains(t)) continue;
                tags.InsertArrayElementAtIndex(tags.arraySize);
                tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = t;
            }
            tm.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---------- materials ----------
        private static Material Mat(string name, Color c, float smooth = 0.4f, float metal = 0f, Color? emission = null)
        {
            Directory.CreateDirectory(Root + "/Materials");
            string path = $"{Root}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", metal);
            if (emission.HasValue)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission.Value);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else m.DisableKeyword("_EMISSION");
            EditorUtility.SetDirty(m);
            return m;
        }

        private static GameObject Prim(PrimitiveType t, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat, bool keepCollider = false)
        {
            var go = GameObject.CreatePrimitive(t);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        // ---------- prefab ----------
        private static void BuildPrefab()
        {
            Directory.CreateDirectory(Root);
            var floorMat = Mat("Floor", new Color(0.16f, 0.18f, 0.21f), 0.55f, 0.2f);
            var lineMat = Mat("FloorLine", new Color(0.95f, 0.75f, 0.1f), 0.3f, 0f, new Color(0.4f, 0.3f, 0f));
            var wallMat = Mat("Wall", new Color(0.32f, 0.35f, 0.4f), 0.35f, 0.5f);
            var blueMat = Mat("CellBlue", new Color(0.15f, 0.55f, 1f), 0.8f, 0.1f, new Color(0.05f, 0.35f, 1.2f));
            var orangeMat = Mat("CellOrange", new Color(1f, 0.5f, 0.1f), 0.8f, 0.1f, new Color(1.2f, 0.4f, 0.02f));
            var genMat = Mat("Generator", new Color(0.25f, 0.27f, 0.3f), 0.6f, 0.8f);
            var genIdle = Mat("GeneratorIdle", new Color(0.5f, 0.1f, 0.1f), 0.5f, 0f, new Color(0.6f, 0.02f, 0.02f));
            var genOn = Mat("GeneratorOn", new Color(0.2f, 1f, 0.4f), 0.5f, 0f, new Color(0.3f, 2.5f, 0.6f));
            var hazOn = Mat("HazardOn", new Color(0.7f, 0.9f, 1f), 0.9f, 0f, new Color(2.5f, 3.5f, 6f));
            var hazOff = Mat("HazardOff", new Color(0.12f, 0.12f, 0.14f), 0.3f, 0.6f);
            var botMat = Mat("BotBody", new Color(0.88f, 0.9f, 0.92f), 0.7f, 0.3f);
            var visorMat = Mat("BotVisor", new Color(0.05f, 0.08f, 0.1f), 0.95f, 0f, new Color(0.1f, 0.9f, 1.2f));
            var accentMat = Mat("BotAccent", new Color(0.95f, 0.75f, 0.1f), 0.5f, 0.2f);

            const float half = 7f;
            var root = new GameObject("RescueArena");
            var arena = root.AddComponent<RescueArena>();
            arena.halfSize = half;

            // Floor + hazard-tape border
            var floor = Prim(PrimitiveType.Cube, "Floor", root.transform, new Vector3(0, -0.05f, 0), new Vector3(half * 2, 0.1f, half * 2), floorMat, true);
            for (int i = 0; i < 4; i++)
            {
                float r = half - 0.6f;
                bool xAxis = i < 2;
                var pos = xAxis ? new Vector3(0, 0.005f, i == 0 ? r : -r) : new Vector3(i == 2 ? r : -r, 0.005f, 0);
                var sc = xAxis ? new Vector3(r * 2, 0.01f, 0.08f) : new Vector3(0.08f, 0.01f, r * 2);
                Prim(PrimitiveType.Cube, "Tape", root.transform, pos, sc, lineMat);
            }

            // Walls
            for (int i = 0; i < 4; i++)
            {
                bool xAxis = i < 2;
                float s = i % 2 == 0 ? 1 : -1;
                var pos = xAxis ? new Vector3(0, 0.6f, s * (half + 0.25f)) : new Vector3(s * (half + 0.25f), 0.6f, 0);
                var sc = xAxis ? new Vector3(half * 2 + 1f, 1.2f, 0.5f) : new Vector3(0.5f, 1.2f, half * 2 + 1f);
                var w = Prim(PrimitiveType.Cube, "Wall", root.transform, pos, sc, wallMat, true);
                w.tag = "Wall";
            }

            // Generator
            var gen = new GameObject("Generator");
            gen.transform.SetParent(root.transform, false);
            gen.tag = "Generator";
            var gcol = gen.AddComponent<CapsuleCollider>();
            gcol.radius = 0.6f; gcol.height = 1.6f; gcol.center = new Vector3(0, 0.8f, 0);
            Prim(PrimitiveType.Cylinder, "Base", gen.transform, new Vector3(0, 0.15f, 0), new Vector3(1.4f, 0.15f, 1.4f), genMat);
            Prim(PrimitiveType.Cube, "Core", gen.transform, new Vector3(0, 0.75f, 0), new Vector3(0.9f, 1.1f, 0.9f), genMat);
            var light = Prim(PrimitiveType.Cylinder, "StatusLight", gen.transform, new Vector3(0, 1.4f, 0), new Vector3(0.5f, 0.1f, 0.5f), genIdle);
            Prim(PrimitiveType.Cube, "Socket", gen.transform, new Vector3(0, 0.75f, 0.46f), new Vector3(0.4f, 0.4f, 0.05f), genIdle);
            arena.generator = gen.transform;
            var sockets = new Transform[2];
            for (int s = 0; s < 2; s++)
            {
                var so = new GameObject(s == 0 ? "SocketBlue" : "SocketOrange").transform;
                so.SetParent(gen.transform, false);
                so.localPosition = new Vector3(s == 0 ? -0.7f : 0.7f, 0.1f, 0f);
                so.localScale = Vector3.one * 0.6f;
                sockets[s] = so;
            }
            arena.sockets = sockets;
            arena.generatorLight = light.GetComponent<Renderer>();
            arena.generatorIdle = genIdle;
            arena.generatorPowered = genOn;

            // Cells (trigger colliders so rays see them but the robot doesn't bump them)
            CellPart MakeCell(CellColor color, Material mat, string tag)
            {
                var c = new GameObject($"Cell{color}");
                c.transform.SetParent(root.transform, false);
                c.tag = tag;
                var col = c.AddComponent<CapsuleCollider>();
                col.isTrigger = true; col.radius = 0.35f; col.height = 1.0f; col.center = new Vector3(0, 0.5f, 0);
                if (color == CellColor.Blue)
                    Prim(PrimitiveType.Cylinder, "Body", c.transform, new Vector3(0, 0.4f, 0), new Vector3(0.45f, 0.4f, 0.45f), mat);
                else
                    Prim(PrimitiveType.Cube, "Body", c.transform, new Vector3(0, 0.4f, 0), new Vector3(0.5f, 0.8f, 0.5f), mat);
                Prim(PrimitiveType.Cylinder, "Cap", c.transform, new Vector3(0, 0.85f, 0), new Vector3(0.22f, 0.06f, 0.22f), wallMat);
                var part = c.AddComponent<CellPart>();
                part.color = color;
                return part;
            }
            arena.cells = new[] { MakeCell(CellColor.Blue, blueMat, "CellBlue"), MakeCell(CellColor.Orange, orangeMat, "CellOrange") };

            // Hazard strips
            var hazards = new List<HazardStrip>();
            for (int i = 0; i < 2; i++)
            {
                var h = new GameObject($"Cable{i}");
                h.transform.SetParent(root.transform, false);
                var box = h.AddComponent<BoxCollider>();
                box.isTrigger = true; box.size = new Vector3(6f, 1f, 0.8f); box.center = new Vector3(0, 0.5f, 0);
                var strip = Prim(PrimitiveType.Cube, "Strip", h.transform, new Vector3(0, 0.03f, 0), new Vector3(6f, 0.06f, 0.8f), hazOff);
                var core = Prim(PrimitiveType.Cylinder, "Cable", h.transform, new Vector3(0, 0.1f, 0), new Vector3(0.12f, 3f, 0.12f), hazOff);
                core.transform.localRotation = Quaternion.Euler(0, 0, 90);
                var hs = h.AddComponent<HazardStrip>();
                hs.onMaterial = hazOn; hs.offMaterial = hazOff;
                hs.renderers = new[] { strip.GetComponent<Renderer>(), core.GetComponent<Renderer>() };
                h.tag = "HazardOff";
                hazards.Add(hs);
            }
            arena.hazards = hazards.ToArray();

            // Robot
            var bot = new GameObject("RescueBot");
            bot.transform.SetParent(root.transform, false);
            var rb = bot.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionY;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var sc2 = bot.AddComponent<SphereCollider>();
            sc2.radius = 0.45f; sc2.center = new Vector3(0, 0.5f, 0);
            var pm = new PhysicsMaterial("Frictionless") { dynamicFriction = 0, staticFriction = 0, frictionCombine = PhysicsMaterialCombine.Minimum };
            AssetDatabase.CreateAsset(pm, Root + "/Frictionless.physicMaterial");
            sc2.sharedMaterial = pm;

            var vis = new GameObject("Visual").transform;
            vis.SetParent(bot.transform, false);
            Prim(PrimitiveType.Capsule, "Body", vis, new Vector3(0, 0.55f, 0), new Vector3(0.7f, 0.4f, 0.6f), botMat);
            Prim(PrimitiveType.Cube, "Head", vis, new Vector3(0, 1.05f, 0), new Vector3(0.55f, 0.32f, 0.45f), botMat);
            Prim(PrimitiveType.Cube, "Visor", vis, new Vector3(0, 1.07f, 0.21f), new Vector3(0.45f, 0.14f, 0.05f), visorMat);
            Prim(PrimitiveType.Cylinder, "Antenna", vis, new Vector3(0.18f, 1.3f, -0.05f), new Vector3(0.04f, 0.12f, 0.04f), accentMat);
            Prim(PrimitiveType.Sphere, "AntennaTip", vis, new Vector3(0.18f, 1.44f, -0.05f), new Vector3(0.08f, 0.08f, 0.08f), visorMat);
            Prim(PrimitiveType.Cylinder, "WheelL", vis, new Vector3(-0.38f, 0.18f, 0), new Vector3(0.32f, 0.06f, 0.32f), accentMat).transform.localRotation = Quaternion.Euler(0, 0, 90);
            Prim(PrimitiveType.Cylinder, "WheelR", vis, new Vector3(0.38f, 0.18f, 0), new Vector3(0.32f, 0.06f, 0.32f), accentMat).transform.localRotation = Quaternion.Euler(0, 0, 90);
            var socket = new GameObject("CarrySocket").transform;
            socket.SetParent(vis, false);
            socket.localPosition = new Vector3(0, 0.25f, 0.55f);
            socket.localScale = Vector3.one * 0.7f;

            var agent = bot.AddComponent<RescueBotAgent>();
            agent.arena = arena;
            agent.carrySocket = socket;
            agent.visual = vis;
            agent.MaxStep = 2000;

            var bp = bot.GetComponent<BehaviorParameters>();
            bp.BehaviorName = BehaviorName;
            bp.BrainParameters.VectorObservationSize = RescueBotAgent.ObservationSize;
            bp.BrainParameters.ActionSpec = ActionSpec.MakeContinuous(2);
            bp.BehaviorType = BehaviorType.Default;

            var dr = bot.AddComponent<DecisionRequester>();
            dr.DecisionPeriod = 5;
            dr.TakeActionsBetweenDecisions = true;

            var rays = bot.AddComponent<RayPerceptionSensorComponent3D>();
            rays.SensorName = "Rays";
            rays.DetectableTags = Tags.ToList();
            rays.RaysPerDirection = 8;
            rays.MaxRayDegrees = 180;
            rays.RayLength = 14f;
            rays.SphereCastRadius = 0.3f;
            rays.StartVerticalOffset = 0.5f;
            rays.EndVerticalOffset = 0.5f;

            arena.defaultHazards = 2;
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            Object.DestroyImmediate(root);
        }

        // ---------- scenes ----------
        private static void AddLighting()
        {
            var lightGo = new GameObject("Sun");
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1.1f;
            l.color = new Color(0.85f, 0.9f, 1f);
            l.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(55, -35, 0);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.22f, 0.25f, 0.3f);
        }

        private static void BuildTrainingScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddLighting();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            const int n = 4;
            const float spacing = 22f;
            for (int x = 0; x < n; x++)
                for (int z = 0; z < n; z++)
                {
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    go.transform.position = new Vector3(x * spacing, 0, z * spacing);
                }
            var cam = new GameObject("Camera").AddComponent<Camera>();
            cam.transform.position = new Vector3(spacing * 1.5f, 70f, -25f);
            cam.transform.rotation = Quaternion.Euler(60, 0, 0);
            cam.backgroundColor = new Color(0.05f, 0.06f, 0.08f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, TrainScene);
        }

        private static void BuildDemoScene(string modelPath)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AddLighting();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var agent = go.GetComponentInChildren<RescueBotAgent>();
            agent.externalTarget = true;
            agent.MaxStep = 0; // no timeout while a person is watching/commanding
            agent.celebrateSeconds = 1.5f;
            agent.visual.localScale = Vector3.one * 1.25f;

            var bp = agent.GetComponent<BehaviorParameters>();
            if (!string.IsNullOrEmpty(modelPath))
            {
                var prop = typeof(BehaviorParameters).GetProperty("Model");
                var model = AssetDatabase.LoadAssetAtPath(modelPath, prop.PropertyType);
                if (model == null) Debug.LogError($"[TeamRescue] model not found at {modelPath}");
                prop.SetValue(bp, model);
                bp.BehaviorType = BehaviorType.InferenceOnly;
            }
            else bp.BehaviorType = BehaviorType.HeuristicOnly;

            var ctrl = new GameObject("DemoController").AddComponent<DemoController>();
            ctrl.agent = agent;
            var lang = ctrl.gameObject.AddComponent<TeamRescue.Language.LanguageCommander>();
            lang.agent = agent;

            var cam = new GameObject("Main Camera").AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.transform.position = new Vector3(0, 13f, -11.2f);
            cam.transform.rotation = Quaternion.Euler(52, 0, 0);
            cam.fieldOfView = 48;
            cam.backgroundColor = new Color(0.02f, 0.025f, 0.035f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            var camData = cam.gameObject.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.SubpixelMorphologicalAntiAliasing;

            DressBlackoutStation();
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, DemoScene);
        }

        /// <summary>Blackout mood for the play scene: dim sun, red emergency lights, bloom, props.</summary>
        private static void DressBlackoutStation()
        {
            var sun = Object.FindFirstObjectByType<Light>();
            if (sun != null) { sun.intensity = 0.35f; sun.color = new Color(0.55f, 0.65f, 0.9f); }
            RenderSettings.ambientLight = new Color(0.1f, 0.11f, 0.15f);

            var crate = Mat("Crate", new Color(0.35f, 0.3f, 0.22f), 0.25f, 0.1f);
            var metal = Mat("Pipe", new Color(0.4f, 0.42f, 0.45f), 0.6f, 0.8f);
            var redLamp = Mat("EmergencyLamp", new Color(1f, 0.1f, 0.05f), 0.5f, 0f, new Color(3f, 0.2f, 0.1f));
            var props = new GameObject("StationProps").transform;

            // Emergency lamps on the four wall corners + red point lights.
            foreach (var c in new[] { new Vector3(-7.3f, 1.4f, -7.3f), new Vector3(7.3f, 1.4f, -7.3f), new Vector3(-7.3f, 1.4f, 7.3f), new Vector3(7.3f, 1.4f, 7.3f) })
            {
                Prim(PrimitiveType.Sphere, "Lamp", props, c, Vector3.one * 0.35f, redLamp);
                var pl = new GameObject("EmergencyLight").AddComponent<Light>();
                pl.transform.SetParent(props, false);
                pl.transform.localPosition = c + Vector3.up * 0.3f;
                pl.type = LightType.Point; pl.color = new Color(1f, 0.15f, 0.08f); pl.range = 9f; pl.intensity = 2.2f;
            }

            // Crates and pipes outside the arena so they never interfere with the agent.
            var rng = new System.Random(7);
            for (int i = 0; i < 18; i++)
            {
                float ang = i / 18f * Mathf.PI * 2f;
                float r = 9.5f + (float)rng.NextDouble() * 3f;
                float s = 0.8f + (float)rng.NextDouble() * 1.2f;
                var p = new Vector3(Mathf.Cos(ang) * r, s * 0.5f, Mathf.Sin(ang) * r);
                Prim(PrimitiveType.Cube, "Crate", props, p, new Vector3(s, s, s), crate).transform.rotation = Quaternion.Euler(0, (float)rng.NextDouble() * 90f, 0);
            }
            var outer = Mat("OuterFloor", new Color(0.08f, 0.09f, 0.1f), 0.4f, 0.3f);
            Prim(PrimitiveType.Cube, "OuterFloor", props, new Vector3(0, -0.12f, 0), new Vector3(40, 0.1f, 40), outer);
            for (int i = -1; i <= 1; i += 2)
            {
                var pipe = Prim(PrimitiveType.Cylinder, "Pipe", props, new Vector3(i * 8.1f, 0.35f, 0), new Vector3(0.3f, 8f, 0.3f), metal);
                pipe.transform.rotation = Quaternion.Euler(90, 0, 0);
            }

            // Global post-processing: bloom makes live cables and the generator glow.
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.intensity.Override(1.4f);
            bloom.threshold.Override(0.9f);
            var tone = profile.Add<UnityEngine.Rendering.Universal.Tonemapping>(true);
            tone.mode.Override(UnityEngine.Rendering.Universal.TonemappingMode.ACES);
            var vig = profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vig.intensity.Override(0.3f);
            AssetDatabase.CreateAsset(profile, Root + "/BlackoutVolume.asset");
            var vol = new GameObject("PostProcess").AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = profile;
        }
    }
}
