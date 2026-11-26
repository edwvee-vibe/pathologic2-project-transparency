using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.PostProcessing;

namespace Pathologic2ProjectTransparency
{
    // Smooths the dither pattern with the game's own TAA (Post Processing Stack v1, TaaComponent).
    // Its history weight comes from AntialiasingModel.TaaSettings: stationaryBlending for still pixels and motionBlending
    // for moving ones (defaults 0.95 / 0.85), plus a sharpen pass (default 0.3) that brings the grain back.
    // While the mod is on in Dither mode, these values are raised on every loaded PostProcessingProfile, so more frames are
    // averaged and the pattern turns into translucency, also while moving (at the cost of more ghosting).
    // The original values are remembered per AntialiasingModel and put back when the mod is turned off or switched to
    // another mode.
    // Per model, not per profile: PostProcessingOverrideController clones the base profile into "<name>_Override" and
    // copies the model references (target.antialiasing = source.antialiasing), so several profiles share one model.
    // Only the game's TAA is affected: the DLSS mod replaces the TAA pass (F8 there switches back to the game's TAA).
    internal sealed class TaaSmoothing
    {
        private readonly ConfigEntry<float> stationaryCfg;
        private readonly ConfigEntry<float> motionCfg;
        private readonly ConfigEntry<float> sharpenCfg;

        // AntialiasingModel does not override Equals/GetHashCode, so the dictionary compares references.
        private readonly Dictionary<AntialiasingModel, AntialiasingModel.TaaSettings> saved =
            new Dictionary<AntialiasingModel, AntialiasingModel.TaaSettings>();

        public TaaSmoothing(ConfigFile config)
        {
            // Used in Mode = Dither. Works only with the game's TAA: with the DLSS mod, press F8 to switch to the game's TAA.
            const string section = "Dither";
            stationaryCfg = config.Bind(section, "StationaryBlending", 0.98f, new ConfigDescription(
                "Share of the history in still pixels (game default 0.95). Higher = smoother, slower to react.",
                new AcceptableValueRange<float>(0f, 0.99f)));
            motionCfg = config.Bind(section, "MotionBlending", 0.95f, new ConfigDescription(
                "Share of the history in moving pixels (game default 0.85). Higher = smoother while moving, but more ghosting.",
                new AcceptableValueRange<float>(0f, 0.99f)));
            sharpenCfg = config.Bind(section, "Sharpen", 0f, new ConfigDescription(
                "TAA sharpening (game default 0.3). Sharpening brings the dither grain back, so it is off by default.",
                new AcceptableValueRange<float>(0f, 3f)));
        }

        public void Apply()
        {
            foreach (PostProcessingProfile profile in Resources.FindObjectsOfTypeAll<PostProcessingProfile>())
            {
                if (profile == null || profile.antialiasing == null)
                {
                    continue;
                }
                AntialiasingModel model = profile.antialiasing;
                AntialiasingModel.Settings settings = model.settings;
                if (!saved.ContainsKey(model))
                {
                    saved[model] = settings.taaSettings;
                    AntialiasingModel.TaaSettings t = settings.taaSettings;
                    Plugin.Log.LogInfo($"TAA smoothing on profile '{profile.name}' (method {settings.method}, enabled " +
                                       $"{model.enabled}): stationary {t.stationaryBlending}, motion {t.motionBlending}, " +
                                       $"sharpen {t.sharpen} -> {stationaryCfg.Value}, {motionCfg.Value}, {sharpenCfg.Value}");
                }
                settings.taaSettings.stationaryBlending = stationaryCfg.Value;
                settings.taaSettings.motionBlending = motionCfg.Value;
                settings.taaSettings.sharpen = sharpenCfg.Value;
                model.settings = settings;
            }
        }

        public void Restore()
        {
            foreach (KeyValuePair<AntialiasingModel, AntialiasingModel.TaaSettings> entry in saved)
            {
                AntialiasingModel.Settings settings = entry.Key.settings;
                settings.taaSettings = entry.Value;
                entry.Key.settings = settings;
            }
            if (saved.Count > 0)
            {
                Plugin.Log.LogInfo($"TAA settings restored on {saved.Count} antialiasing model(s)");
            }
            saved.Clear();
        }
    }
}
