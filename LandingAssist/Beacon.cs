using UnityEngine;

namespace LandingAssist
{
    /// <summary>
    /// A thin vertical beam rising from the landing site, fading towards the top, drawn with a line
    /// renderer. Parented to the body it stands on so it turns with the planet; it widens with camera
    /// distance so it stays visible from afar.
    /// </summary>
    public class Beacon
    {
        private const float BeamHeight = 150f;

        private readonly string _name;
        private readonly Color _color;
        private GameObject _root;
        private LineRenderer _beam;

        private static Material _material;

        public Beacon(string name, Color color)
        {
            _name = name;
            _color = color;
        }

        public void Show(Transform parent, Vector3 worldPoint, Vector3 up)
        {
            // Scene objects are destroyed when the loop restarts; rebuild on demand.
            if (_root == null)
            {
                Build();
            }
            if (_root.transform.parent != parent)
            {
                _root.transform.SetParent(parent, true);
            }
            _root.transform.position = worldPoint;
            _root.transform.rotation = Quaternion.FromToRotation(Vector3.up, up);

            var cam = Locator.GetActiveCamera();
            float distance = cam != null ? Vector3.Distance(cam.transform.position, worldPoint) : 100f;
            _beam.widthMultiplier = Mathf.Clamp(distance * 0.002f, 0.1f, 25f);
            _root.SetActive(true);
        }

        public void Hide()
        {
            if (_root != null && _root.activeSelf)
            {
                _root.SetActive(false);
            }
        }

        private void Build()
        {
            if (_material == null)
            {
                var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
                _material = new Material(shader);
            }
            _root = new GameObject(_name);
            var go = new GameObject("Beam");
            go.transform.SetParent(_root.transform, false);
            _beam = go.AddComponent<LineRenderer>();
            _beam.useWorldSpace = false;
            _beam.sharedMaterial = _material;
            _beam.positionCount = 2;
            _beam.SetPosition(0, Vector3.zero);
            _beam.SetPosition(1, Vector3.up * BeamHeight);
            _beam.startColor = _color;
            _beam.endColor = new Color(_color.r, _color.g, _color.b, 0f);
            _beam.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _beam.receiveShadows = false;
        }
    }
}
