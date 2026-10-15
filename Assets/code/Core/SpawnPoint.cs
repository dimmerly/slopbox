using Prowl.Runtime;

namespace Slopbox;

/// <summary>Marks a place the player can spawn. With several, one is picked at random.</summary>
[AddComponentMenu("Slopbox/Spawn Point")]
public sealed class SpawnPoint : Component
{
    public override void DrawGizmos()
    {
        Debug.DrawWireSphere(Transform.Position + Prowl.Vector.Float3.UnitY * 0.9f, 0.4f, new Prowl.Vector.Color(0.94f, 0.33f, 0f, 1f));
    }
}
