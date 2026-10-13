using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace LandingAssist
{
    /// <summary>
    /// Adds our hotkeys to the cockpit's upper-left prompt list, under the game's own
    /// (Engage Autopilot, Match Velocity...). Custom-sprite prompts cannot change their icon, so each
    /// action has a keyboard and a gamepad prompt and we show the one matching the active device.
    /// </summary>
    /// <summary>
    /// Cockpit prompts under the game's own: "Land At Cursor" when the cursor is on ground, and
    /// "Cancel Landing" while landing. One keyboard and one gamepad prompt each (custom-sprite
    /// prompts cannot change icon), shown to match the active device.
    /// </summary>
    [HarmonyPatch(typeof(ShipPromptController))]
    public static class Prompts
    {
        private static readonly AccessTools.FieldRef<ShipPromptController, bool> ShipDestroyed =
            AccessTools.FieldRefAccess<ShipPromptController, bool>("_shipDestroyed");

        private class PromptPair
        {
            public ScreenPrompt Keyboard;
            public ScreenPrompt Gamepad;
            public Key Key;
            public GamepadButton? Button;
        }

        private static PromptPair _land;
        private static float _nextTextUpdate;
        private static string _text = "Land At Cursor";

        [HarmonyPostfix]
        [HarmonyPatch(nameof(ShipPromptController.LateInitialize))]
        public static void LateInitialize_Postfix()
        {
            _land = Create(LandingAssist.LandKey, LandingAssist.LandButton);
        }

        [HarmonyPostfix]
        [HarmonyPatch("Update")]
        public static void Update_Postfix(ShipPromptController __instance)
        {
            if (_land == null)
            {
                return;
            }
            if (_land.Key != LandingAssist.LandKey || _land.Button != LandingAssist.LandButton)
            {
                Remove(_land);
                LateInitialize_Postfix();
            }

            bool cockpit = OWInput.IsInputMode(InputMode.ShipCockpit | InputMode.LandingCam) && !ShipDestroyed(__instance);

            // Live readout while landing; refreshed a few times a second (each text change re-lays out the prompt).
            if (Time.unscaledTime >= _nextTextUpdate)
            {
                _nextTextUpdate = Time.unscaledTime + 0.25f;
                _text = LandingController.Active
                    ? $"Cancel Landing   {LandingController.ReadoutDistance:F0} m away, {Mathf.Max(LandingController.ReadoutHeight, 0f):F0} m up, "
                        + (LandingController.ReadoutSink >= 0f ? $"sinking {LandingController.ReadoutSink:F0}" : $"rising {-LandingController.ReadoutSink:F0}") + " m/s"
                    : "Land At Cursor";
            }
            string text = _text;
            _land.Keyboard?.SetText("<CMD>   " + text);
            // Same "(Hold)" text the game uses for its own hold prompts (e.g. Match Velocity).
            _land.Gamepad?.SetText("<CMD>   " + text);
            Show(_land, cockpit && (LandingController.Active || LandingController.AimValid));
        }

        private static PromptPair Create(Key key, GamepadButton? button)
        {
            var pair = new PromptPair { Key = key, Button = button };
            pair.Keyboard = Add(KeyboardSprite(key));
            pair.Gamepad = button != null ? Add(GamepadSprite(button.Value)) : null;
            return pair;
        }

        private static ScreenPrompt Add(Sprite sprite)
        {
            if (sprite == null)
            {
                return null;
            }
            var prompt = new ScreenPrompt("<CMD>   ", sprite);
            Locator.GetPromptManager().AddScreenPrompt(prompt, PromptPosition.UpperLeft);
            return prompt;
        }

        private static void Remove(PromptPair pair)
        {
            var manager = Locator.GetPromptManager();
            if (manager == null)
            {
                return;
            }
            if (pair.Keyboard != null)
            {
                manager.RemoveScreenPrompt(pair.Keyboard, PromptPosition.UpperLeft);
            }
            if (pair.Gamepad != null)
            {
                manager.RemoveScreenPrompt(pair.Gamepad, PromptPosition.UpperLeft);
            }
        }

        private static void Show(PromptPair pair, bool visible)
        {
            // Custom-sprite prompts skip the game's "show prompts" option, so check it ourselves.
            visible &= PlayerData.GetPromptsEnabled();
            bool gamepad = OWInput.UsingGamepad();
            pair.Keyboard?.SetVisibility(visible && !gamepad);
            pair.Gamepad?.SetVisibility(visible && gamepad);
        }

        private static Sprite KeyboardSprite(Key key) => ToSprite(ButtonPromptLibrary.SharedInstance.GetButtonTexture(ToKeyCode(key)));

        private static Sprite GamepadSprite(GamepadButton button)
        {
            var joystick = ToJoystickButton(button);
            return joystick == JoystickButton.None ? null : ToSprite(ButtonPromptLibrary.SharedInstance.GetButtonTexture(joystick));
        }

        private static Sprite ToSprite(Texture2D texture)
        {
            return texture == null
                ? null
                : Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        }

        // The prompt icons are indexed by the legacy KeyCode, the hotkeys by the new Input System Key.
        private static KeyCode ToKeyCode(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return KeyCode.A + (key - Key.A);
            }
            if (key >= Key.Digit1 && key <= Key.Digit9)
            {
                return KeyCode.Alpha1 + (key - Key.Digit1);
            }
            if (key == Key.Digit0)
            {
                return KeyCode.Alpha0;
            }
            if (key >= Key.F1 && key <= Key.F12)
            {
                return KeyCode.F1 + (key - Key.F1);
            }
            if (key >= Key.Numpad0 && key <= Key.Numpad9)
            {
                return KeyCode.Keypad0 + (key - Key.Numpad0);
            }
            return Enum.TryParse(key.ToString(), true, out KeyCode code) ? code : KeyCode.None;
        }

        private static JoystickButton ToJoystickButton(GamepadButton button)
        {
            switch (button)
            {
                case GamepadButton.DpadUp: return JoystickButton.DPadUp;
                case GamepadButton.DpadDown: return JoystickButton.DPadDown;
                case GamepadButton.DpadLeft: return JoystickButton.DPadLeft;
                case GamepadButton.DpadRight: return JoystickButton.DPadRight;
                case GamepadButton.North: return JoystickButton.FaceUp;
                case GamepadButton.South: return JoystickButton.FaceDown;
                case GamepadButton.West: return JoystickButton.FaceLeft;
                case GamepadButton.East: return JoystickButton.FaceRight;
                case GamepadButton.LeftShoulder: return JoystickButton.LeftBumper;
                case GamepadButton.RightShoulder: return JoystickButton.RightBumper;
                case GamepadButton.LeftTrigger: return JoystickButton.LeftTrigger;
                case GamepadButton.RightTrigger: return JoystickButton.RightTrigger;
                case GamepadButton.LeftStick: return JoystickButton.LeftStickClick;
                case GamepadButton.RightStick: return JoystickButton.RightStickClick;
                case GamepadButton.Start: return JoystickButton.Start;
                case GamepadButton.Select: return JoystickButton.Select;
                default: return JoystickButton.None;
            }
        }
    }
}
