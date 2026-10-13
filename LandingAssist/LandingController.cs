using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LandingAssist
{
    /// <summary>
    /// Flies the ship to the chosen site and sets it down. Works relative to the (turning) surface:
    /// climb to a safe height if needed, cross over the site following the planet's curve while
    /// watching for terrain ahead, then descend vertically, upright, touching down gently.
    /// It drives the ship through the normal flight controls, so any stick input hands control back.
    /// </summary>
    [HarmonyPatch(typeof(ShipThrusterController))]
    public static class LandingController
    {
        private enum Phase { Idle, Travel, Descend, Settle }

        // After touchdown: press onto the legs and damp any rocking, then hand back.
        private const float SettleSeconds = 1.2f;
        private const float SettlePress = 0.6f;         // Share of thrust pushing the ship onto its legs.
        private static float _settleStart;

        private const float GraceSeconds = 1f;          // Ignore stick input right after engaging.
        private const float VelocityTimeConstant = 0.5f;
        private const float MaxTravelSpeed = 250f;
        private const float MaxDescentSpeed = 70f;
        private const float MinGroundClearance = 40f;   // While travelling (away from the site).
        private const float GlideSlope = 0.6f;          // Height kept above the site per metre still to go.
        private const float ArrivalHeight = 10f;        // Lowest the glide goes before the vertical descent.
        private const float ApproachClearance = 7f;     // Radius kept clear along the glide to the site.
        private const float GearHeight = 3f;            // Rough height of the ship's centre above its feet.
        private static float TouchdownSpeed => LandingAssist.TouchdownSpeed; // Damage starts at 5 m/s.
        private const float FinalStretch = 1f;          // Height of the touchdown-speed final descent.
        private const float DescentAccel = 0.65f;       // Share of thrust planned for braking the descent (gravity takes some).
        private const float MaxTurnRate = 0.6f;
        private const float UprightDeadZone = 1.5f * Mathf.Deg2Rad;

        private static Phase _phase;
        private static SiteFinder.Site _site;
        private static float _engageTime;
        private static float _nextLog;
        private static Vector3 _rotation;
        private static ForceDetector _forces;
        private static LandingPadManager _pads;

        // UI: a beam over the chosen site, and a live readout for the prompt.
        private static readonly Beacon SiteBeacon = new Beacon("LandingAssist_Site", new Color(1f, 0.55f, 0.15f, 0.8f));
        // Engaged from the ground: only count "landed" once we have been airborne.
        private static bool _wasAirborne;
        private const float LiftClearance = 10f;        // Lift straight up this far before moving sideways.
        public static bool AimValid { get; private set; }
        public static float ReadoutDistance { get; private set; }
        public static float ReadoutHeight { get; private set; }
        public static float ReadoutSink { get; private set; }

        public static bool Active => _phase != Phase.Idle;
        public static string PhaseName => _phase == Phase.Settle ? "Settling" : _phase == Phase.Descend ? "Descending" : "Flying To Site";

        public static void Update()
        {
            if (Active && (!PlayerState.AtFlightConsole() || Locator.GetShipBody() == null))
            {
                Cancel("cancelled: left the controls");
                return;
            }
            UpdateMarkers();
            if (!OWInput.IsInputMode(InputMode.ShipCockpit | InputMode.LandingCam))
            {
                return;
            }
            if (Triggered())
            {
                if (Active)
                {
                    Cancel("cancelled");
                }
                else
                {
                    Engage();
                }
            }
        }

        private static void UpdateMarkers()
        {
            if (Active && _site?.Body != null && LandingAssist.ShowBeacon)
            {
                SiteBeacon.Show(_site.Body.transform, _site.WorldPoint, _site.Up);
            }
            else
            {
                SiteBeacon.Hide();
            }

            // Whether a landing could be started right now (drives the prompt).
            AimValid = false;
            var ship = Locator.GetShipBody();
            if (!Active && ship != null && PlayerState.AtFlightConsole() && OWInput.IsInputMode(InputMode.ShipCockpit | InputMode.LandingCam))
            {
                var autopilot = ship.GetComponent<Autopilot>();
                var pads = ship.GetComponentInChildren<LandingPadManager>();
                AimValid = (autopilot == null || !autopilot.enabled) && pads != null && SiteFinder.Aim(out _, out _);
            }
        }

        private static bool Triggered()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[LandingAssist.LandKey].wasPressedThisFrame)
            {
                return true;
            }
            var gamepad = Gamepad.current;
            if (gamepad == null || LandingAssist.LandButton == null)
            {
                return false;
            }
            // Leave the D-pad to the scout camera while a tool is out.
            var tools = Locator.GetToolModeSwapper();
            if (tools != null && tools.GetToolMode() != ToolMode.None)
            {
                return false;
            }
            return gamepad[LandingAssist.LandButton.Value].wasPressedThisFrame;
        }

        private static void Engage()
        {
            var ship = Locator.GetShipBody();
            var autopilot = ship.GetComponent<Autopilot>();
            if (autopilot != null && autopilot.enabled)
            {
                Refuse("AUTOPILOT IS ACTIVE");
                return;
            }
            var pads = ship.GetComponentInChildren<LandingPadManager>();
            if (pads == null)
            {
                Refuse("NO LANDING GEAR FOUND");
                return;
            }
            if (!SiteFinder.Aim(out RaycastHit ground, out OWRigidbody body))
            {
                Refuse("NO GROUND UNDER CURSOR");
                return;
            }
            var site = SiteFinder.FindSite(body, ground.point, out string reason);
            if (site == null)
            {
                Refuse($"NO SAFE SPOT NEARBY ({reason.ToUpper()})");
                return;
            }

            if (pads.IsLanded() && Vector3.Distance(ship.GetWorldCenterOfMass(), site.WorldPoint) < 10f)
            {
                Refuse("ALREADY HERE");
                return;
            }

            _site = site;
            _phase = Phase.Travel;
            _engageTime = Time.time;
            _forces = ship.GetComponentInChildren<ForceDetector>();
            _pads = pads;
            _wasAirborne = !pads.IsLanded();
            Notify(site.DistanceFromCursor < 3f
                ? "LANDING: FLYING TO SITE"
                : $"LANDING: SAFE SITE {site.DistanceFromCursor:F0} M FROM CURSOR");
            LandingAssist.Log($"Landing on {body.name}, {Vector3.Distance(ship.GetWorldCenterOfMass(), site.WorldPoint):F0} m away");
        }

        public static void Cancel(string why)
        {
            if (!Active)
            {
                return;
            }
            _phase = Phase.Idle;
            _site = null;
            _rotation = Vector3.zero;
            Notify("LANDING " + why.ToUpper());
            LandingAssist.Log("Landing " + why);
        }

        [HarmonyPostfix]
        [HarmonyPatch("ReadTranslationalInput")]
        public static void ReadTranslationalInput_Postfix(ShipThrusterController __instance, ref Vector3 __result)
        {
            if (!Active)
            {
                return;
            }
            if (Time.time > _engageTime + GraceSeconds && PlayerSteering())
            {
                Cancel("cancelled: manual control");
                return;
            }
            var ship = Locator.GetShipBody();
            var autopilot = ship.GetComponent<Autopilot>();
            if (autopilot != null && autopilot.enabled)
            {
                Cancel("cancelled: autopilot engaged");
                return;
            }
            if (!ship.GetComponent<ShipResources>().AreThrustersUsable())
            {
                Cancel("cancelled: thrusters unusable");
                return;
            }
            __result = Step(ship, __instance.transform);
        }

        [HarmonyPostfix]
        [HarmonyPatch("ReadRotationalInput")]
        public static void ReadRotationalInput_Postfix(ref Vector3 __result)
        {
            if (Active)
            {
                __result = _rotation;
            }
        }

        private static Vector3 Step(OWRigidbody ship, Transform transform)
        {
            if (_site.Body == null || _site.GravityCenter == null)
            {
                Cancel("cancelled: landing site lost");
                return Vector3.zero;
            }
            var model = ship.GetComponent<ThrusterModel>();
            if (!_pads.IsLanded() && _pads.GetContactCount() == 0)
            {
                _wasAirborne = true;
            }

            // First leg contact after flying: settle. A firm touchdown makes the ship rock and bounce on
            // its legs, so press it down onto them and firmly damp the rotation until it is still.
            // (A leg brushing something on the way over does not count; only during the final descent.)
            if (_wasAirborne && _phase != Phase.Settle && (_pads.IsLanded() || (_phase == Phase.Descend && _pads.GetContactCount() > 0)))
            {
                _phase = Phase.Settle;
                _settleStart = Time.time;
                LandingAssist.Log($"Touchdown at {-Vector3.Dot(ship.GetVelocity() - _site.Body.GetPointVelocity(ship.GetWorldCenterOfMass()), _site.Up):F1} m/s, settling");
            }
            if (_phase == Phase.Settle)
            {
                Vector3 settleUp = (ship.GetWorldCenterOfMass() - _site.GravityCenter.position).normalized;
                Vector3 motion = ship.GetVelocity() - _site.Body.GetPointVelocity(ship.GetWorldCenterOfMass());
                bool still = model.GetAngularVelocity().magnitude < 0.05f && motion.magnitude < 0.5f && _pads.IsLanded();
                if ((still && Time.time > _settleStart + 0.3f) || Time.time > _settleStart + SettleSeconds)
                {
                    _phase = Phase.Idle;
                    _site = null;
                    _rotation = Vector3.zero;
                    Notify("LANDED");
                    LandingAssist.Log("Landed");
                    return Vector3.zero;
                }
                _rotation = RotationToward(transform, model, settleUp, firm: true);
                return transform.InverseTransformDirection(-settleUp) * SettlePress;
            }

            float maxThrust = model.GetMaxTranslationalThrust();
            var body = _site.Body;
            Vector3 spot = _site.WorldPoint;
            Vector3 center = _site.GravityCenter.position;
            Vector3 shipPos = ship.GetWorldCenterOfMass();
            Vector3 up = (shipPos - center).normalized;
            float radius = Vector3.Distance(shipPos, center);
            float spotRadius = Vector3.Distance(spot, center);

            Vector3 relVel = ship.GetVelocity() - body.GetPointVelocity(shipPos);
            float climbRate = Vector3.Dot(relVel, up);
            Vector3 toSpot = spot - shipPos;
            Vector3 horizontal = toSpot - up * Vector3.Dot(toSpot, up);
            float hDist = horizontal.magnitude;
            Vector3 hDir = hDist > 0.01f ? horizontal / hDist : Vector3.zero;
            float groundHeight = HeightAboveGround(shipPos, up, radius - spotRadius + 50f);

            // Velocity control is relative to the surface, cancelling gravity.
            Vector3 externalRel = (_forces != null ? _forces.GetForceAcceleration() : Vector3.zero) - body.GetPointAcceleration(shipPos);
            // Braking the descent uses the up-thrusters, which also hold us against gravity: plan with
            // what is left, keeping 15% spare for corrections.
            float gravityDown = Mathf.Max(0f, -Vector3.Dot(externalRel, up));
            float descentAccel = Mathf.Max(Mathf.Min(DescentAccel, 0.85f - gravityDown / maxThrust), 0.15f) * maxThrust;
            float descentLimit = DescentCurve(Mathf.Max(groundHeight - GearHeight, 0f), descentAccel);

            Vector3 desired;
            Vector3 brakingFeedForward = Vector3.zero;
            if (_phase == Phase.Travel)
            {
                // Horizontal: brake-able speed towards the point above the site.
                float horizontalAccel = 0.8f * maxThrust;
                float hSpeed = Mathf.Min(Mathf.Sqrt(2f * horizontalAccel * Mathf.Max(hDist - 2f, 0f)), MaxTravelSpeed);
                // Right by the ground (e.g. hopping from where we landed): straight up first, no scraping.
                bool lifting = groundHeight - GearHeight < LiftClearance && hDist > 6f;
                if (lifting)
                {
                    hSpeed = 0f;
                }
                if (hDist < 15f)
                {
                    hSpeed = Mathf.Min(hSpeed, hDist * 0.5f);
                }

                // Vertical: glide down while closing in (height above the site shrinks with distance,
                // capped at the travel height), but always keep clear of the ground below and ahead.
                float heightAboveSite = radius - spotRadius;
                float targetHeight = Mathf.Clamp(hDist * GlideSlope, ArrivalHeight, LandingAssist.CruiseClearance);
                float heightError = targetHeight - heightAboveSite;
                float sinkAccel = DescentAccel * maxThrust;
                float sinkLag = sinkAccel * 0.75f;
                float verticalSpeed = heightError > 0f
                    ? Mathf.Min(heightError * 0.8f, 50f)
                    : -Mathf.Min(Mathf.Sqrt(2f * sinkAccel * -heightError + sinkLag * sinkLag) - sinkLag, MaxDescentSpeed);
                verticalSpeed = Mathf.Max(verticalSpeed, -descentLimit);
                if (groundHeight < MinGroundClearance && hDist > 20f)
                {
                    verticalSpeed = Mathf.Max(verticalSpeed, 25f);
                }
                Vector3 horizontalVel = relVel - up * climbRate;
                float lookAhead = horizontalVel.magnitude * 4f + 40f;
                if (horizontalVel.magnitude > 2f
                    && Physics.SphereCast(shipPos, 8f, horizontalVel.normalized, out RaycastHit ahead, lookAhead, OWLayerMask.physicalMask, QueryTriggerInteraction.Ignore)
                    && ahead.collider.GetAttachedOWRigidbody() != ship)
                {
                    // Terrain in the way: climb and slow down until it clears.
                    verticalSpeed = 40f;
                    hSpeed = Mathf.Min(hSpeed, 10f);
                }
                if (lifting)
                {
                    verticalSpeed = Mathf.Max(verticalSpeed, 15f);
                }
                // If the glide line to the point above the site is blocked (trees, rocks round the site),
                // stop descending but keep going across: once overhead, the site's clear column takes
                // us straight down. Climbing would not help, as the line still ends just above the site.
                Vector3 approachPoint = spot + _site.Up * ArrivalHeight;
                if (hDist > 6f && verticalSpeed < 0f && PathBlocked(shipPos, approachPoint, ship))
                {
                    verticalSpeed = 0f;
                }
                desired = hDir * hSpeed + up * verticalSpeed;

                if (hDist < 6f && horizontalVel.magnitude < 3f)
                {
                    _phase = Phase.Descend;
                    LandingAssist.Log($"Over the site, descending from {groundHeight:F0} m");
                }
            }
            else
            {
                if (hDist > 25f)
                {
                    _phase = Phase.Travel; // Drifted off; go back over the site.
                    LandingAssist.Log("Drifted off the site, repositioning");
                }
                // Straight down: a braking curve that ends 8 m up (allowing for reaction lag, or the
                // ship trails the curve and lands hard), then the last stretch at touchdown speed.
                float height = Mathf.Max(groundHeight - GearHeight, 0f);
                float descent = descentLimit;
                desired = Vector3.ClampMagnitude(horizontal * 0.5f, 6f) - up * descent;
                // On the braking curve, command its deceleration up front instead of waiting to fall
                // behind it; that is what lets the slow final stretch be so short.
                float sinking = -climbRate;
                if (descent > TouchdownSpeed + 0.5f && descent < MaxDescentSpeed - 0.5f && sinking > TouchdownSpeed)
                {
                    brakingFeedForward = up * descentAccel * Mathf.Min(1f, sinking / descent);
                }
            }

            Vector3 thrust = (desired - relVel) / VelocityTimeConstant - externalRel + brakingFeedForward;

            // Stay upright (landing gear down); yaw is left alone.
            _rotation = RotationToward(transform, model, up);

            ReadoutDistance = Vector3.Distance(shipPos, spot);
            ReadoutHeight = groundHeight - GearHeight;
            ReadoutSink = -climbRate;

            if (Time.time >= _nextLog)
            {
                _nextLog = Time.time + 1f;
                LandingAssist.Log($"  {_phase}: {hDist:F0} m from site, {groundHeight:F0} m above ground, climb {climbRate:F1} m/s, sideways {(relVel - up * climbRate).magnitude:F1} m/s"
                    + $", tilt {Vector3.Angle(transform.up, up):F1} deg, spin {model.GetAngularVelocity().magnitude * Mathf.Rad2Deg:F0} deg/s");
            }

            Vector3 input = transform.InverseTransformDirection(thrust) / maxThrust;
            float largest = Mathf.Max(Mathf.Abs(input.x), Mathf.Max(Mathf.Abs(input.y), Mathf.Abs(input.z)));
            return largest > 1f ? input / largest : input;
        }

        /// <summary>
        /// Sink speed allowed at a height: a braking parabola at the given deceleration that ends at
        /// touchdown speed (FinalStretch above the feet), capped at the maximum descent speed.
        /// </summary>
        private static float DescentCurve(float height, float accel)
        {
            float td = TouchdownSpeed;
            return Mathf.Min(Mathf.Sqrt(td * td + 2f * accel * Mathf.Max(height - FinalStretch, 0f)), MaxDescentSpeed);
        }

        private static bool PathBlocked(Vector3 from, Vector3 to, OWRigidbody ship)
        {
            Vector3 line = to - from;
            float length = line.magnitude;
            if (length < 1f)
            {
                return false;
            }
            foreach (var hit in Physics.SphereCastAll(from, ApproachClearance, line / length, length, OWLayerMask.physicalMask, QueryTriggerInteraction.Ignore))
            {
                var attached = hit.collider.GetAttachedOWRigidbody();
                if (attached != ship && attached != Locator.GetPlayerBody() && hit.distance > 0f)
                {
                    return true;
                }
            }
            return false;
        }

        private static float HeightAboveGround(Vector3 shipPos, Vector3 up, float fallback)
        {
            foreach (var hit in Physics.RaycastAll(shipPos, -up, 2000f, OWLayerMask.physicalMask, QueryTriggerInteraction.Ignore))
            {
                var attached = hit.collider.GetAttachedOWRigidbody();
                if (attached != Locator.GetShipBody() && attached != Locator.GetPlayerBody())
                {
                    fallback = Mathf.Min(fallback, hit.distance);
                }
            }
            return fallback;
        }

        private static Vector3 RotationToward(Transform transform, ThrusterModel model, Vector3 desiredUp, bool firm = false)
        {
            Vector3 current = transform.up;
            Vector3 axis = Vector3.Cross(current, desiredUp);
            float sin = axis.magnitude;
            float angle = Mathf.Atan2(sin, Vector3.Dot(current, desiredUp));
            // Calm attitude hold: gentle gain, and within a small dead zone only damp the spin, so
            // tiny errors do not keep the rotation thrusters twitching.
            // Firm (settling on the legs): no dead zone and quick damping, to stop rocking.
            float deadZone = firm ? 0f : UprightDeadZone;
            Vector3 targetSpin = sin > 1e-5f && angle > deadZone
                ? axis / sin * Mathf.Min((angle - deadZone) * 1.5f, MaxTurnRate)
                : Vector3.zero;
            Vector3 angularAccel = (targetSpin - model.GetAngularVelocity()) / (firm ? 0.12f : 0.35f);
            Vector3 input = transform.InverseTransformDirection(angularAccel) / model.GetMaxRotationalThrust();
            return new Vector3(Mathf.Clamp(input.x, -1f, 1f), Mathf.Clamp(input.y, -1f, 1f), Mathf.Clamp(input.z, -1f, 1f));
        }

        private static bool PlayerSteering()
        {
            // Free look uses the same sticks but does not steer the ship.
            if (OWInput.IsPressed(InputLibrary.freeLook))
            {
                return false;
            }
            float input = Mathf.Abs(OWInput.GetValue(InputLibrary.thrustX)) + Mathf.Abs(OWInput.GetValue(InputLibrary.thrustZ))
                + Mathf.Abs(OWInput.GetValue(InputLibrary.thrustUp)) + Mathf.Abs(OWInput.GetValue(InputLibrary.thrustDown))
                + Mathf.Abs(OWInput.GetValue(InputLibrary.pitch)) + Mathf.Abs(OWInput.GetValue(InputLibrary.yaw));
            return input > 0.3f;
        }

        private static void Refuse(string why)
        {
            Notify("LANDING: " + why);
            LandingAssist.Log("Landing refused: " + why.ToLower());
        }

        private static void Notify(string text)
        {
            NotificationManager.SharedInstance?.PostNotification(new NotificationData(NotificationTarget.Ship, text, 3f));
        }
    }
}
