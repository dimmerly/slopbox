using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// The local player: turns input into view angles and a <see cref="MoveInput"/> for the
/// <see cref="PlayerController"/>, owns the camera and the equipment. Split across partial files by
/// concern (camera, inventory), the way dxrp's Player is.
/// </summary>
[RequireComponent(typeof(PlayerController))]
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Player")]
public sealed partial class Player : Component
{
    public static Player Local { get; private set; }

    /// <summary>Degrees. Pitch is positive looking down and clamped to straight up or down.</summary>
    public float Yaw, Pitch;

    public PlayerController Controller { get; private set; }

    /// <summary>Set by equipment each frame to take the mouse for itself (the physgun's rotate mode).</summary>
    [SerializeIgnore] public bool LookLocked;

    private bool _menuWasOpen;

    // A player spawned from another component's Start gets that frame's LateUpdate before its own
    // Start, so every callback waits until the camera and equipment exist.
    private bool _ready;

    /// <summary>False while a menu is open: equipment should ignore attacks.</summary>
    public bool InputEnabled => !GameManager.Instance.IsMenuOpen;

    public Float3 EyePosition => Controller.EyePosition;
    public Quaternion EyeRotation => Quaternion.FromEuler(Pitch, Yaw, 0f);
    /// <summary>
    /// Where the player aims from the eyes. In third person it bends toward what the crosshair is over, since the
    /// camera sits off to one side, so traces still land under the crosshair.
    /// </summary>
    public Float3 Forward => ThirdPerson && _aimConverged ? _aimDirection : EyeRotation * Float3.UnitZ;
    public Ray AimRay => new(EyePosition, Forward);

    /// <summary>The muzzle of whichever model of the held equipment is being seen, where beams and shots start.</summary>
    public Float3 MuzzlePosition => _orbitShown && WorldModel.IsValid() && WorldModel.HasModel ? WorldModel.MuzzlePosition : ViewModel.MuzzlePosition;

    /// <summary>Recoil on both models of the held equipment.</summary>
    public void Kick(float amount = 1f)
    {
        ViewModel.Kick(amount);
        if (WorldModel.IsValid()) WorldModel.Kick(amount);
    }

    public override void OnEnable()
    {
        Controller = GetComponent<PlayerController>();
        Local = this;
        Yaw = Transform.EulerAngles.Y;
        Controller.Landed += OnLanded;
        Controller.Jumped += OnJumped;
        Controller.Landed += OnLandedBody;
        Controller.Jumped += OnJumpedBody;
    }

    public override void OnDisable()
    {
        Controller.Landed -= OnLanded;
        Controller.Jumped -= OnJumped;
        Controller.Landed -= OnLandedBody;
        Controller.Jumped -= OnJumpedBody;
        if (Local == this) Local = null;
    }

    public override void Start()
    {
        SetupCamera();
        SetupInventory();
        SetupFlashlight();
        SetupBody();
        _ready = true;
    }

    public override void Update()
    {
        if (!_ready) return;
        float dt = Time.DeltaTime;
        GameManager game = GameManager.Instance;

        // The frame a menu closes the cursor jumps back to the centre, which reads as one huge mouse move.
        _freeHeld = !game.IsMenuOpen && Input.GetMouseButton(Controls.FreeLook);
        if (!game.IsMenuOpen && !_menuWasOpen && !LookLocked) UpdateLook();
        _menuWasOpen = game.IsMenuOpen;
        LookLocked = false;

        if (!game.IsMenuOpen && Controls.Pressed(Controls.Noclip)) Controller.ToggleNoclip();
        if (!game.IsMenuOpen && Controls.Pressed(Controls.ThirdPerson)) ToggleThirdPerson();
        UpdateFlashlight();

        Controller.Simulate(ReadMoveInput(), dt);
        UpdateBody(dt);
        UpdateMovementAudio(dt);
        UpdateInventory();
    }

    public override void FixedUpdate()
    {
        if (_ready) FixedUpdateInventory();
    }

    public override void LateUpdate()
    {
        if (!_ready) return;
        LateUpdateBody();
        UpdateCamera(Time.DeltaTime);
        LateUpdateInventory();
    }

    private void UpdateLook()
    {
        Settings settings = GameManager.Instance.Settings;
        float scale = 0.022f * settings.Sensitivity;
        Float2 delta = Input.MouseDelta;

        // Free look takes the mouse for the camera alone; the aim stays where it was.
        if (FreeLooking)
        {
            FreeLook(delta.X * scale, delta.Y * scale * (settings.InvertY ? -1f : 1f));
            return;
        }

        Yaw = (Yaw + delta.X * scale) % 360f;
        Pitch = Maths.Clamp(Pitch + delta.Y * scale * (settings.InvertY ? -1f : 1f), -89f, 89f);
    }

    private MoveInput ReadMoveInput()
    {
        var input = new MoveInput { Yaw = Yaw, Pitch = Pitch };

        // Walking keeps working while a menu is open, like gmod. Nothing else reaches the game.
        GameManager game = GameManager.Instance;
        bool typing = game.Paper != null && game.Paper.WantsCaptureKeyboard;
        if (typing) return input;

        if (Controls.Down(Controls.Forward)) input.Move.Y += 1f;
        if (Controls.Down(Controls.Back)) input.Move.Y -= 1f;
        if (Controls.Down(Controls.Right)) input.Move.X += 1f;
        if (Controls.Down(Controls.Left)) input.Move.X -= 1f;

        input.Jump = Controls.Down(Controls.Jump);
        input.JumpPressed = Controls.Pressed(Controls.Jump);
        input.Duck = Controls.Down(Controls.Duck);
        input.Sprint = Controls.Down(Controls.Sprint);
        input.Walk = Controls.Down(Controls.Walk);
        input.Vertical = (input.Jump ? 1f : 0f) - (input.Duck ? 1f : 0f);
        return input;
    }

    // ----------------------------------------------------------------
    //  Traces
    // ----------------------------------------------------------------

    /// <summary>Traces along the aim. The player has no body, so nothing needs ignoring.</summary>
    public bool Trace(float distance, out RaycastHit hit)
        => Scene.Current.Physics.Raycast(EyePosition, Forward, out hit, distance, QueryFilter.Default);

    /// <summary>Back to a spawn point, standing still and looking level.</summary>
    public void Respawn()
    {
        (Float3 position, float yaw) = GameManager.Instance.FindSpawn();
        Controller.Teleport(position);
        Yaw = yaw;
        Pitch = 0f;
        _stepDistance = 0f;
        GameAudio.Play("spawn", 0.4f, 1.15f);
    }
}
