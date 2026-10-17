using System.Collections.Generic;
using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

/// <summary>A key and what it does, shown in the HUD's hint panel.</summary>
public readonly record struct InputHint(string Key, string Action);

/// <summary>
/// Something the player holds: physgun, gravity gun, tool gun, later weapons. These are plain objects
/// owned by the <see cref="Player"/>'s inventory, not components, so they keep their state (a held
/// prop, tool settings) across switching without any scene bookkeeping. The player forwards the
/// frame callbacks to whichever one is out.
/// </summary>
public abstract class Equipment
{
    public Player Owner { get; internal set; }

    public abstract string Name { get; }
    public virtual string Description => "";

    /// <summary>Accent used by the HUD and effects.</summary>
    public virtual Color Tint => Theme.Accent;

    /// <summary>True while the mouse wheel belongs to this instead of weapon switching.</summary>
    public virtual bool CapturesScroll => false;

    /// <summary>Controls shown on the HUD while this is out.</summary>
    public virtual IEnumerable<InputHint> Hints => [];

    public bool IsActive => Owner.IsValid() && Owner.ActiveEquipment == this;

    public virtual void OnEquip() { }
    public virtual void OnHolster() { }

    /// <summary>Where models from Half-Life 2 are looked for: the game itself, then the copy Garry's Mod ships with.</summary>
    protected static readonly string[] HalfLife2 = ["hl2", "garrysmod"];

    /// <summary>
    /// Adds this equipment's parts to the view model. Called on every equip, and again when a game finishes
    /// mounting, so equipment can use a mounted game's model and build its own parts without one.
    /// </summary>
    public virtual void BuildViewModel(ViewModel vm) { }

    /// <summary>Every frame while out, after movement.</summary>
    public virtual void Update() { }

    /// <summary>Every physics step while out.</summary>
    public virtual void FixedUpdate() { }

    /// <summary>Every frame while out, after the camera moved, for beams and other effects drawn from the muzzle.</summary>
    public virtual void LateUpdate() { }

    // Input helpers that respect open menus.
    protected bool Attack1Pressed => Owner.InputEnabled && Input.GetMouseButtonDown(Controls.Attack1);
    protected bool Attack1Down => Owner.InputEnabled && Input.GetMouseButton(Controls.Attack1);
    protected bool Attack2Pressed => Owner.InputEnabled && Input.GetMouseButtonDown(Controls.Attack2);
    protected bool Attack2Down => Owner.InputEnabled && Input.GetMouseButton(Controls.Attack2);
    protected bool ReloadPressed => Owner.InputEnabled && Controls.Pressed(Controls.Reload);
    protected bool UseDown => Owner.InputEnabled && Controls.Down(Controls.Use);
}
