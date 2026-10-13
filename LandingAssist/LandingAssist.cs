using System;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace LandingAssist
{
    public class LandingAssist : ModBehaviour
    {
        public static LandingAssist Instance;

        public static Key LandKey = Key.B;
        // D-pad Left: Better Autopilot only uses it while the autopilot flies, when landing is unavailable.
        public static GamepadButton? LandButton = GamepadButton.DpadLeft;
        public static float SearchRadius = 40f;
        public static float MaxSlope = 15f;
        public static float CruiseClearance = 120f;
        public static float MaxRange = 3000f;
        public static float TouchdownSpeed = 4f;
        public static bool ShowBeacon = true;
        public static bool DebugLog;

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            new Harmony(ModHelper.Manifest.UniqueName).PatchAll(Assembly.GetExecutingAssembly());
            ModHelper.Console.WriteLine("Landing Assist ready", MessageType.Success);
        }

        private void Update() => LandingController.Update();

        public override void Configure(IModConfig config)
        {
            LandKey = ParseKey(config.GetSettingsValue<string>("landKey"), Key.B);
            LandButton = ParseButton(config.GetSettingsValue<string>("landButton"), GamepadButton.DpadLeft);
            SearchRadius = config.GetSettingsValue<float>("searchRadius");
            MaxSlope = config.GetSettingsValue<float>("maxSlope");
            CruiseClearance = config.GetSettingsValue<float>("cruiseClearance");
            MaxRange = config.GetSettingsValue<float>("maxRange");
            // Touchdown speed per setting. Hull damage starts at 30 m/s, and the legs only feel half of a
            // straight-down impact, so even "Fast" leaves margin.
            var landingSpeed = config.GetSettingsValue<string>("landingSpeed") ?? "";
            TouchdownSpeed = landingSpeed.StartsWith("Gentle") ? 4f : landingSpeed.StartsWith("Normal") ? 15f : 30f;
            ShowBeacon = config.GetSettingsValue<bool>("showBeacon");
            DebugLog = config.GetSettingsValue<bool>("debugLog");
        }

        public static void Log(string message)
        {
            if (DebugLog)
            {
                Instance?.ModHelper.Console.WriteLine(message, MessageType.Info);
            }
        }

        private static Key ParseKey(string name, Key fallback)
        {
            return Enum.TryParse(name?.Trim(), true, out Key key) && key != Key.None ? key : fallback;
        }

        private static GamepadButton? ParseButton(string name, GamepadButton? fallback)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return Enum.TryParse(name, true, out GamepadButton button) ? button : fallback;
        }
    }
}
