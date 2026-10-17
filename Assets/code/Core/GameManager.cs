using System.Collections.Generic;
using System.Linq;
using Prowl.PaperUI;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// The root of a running game. Drop one into a scene and it builds everything else in code: the player,
/// the HUD, the spawn catalog, tool registry and undo history. Systems reach each other through
/// <see cref="Instance"/> rather than scene references, so a scene needs nothing but this component.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Game Manager")]
public sealed class GameManager : Component
{
    private static GameManager s_instance;

    /// <summary>The running manager. Found again by search if the static was lost, such as after a script reload.</summary>
    public static GameManager Instance
    {
        get
        {
            if (s_instance.IsNotValid())
                s_instance = Scene.Current.FindObjectsOfType<GameManager>().FirstOrDefault(m => m.EnabledInHierarchy);
            return s_instance;
        }
    }

    [Header("Player")]
    [Tooltip("Where the player spawns when the scene has no SpawnPoint.")]
    public Float3 DefaultSpawn = new(0f, 0.1f, 0f);

    [Tooltip("Players below this height are respawned.")]
    public float KillHeight = -100f;

    [Tooltip("The player's humanoid body, seen in third person and as a shadow in first.")]
    public PrefabAsset PlayerModel;

    [Tooltip("Animation graph that drives the player's body from its movement and aim.")]
    public AnimationGraph PlayerAnimation;

    [Header("Content")]
    [Tooltip("The game's own sounds, played by clip name, e.g. \"click\" or \"step_grass_2\". From Assets/sounds.")]
    public List<AudioClip> Sounds = new();

    [Tooltip("Font for wordmarks and big titles.")]
    public FontAsset TitleFont;

    [Header("Physics")]
    [Tooltip("Downward acceleration in m/s², for props and the player alike. Real gravity (9.81) looks floaty at human scale; 15.2 matches gmod's 600 units/s² relative to player height.")]
    public float Gravity = 15.2f;

    [Header("Spawning")]
    [Tooltip("Resources folder that is scanned for model props, e.g. Assets/Resources/props.")]
    public string PropsFolder = "props";

    [Header("Camera")]
    [Tooltip("Adds bloom, tonemapping and FXAA to the camera when it has no effects of its own.")]
    public bool DefaultCameraEffects = true;

    public Player LocalPlayer { get; private set; }
    public SpawnCatalog Catalog { get; private set; }
    public ToolRegistry Tools { get; private set; }
    public UndoHistory Undo { get; private set; }
    public Thumbnails Thumbnails { get; private set; }
    public MaterialCatalog MaterialLibrary { get; private set; }
    public Settings Settings { get; } = new();
    public GameAudio Audio { get; private set; }

    /// <summary>The Paper instance from the last OnGui, for asking whether UI wants the pointer.</summary>
    public Paper Paper { get; internal set; }

    /// <summary>True while a menu owns the mouse, so gameplay should ignore clicks and look input.</summary>
    public bool IsMenuOpen => _menus.Count > 0;

    private readonly HashSet<object> _menus = new();

    public override void OnEnable()
    {
        if (s_instance.IsValid() && s_instance != this && s_instance.EnabledInHierarchy)
        {
            Debug.LogWarning("A second GameManager was enabled, it will be ignored.");
            return;
        }

        s_instance = this;
    }

    public override void OnDisable()
    {
        if (s_instance == this) s_instance = null;
        Input.UnlockCursor();
    }

    public override void Start()
    {
        if (Instance != this) return;

        Audio = GameObject.AddComponent<GameAudio>();
        ApplyGravity();
        Catalog = new SpawnCatalog(PropsFolder);
        MaterialLibrary = new MaterialCatalog();
        Tools = new ToolRegistry();
        Undo = new UndoHistory();

        Thumbnails = GameObject.AddComponent<Thumbnails>();
        GameObject.AddComponent<Hud>();
        GameObject.AddComponent<SpawnMenu>();
        GameObject.AddComponent<PauseMenu>();

        SpawnPlayer();
    }

    public override void Update()
    {
        if (Instance != this) return;

        if (LocalPlayer.IsValid() && LocalPlayer.Transform.Position.Y < KillHeight)
            LocalPlayer.Respawn();

        if (!IsMenuOpen && Controls.Pressed(Controls.Undo)) Undo.UndoLast();

        UpdateCursor();
    }

    public override void OnValidate()
    {
        if (Instance == this) ApplyGravity();
    }

    /// <summary>Pushes <see cref="Gravity"/> to the physics world and the player, so they always fall alike.</summary>
    public void ApplyGravity()
    {
        Scene.Current.Physics.Gravity = new Float3(0f, -Gravity, 0f);
        if (LocalPlayer.IsValid()) LocalPlayer.Controller.Gravity = Gravity;
    }

    /// <summary>Marks a menu as open or closed. The cursor is free while any menu is open.</summary>
    public void SetMenuOpen(object menu, bool open)
    {
        bool changed = open ? _menus.Add(menu) : _menus.Remove(menu);
        if (changed && menu is not SpawnMenu) GameAudio.Play(open ? "open" : "close", 0.3f, ui: true);
    }

    private void UpdateCursor()
    {
        bool wantLocked = !IsMenuOpen;
        if (wantLocked && !Input.CursorLocked) Input.LockCursor();
        else if (!wantLocked && Input.CursorLocked) Input.UnlockCursor();
    }

    // ----------------------------------------------------------------
    //  Player
    // ----------------------------------------------------------------

    private void SpawnPlayer()
    {
        (Float3 position, float yaw) = FindSpawn();
        var go = new GameObject("Player");
        go.Transform.Position = position;
        go.Transform.Rotation = Quaternion.FromEuler(0f, yaw, 0f);
        go.AddComponent<CharacterController>();
        go.AddComponent<PlayerController>();
        LocalPlayer = go.AddComponent<Player>();
        Scene.Current.Add(go);
        ApplyGravity();
    }

    /// <summary>A random SpawnPoint's position and facing, or <see cref="DefaultSpawn"/> when there are none.</summary>
    public (Float3 Position, float Yaw) FindSpawn()
    {
        var points = Scene.Current.FindObjectsOfType<SpawnPoint>();
        if (points.Length == 0) return (DefaultSpawn, 0f);
        Transform t = points[System.Random.Shared.Next(points.Length)].Transform;
        return (t.Position, t.EulerAngles.Y);
    }

    // ----------------------------------------------------------------
    //  World
    // ----------------------------------------------------------------

    /// <summary>Removes every spawned prop, like gmod's "clean up map".</summary>
    public void Cleanup()
    {
        int count = 0;
        foreach (Prop prop in Prop.All)
        {
            if (prop.IsNotValid() || prop.GameObject.IsDestroyQueued) continue;
            prop.GameObject.Destroy();
            count++;
        }

        Undo.Clear();
        Notify.Info($"Cleaned up {count} props");
    }
}
