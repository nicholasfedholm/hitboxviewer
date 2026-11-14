using System.Collections.Generic;
using System.Globalization;
using ProtoBuf;

namespace HitboxViewer;

/// <summary>Client -> server: entity ids whose CO zone data the KEYPOINTS view needs.</summary>
[ProtoContract]
public sealed class HbvZoneRequest
{
    [ProtoMember(1)] public long[] EntityIds = System.Array.Empty<long>();
}

/// <summary>Server -> client: one entry per requested entity that the server could resolve.</summary>
[ProtoContract]
public sealed class HbvZoneReply
{
    [ProtoMember(1)] public HbvEntityZones[] Entities = System.Array.Empty<HbvEntityZones>();
}

/// <summary>
/// CO's loaded server data for one entity. Zones: EntityDamageModelBehavior.DamageMultipliers (ColliderTypes -> float).
/// Parts: the server's CollidersEntityBehavior.CollidersTypes (collider name -> ColliderTypes), the same map
/// OnReceiveDamageHandler consults with the collider name the client sends. PartZones[i] indexes ZoneNames.
/// </summary>
[ProtoContract]
public sealed class HbvEntityZones
{
    [ProtoMember(1)] public long EntityId;
    /// <summary>"ok", "no-entity", "no-co", "no-damage-model".</summary>
    [ProtoMember(2)] public string Status = "ok";
    [ProtoMember(3)] public string[] ZoneNames = System.Array.Empty<string>();
    [ProtoMember(4)] public float[] ZoneMults = System.Array.Empty<float>();
    [ProtoMember(5)] public string[] PartNames = System.Array.Empty<string>();
    [ProtoMember(6)] public int[] PartZones = System.Array.Empty<int>();
}

/// <summary>Client-side view of one HbvEntityZones, built once when the reply arrives (never per frame).</summary>
internal sealed class ZoneData
{
    internal const string FallbackZone = "Torso"; // CO: a collider missing from CollidersTypes counts as Torso

    internal readonly string Status;
    internal readonly Dictionary<string, int> PartZone = new();
    internal readonly string[] ZoneNames;
    internal readonly float[] ZoneMults;
    internal readonly string[] ZoneLabels;
    internal readonly int FallbackIndex = -1;
    internal readonly Dictionary<string, string> PartLabels = new();
    internal readonly string Header;

    // Scratch for per-zone label centroids (zone count is at most 6), reused every frame.
    internal readonly double[] SumX, SumY, SumZ;
    internal readonly int[] Count;

    internal ZoneData(HbvEntityZones z, string entityCode)
    {
        Status = z.Status ?? "ok";
        ZoneNames = z.ZoneNames ?? System.Array.Empty<string>();
        ZoneMults = z.ZoneMults ?? System.Array.Empty<float>();
        int n = System.Math.Min(ZoneNames.Length, ZoneMults.Length);
        ZoneLabels = new string[n];
        SumX = new double[n]; SumY = new double[n]; SumZ = new double[n]; Count = new int[n];
        int bonus = 0;
        for (int i = 0; i < n; i++)
        {
            ZoneLabels[i] = ZoneNames[i] + " x" + Fmt(ZoneMults[i]);
            if (ZoneNames[i] == FallbackZone) FallbackIndex = i;
            if (ZoneMults[i] > 1f) bonus++;
        }
        string[] parts = z.PartNames ?? System.Array.Empty<string>();
        int[] pz = z.PartZones ?? System.Array.Empty<int>();
        for (int i = 0; i < parts.Length && i < pz.Length; i++)
        {
            if (pz[i] < 0 || pz[i] >= n) continue;
            PartZone[parts[i]] = pz[i];
            string? organ = HbvRenderer.OrganOf(parts[i]);
            if (organ != null && ZoneMults[pz[i]] > 1f) PartLabels[parts[i]] = parts[i] + ": " + ZoneLabels[pz[i]];
        }
        Header = Status == "ok"
            ? entityCode + "  KEYPOINTS: " + bonus + " bonus zone(s) of " + n + " (server data)"
            : entityCode + "  KEYPOINTS: server says " + Status;
    }

    /// <summary>Zone index for a client collider name, applying CO's Torso fallback; -1 if unknown.</summary>
    internal int ZoneOf(string part) => PartZone.TryGetValue(part, out int i) ? i : FallbackIndex;

    internal static string Fmt(float m) => m.ToString("0.##", CultureInfo.InvariantCulture);
}
