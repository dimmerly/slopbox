using System;
using System.Linq;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Rendering;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

public sealed partial class Player
{
    public Camera Camera { get; private set; }
    public ViewModel ViewModel { get; private set; }

    /// <summary>The held equipment as the body holds it, seen in third person and as a shadow in first.</summary>
    public ViewModel WorldModel { get; private set; }

    // Third person aim: from the eyes toward what the crosshair is over.
    private Float3 _aimDirection;
    private bool _aimConverged;

    /// <summary>Added to the settings FOV, for zooming. Equipment can set it.</summary>
    [SerializeIgnore] public float FovOffset;

    // View punch: an angular offset that springs back to zero, as in Source.
    private Float3 _punch, _punchVelocity;
    private const float PunchSpring = 65f, PunchDamping = 12f;

    private float _bobTime, _bobWeight, _roll;

    /// <summary>Whether the camera sits behind the body instead of in the eyes. Toggled with <see cref="Controls.ThirdPerson"/>.</summary>
    public bool ThirdPerson { get; private set; }

    // 0 in the eyes, 1 fully behind. Eased so the toggle glides rather than cuts.
    private float _thirdBlend, _thirdDistance;
    private bool _orbitShown;

    // Free look: how far the orbit is turned away from the aim, in degrees. Springs back when released.
    private float _freeYaw, _freePitch;
    private bool _freeHeld;

    /// <summary>True while the free look button is held, so the mouse turns the camera rather than the aim.</summary>
    public bool FreeLooking => _freeHeld;

    /// <summary>How far free look has the camera turned off the aim: 0 on it, 1 well away. For hiding the crosshair.</summary>
    public float FreeLookAmount => _freeHeld ? 1f : MathF.Min(1f, MathF.Max(MathF.Abs(_freeYaw), MathF.Abs(_freePitch)) / 8f);

    /// <summary>Whether the camera is outside the head, from the toggle or a free look still swinging back.</summary>
    public bool Orbiting => ThirdPerson || _freeHeld || MathF.Abs(_freeYaw) > 1f || MathF.Abs(_freePitch) > 1f;

    private void FreeLook(float yaw, float pitch)
    {
        _freeYaw += yaw;
        _freePitch = Maths.Clamp(Pitch + _freePitch + pitch, -80f, 80f) - Pitch;
    }
    private const float ThirdDistance = 2.6f, ThirdShoulder = 0.35f, ThirdHeight = 0.15f, CameraRadius = 0.2f;

    public void ToggleThirdPerson()
    {
        ThirdPerson = !ThirdPerson;
        if (ThirdPerson) _thirdDistance = 0f;
        ApplyViewVisibility();
    }

    /// <summary>First person draws the view model and only the shadows of the body and what it holds. Third person the reverse.</summary>
    private void ApplyViewVisibility()
    {
        _orbitShown = Orbiting;
        UpdateBodyVisibility();
        if (ViewModel.IsValid()) ViewModel.Visible = !_orbitShown;
        if (WorldModel.IsValid()) WorldModel.Shadows = _orbitShown ? ShadowCasting.On : ShadowCasting.ShadowsOnly;
    }

    private void SetupCamera()
    {
        // Reuse the scene's camera so its effects and settings stay, else make one.
        Camera = Scene.Current.FindObjectsOfType<Camera>()
            .Where(c => c.Target.IsNotValid())
            .OrderByDescending(c => c.Depth)
            .FirstOrDefault();

        if (Camera.IsNotValid())
        {
            var go = new GameObject("Player Camera") { Tag = "Main Camera" };
            Camera = go.AddComponent<Camera>();
            Scene.Current.Add(go);
        }

        Camera.GameObject.SetParent(null);
        Camera.NearClipPlane = 0.03f;
        Camera.FarClipPlane = MathF.Max(Camera.FarClipPlane, 1000f);

        if (GameManager.Instance.DefaultCameraEffects && (Camera.Profile.IsNotValid() || Camera.Profile.Components.Count == 0))
        {
            Camera.Profile = new VolumeProfile();
            Bloom bloom = Camera.Profile.Add<Bloom>();
            bloom.Intensity.Set(0.3f);
            bloom.Threshold.Set(1.1f);
            Camera.Profile.Add<Tonemapping>().Mode.Set(TonemappingMode.ACES);
            Camera.AntiAliasing = AntiAliasingMode.FXAA;
        }

        if (!Camera.GameObject.TryGetComponent(out AudioListener _))
            Camera.GameObject.AddComponent<AudioListener>();
        if (!Camera.GameObject.TryGetComponent(out MapCameraLook _))
            Camera.GameObject.AddComponent<MapCameraLook>();

        var vm = new GameObject("View Model");
        vm.SetParent(Camera.GameObject, false);
        ViewModel = vm.AddComponent<ViewModel>();

        // In a map, the held equipment is out of the sun like everything else there.
        vm.AddComponent<MapLit>();
        ViewModel.Owner = this;
        ViewModel.Shadows = ShadowCasting.Off;

        var world = new GameObject("World Model");
        world.SetParent(GameObject, false);
        WorldModel = world.AddComponent<ViewModel>();
        WorldModel.Owner = this;
        WorldModel.World = true;
        // Larger than life, as world models usually are, so it reads from behind.
        WorldModel.Scale = 1.2f;
        world.AddComponent<MapLit>();
        ApplyViewVisibility();

        UpdateCamera(0f);
    }

    /// <summary>Kicks the view by <paramref name="angles"/> degrees (pitch, yaw, roll), which springs back.</summary>
    public void ViewPunch(Float3 angles) => _punchVelocity += angles * PunchSpring * 0.15f;

