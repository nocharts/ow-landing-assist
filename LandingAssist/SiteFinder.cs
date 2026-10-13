using System.Linq;
using UnityEngine;

namespace LandingAssist
{
    /// <summary>
    /// Finds where the cursor points on the ground, then searches outward from there for a spot the
    /// ship can sit on: flat enough, level across the ship's footprint, nothing overhead, and not in
    /// water, sand, plasma or a hazard.
    /// </summary>
    public static class SiteFinder
    {
        // Rough half-width of the ship's landing footprint, with some margin.
        private const float FootprintRadius = 6f;
        // Wider space the ship needs around and above the spot (branches, trunks, rocks).
        private const float ClearanceRadius = 9f;
        private const float ClearanceHeight = 40f;
        private const float FootprintLevelTolerance = 1.2f;
        private const float SearchSpacing = 5f;

        public class Site
        {
            public OWRigidbody Body;          // What we land on (a planet, moon, island...).
            public Transform GravityCenter;   // What "up" points away from.
            public Vector3 LocalPoint;        // Landing point in Body's local space.
            public float SlopeDegrees;
            public float DistanceFromCursor;

            public Vector3 WorldPoint => Body.transform.TransformPoint(LocalPoint);
            public Vector3 Up => (WorldPoint - GravityCenter.position).normalized;
        }

        /// <summary>What the cursor (centre of the active camera) is pointing at on solid ground.</summary>
        public static bool Aim(out RaycastHit ground, out OWRigidbody body)
        {
            ground = default;
            body = null;
            var cam = Locator.GetActiveCamera();
            if (cam == null)
            {
                return false;
            }
            var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, LandingAssist.MaxRange,
                OWLayerMask.physicalMask, QueryTriggerInteraction.Ignore);
            foreach (var hit in hits.OrderBy(h => h.distance))
            {
                var attached = hit.collider.GetAttachedOWRigidbody();
                if (attached == null || attached == Locator.GetShipBody() || attached == Locator.GetPlayerBody())
                {
                    continue;
                }
                ground = hit;
                body = attached;
                return true;
            }
            return false;
        }

        public static Site FindSite(OWRigidbody body, Vector3 aimPoint, out string reason)
        {
            reason = null;
            var gravityCenter = GravityCenterFor(body, aimPoint);
            Vector3 up = (aimPoint - gravityCenter.position).normalized;
            Vector3 t1 = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 t2 = Vector3.Cross(up, t1);

            int checkedSpots = 0;
            string lastRejection = "no ground";
            for (float ring = 0f; ring <= LandingAssist.SearchRadius; ring += SearchSpacing)
            {
                Site best = null;
                int count = ring <= 0f ? 1 : Mathf.Max(6, Mathf.FloorToInt(2f * Mathf.PI * ring / SearchSpacing));
                for (int i = 0; i < count; i++)
                {
                    float angle = i * Mathf.PI * 2f / count;
                    Vector3 probe = aimPoint + (t1 * Mathf.Cos(angle) + t2 * Mathf.Sin(angle)) * ring;
                    checkedSpots++;
                    if (!Evaluate(body, probe, up, out Vector3 point, out float slope, out string rejection))
                    {
                        lastRejection = rejection;
                        continue;
                    }
                    if (best == null || slope < best.SlopeDegrees)
                    {
                        best = new Site
                        {
                            Body = body,
                            GravityCenter = gravityCenter,
                            LocalPoint = body.transform.InverseTransformPoint(point),
                            SlopeDegrees = slope,
                            DistanceFromCursor = Vector3.Distance(point, aimPoint),
                        };
                    }
                }
                // Closest ring with any good spot wins (flattest within it).
                if (best != null)
                {
                    LandingAssist.Log($"Site found {best.DistanceFromCursor:F0} m from the cursor, slope {best.SlopeDegrees:F1} deg ({checkedSpots} spots checked)");
                    return best;
                }
            }
            reason = lastRejection;
            LandingAssist.Log($"No safe site within {LandingAssist.SearchRadius:F0} m ({checkedSpots} spots checked, last: {lastRejection})");
            return null;
        }

