using System;
using System.Collections.Generic;
using CombatOverhaul.Colliders;
using CombatOverhaul.DamageSystems;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace HitboxViewer;

/// <summary>
/// Server side of KEYPOINTS. Reads CO's LIVE loaded data on the server entity (never pack JSON):
/// zones = EntityDamageModelBehavior.DamageMultipliers, parts = CollidersEntityBehavior.CollidersTypes.
/// Request driven only; result cached per entity code (both maps come from the entity type's behavior config).
/// Every CO type reference lives in methods only reached after CoPresent, so a server without CO never loads them.
/// </summary>
internal static class ServerCoAccess
{
    private const int MaxIdsPerRequest = 64;
    private static readonly Dictionary<string, HbvEntityZones> cache = new();
    private static bool? coPresent;

    internal static bool CoPresent => coPresent ??= Type.GetType("CombatOverhaul.Colliders.CollidersEntityBehavior, OverhaullibLegacyCompat") != null;

    internal static HbvZoneReply Answer(ICoreServerAPI api, HbvZoneRequest req)
    {
        long[] ids = req?.EntityIds ?? Array.Empty<long>();
        int n = Math.Min(ids.Length, MaxIdsPerRequest);
        var list = new List<HbvEntityZones>(n);
        for (int i = 0; i < n; i++)
        {
            Entity? e = api.World.GetEntityById(ids[i]);
            if (e == null) { list.Add(new HbvEntityZones { EntityId = ids[i], Status = "no-entity" }); continue; }
            if (!CoPresent) { list.Add(new HbvEntityZones { EntityId = ids[i], Status = "no-co" }); continue; }
            HbvEntityZones z = ForEntity(e);
            list.Add(new HbvEntityZones
            {
                EntityId = ids[i], Status = z.Status, ZoneNames = z.ZoneNames, ZoneMults = z.ZoneMults,
                PartNames = z.PartNames, PartZones = z.PartZones,
            });
        }
        return new HbvZoneReply { Entities = list.ToArray() };
    }

    private static HbvEntityZones ForEntity(Entity e)
    {
        string key = e.Code?.ToString() ?? "";
        if (cache.TryGetValue(key, out HbvEntityZones? hit)) return hit;
        HbvEntityZones z = Build(e);
        if (z.Status == "ok" || z.Status == "no-damage-model") cache[key] = z; // "no-co" may be transient while behaviors init
        return z;
    }

    private static HbvEntityZones Build(Entity e)
    {
        EntityDamageModelBehavior? dm = e.GetBehavior<EntityDamageModelBehavior>();
        CollidersEntityBehavior? cb = e.GetBehavior<CollidersEntityBehavior>();
        if (dm == null) return new HbvEntityZones { Status = "no-damage-model" };
        if (cb == null || cb.CollidersTypes == null) return new HbvEntityZones { Status = "no-co" };

        var zoneIndex = new Dictionary<ColliderTypes, int>();
        var names = new List<string>();
        var mults = new List<float>();
        foreach (KeyValuePair<ColliderTypes, float> kv in dm.DamageMultipliers)
        {
            zoneIndex[kv.Key] = names.Count;
            names.Add(kv.Key.ToString());
            mults.Add(kv.Value);
        }
        // CO falls back to Torso for a part missing from CollidersTypes; make sure the client can apply it.
        if (!zoneIndex.ContainsKey(ColliderTypes.Torso))
        {
            zoneIndex[ColliderTypes.Torso] = names.Count;
            names.Add(nameof(ColliderTypes.Torso));
            mults.Add(1f);
        }
        var parts = new List<string>();
        var partZones = new List<int>();
        foreach (KeyValuePair<string, ColliderTypes> kv in cb.CollidersTypes)
        {
            if (!zoneIndex.TryGetValue(kv.Value, out int zi))
            {
                // Zone with no multiplier entry: CO's lookup misses too, so it deals unscaled damage (x1).
                zi = names.Count; zoneIndex[kv.Value] = zi; names.Add(kv.Value.ToString()); mults.Add(1f);
            }
            parts.Add(kv.Key);
            partZones.Add(zi);
        }
        return new HbvEntityZones
        {
            Status = "ok", ZoneNames = names.ToArray(), ZoneMults = mults.ToArray(),
            PartNames = parts.ToArray(), PartZones = partZones.ToArray(),
        };
    }
}
