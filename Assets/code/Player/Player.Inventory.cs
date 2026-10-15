using System.Collections.Generic;
using Prowl.Runtime;

namespace Slopbox;

public sealed partial class Player
{
    private readonly List<Equipment> _equipment = new();
    private int _activeIndex = -1;

    public IReadOnlyList<Equipment> Equipment => _equipment;
    public Equipment ActiveEquipment => _activeIndex >= 0 && _activeIndex < _equipment.Count ? _equipment[_activeIndex] : null;
    public int ActiveIndex => _activeIndex;

    /// <summary>When the selection last changed, for the HUD's weapon bar.</summary>
    public float SwitchedAt { get; private set; } = -100f;

    private void SetupInventory()
    {
        Give(new Physgun());
        Give(new GravityGun());
        Give(new ToolGun());
        Give(new RocketLauncher());
        Switch(0);
    }

    public T Give<T>(T equipment) where T : Equipment
    {
        equipment.Owner = this;
        _equipment.Add(equipment);
        return equipment;
    }

    public T Get<T>() where T : Equipment
    {
        foreach (Equipment e in _equipment)
            if (e is T match) return match;
        return null;
    }

    public void Switch(int index)
    {
        if (index < 0 || index >= _equipment.Count) return;
        SwitchedAt = Time.TimeSinceStartup;
        if (index == _activeIndex) return;

        ActiveEquipment?.OnHolster();
        _activeIndex = index;
        ViewModel.Rebuild(ActiveEquipment);
        ActiveEquipment.OnEquip();
        GameAudio.Play("equip", 0.35f, 0.96f + System.Random.Shared.NextSingle() * 0.08f);
    }

    public void Switch(Equipment equipment) => Switch(_equipment.IndexOf(equipment));

    private void UpdateInventory()
    {
        if (InputEnabled)
        {
            for (int i = 0; i < Controls.Slots.Length && i < _equipment.Count; i++)
                if (Controls.Pressed(Controls.Slots[i])) Switch(i);

            float wheel = Input.MouseWheelDelta;
            Equipment active = ActiveEquipment;
            if (wheel != 0f && (active == null || !active.CapturesScroll) && _equipment.Count > 0)
            {
                int step = wheel < 0f ? 1 : -1;
                Switch(((_activeIndex + step) % _equipment.Count + _equipment.Count) % _equipment.Count);
            }
        }

        ActiveEquipment?.Update();
    }

    private void FixedUpdateInventory() => ActiveEquipment?.FixedUpdate();

    private void LateUpdateInventory()
    {
        ViewModel.Animate(Time.DeltaTime);
        ActiveEquipment?.LateUpdate();
    }
}