    private void OnLanded(float fallSpeed)
    {
        if (fallSpeed > 2f)
        {
            Footstep(Math.Clamp(fallSpeed * 0.055f, 0.18f, 0.55f));
            _stepDistance = -0.5f;
        }
        // Small landings do nothing, hard ones dip the view like HL2's fall punch.
        if (fallSpeed > 5f) ViewPunch(new Float3(MathF.Min(fallSpeed * 0.35f, 8f), 0f, 0f));
    }

    private void UpdateCamera(float dt)
    {
        if (Camera.IsNotValid()) return;
        Settings settings = GameManager.Instance.Settings;

        // Damped spring back to zero.
        if (dt > 0f)
        {
            _punchVelocity += (-_punch * PunchSpring - _punchVelocity * PunchDamping) * dt;
            _punch += _punchVelocity * dt;
        }

        // Bob only while walking on the ground, scaled by speed.
        Float3 vel = Controller.Velocity;
        float speed = MathF.Sqrt(vel.X * vel.X + vel.Z * vel.Z);
        bool walking = Controller.IsGrounded && Controller.Mode == Controller.Walk && !Orbiting;
        float targetWeight = walking ? MathF.Min(speed / Controller.SprintSpeed, 1f) : 0f;
        _bobWeight += (targetWeight - _bobWeight) * MathF.Min(1f, dt * 8f);
        _bobTime += dt * (6f + speed * 0.9f);

        float bob = settings.ViewBob * _bobWeight;
        Float3 right = Quaternion.FromEuler(0f, Yaw, 0f) * Float3.UnitX;
        Float3 bobOffset = Float3.UnitY * (MathF.Sin(_bobTime * 2f) * 0.035f * bob) + right * (MathF.Sin(_bobTime) * 0.02f * bob);

        // Optional roll into strafes.
        float side = Float3.Dot(vel, right) / MathF.Max(Controller.SprintSpeed, 0.01f);
        _roll += (-side * settings.ViewRoll - _roll) * MathF.Min(1f, dt * 10f);

        // Free look swings back behind the aim once let go, the short way round.
        if (!_freeHeld)
        {
            float back = MathF.Min(1f, dt * 7f);
            _freeYaw = ((_freeYaw % 360f) + 540f) % 360f - 180f;
            _freeYaw -= _freeYaw * back;
            _freePitch -= _freePitch * back;
            if (MathF.Abs(_freeYaw) < 0.05f) _freeYaw = 0f;
            if (MathF.Abs(_freePitch) < 0.05f) _freePitch = 0f;
        }
        bool free = _freeYaw != 0f || _freePitch != 0f;
        if (Orbiting != _orbitShown) ApplyViewVisibility();

        Quaternion view = free
            ? Quaternion.FromEuler(Pitch + _freePitch, Yaw + _freeYaw, 0f)
            : Quaternion.FromEuler(Pitch + _punch.X, Yaw + _punch.Y, _roll + _punch.Z);
        Float3 eye = EyePosition + bobOffset * (1f - _thirdBlend);

        float target = Orbiting ? 1f : 0f;
        _thirdBlend += (target - _thirdBlend) * MathF.Min(1f, dt * 9f);
        if (MathF.Abs(target - _thirdBlend) < 0.001f) _thirdBlend = target;

        if (_thirdBlend > 0f)
        {
            // Orbit behind the head, over the right shoulder, pulled in front of anything in the way.
            Quaternion look = Quaternion.FromEuler(Pitch + _freePitch, Yaw + _freeYaw, 0f);
            Float3 pivot = EyePosition + Float3.UnitY * ThirdHeight;
            Float3 offset = look * new Float3(ThirdShoulder, 0f, -ThirdDistance) * _thirdBlend;
            float want = Float3.Length(offset);
            float allowed = want;
            if (want > 0.01f)
            {
                Float3 dir = offset / want;
                if (Scene.Current.Physics.SphereCast(pivot, CameraRadius, dir, want, out ShapeCastHit hit, QueryFilter.Default) && hit.Hit)
                    allowed = MathF.Max(0f, hit.Distance);
            }
            // Pull in at once when something blocks, ease back out when it clears.
            _thirdDistance = allowed < _thirdDistance ? allowed : _thirdDistance + (allowed - _thirdDistance) * MathF.Min(1f, dt * 5f);
            Float3 behind = want > 0.01f ? pivot + offset / want * _thirdDistance : pivot;
            eye = Maths.Lerp(eye, behind, MathF.Min(1f, _thirdBlend * 4f));
        }

        Camera.Transform.Position = eye;
        Camera.Transform.Rotation = view;
        // While free looking the crosshair is not over the aim, so keep the last converged aim.
        if (!free) ConvergeAim(eye, look: Quaternion.FromEuler(Pitch, Yaw, 0f) * Float3.UnitZ);
        Camera.FieldOfView = Maths.Clamp(settings.FieldOfView + FovOffset, 10f, 130f);
    }

    /// <summary>Finds what the crosshair is over, so third person aim from the eyes can point at it.</summary>
    private void ConvergeAim(Float3 camera, Float3 look)
    {
        _aimConverged = false;
        if (_thirdBlend <= 0f) return;

        const float Far = 1000f;
        Float3 target = Scene.Current.Physics.Raycast(camera, look, out RaycastHit hit, Far, QueryFilter.Default) ? hit.Point : camera + look * Far;
        Float3 toTarget = target - EyePosition;
        float distance = Float3.Length(toTarget);
        // Anything between the camera and the head would turn the aim round, so ignore it.
        if (distance < 0.75f || Float3.Dot(toTarget / distance, look) < 0.5f) return;
        _aimDirection = toTarget / distance;
        _aimConverged = true;
    }

    /// <summary>How far through a step the bob is and how strong, for the view model to match.</summary>
    public (float Time, float Weight) Bob => (_bobTime, _bobWeight);
}
