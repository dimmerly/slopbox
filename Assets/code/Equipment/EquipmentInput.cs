using Prowl.Vector;

namespace Slopbox;

public struct EquipmentInput
{
    public bool Enabled, Primary, PrimaryPressed, Secondary, SecondaryPressed, Reload, Use, Shift;
    public Float2 Mouse;
    public float Wheel;
    public int Slot, Revision;
    public string KeysDown, KeysPressed;

    internal readonly bool SameState(in EquipmentInput other)
        => Enabled == other.Enabled && Primary == other.Primary && Secondary == other.Secondary
           && Use == other.Use && Shift == other.Shift && Slot == other.Slot && KeysDown == other.KeysDown;

    internal readonly bool HasEvents => PrimaryPressed || SecondaryPressed || Reload
        || Mouse != Float2.Zero || Wheel != 0 || !string.IsNullOrEmpty(KeysPressed);
}
