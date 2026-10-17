using System;
using System.Collections.Generic;
using Prowl.Vector;
using Slopbox.Mounting.GoldSrc.Bsp;
using Slopbox.Mounting.GoldSrc.Mdl;
using Vector3 = System.Numerics.Vector3;

namespace Slopbox.Mounting.GoldSrc;

/// <summary>The models a GoldSrc map's point entities show: its NPCs, items, weapons and ammo.</summary>
internal static class MapEntities
{
    /// <summary>What each entity class looks like in the game, where the class names its own model.</summary>
    private static readonly Dictionary<string, string> s_models = new(StringComparer.OrdinalIgnoreCase)
    {
        // NPCs
        ["monster_scientist"] = "models/scientist.mdl",
        ["monster_sitting_scientist"] = "models/scientist.mdl",
        ["monster_scientist_dead"] = "models/scientist.mdl",
        ["monster_barney"] = "models/barney.mdl",
        ["monster_barney_dead"] = "models/barney.mdl",
        ["monster_gman"] = "models/gman.mdl",
        ["monster_human_grunt"] = "models/hgrunt.mdl",
        ["monster_hgrunt_dead"] = "models/hgrunt.mdl",
        ["monster_human_assassin"] = "models/hassassin.mdl",
        ["monster_hevsuit_dead"] = "models/player.mdl",
        ["monster_headcrab"] = "models/headcrab.mdl",
        ["monster_babycrab"] = "models/baby_headcrab.mdl",
        ["monster_zombie"] = "models/zombie.mdl",
        ["monster_houndeye"] = "models/houndeye.mdl",
        ["monster_bullchicken"] = "models/bullsquid.mdl",
        ["monster_alien_slave"] = "models/islave.mdl",
        ["monster_vortigaunt"] = "models/islave.mdl",
        ["monster_alien_grunt"] = "models/agrunt.mdl",
        ["monster_alien_controller"] = "models/controller.mdl",
        ["monster_barnacle"] = "models/barnacle.mdl",
        ["monster_ichthyosaur"] = "models/icky.mdl",
        ["monster_leech"] = "models/leech.mdl",
        ["monster_tentacle"] = "models/tentacle2.mdl",
        ["monster_gargantua"] = "models/garg.mdl",
        ["monster_bigmomma"] = "models/big_mom.mdl",
        ["monster_nihilanth"] = "models/nihilanth.mdl",
        ["monster_cockroach"] = "models/roach.mdl",
        ["monster_rat"] = "models/bigrat.mdl",
        ["monster_snark"] = "models/w_squeak.mdl",
        ["monster_sentry"] = "models/sentry.mdl",
        ["monster_turret"] = "models/turret.mdl",
        ["monster_miniturret"] = "models/miniturret.mdl",
        ["monster_apache"] = "models/apache.mdl",
        ["monster_osprey"] = "models/osprey.mdl",
        ["monster_flyer_flock"] = "models/aflock.mdl",

        // Items
        ["item_healthkit"] = "models/w_medkit.mdl",
        ["item_battery"] = "models/w_battery.mdl",
        ["item_suit"] = "models/w_suit.mdl",
        ["item_longjump"] = "models/w_longjump.mdl",
        ["item_antidote"] = "models/w_antidote.mdl",
        ["item_security"] = "models/w_security.mdl",

        // Weapons
        ["weapon_crowbar"] = "models/w_crowbar.mdl",
        ["weapon_9mmhandgun"] = "models/w_9mmhandgun.mdl",
        ["weapon_glock"] = "models/w_9mmhandgun.mdl",
        ["weapon_357"] = "models/w_357.mdl",
        ["weapon_python"] = "models/w_357.mdl",
        ["weapon_9mmAR"] = "models/w_9mmAR.mdl",
        ["weapon_mp5"] = "models/w_9mmAR.mdl",
        ["weapon_shotgun"] = "models/w_shotgun.mdl",
        ["weapon_crossbow"] = "models/w_crossbow.mdl",
        ["weapon_rpg"] = "models/w_rpg.mdl",
        ["weapon_gauss"] = "models/w_gauss.mdl",
        ["weapon_egon"] = "models/w_egon.mdl",
        ["weapon_hornetgun"] = "models/w_hgun.mdl",
        ["weapon_handgrenade"] = "models/w_grenade.mdl",
        ["weapon_satchel"] = "models/w_satchel.mdl",
        ["weapon_tripmine"] = "models/v_tripmine.mdl",
        ["weapon_snark"] = "models/w_sqknest.mdl",
        ["weaponbox"] = "models/w_weaponbox.mdl",

        // Ammo
        ["ammo_9mmclip"] = "models/w_9mmclip.mdl",
        ["ammo_glockclip"] = "models/w_9mmclip.mdl",
        ["ammo_9mmAR"] = "models/w_9mmARclip.mdl",
        ["ammo_mp5clip"] = "models/w_9mmARclip.mdl",
        ["ammo_9mmbox"] = "models/w_chainammo.mdl",
        ["ammo_ARgrenades"] = "models/w_ARgrenade.mdl",
        ["ammo_mp5grenades"] = "models/w_ARgrenade.mdl",
        ["ammo_357"] = "models/w_357ammobox.mdl",
        ["ammo_buckshot"] = "models/w_shotbox.mdl",
        ["ammo_crossbow"] = "models/w_crossbow_clip.mdl",
        ["ammo_rpgclip"] = "models/w_rpgammo.mdl",
        ["ammo_gaussclip"] = "models/w_gaussammo.mdl",
        ["ammo_egonclip"] = "models/w_chainammo.mdl",
    };

    /// <summary>
    /// The model an entity shows, or null: its own "model" key when that names a studio model (cycler,
    /// monster_generic, monster_furniture and the like), else the model its class always has.
    /// </summary>
    public static string ModelFor(Entity entity)
    {
        string model = entity.Get("model");
        if (model != null && model.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            return model.Replace('\\', '/').TrimStart('/');
        return s_models.GetValueOrDefault(entity.ClassName);
    }

    /// <summary>A placed model from an entity, or null when it shows none.</summary>
    public static MountedMap.PlacedModel Place(Entity entity)
    {
        string model = ModelFor(entity);
        if (model == null) return null;

        // "angles" is pitch, yaw and roll in degrees; older entities give only a yaw as "angle". Studio models turn
        // their pitch the other way, as the game's renderer does.
        Vector3 angles = entity.Get("angles") != null ? entity.GetVector("angles") : new Vector3(0, entity.GetFloat("angle"), 0);
        if (angles.Y is -1f or -2f) angles.Y = 0f;   // "up" and "down", for things that are not models

        const float ToRadians = MathF.PI / 180f;
        System.Numerics.Quaternion rotation = MdlFile.AngleQuaternion(new Vector3(angles.Z, -angles.X, angles.Y) * ToRadians);

        return new MountedMap.PlacedModel(entity.ClassName, model, GoldSrcAssets.Position(entity.GetVector("origin")), GoldSrcAssets.Rotation(rotation));
    }
}
