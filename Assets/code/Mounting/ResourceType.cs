// Ported from s&box's Sandbox.Mounting (Copyright (c) 2025 Facepunch Studios Ltd, MIT licensed).

using System;

namespace Slopbox.Mounting;

/// <summary>What resource can come out of a mounted file.</summary>
public enum ResourceType
{
    None,
    Text,
    Binary,

    /// <summary>Gives a <see cref="MountedModel"/>.</summary>
    Model,
    Scene,

    /// <summary>Gives a Texture2D.</summary>
    Texture,

    /// <summary>Gives an AudioClip.</summary>
    Sound,

    /// <summary>Gives a Material.</summary>
    Material,
    PrefabFile,
    GameResource,
}

/// <summary>Hints about how a resource can be used.</summary>
[Flags]
public enum ResourceFlags
{
    /// <summary>For developers only. Kept out of the spawn menu.</summary>
    DeveloperOnly = 1 << 0,

    /// <summary>Never treat it as a ragdoll.</summary>
    NeverRagdoll = 1 << 1,

    /// <summary>Not solid. It should get no physics.</summary>
    Effect = 1 << 2,
}
