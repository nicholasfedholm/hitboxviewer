using System.Collections.Generic;
using System.Text;
using CombatOverhaul.Colliders;
using CombatOverhaul.DamageSystems;
using OpenTK.Mathematics;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace HitboxViewer;

/// <summary>
/// Every reference to Combat Overhaul types lives here, so the rest of the mod still loads (and draws
/// collision/selection boxes) when OverhaullibLegacyCompat is missing. Reads only CO's stored runtime data:
/// CollidersEntityBehavior.Colliders[name].InworldVertices (the corners CO's Collide() consumes) and
/// CollidersTypes[name]. Nothing is recomputed from JSON or the animator (hard rule: draw what CO tests).
/// </summary>
internal static class CoAccess
{
    internal static bool Probe() => typeof(CollidersEntityBehavior).FullName != null;

    private static bool Untransformed(Vector4d[] v)
    {
        // InworldVertices stay all-zero until CO's first transform (needs a rendered frame).
        for (int i = 0; i < 8; i++) if (v[i].X != 0 || v[i].Y != 0 || v[i].Z != 0) return false;
        return true;
    }

    internal static void DrawColliders(HbvRenderer r, Entity e, Vec3d cam)
    {
        CollidersEntityBehavior? b = e.GetBehavior<CollidersEntityBehavior>();
        if (b?.Colliders == null) return;
        Dictionary<string, ColliderTypes>? types = b.CollidersTypes;
        foreach (KeyValuePair<string, ShapeElementCollider> kv in b.Colliders)
        {
            Vector4d[]? v = kv.Value?.InworldVertices;
            if (v == null || v.Length < 8 || Untransformed(v)) continue;
            ColliderTypes zone = ColliderTypes.Torso;
            bool hasZone = types != null && types.TryGetValue(kv.Key, out zone);
            Vec4f col = ColorFor(kv.Key, hasZone, zone);
            Vector4d v0 = v[0], v1 = v[1], v3 = v[3], v4 = v[4];
            r.DrawAffine(v0.X - cam.X, v0.Y - cam.Y, v0.Z - cam.Z,
                v1.X - v0.X, v1.Y - v0.Y, v1.Z - v0.Z,
                v3.X - v0.X, v3.Y - v0.Y, v3.Z - v0.Z,
                v4.X - v0.X, v4.Y - v0.Y, v4.Z - v0.Z, col);
        }
    }

    private static Vec4f ColorFor(string name, bool hasZone, ColliderTypes zone)
    {
        if (hasZone && zone == ColliderTypes.Critical)
        {
            return HbvRenderer.OrganColor(HbvRenderer.OrganOf(name)) ?? HbvRenderer.ColCriticalOther;
        }
        return HbvRenderer.PartColor(name);
    }

    private static float? Multiplier(Entity e, bool hasZone, ColliderTypes zone)
    {
        // Normally absent on the client (LoP adds the damage model under server behaviors only).
        EntityDamageModelBehavior? dm = e.GetBehavior<EntityDamageModelBehavior>();
        if (dm?.DamageMultipliers == null) return null;
        ColliderTypes key = hasZone ? zone : ColliderTypes.Torso;
        return dm.DamageMultipliers.TryGetValue(key, out float m) ? m : null;
    }