        private static bool Evaluate(OWRigidbody body, Vector3 probe, Vector3 up, out Vector3 point, out float slope, out string rejection)
        {
            point = default;
            slope = 90f;
            rejection = null;

            if (!GroundBelow(body, probe, up, 80f, out RaycastHit center))
            {
                rejection = "no ground";
                return false;
            }
            point = center.point;
            slope = Vector3.Angle(center.normal, up);
            if (slope > LandingAssist.MaxSlope)
            {
                rejection = $"too steep ({slope:F0} deg)";
                return false;
            }

            // Level across the footprint.
            Vector3 t1 = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            Vector3 t2 = Vector3.Cross(up, t1);
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI / 3f;
                Vector3 foot = point + (t1 * Mathf.Cos(a) + t2 * Mathf.Sin(a)) * FootprintRadius;
                if (!GroundBelow(body, foot + up * 10f, up, 25f, out RaycastHit footHit))
                {
                    rejection = "edge or drop";
                    return false;
                }
                float height = Vector3.Dot(footHit.point - point, up);
                if (Mathf.Abs(height) > FootprintLevelTolerance || Vector3.Angle(footHit.normal, up) > LandingAssist.MaxSlope + 10f)
                {
                    rejection = "uneven";
                    return false;
                }
            }

            // Nothing overhead (trees, arches, overhangs): a ship-wide sweep from above must land on the spot.
            const float above = 120f;
            if (Physics.SphereCast(point + up * above, FootprintRadius, -up, out RaycastHit sweep, above,
                    OWLayerMask.physicalMask, QueryTriggerInteraction.Ignore)
                && sweep.distance < above - FootprintRadius - FootprintLevelTolerance - 2f
                && sweep.collider.GetAttachedOWRigidbody() != Locator.GetShipBody())
            {
                rejection = "something overhead";
                return false;
            }

            // Room around and above the spot: nothing but the ground itself within the ship's reach.
            foreach (var collider in Physics.OverlapCapsule(point + up * (ClearanceRadius + 3.5f), point + up * ClearanceHeight,
                         ClearanceRadius, OWLayerMask.physicalMask, QueryTriggerInteraction.Ignore))
            {
                var attached = collider.GetAttachedOWRigidbody();
                if (attached != Locator.GetShipBody() && attached != Locator.GetPlayerBody())
                {
                    rejection = "trees or rocks too close";
                    return false;
                }
            }

            // Water, sand, plasma, hazards and anything that destroys the ship.
            foreach (var collider in Physics.OverlapSphere(point + up * 2f, FootprintRadius, ~0, QueryTriggerInteraction.Collide))
            {
                var fluid = collider.GetComponent<FluidVolume>();
                if (fluid != null && !fluid.IsAtmosphereFluid())
                {
                    rejection = $"in {fluid.GetFluidType().ToString().ToLower()}";
                    return false;
                }
                if (collider.GetComponent<HazardVolume>() != null || collider.GetComponent<DestructionVolume>() != null)
                {
                    rejection = "hazard";
                    return false;
                }
            }
            return true;
        }

        private static bool GroundBelow(OWRigidbody body, Vector3 from, Vector3 up, float depth, out RaycastHit hit)
        {
            Vector3 start = from + up * (depth * 0.5f);
            foreach (var h in Physics.RaycastAll(start, -up, depth * 1.5f, OWLayerMask.physicalMask, QueryTriggerInteraction.Ignore).OrderBy(x => x.distance))
            {
                var attached = h.collider.GetAttachedOWRigidbody();
                if (attached == Locator.GetShipBody() || attached == Locator.GetPlayerBody())
                {
                    continue;
                }
                hit = h;
                // Must be the same thing we aimed at (not a neighbouring floating island, say).
                return attached == body;
            }
            hit = default;
            return false;
        }

        /// <summary>The nearest planet or moon centre: "up" is away from it.</summary>
        private static Transform GravityCenterFor(OWRigidbody body, Vector3 point)
        {
            var own = body.GetComponent<AstroObject>();
            if (own != null)
            {
                return body.transform;
            }
            AstroObject nearest = null;
            float best = float.MaxValue;
            foreach (var astro in Object.FindObjectsOfType<AstroObject>())
            {
                if (astro.GetOWRigidbody() == null || astro.GetAstroObjectName() == AstroObject.Name.Sun)
                {
                    continue;
                }
                float d = Vector3.Distance(astro.GetOWRigidbody().GetWorldCenterOfMass(), point);
                if (d < best)
                {
                    best = d;
                    nearest = astro;
                }
            }
            return nearest != null ? nearest.GetOWRigidbody().transform : body.transform;
        }
    }
}
