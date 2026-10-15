using System;
using Prowl.Runtime;
using Prowl.Vector;

namespace Slopbox;

/// <summary>Registers a tool with the tool gun and the spawn menu's tool list.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ToolAttribute(string name, string description) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;

    /// <summary>The heading it is listed under in the spawn menu.</summary>
    public string Category { get; set; } = "Construction";

    /// <summary>Order within the category, lowest first, then by name.</summary>
    public int Order { get; set; }
}

/// <summary>
/// Shows a public field of a tool in the spawn menu's settings panel. bool becomes a checkbox, float and
/// int a slider between <see cref="Min"/> and <see cref="Max"/>, Color a colour picker and enums a selector.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class ToolSettingAttribute(string label) : Attribute
{
    public string Label { get; } = label;
    public float Min { get; set; }
    public float Max { get; set; } = 1f;
    public string Format { get; set; } = "0.00";
}

/// <summary>What the tool gun's trace hit.</summary>
public readonly struct ToolTrace
{
    public readonly bool Hit;
    public readonly Float3 Point, Normal;
    public readonly float Distance;
    public readonly Rigidbody3D Body;
    public readonly GameObject Object;

    public ToolTrace(bool hit, in RaycastHit info)
    {
        Hit = hit;
        if (!hit) return;
        RaycastHit h = info;
        Point = h.Point;
        Normal = h.Normal;
        Distance = h.Distance;
        Body = h.Rigidbody;
        Object = h.Transform != null ? h.Transform.GameObject : null;
    }

    /// <summary>Hit something with a dynamic or frozen body, as opposed to the static world.</summary>
    public bool HitBody => Hit && Body.IsValid();

    /// <summary>The prop that was hit, made into one if it was a plain rigidbody. Null for the world.</summary>
    public Prop Prop => HitBody ? Prop.Of(Body) : null;
}

/// <summary>
/// One mode of the tool gun, in the spirit of gmod's TOOL tables. Override the click handlers and return
/// true when the click did something, which plays the tool gun's shot effect. Public fields marked
/// <see cref="ToolSettingAttribute"/> appear in the spawn menu and keep their values while switching.
/// </summary>
public abstract class BaseTool
{
    public ToolGun Gun { get; internal set; }
    public ToolAttribute Info { get; internal set; }
    public Player Owner => Gun.Owner;

    public string Name => Info.Name;
    public string Description => Info.Description;

    public virtual string PrimaryHint => "";
    public virtual string SecondaryHint => "";
    public virtual string ReloadHint => "";

    /// <summary>A line under the tool name on the HUD, such as "Select the second object".</summary>
    public virtual string Status => "";

    public virtual bool Primary(in ToolTrace tr) => false;
    public virtual bool Secondary(in ToolTrace tr) => false;
    public virtual bool Reload(in ToolTrace tr) => false;

    /// <summary>Every frame while the tool is out, for previews.</summary>
    public virtual void Think() { }

    public virtual void OnSelect() { }

    /// <summary>Extra controls drawn under the settings in the spawn menu.</summary>
    public virtual void Inspector(Prowl.PaperUI.Paper paper) { }

    /// <summary>Called when switching away. Clear any half finished multi click action here.</summary>
    public virtual void OnDeselect() { }

    protected static UndoHistory Undo => GameManager.Instance.Undo;
}
