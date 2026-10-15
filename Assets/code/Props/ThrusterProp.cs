using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// A placed thruster: a small nozzle that, while its key is held, pushes whatever it is welded to away
/// from the side its exhaust points. The exhaust is the thruster's local +Y, so the push is along -Y.
/// Made by the <see cref="ThrusterTool"/>.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Thruster Prop")]
public sealed class ThrusterProp : Component
{
    public const float Radius = 0.12f;
    public const float Height = 0.2f;

    /// <summary>Newtons.</summary>
    public float Force = 1000f;
    /// <summary>Unknown when unbound.</summary>
    public KeyCode Forward = KeyCode.Keypad8;
    public KeyCode Back = KeyCode.Keypad2;
    /// <summary>A press turns it on and the next turns it off, instead of firing only while held.</summary>
    public bool Toggle;

    /// <summary>+1 pushing, -1 pulling, 0 off.</summary>
    [SerializeIgnore] private int _thrust;
    [SerializeIgnore] private MeshRenderer _flame;

    private static Material s_body, s_flame;

    /// <summary>Builds a thruster, detached from the scene, ready to be positioned and added.</summary>
    public static GameObject Create(float force, KeyCode forward, KeyCode back, bool toggle)
    {
        var go = new GameObject("Thruster");
        var collider = go.AddComponent<CylinderCollider>();
        collider.Radius = Radius;
        collider.Height = Height;
        go.AddComponent<Rigidbody3D>().Mass = 1f;
        go.AddComponent<Prop>();

        if (s_body.IsNotValid()) s_body = Materials.Lit(new Color(0.2f, 0.2f, 0.22f, 1f), 0.8f, 0.4f);
        if (s_flame.IsNotValid()) s_flame = Materials.Lit(new Color(0f, 0f, 0f, 1f), 0f, 1f).Emissive(new Color(1f, 0.55f, 0.15f, 1f), 6f);

        var nozzle = new GameObject("Nozzle");
        nozzle.SetParent(go, false);
        nozzle.Transform.LocalScale = new Float3(Radius * 2f, Height, Radius * 2f);
        var renderer = nozzle.AddComponent<MeshRenderer>();
        renderer.Mesh = Shapes.Cylinder;
        renderer.Material = s_body;

        var flame = new GameObject("Flame");
        flame.SetParent(go, false);
        flame.Transform.LocalPosition = new Float3(0f, Height * 0.5f + 0.15f, 0f);
        flame.Transform.LocalScale = new Float3(Radius * 1.3f, 0.3f, Radius * 1.3f);
        var glow = flame.AddComponent<MeshRenderer>();
        glow.Mesh = Shapes.Cylinder;
        glow.Material = s_flame;
        glow.Enabled = false;

        var prop = go.AddComponent<ThrusterProp>();
        prop.Set(force, forward, back, toggle);
        return go;
    }

    public void Set(float force, KeyCode forward, KeyCode back, bool toggle)
    {
        Force = force;
        Forward = forward;
        Back = back;
        Toggle = toggle;
        _thrust = 0;
    }

    public override void Update()
    {
        // Like player movement, keys still work with the spawn menu open but not while paused or typing.
        GameManager game = GameManager.Instance;
        bool paused = game.IsMenuOpen && Time.TimeScale <= 0f;
        bool typing = game.Paper != null && game.Paper.WantsCaptureKeyboard;

        if (!paused && !typing && !SpawnMenu.IsBindingKey)
        {
            if (Toggle)
            {
                if (Pressed(Forward)) _thrust = _thrust == 1 ? 0 : 1;
                if (Pressed(Back)) _thrust = _thrust == -1 ? 0 : -1;
            }
            else
            {
                _thrust = (Down(Forward) ? 1 : 0) - (Down(Back) ? 1 : 0);
            }
        }

        if (_flame.IsNotValid())
            foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>())
                if (r.GameObject.Name == "Flame") _flame = r;
        if (_flame.IsValid()) _flame.Enabled = _thrust != 0;
        GameAudio.SetLoop(this, "thruster", _thrust != 0 && !paused, Transform.Position, true, 0.22f);
    }

    public override void OnDisable() => GameAudio.SetLoop(this, "thruster", false);

    public override void FixedUpdate()
    {
        if (_thrust == 0) return;

        // Push the welded body directly at the nozzle, so a heavy prop is not dragged through the weld
        // by a one kilogram thruster.
        FixedAngleConstraint weld = GetComponent<FixedAngleConstraint>();
        Rigidbody3D body = weld.IsValid() ? weld.ConnectedBody : null;
        if (body.IsNotValid() || body.MotionType != Jitter2.Dynamics.MotionType.Dynamic) body = GetComponent<Rigidbody3D>();
        if (body.IsNotValid()) return;

        Float3 direction = Transform.Rotation * -Float3.UnitY;
        body.SetActive(true);
        body.AddForceAtPosition(direction * (Force * _thrust), Transform.Position);
    }

    private static bool Usable(KeyCode key) => key != KeyCode.Unknown && key != SpawnMenu.SwallowedKey;
    private static bool Down(KeyCode key) => Usable(key) && Controls.Down(key);
    private static bool Pressed(KeyCode key) => Usable(key) && Controls.Pressed(key);
}
