using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.PaperUI;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Runtime.Networking;
using Prowl.Courier;
using Prowl.Vector;

namespace Slopbox;

/// <summary>
/// The root of a running game. Drop one into a scene and it builds everything else in code: the player,
/// the HUD, the spawn catalog, tool registry and undo history. Systems reach each other through
/// <see cref="Instance"/> rather than scene references, so a scene needs nothing but this component.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("Slopbox/Game Manager")]
public sealed class GameManager : Component, INetworkListener
{
    private static GameManager s_instance;

    /// <summary>The running manager. Found again by search if the static was lost, such as after a script reload.</summary>
    public static GameManager Instance
    {
        get
        {
            if (s_instance.IsNotValid() && SceneInstance.HasMain)
                s_instance = SceneInstance.Active.GetAll<GameManager>().FirstOrDefault(m => m.EnabledInHierarchy);
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

    [Header("Networked prefabs")]
    [Tooltip("A player: CharacterController, PlayerController, Player, NetworkAnimator and NetworkTransform.")]
    public PrefabAsset PlayerPrefab;

    [Tooltip("The networked shell of a spawned prop: Prop, Rigidbody3D and NetworkTransform. Each peer builds the model under it.")]
    public PrefabAsset PropPrefab;

    [Tooltip("The shell of a spawned map: Prop and NetworkTransform, no rigidbody.")]
    public PrefabAsset MapPrefab;

    [Tooltip("A light prop: SphereCollider, Rigidbody3D, Prop, PointLight, LightProp and NetworkTransform.")]
    public PrefabAsset LightPrefab;

    [Tooltip("A thruster prop: CylinderCollider, Rigidbody3D, Prop, ThrusterProp and NetworkTransform.")]
    public PrefabAsset ThrusterPrefab;

    [Tooltip("A rope's drawing: RopeVisual.")]
    public PrefabAsset RopePrefab;

    [Tooltip("A rocket: RocketProjectile and NetworkTransform.")]
    public PrefabAsset RocketPrefab;

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

    public Player LocalPlayer { get; internal set; }
    internal Player ActingPlayer { get; set; }
    public SpawnCatalog Catalog { get; private set; }
    public ToolRegistry Tools => LocalPlayer.IsValid() ? LocalPlayer.Tools : _fallbackTools;
    private ToolRegistry _fallbackTools;
    public UndoHistory Undo => LocalPlayer.IsValid() ? LocalPlayer.Undo : _fallbackUndo;
    private UndoHistory _fallbackUndo;
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
        // One left from another world, such as the editor's scene while playing, doesn't count.
        if (s_instance.IsValid() && s_instance != this && s_instance.EnabledInHierarchy && s_instance.Scene == Scene)
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

        Audio = GetComponent<GameAudio>();
        if (Audio.IsNotValid()) Audio = GameObject.AddComponent<GameAudio>();
        ApplyGravity();
        Catalog = new SpawnCatalog(PropsFolder);
        MaterialLibrary = new MaterialCatalog();
        _fallbackTools = new ToolRegistry();
        _fallbackUndo = new UndoHistory();

        Thumbnails = GetComponent<Thumbnails>();
        if (Thumbnails.IsNotValid()) Thumbnails = GameObject.AddComponent<Thumbnails>();
        if (GetComponent<Hud>().IsNotValid()) GameObject.AddComponent<Hud>();
        if (GetComponent<SpawnMenu>().IsNotValid()) GameObject.AddComponent<SpawnMenu>();
        if (GetComponent<PauseMenu>().IsNotValid()) GameObject.AddComponent<PauseMenu>();
#if PROWL_EDITOR
        // After the menus, so it draws over their backdrops.
        if (GetComponent<BenchState>().IsNotValid()) GameObject.AddComponent<BenchState>();
#endif

        if (!Networking.IsClient) SpawnPlayer();
    }

    public override void Update()
    {
        if (Instance != this) return;

        if (LocalPlayer.IsValid() && LocalPlayer.Transform.Position.Y < KillHeight)
            LocalPlayer.Respawn();

        if (!IsMenuOpen && Controls.Pressed(Controls.Undo)) UndoLast();

        UpdateSession();
        UpdateCursor();
    }

    public override void OnValidate()
    {
        // Not through Instance: in the editor that would cache the edited scene's manager for play mode.
        if (s_instance == this) ApplyGravity();
    }

    /// <summary>Pushes <see cref="Gravity"/> to the physics world and the player, so they always fall alike.</summary>
    public void ApplyGravity()
    {
        Scene.Physics.Gravity = new Float3(0f, -Gravity, 0f);
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
        // A frozen player has no use for the mouse, so it is left free.
        bool wantLocked = !IsMenuOpen && !(LocalPlayer.IsValid() && LocalPlayer.Frozen);
        if (wantLocked && !Input.CursorLocked) Input.LockCursor();
        else if (!wantLocked && Input.CursorLocked) Input.UnlockCursor();
    }

    // ----------------------------------------------------------------
    //  Player
    // ----------------------------------------------------------------

    public Player SpawnPlayer(NetPeer owner = null)
    {
        (Float3 position, float yaw) = FindSpawn();
        GameObject go = Networking.SpawnPrefab(PlayerPrefab, position, Quaternion.FromEuler(0f, yaw, 0f), owner);
        go.Name = owner == null ? "Player" : owner.Name;
        Player player = go.GetComponent<Player>();
        go.GetComponent<PlayerController>().Gravity = Gravity;
        if (owner == null || owner.IsLocal) LocalPlayer = player;
        ApplyGravity();
        return player;
    }

    /// <summary>A random SpawnPoint's position and facing, or <see cref="DefaultSpawn"/> when there are none.</summary>
    public (Float3 Position, float Yaw) FindSpawn()
    {
        var points = Scene.GetAll<SpawnPoint>();
        if (points.Length == 0) return (DefaultSpawn, 0f);
        Transform t = points[System.Random.Shared.Next(points.Length)].Transform;
        return (t.Position, t.EulerAngles.Y);
    }

    // ----------------------------------------------------------------
    //  World
    // ----------------------------------------------------------------

    /// <summary>Removes every spawned prop, like gmod's "clean up map".</summary>
    public void UndoLast()
    {
        if (Networking.IsClient) { if (LocalPlayer.IsValid()) LocalPlayer.RequestUndo(); }
        else Undo.UndoLast();
    }

    public void Cleanup()
    {
        if (Networking.IsClient) { Notify.Error("Only the host can clean up the map"); return; }
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
    private readonly Dictionary<int, Player> _players = new();
    private bool _hadSession;
    public static string NetworkStatus { get; private set; } = "Single player";

    public void Host(string name)
    {
        try
        {
            // Prefab instances made before this, the world's props and players, spawn with the session.
            Networking.Host(string.IsNullOrWhiteSpace(name) ? "Host" : name.Trim());
            NetworkStatus = $"Hosting on port {Networking.LocalPort}";
        }
        catch (Exception e) { NetworkStatus = e.Message; Notify.Error(NetworkStatus); }
    }

    public void Join(string address, string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(address)) { Notify.Error("Enter a host address"); return; }
            Networking.Join(address.Trim(), string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim());
            NetworkStatus = "Connecting...";
        }
        catch (Exception e) { NetworkStatus = e.Message; Notify.Error(NetworkStatus); }
    }

    public void LeaveSession()
    {
        Networking.Shutdown();
        NetworkStatus = "Single player";
    }

    public void OnPeerReady(NetPeer peer)
    {
        if (peer.IsLocal || _players.ContainsKey(peer.Id)) return;
        _players[peer.Id] = GameManager.Instance.SpawnPlayer(peer);
    }

    public void OnPeerDisconnected(NetPeer peer, SessionEndReason reason)
    {
        if (_players.Remove(peer.Id, out Player player) && player.IsValid()) player.GameObject.Destroy();
    }

    public void OnNetworkReady() => NetworkStatus = Networking.IsHost
        ? $"Hosting on port {Networking.LocalPort}" : "Connected";

    private void UpdateSession()
    {
        if (Networking.Session != null) { _hadSession = true; return; }
        if (!_hadSession) return;
        _hadSession = false;
        foreach (Player player in Scene.GetAll<Player>())
            if (player != GameManager.Instance.LocalPlayer) player.GameObject.Destroy();
        _players.Clear();
        if (GameManager.Instance.LocalPlayer.IsNotValid()) GameManager.Instance.SpawnPlayer();
        NetworkStatus = "Disconnected — single player";
    }
}
