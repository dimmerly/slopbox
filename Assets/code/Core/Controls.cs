using Prowl.Runtime;

namespace Slopbox;

/// <summary>
/// One place for every key the game reads, so bindings can be changed (or made rebindable) without
/// hunting through gameplay code. Mouse buttons are 0 left, 1 right, 2 middle.
/// </summary>
public static class Controls
{
    // Movement
    public static KeyCode Forward = KeyCode.W;
    public static KeyCode Back = KeyCode.S;
    public static KeyCode Left = KeyCode.A;
    public static KeyCode Right = KeyCode.D;
    public static KeyCode Jump = KeyCode.Space;
    public static KeyCode Duck = KeyCode.ControlLeft;
    public static KeyCode Sprint = KeyCode.ShiftLeft;
    public static KeyCode Walk = KeyCode.AltLeft;
    public static KeyCode Noclip = KeyCode.V;

    // Actions
    public static KeyCode Use = KeyCode.E;
    public static KeyCode Flashlight = KeyCode.F;
    public static KeyCode Reload = KeyCode.R;
    public static KeyCode Undo = KeyCode.Z;
    public static KeyCode SpawnMenu = KeyCode.Q;
    public static KeyCode Pause = KeyCode.Escape;
    public static KeyCode ToggleHud = KeyCode.F1;

    public const int Attack1 = 0;
    public const int Attack2 = 1;

    public static readonly KeyCode[] Slots =
    [
        KeyCode.Number1, KeyCode.Number2, KeyCode.Number3, KeyCode.Number4, KeyCode.Number5,
        KeyCode.Number6, KeyCode.Number7, KeyCode.Number8, KeyCode.Number9,
    ];

    public static bool Down(KeyCode key) => Input.GetKey(key);
    public static bool Pressed(KeyCode key) => Input.GetKeyDown(key);
    public static bool Released(KeyCode key) => Input.GetKeyUp(key);
}