    internal static void DrawLabels(HbvRenderer r, Entity e)
    {
        CollidersEntityBehavior? b = e.GetBehavior<CollidersEntityBehavior>();
        double topY = e.Pos.Y + (e.SelectionBox?.Y2 ?? 1) + 0.4;
        if (b == null)
        {
            r.DrawLabelAt(e.Pos.X, topY, e.Pos.Z, e.Code + "  coBehavior: ABSENT (CO tests AABB only)");
            return;
        }
        r.DrawLabelAt(e.Pos.X, topY, e.Pos.Z, e.Code + "  coBehavior: present  OBB: " + b.HasOBBCollider + (e.IsRendered ? "" : "  (not rendered: corners stale)"));
        if (b.Colliders == null) return;
        Dictionary<string, ColliderTypes>? types = b.CollidersTypes;
        foreach (KeyValuePair<string, ShapeElementCollider> kv in b.Colliders)
        {
            Vector4d[]? v = kv.Value?.InworldVertices;
            if (v == null || v.Length < 8 || Untransformed(v)) continue;
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < 8; i++) { cx += v[i].X; cy += v[i].Y; cz += v[i].Z; }
            ColliderTypes zone = ColliderTypes.Torso;
            bool hasZone = types != null && types.TryGetValue(kv.Key, out zone);
            string text = kv.Key;
            if (hasZone && zone == ColliderTypes.Critical)
            {
                string organ = HbvRenderer.OrganOf(kv.Key) ?? "Critical";
                float? m = Multiplier(e, hasZone, zone);
                text = kv.Key + " [" + organ + (m.HasValue ? " x" + m.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "") + "]";
            }
            r.DrawLabelAt(cx / 8, cy / 8, cz / 8, text);
        }
    }

    // ---------------------------------------------------------------- KEYPOINTS (0.2.0)

    /// <summary>
    /// KEYPOINTS: draw only client colliders whose server zone multiplier is above 1.0. Geometry is the same
    /// runtime InworldVertices as DrawColliders; the zone and multiplier come from the server reply (ZoneData).
    /// </summary>
    internal static void DrawKeypoints(HbvRenderer r, Entity e, Vec3d cam, ZoneData z)
    {
        if (z.Status != "ok") return;
        CollidersEntityBehavior? b = e.GetBehavior<CollidersEntityBehavior>();
        if (b?.Colliders == null) return;
        foreach (KeyValuePair<string, ShapeElementCollider> kv in b.Colliders)
        {
            int zi = z.ZoneOf(kv.Key);
            if (zi < 0) continue;
            float m = z.ZoneMults[zi];
            if (!(m > 1f)) continue;
            Vector4d[]? v = kv.Value?.InworldVertices;
            if (v == null || v.Length < 8 || Untransformed(v)) continue;
            bool strong = m >= 2f;
            Vec4f col = HbvRenderer.KeypointColor(z.ZoneNames[zi], kv.Key, strong);
            Vector4d v0 = v[0], v1 = v[1], v3 = v[3], v4 = v[4];
            r.DrawAffine(v0.X - cam.X, v0.Y - cam.Y, v0.Z - cam.Z,
                v1.X - v0.X, v1.Y - v0.Y, v1.Z - v0.Z,
                v3.X - v0.X, v3.Y - v0.Y, v3.Z - v0.Z,
                v4.X - v0.X, v4.Y - v0.Y, v4.Z - v0.Z, col, strong ? 2f : 1.4f);
        }
    }

    /// <summary>KEYPOINTS labels: organs get their own "Part: Zone xN"; other bonus zones one "Zone xN" at the zone centroid.</summary>
    internal static void DrawKeypointLabels(HbvRenderer r, Entity e, ZoneData? z)
    {
        double topY = e.Pos.Y + (e.SelectionBox?.Y2 ?? 1) + 0.4;
        if (z == null) { r.DrawLabelAt(e.Pos.X, topY, e.Pos.Z, HbvRenderer.WaitingLabel); return; }
        r.DrawLabelAt(e.Pos.X, topY, e.Pos.Z, z.Header);
        if (z.Status != "ok") return;
        CollidersEntityBehavior? b = e.GetBehavior<CollidersEntityBehavior>();
        if (b?.Colliders == null) return;
        int n = z.Count.Length;
        for (int i = 0; i < n; i++) { z.SumX[i] = 0; z.SumY[i] = 0; z.SumZ[i] = 0; z.Count[i] = 0; }
        foreach (KeyValuePair<string, ShapeElementCollider> kv in b.Colliders)
        {
            int zi = z.ZoneOf(kv.Key);
            if (zi < 0 || zi >= n || !(z.ZoneMults[zi] > 1f)) continue;
            Vector4d[]? v = kv.Value?.InworldVertices;
            if (v == null || v.Length < 8 || Untransformed(v)) continue;
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < 8; i++) { cx += v[i].X; cy += v[i].Y; cz += v[i].Z; }
            cx /= 8; cy /= 8; cz /= 8;
            if (z.PartLabels.TryGetValue(kv.Key, out string? own)) { r.DrawLabelAt(cx, cy, cz, own); continue; }
            z.SumX[zi] += cx; z.SumY[zi] += cy; z.SumZ[zi] += cz; z.Count[zi]++;
        }
        for (int i = 0; i < n; i++)
        {
            int c = z.Count[i];
            if (c > 0) r.DrawLabelAt(z.SumX[i] / c, z.SumY[i] / c, z.SumZ[i] / c, z.ZoneLabels[i]);
        }
    }

    internal static void DumpColliders(StringBuilder sb, Entity e)
    {
        CollidersEntityBehavior? b = e.GetBehavior<CollidersEntityBehavior>();
        if (b == null)
        {
            HbvRenderer.J(sb, "coBehavior", "absent");
            sb.Append("  \"colliders\": []\n");
            return;
        }
        HbvRenderer.J(sb, "coBehavior", "present");
        HbvRenderer.Jb(sb, "hasOBBCollider", b.HasOBBCollider);
        HbvRenderer.Jb(sb, "hasUsableDetailedColliders", b.HasUsableDetailedColliders);
        HbvRenderer.Jb(sb, "hasDamageModelOnClient", e.GetBehavior<EntityDamageModelBehavior>() != null);
        sb.Append("  \"colliders\": [");
        bool first = true;
        Dictionary<string, ColliderTypes>? types = b.CollidersTypes;
        if (b.Colliders != null)
        {
            foreach (KeyValuePair<string, ShapeElementCollider> kv in b.Colliders)
            {
                ShapeElementCollider? c = kv.Value;
                if (c == null) continue;
                ColliderTypes zone = ColliderTypes.Torso;
                bool hasZone = types != null && types.TryGetValue(kv.Key, out zone);
                sb.Append(first ? "\n" : ",\n");
                first = false;
                sb.Append("    {\"name\": \"").Append(HbvRenderer.Esc(kv.Key)).Append('"');
                sb.Append(", \"zone\": ").Append(hasZone ? "\"" + zone + "\"" : "null");
                string? organ = hasZone && zone == ColliderTypes.Critical ? HbvRenderer.OrganOf(kv.Key) ?? "Critical" : null;
                sb.Append(", \"organ\": ").Append(organ == null ? "null" : "\"" + organ + "\"");
                float? m = Multiplier(e, hasZone, zone);
                sb.Append(", \"multiplier\": ").Append(m.HasValue ? HbvRenderer.F(m.Value) : "null");
                sb.Append(", \"jointId\": ").Append(c.JointId);
                sb.Append(", \"hasRenderer\": ").Append(c.HasRenderer ? "true" : "false");
                Vector4d[]? w = c.InworldVertices;
                bool untransformed = w == null || w.Length < 8 || Untransformed(w);
                sb.Append(", \"untransformed\": ").Append(untransformed ? "true" : "false");
                if (!untransformed && w != null)
                {
                    double cx = 0, cy = 0, cz = 0;
                    sb.Append(", \"cornersRel\": [");
                    for (int i = 0; i < 8; i++)
                    {
                        double x = w[i].X - e.Pos.X, y = w[i].Y - e.Pos.Y, z = w[i].Z - e.Pos.Z;
                        cx += x; cy += y; cz += z;
                        sb.Append(i == 0 ? "[" : ", [").Append(HbvRenderer.F(x)).Append(", ").Append(HbvRenderer.F(y)).Append(", ").Append(HbvRenderer.F(z)).Append(']');
                    }
                    sb.Append("], \"centreRel\": [").Append(HbvRenderer.F(cx / 8)).Append(", ").Append(HbvRenderer.F(cy / 8)).Append(", ").Append(HbvRenderer.F(cz / 8)).Append(']');
                    sb.Append(", \"edges\": [").Append(HbvRenderer.F(Len(w[1], w[0]))).Append(", ").Append(HbvRenderer.F(Len(w[3], w[0]))).Append(", ").Append(HbvRenderer.F(Len(w[4], w[0]))).Append(']');
                }
                Vector4d[]? ev = c.ElementVertices;
                if (ev != null && ev.Length >= 8)
                {
                    // Element-local corners in block units (from/16, to/16 after CO's rest-pose processing).
                    sb.Append(", \"elementFrom\": [").Append(HbvRenderer.F(ev[0].X)).Append(", ").Append(HbvRenderer.F(ev[0].Y)).Append(", ").Append(HbvRenderer.F(ev[0].Z)).Append(']');
                    sb.Append(", \"elementTo\": [").Append(HbvRenderer.F(ev[6].X)).Append(", ").Append(HbvRenderer.F(ev[6].Y)).Append(", ").Append(HbvRenderer.F(ev[6].Z)).Append(']');
                }
                sb.Append('}');
            }
        }
        sb.Append("\n  ]\n");
    }

    private static double Len(Vector4d a, Vector4d b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
