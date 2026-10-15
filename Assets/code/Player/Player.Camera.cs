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

    /// <summary>Added to the settings FOV, for zooming. Equipment can set it.</summary>
    [SerializeIgnore] public float FovOffset;

    // View punch: an angular offset that springs back to zero, as in Source.
    private Float3 _punch, _punchVelocity;
    private const float PunchSpring = 65f, PunchDamping = 12f;

    private float _bobTime, _bobWeight, _roll;

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

        var vm = new GameObject("View Model");
        vm.SetParent(Camera.GameObject, false);
        ViewModel = vm.AddComponent<ViewModel>();
        ViewModel.Owner = this;

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
        bool walking = Controller.IsGrounded && Controller.Mode == Controller.Walk;
        float targetWeight = walking ? MathF.Min(speed / Controller.SprintSpeed, 1f) : 0f;
        _bobWeight += (targetWeight - _bobWeight) * MathF.Min(1f, dt * 8f);
        _bobTime += dt * (6f + speed * 0.9f);

        float bob = settings.ViewBob * _bobWeight;
        Float3 right = Quaternion.FromEuler(0f, Yaw, 0f) * Float3.UnitX;
        Float3 bobOffset = Float3.UnitY * (MathF.Sin(_bobTime * 2f) * 0.035f * bob) + right * (MathF.Sin(_bobTime) * 0.02f * bob);

        // Optional roll into strafes.
        float side = Float3.Dot(vel, right) / MathF.Max(Controller.SprintSpeed, 0.01f);
        _roll += (-side * settings.ViewRoll - _roll) * MathF.Min(1f, dt * 10f);

        Camera.Transform.Position = EyePosition + bobOffset;
        Camera.Transform.Rotation = Quaternion.FromEuler(Pitch + _punch.X, Yaw + _punch.Y, _roll + _punch.Z);
        Camera.FieldOfView = Maths.Clamp(settings.FieldOfView + FovOffset, 10f, 130f);
    }

    /// <summary>How far through a step the bob is and how strong, for the view model to match.</summary>
    public (float Time, float Weight) Bob => (_bobTime, _bobWeight);
}
