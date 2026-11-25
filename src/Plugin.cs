using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Pathologic2ProjectTransparency
{
    // Makes the world semi-transparent with screen-door (dithered) transparency (Mode = Dither).
    //
    // The game already has "Pathologic/Universal Dithered" and "Pathologic/Universal Metallic Dithered" (used for soft hair
    // edges). They render in the deferred pass and discard a pixel when albedo.a * _Color.a is below an interleaved-gradient-noise
    // threshold, so lighting, shadows, fog and depth all keep working and there is no sorting. TAA/DLSS blur the pattern into
    // translucency. The noise depends on the screen position plus the mesh UV, so overlapping surfaces get different patterns
    // and the layers behind are visible too (each one at Alpha of what is left).
    //
    // These shaders have exactly the same properties as "Universal Opaque/Cutout" and "Universal Metallic Opaque/Cutout",
    // which cover most of the world, so swapping the shader on the material keeps all textures and settings.
    // Other opaque shaders (Standard, skin, SpeedTree, ...) are swapped to Universal Dithered as a best effort
    // (Scope = AllOpaque): textures with the usual names (_MainTex, _BumpMap, ...) carry over, the rest is lost.
    //
    // Mode = Fade is the alternative: real alpha blending with "Pathologic/Universal Fade" / "Universal Metallic Fade"
    // (same properties again; Blend SrcAlpha OneMinusSrcAlpha, ZWrite Off, queue Transparent, passes ForwardBase + ForwardAdd).
    // Every converted object is then drawn in the forward path, once more per extra light, sorted per object (large meshes
    // can sort wrong), without writing depth (no motion vectors for DLSS/TAA, no deferred fog/AO on it) and without a
    // ShadowCaster pass (houses cast no shadows).
    //
    // Loaded materials are changed in memory (Resources.FindObjectsOfTypeAll<Material>), so every renderer that uses them
    // changes too. Streamed-in scenes bring new materials: they are picked up on scene load and by a periodic rescan.
    // Original shaders, keywords and colors are remembered and put back when the mod is toggled off.
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "pathologic2.projecttransparency";
        public const string Name = "Pathologic 2 Project Transparency";
        public const string Version = "1.0.0";

        private const string DitheredName = "Pathologic/Universal Dithered";
        private const string MetallicDitheredName = "Pathologic/Universal Metallic Dithered";
        private const string FadeName = "Pathologic/Universal Fade";
        private const string MetallicFadeName = "Pathologic/Universal Metallic Fade";

        internal static ManualLogSource Log;
        private ConfigEntry<bool> enabledCfg;
        private ConfigEntry<float> alphaCfg;
        private ConfigEntry<Mode> modeCfg;
        private ConfigEntry<Scope> scopeCfg;
        private ConfigEntry<bool> noOcclusionCfg;
        private ConfigEntry<float> rescanCfg;
        private ConfigEntry<string> excludeCfg;
        private ConfigEntry<KeyboardShortcut> toggleKeyCfg;
        private ConfigEntry<KeyboardShortcut> modeKeyCfg;
        private ConfigEntry<bool> showMessagesCfg;

        private sealed class Saved
        {
            public Material Mat;
            public Shader Shader;
            public string[] Keywords;
            public bool HasColor;
            public Color Color;
            public int Queue; // raw custom render queue, -1 = from the shader
            public Shader Target;
            public float AppliedAlpha;
        }

        // Key: material instance id.
        private readonly Dictionary<int, Saved> saved = new Dictionary<int, Saved>();
        // First converted material of each name; used to recognize runtime copies ("X (Instance)") made after the swap.
        private readonly Dictionary<string, Saved> savedByName = new Dictionary<string, Saved>();
        private readonly HashSet<int> skipped = new HashSet<int>();
        private readonly HashSet<string> loggedSkippedShaders = new HashSet<string>();
        private readonly HashSet<string> loggedBestEffortShaders = new HashSet<string>();
        private readonly List<Camera> occlusionOff = new List<Camera>();

        private Shader dithered;
        private Shader metallicDithered;
        private Shader fade;
        private Shader metallicFade;
        private bool warnedNoFade;
        private float nextScan;
        private bool applied;

        private static string osdText;
        private static float osdUntil;
        private static GUIStyle osdStyle;

        private static readonly string[] ExcludedPrefixes =
        {
            "Hidden/", "UI/", "Sprites/", "TextMeshPro", "GUI/", "Skybox/", "Legacy Shaders/Particles", "Mobile/Particles",
            "Particles/", "Pathologic/Particles", "Pathologic/GPU", "Pathologic/Deferred Decals", "Pathologic/Special/Terrain Details",
            "Custom/Plaguespot", "Custom/Plague Face", "Rain/", "Nature/Terrain", "Plague/", "AnimationInstancing/",
            "Sandbox/", "Time of Day/", "Pathologic/Nature/Terrain", "Pathologic/Terrain",
        };

        private void Awake()
        {
            Log = Logger;
            enabledCfg = Config.Bind("General", "Enabled", true,
                "true: the world is drawn semi-transparent. false: vanilla.");
            modeCfg = Config.Bind("General", "Mode", Mode.Fade,
                "Dither: screen-door transparency in the deferred pass (shadows, fog, DLSS keep working; fine grain).\n" +
                "Fade: real alpha blending in the forward pass (smooth, true layering; no shadows from converted " +
                "objects, sorting errors on large meshes, may ghost with DLSS/TAA).");
            alphaCfg = Config.Bind("General", "Alpha", 0.5f, new ConfigDescription(
                "Opacity of every surface: 1 = opaque, 0.5 = half of the pixels drawn, 0 = invisible. " +
                "Materials that were already dithered (soft hair edges) get their own opacity multiplied by this value.",
                new AcceptableValueRange<float>(0f, 1f)));
            scopeCfg = Config.Bind("General", "Scope", Scope.AllOpaque,
                "Universal: only the game's main shaders (Universal Opaque/Cutout and Metallic Opaque/Cutout, most of the town); " +
                "the result looks exactly like the original, only transparent.\n" +
                "AllOpaque: also every other opaque shader (people's skin, trees, glass frames, first-person hands, ...), " +
                "swapped to Universal Dithered as a best effort: some special effects (tree wind, skin shading) are lost.");
            noOcclusionCfg = Config.Bind("General", "DisableOcclusionCulling", false,
                "Turn off occlusion culling on cameras while the mod is on, so houses hidden behind other houses are drawn " +
                "and can be seen through walls. Costs some FPS.");
            rescanCfg = Config.Bind("General", "RescanInterval", 2f, new ConfigDescription(
                "Seconds between scans for newly loaded materials (houses and people stream in while you walk).",
                new AcceptableValueRange<float>(0.25f, 30f)));
            excludeCfg = Config.Bind("General", "ExcludeShaders", "",
                "Comma-separated shader names (or name prefixes) to leave as they are, e.g. \"Pathologic/Nature/SpeedTree, Skin/\". " +
                "The log lists every shader the mod converted as a best effort (Scope = AllOpaque).");
            toggleKeyCfg = Config.Bind("General", "ToggleKey", new KeyboardShortcut(KeyCode.PageUp), "Key to toggle the mod in game.");
            modeKeyCfg = Config.Bind("General", "ModeKey", new KeyboardShortcut(KeyCode.PageDown),
                "Key to switch between Dither and Fade (also turns the mod on).");
            showMessagesCfg = Config.Bind("General", "ShowMessages", true, "Show an on-screen message when toggling.");

            enabledCfg.SettingChanged += (s, e) =>
            {
                if (!enabledCfg.Value)
                {
                    Restore();
                }
                nextScan = 0f;
            };
            alphaCfg.SettingChanged += (s, e) => Reapply();
            scopeCfg.SettingChanged += (s, e) => Reapply();
            modeCfg.SettingChanged += (s, e) => Reapply();
            noOcclusionCfg.SettingChanged += (s, e) => Reapply();
            excludeCfg.SettingChanged += (s, e) => Reapply();
            SceneManager.sceneLoaded += (scene, mode) => nextScan = 0f;

            Log.LogInfo($"{Name} {Version}: Enabled={enabledCfg.Value}, Mode={modeCfg.Value}, Alpha={alphaCfg.Value}, Scope={scopeCfg.Value}, " +
                        $"ToggleKey={toggleKeyCfg.Value}");
        }

        private void Update()
        {
            if (IsPressed(toggleKeyCfg.Value))
            {
                bool turnOn = !enabledCfg.Value;
                if (turnOn)
                {
                    Config.Reload(); // pick up settings edited in the .cfg while the mod was off
                }
                enabledCfg.Value = turnOn; // SettingChanged restores the materials when turned off
                string text = enabledCfg.Value ? StateText() : "Transparency: OFF";
                Log.LogInfo(text);
                ShowMessage(text);
            }
            else if (IsPressed(modeKeyCfg.Value))
            {
                modeCfg.Value = modeCfg.Value == Mode.Dither ? Mode.Fade : Mode.Dither; // SettingChanged restores, the scan reapplies
                enabledCfg.Value = true;
                string text = StateText();
                Log.LogInfo(text);
                ShowMessage(text);
            }

            if (enabledCfg.Value && Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + rescanCfg.Value;
                try
                {
                    Scan();
                }
                catch (Exception e)
                {
                    Log.LogError("Scan failed: " + e);
                }
            }
        }

        private string StateText()
        {
            return $"Transparency: {modeCfg.Value} (alpha {alphaCfg.Value:0.##})";
        }

        private void OnDestroy()
        {
            Restore();
        }

        private void Reapply()
        {
            Restore();
            nextScan = 0f;
        }

        private bool FindShaders()
        {
            if (dithered == null)
            {
                dithered = FindShader(DitheredName);
            }
            if (metallicDithered == null)
            {
                metallicDithered = FindShader(MetallicDitheredName);
            }
            if (modeCfg.Value == Mode.Fade)
            {
                if (fade == null)
                {
                    fade = FindShader(FadeName);
                }
                if (metallicFade == null)
                {
                    metallicFade = FindShader(MetallicFadeName);
                }
                if (fade == null)
                {
                    if (!warnedNoFade)
                    {
                        warnedNoFade = true;
                        Log.LogWarning($"Shader '{FadeName}' is not loaded yet; Fade mode waits for it");
                    }
                    return false;
                }
            }
            return dithered != null;
        }

        private static Shader FindShader(string name)
        {
            Shader shader = Shader.Find(name);
            if (shader != null)
            {
                return shader;
            }
            foreach (Shader s in Resources.FindObjectsOfTypeAll<Shader>())
            {
                if (s != null && s.name == name)
                {
                    return s;
                }
            }
            return null;
        }

        private void Scan()
        {
            if (!FindShaders())
            {
                return; // not loaded yet (main menu before the first character); try again later
            }

            if (noOcclusionCfg.Value)
            {
                foreach (Camera cam in Camera.allCameras)
                {
                    if (cam != null && cam.useOcclusionCulling)
                    {
                        cam.useOcclusionCulling = false;
                        occlusionOff.Add(cam);
                    }
                }
            }

            float alpha = alphaCfg.Value;
            int converted = 0;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null)
                {
                    continue;
                }
                int id = m.GetInstanceID();
                if (saved.ContainsKey(id) || skipped.Contains(id))
                {
                    continue;
                }
                Shader shader = m.shader;
                if (shader == null)
                {
                    skipped.Add(id);
                    continue;
                }

                if (TryAdoptCopy(m, id, shader))
                {
                    continue;
                }

                bool wasDithered, bestEffort;
                Shader target = PickTarget(m, shader, out wasDithered, out bestEffort);
                if (target == null)
                {
                    skipped.Add(id);
                    if (loggedSkippedShaders.Add(shader.name))
                    {
                        Log.LogInfo($"Left as is: shader '{shader.name}' (e.g. material '{m.name}')");
                    }
                    continue;
                }

                if (bestEffort && loggedBestEffortShaders.Add(shader.name))
                {
                    Log.LogInfo($"Best effort: shader '{shader.name}' -> '{target.name}' (e.g. material '{m.name}', queue {m.renderQueue})");
                }
                var s = new Saved
                {
                    Mat = m,
                    Shader = shader,
                    Keywords = m.shaderKeywords,
                    HasColor = m.HasProperty("_Color"),
                    Queue = m.renderQueue == shader.renderQueue ? -1 : m.renderQueue,
                    Target = target,
                };
                if (s.HasColor)
                {
                    s.Color = m.GetColor("_Color");
                }

                if (target != shader)
                {
                    m.shader = target;
                    m.shaderKeywords = s.Keywords;
                }
                if (modeCfg.Value == Mode.Fade)
                {
                    m.renderQueue = -1; // the Transparent queue of the Fade shader: after the opaque world and the sky
                }
                if (m.HasProperty("_Color"))
                {
                    // Opaque materials often have a meaningless _Color.a (it was never used), so it is replaced, not multiplied.
                    Color c = s.HasColor ? s.Color : Color.white;
                    c.a = wasDithered ? c.a * alpha : alpha;
                    m.SetColor("_Color", c);
                    s.AppliedAlpha = c.a;
                }
                saved[id] = s;
                if (!savedByName.ContainsKey(m.name))
                {
                    savedByName[m.name] = s;
                }
                converted++;
            }

            if (converted > 0)
            {
                applied = true;
                Log.LogInfo($"Made {converted} materials see-through (total {saved.Count}, left as is {skipped.Count})");
            }
        }

        // A runtime copy of an already converted material (renderer.material made after the swap) is already see-through;
        // remember the original of its source so that it is restored too, and do not apply the alpha twice.
        private bool TryAdoptCopy(Material m, int id, Shader shader)
        {
            const string suffix = " (Instance)";
            string name = m.name;
            if (!name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }
            Saved source;
            if (!savedByName.TryGetValue(name.Substring(0, name.Length - suffix.Length), out source) || shader != source.Target)
            {
                return false;
            }
            if (source.HasColor && m.HasProperty("_Color") && Mathf.Abs(m.GetColor("_Color").a - source.AppliedAlpha) > 0.001f)
            {
                return false;
            }
            saved[id] = new Saved
            {
                Mat = m,
                Shader = source.Shader,
                Keywords = source.Keywords,
                HasColor = source.HasColor,
                Color = source.Color,
                Queue = source.Queue,
                Target = source.Target,
                AppliedAlpha = source.AppliedAlpha,
            };
            return true;
        }

        private Shader PickTarget(Material m, Shader shader, out bool wasDithered, out bool bestEffort)
        {
            wasDithered = false;
            bestEffort = false;
            string name = shader.name;
            foreach (string part in excludeCfg.Value.Split(','))
            {
                string prefix = part.Trim();
                if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }
            }
            bool isFade = modeCfg.Value == Mode.Fade;
            Shader plain = isFade ? fade : dithered;
            Shader metallic = isFade ? (metallicFade != null ? metallicFade : fade)
                                     : (metallicDithered != null ? metallicDithered : dithered);
            switch (shader.name)
            {
                case "Pathologic/Universal Opaque":
                case "Pathologic/Universal Cutout":
                    return plain;
                case "Pathologic/Universal Metallic Opaque":
                case "Pathologic/Universal Metallic Cutout":
                    return metallic;
                case DitheredName:
                    wasDithered = true;
                    return plain;
                case MetallicDitheredName:
                    wasDithered = true;
                    return metallic;
            }

            if (scopeCfg.Value != Scope.AllOpaque)
            {
                return null;
            }
            // Only the opaque and alpha-test range: below 2000 are the sky and background, from 2500 transparent things.
            if (m.renderQueue < 2000 || m.renderQueue >= 2500 || !m.HasProperty("_MainTex"))
            {
                return null;
            }
            foreach (string prefix in ExcludedPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }
            }
            if (name.IndexOf("Fade", StringComparison.Ordinal) >= 0 || name.IndexOf("Transparent", StringComparison.Ordinal) >= 0)
            {
                return null;
            }
            bestEffort = true;
            return name.IndexOf("Metallic", StringComparison.Ordinal) >= 0 ? metallic : plain;
        }

        private void Restore()
        {
            int restored = 0;
            foreach (Saved s in saved.Values)
            {
                if (s.Mat == null)
                {
                    continue; // unloaded together with its scene
                }
                if (s.Mat.shader != s.Shader)
                {
                    s.Mat.shader = s.Shader;
                }
                s.Mat.shaderKeywords = s.Keywords;
                s.Mat.renderQueue = s.Queue;
                if (s.HasColor && s.Mat.HasProperty("_Color"))
                {
                    s.Mat.SetColor("_Color", s.Color);
                }
                restored++;
            }
            foreach (Camera cam in occlusionOff)
            {
                if (cam != null)
                {
                    cam.useOcclusionCulling = true;
                }
            }
            if (applied)
            {
                Log.LogInfo($"Restored {restored} materials");
            }
            occlusionOff.Clear();
            saved.Clear();
            savedByName.Clear();
            skipped.Clear();
            loggedSkippedShaders.Clear();
            loggedBestEffortShaders.Clear();
            applied = false;
        }

        // BepInEx's KeyboardShortcut.IsDown() does not fire while any unrelated key is held (WASD, Shift while running).
        private static bool IsPressed(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
            {
                return false;
            }
            foreach (KeyCode modifier in shortcut.Modifiers)
            {
                if (!Input.GetKey(modifier))
                {
                    return false;
                }
            }
            return true;
        }

        private void OnGUI()
        {
            if (osdText == null || Time.unscaledTime > osdUntil || !showMessagesCfg.Value)
            {
                return;
            }
            GUI.depth = -1000;
            if (osdStyle == null)
            {
                osdStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            }
            var rect = new Rect(24, 64, 900, 40);
            var shadow = new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height);
            osdStyle.normal.textColor = Color.black;
            GUI.Label(shadow, osdText, osdStyle);
            osdStyle.normal.textColor = Color.white;
            GUI.Label(rect, osdText, osdStyle);
        }

        private static void ShowMessage(string text)
        {
            osdText = text;
            osdUntil = Time.unscaledTime + 2.5f;
        }
    }

    public enum Mode
    {
        Dither,
        Fade
    }

    public enum Scope
    {
        Universal,
        AllOpaque
    }
}
