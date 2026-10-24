using Prowl.Vector;

namespace Slopbox;

public struct EquipmentInput
{
    public bool Enabled, Primary, PrimaryPressed, Secondary, SecondaryPressed, Reload, Use, Shift;
    public Float2 Mouse;
    public float Wheel;
    public int Slot, Revision;
    public string KeysDown, KeysPressed;
}
