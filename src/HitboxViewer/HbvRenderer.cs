using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using OGL = OpenTK.Graphics.OpenGL;

namespace HitboxViewer;

/// <summary>
/// One renderer, two stages: AfterFinalComposition draws the wireframes, Ortho draws the labels.
/// TARGET and RADIUS wireframes are depth-tested like the vanilla selection box. KEYPOINTS (0.2.1) draws on
/// top: organs sit inside the body, so a depth-tested pass hid them (see BeginOnTop). Labels draw at Ortho
/// on the default framebuffer, which holds no 3D scene depth, so the body never occludes them. When the mode is Off both stages return on the first check.
/// </summary>
public sealed class HbvRenderer : IRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly HitboxViewerConfig cfg;
    private WireframeCube? cube;

    // Preallocated: no heap allocation in the draw loop.
    private readonly Matrixf mv = new();
    private readonly float[] model = new float[16];
    private readonly float[] prod = new float[16];
    private readonly Entity?[] picked;
    private readonly double[] pickedDist;
    private int pickedCount;
    private long lastScanMs;

    private readonly Vec4f colCollision = new(1f, 1f, 1f, 1f);
    private readonly Vec4f colSelection = new(1f, 0.9f, 0.1f, 1f);
    internal static readonly Vec4f ColHeart = new(1f, 0.05f, 0.05f, 1f);
    internal static readonly Vec4f ColLungs = new(1f, 0.45f, 0.75f, 1f);
    internal static readonly Vec4f ColBrain = new(0.65f, 0.2f, 1f, 1f);
    internal static readonly Vec4f ColEyes = new(0f, 1f, 1f, 1f);
    internal static readonly Vec4f ColCriticalOther = new(1f, 0.55f, 0f, 1f);

    // KEYPOINTS: bonus zones. Organs keep their organ colour (dimmed below x2); other bonus zones one
    // highlight colour in two tiers: amber for >1.0, bright yellow for >=2.0. 0.2.1: translucent (Kp* below).

    // KEYPOINTS (0.2.1): on-top pass, so the highlights are translucent to keep the body readable through
    // them. Separate instances: TARGET keeps the opaque organ colours above unchanged.
    private const float KeyAlphaStrong = 0.8f, KeyAlphaDim = 0.65f;
    private static readonly Vec4f KpHeart = new(1f, 0.05f, 0.05f, KeyAlphaStrong);
    private static readonly Vec4f KpLungs = new(1f, 0.45f, 0.75f, KeyAlphaStrong);
    private static readonly Vec4f KpBrain = new(0.65f, 0.2f, 1f, KeyAlphaStrong);
    private static readonly Vec4f KpEyes = new(0f, 1f, 1f, KeyAlphaStrong);
    private static readonly Vec4f KpHeartDim = new(0.65f, 0.03f, 0.03f, KeyAlphaDim);
    private static readonly Vec4f KpLungsDim = new(0.65f, 0.3f, 0.5f, KeyAlphaDim);
    private static readonly Vec4f KpBrainDim = new(0.42f, 0.13f, 0.65f, KeyAlphaDim);
    private static readonly Vec4f KpEyesDim = new(0f, 0.65f, 0.65f, KeyAlphaDim);
    private static readonly Vec4f KpStd = new(1f, 0.6f, 0.1f, KeyAlphaDim);
    private static readonly Vec4f KpStrong = new(1f, 1f, 0.35f, KeyAlphaStrong);

    // KEYPOINTS on-top pass: GL state saved by BeginOnTop and restored by EndOnTop (value fields, no allocation).
    private bool onTop, savedDepthTest, savedDepthMask, savedBlend;
    private int savedDepthFunc, savedSrcRgb, savedDstRgb, savedSrcA, savedDstA;
    private float savedLineWidth;
    internal const string WaitingLabel = "KEYPOINTS: waiting for server data";
    private const string NoServerLine = "hitboxviewer: no server data";
    private const long RequestIntervalMs = 250, ReplyTimeoutMs = 3000;

    // KEYPOINTS server data: built once per reply, looked up per frame.
    private readonly Dictionary<long, ZoneData> zones = new();
    private readonly Dictionary<long, long> pendingSince = new();
    private readonly List<long> reqIds = new();
    private long lastReqMs;
    private bool serverSilent;
    private bool noServerReported;
    internal IClientNetworkChannel? Channel { get; set; }

    /// <summary>Muted 12-colour body-part palette; index = FNV-1a(part name) % 12, stable across runs.</summary>
    internal static readonly Vec4f[] Palette =
    {
        new(0.55f, 0.62f, 0.45f, 1f), new(0.45f, 0.55f, 0.65f, 1f), new(0.65f, 0.55f, 0.45f, 1f),
        new(0.50f, 0.65f, 0.60f, 1f), new(0.60f, 0.50f, 0.60f, 1f), new(0.62f, 0.62f, 0.50f, 1f),
        new(0.40f, 0.50f, 0.45f, 1f), new(0.55f, 0.45f, 0.40f, 1f), new(0.45f, 0.45f, 0.60f, 1f),
        new(0.60f, 0.60f, 0.62f, 1f), new(0.50f, 0.58f, 0.40f, 1f), new(0.58f, 0.48f, 0.52f, 1f),
    };

    private readonly Dictionary<string, LoadedTexture> labelTex = new();
    private readonly CairoFont labelFont = CairoFont.WhiteSmallText();

    public ViewMode Mode { get; private set; } = ViewMode.Off;
    public bool Labels { get; set; }
    public bool CoAvailable { get; }

    public double RenderOrder => 0.95;
    public int RenderRange => 999;

    public HbvRenderer(ICoreClientAPI capi, HitboxViewerConfig cfg)
    {
        this.capi = capi;
        this.cfg = cfg;
        picked = new Entity?[cfg.EntityCap];
        pickedDist = new double[cfg.EntityCap];
        CoAvailable = ProbeCo();
    }

    private bool ProbeCo()
    {
        try { return CoAccess.Probe(); }
        catch (Exception e)
        {
            capi.Logger.Warning("[hitboxviewer] Combat Overhaul types unavailable: " + e.GetType().Name + ": " + e.Message);
            return false;
        }
    }

    public void SetMode(ViewMode m)
    {
        Mode = m;
        Labels = m == ViewMode.Radius ? cfg.LabelsRadius : m == ViewMode.Keypoints ? cfg.LabelsKeypoints : cfg.LabelsTarget;
        if (m == ViewMode.Keypoints)
        {
            zones.Clear(); pendingSince.Clear();
            serverSilent = false; noServerReported = false; lastReqMs = 0;
        }
        pickedCount = 0;
        lastScanMs = 0;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (Mode == ViewMode.Off) return;
        if (stage == EnumRenderStage.AfterFinalComposition) DrawBoxes();
        else if (stage == EnumRenderStage.Ortho && Labels) DrawLabels();
    }

    // ---------------------------------------------------------------- picking

    /// <summary>The entity vanilla shows the selection box for.</summary>
    internal Entity? AimedEntity() => capi.World.Player?.CurrentEntitySelection?.Entity;

    private void Pick()
    {
        EntityPlayer? self = capi.World.Player?.Entity;
        if (self == null) { pickedCount = 0; return; }
        if (Mode == ViewMode.Target)
        {
            Entity? e = AimedEntity();
            pickedCount = 0;
            if (e != null && e.Pos.DistanceTo(self.Pos) <= cfg.MaxDrawDist) { picked[0] = e; pickedDist[0] = 0; pickedCount = 1; }
            return;
        }
        long now = capi.ElapsedMilliseconds;
        if (now - lastScanMs < 100) return; // rescan RADIUS candidates at 10 Hz
        lastScanMs = now;
        pickedCount = 0;
        double r2 = cfg.Radius * cfg.Radius;
        double px = self.Pos.X, py = self.Pos.Y, pz = self.Pos.Z;
        int cap = picked.Length;
        foreach (Entity e in capi.World.LoadedEntities.Values)
        {
            if (e == self) continue;
            double dx = e.Pos.X - px, dy = e.Pos.Y - py, dz = e.Pos.Z - pz;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > r2) continue;
            if (pickedCount == cap && d2 >= pickedDist[cap - 1]) continue;
            int i = pickedCount < cap ? pickedCount++ : cap - 1;
            while (i > 0 && pickedDist[i - 1] > d2) { picked[i] = picked[i - 1]; pickedDist[i] = pickedDist[i - 1]; i--; }
            picked[i] = e; pickedDist[i] = d2;
        }
        for (int i = pickedCount; i < cap; i++) picked[i] = null;
    }

    // ---------------------------------------------------------------- boxes

    private void DrawBoxes()
    {
        cube ??= WireframeCube.CreateUnitCube(capi, -1); // [0,1]^3 line cube, depth nudge flags set by vanilla
        Pick();
        if (Mode == ViewMode.Keypoints) RequestMissing();
        EntityPlayer? self = capi.World.Player?.Entity;
        if (self == null) return;
        Vec3d cam = self.CameraPos;
        double maxD2 = cfg.MaxDrawDist * cfg.MaxDrawDist;
        try
        {
            for (int n = 0; n < pickedCount; n++)
            {
                Entity? e = picked[n];
                if (e == null || e.Pos == null) continue;
                double dx = e.Pos.X - cam.X, dy = e.Pos.Y - cam.Y, dz = e.Pos.Z - cam.Z;
                if (dx * dx + dy * dy + dz * dz > maxD2) continue;
                if (Mode == ViewMode.Keypoints)
                {
                    if (CoAvailable && !serverSilent && zones.TryGetValue(e.EntityId, out ZoneData? z))
                    {
                        if (!onTop) BeginOnTop(); // KEYPOINTS only; TARGET/RADIUS below never enter it
                        CoAccess.DrawKeypoints(this, e, cam, z);
                    }
                    continue;
                }
                DrawAabb(e, e.CollisionBox, cam, colCollision);
                DrawAabb(e, e.SelectionBox, cam, colSelection);
                if (CoAvailable) CoAccess.DrawColliders(this, e, cam);
            }
        }
        finally
        {
            if (onTop) EndOnTop(); // every path out of the pass restores what BeginOnTop changed
        }
    }

    /// <summary>
    /// 0.2.1 KEYPOINTS fix. WireframeCube.Render forces GL_DEPTH_TEST on (WireframeCube.cs:67) against the
    /// scene depth still bound at AfterFinalComposition, so organ colliders inside the body failed the test and
    /// only flickered through where the moving mesh and the collider positions drifted apart. Disabling the
    /// depth test here would be undone inside Render, but Render never touches the depth FUNCTION, so
    /// DepthFunc(Always) makes every fragment pass while the test stays enabled. Depth writes stay off (Render
    /// sets the mask false itself). Blend is set to standard alpha for the translucent highlight colours.
    /// Everything changed here is captured first and put back in EndOnTop.
    /// </summary>
    private void BeginOnTop()
    {
        savedDepthTest = OGL.GL.IsEnabled(OGL.EnableCap.DepthTest);
        savedDepthMask = OGL.GL.GetBoolean(OGL.GetPName.DepthWritemask);
        savedDepthFunc = OGL.GL.GetInteger(OGL.GetPName.DepthFunc);
        savedBlend = OGL.GL.IsEnabled(OGL.EnableCap.Blend);
        savedSrcRgb = OGL.GL.GetInteger(OGL.GetPName.BlendSrcRgb);
        savedDstRgb = OGL.GL.GetInteger(OGL.GetPName.BlendDstRgb);
        savedSrcA = OGL.GL.GetInteger(OGL.GetPName.BlendSrcAlpha);
        savedDstA = OGL.GL.GetInteger(OGL.GetPName.BlendDstAlpha);
        savedLineWidth = OGL.GL.GetFloat(OGL.GetPName.LineWidth);
        onTop = true;
        OGL.GL.DepthFunc(OGL.DepthFunction.Always);
        OGL.GL.BlendFuncSeparate(OGL.BlendingFactorSrc.SrcAlpha, OGL.BlendingFactorDest.OneMinusSrcAlpha,
            OGL.BlendingFactorSrc.One, OGL.BlendingFactorDest.OneMinusSrcAlpha);
    }

    /// <summary>Restores depth func, depth test, depth mask, blend enable, blend func and line width.</summary>
    private void EndOnTop()
    {
        onTop = false;
        OGL.GL.DepthFunc((OGL.DepthFunction)savedDepthFunc);
        if (savedDepthTest) capi.Render.GLEnableDepthTest(); else capi.Render.GLDisableDepthTest();
        capi.Render.GLDepthMask(savedDepthMask);
        if (savedBlend) OGL.GL.Enable(OGL.EnableCap.Blend); else OGL.GL.Disable(OGL.EnableCap.Blend);
        OGL.GL.BlendFuncSeparate((OGL.BlendingFactorSrc)savedSrcRgb, (OGL.BlendingFactorDest)savedDstRgb,
            (OGL.BlendingFactorSrc)savedSrcA, (OGL.BlendingFactorDest)savedDstA);
        OGL.GL.LineWidth(savedLineWidth);
    }

    private void DrawAabb(Entity e, Cuboidf? b, Vec3d cam, Vec4f col)
    {
        if (b == null) return;
        DrawAffine(e.Pos.X + b.X1 - cam.X, e.Pos.Y + b.Y1 - cam.Y, e.Pos.Z + b.Z1 - cam.Z,
            b.X2 - b.X1, 0, 0, 0, b.Y2 - b.Y1, 0, 0, 0, b.Z2 - b.Z1, col);
    }

    /// <summary>
    /// Draws the affine image of the unit cube: origin o (camera-relative), axes a (unit x), b (unit y), c (unit z).
    /// For a CO collider: o = v0 - cam, a = v1 - v0, b = v3 - v0, c = v4 - v0 (CO corner order), exact for any affine pose.
    /// </summary>
    internal void DrawAffine(double ox, double oy, double oz,
        double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, Vec4f col)
        => DrawAffine(ox, oy, oz, ax, ay, az, bx, by, bz, cx, cy, cz, col, 1f);

    /// <summary>As above, line width = config LineWidth * widthScale (KEYPOINTS draws its strong tier thicker).</summary>
    internal void DrawAffine(double ox, double oy, double oz,
        double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, Vec4f col, float widthScale)
    {
        float[] m = model;
        m[0] = (float)ax; m[1] = (float)ay; m[2] = (float)az; m[3] = 0;
        m[4] = (float)bx; m[5] = (float)by; m[6] = (float)bz; m[7] = 0;
        m[8] = (float)cx; m[9] = (float)cy; m[10] = (float)cz; m[11] = 0;
        m[12] = (float)ox; m[13] = (float)oy; m[14] = (float)oz; m[15] = 1;
        float[] v = capi.Render.CameraMatrixOriginf;
        for (int c = 0; c < 4; c++)
        {
            for (int r = 0; r < 4; r++)
            {
                prod[c * 4 + r] = v[r] * m[c * 4] + v[4 + r] * m[c * 4 + 1] + v[8 + r] * m[c * 4 + 2] + v[12 + r] * m[c * 4 + 3];
            }
        }
        mv.Set(prod);
        cube!.Render(capi, mv, cfg.LineWidth * widthScale, col);
    }

    // ---------------------------------------------------------------- labels

    private void DrawLabels()
    {
        Entity? e = AimedEntity();
        EntityPlayer? self = capi.World.Player?.Entity;
        if (e == null || self == null || e.Pos.DistanceTo(self.Pos) > cfg.MaxDrawDist) return;
        if (Mode == ViewMode.Keypoints)
        {
            if (serverSilent) return;
            if (CoAvailable) CoAccess.DrawKeypointLabels(this, e, zones.TryGetValue(e.EntityId, out ZoneData? z) ? z : null);
            else DrawLabelAt(e.Pos.X, e.Pos.Y + (e.SelectionBox?.Y2 ?? 1) + 0.4, e.Pos.Z, e.Code + "  co: lib missing");
            return;
        }
        if (CoAvailable) CoAccess.DrawLabels(this, e);
        else DrawLabelAt(e.Pos.X, e.Pos.Y + (e.SelectionBox?.Y2 ?? 1) + 0.4, e.Pos.Z, e.Code + "  co: lib missing");
    }

    internal void DrawLabelAt(double wx, double wy, double wz, string text)
    {
        IRenderAPI r = capi.Render;
        Vec3d p = MatrixToolsd.Project(new Vec3d(wx, wy, wz), r.PerspectiveProjectionMat, r.PerspectiveViewMat, r.FrameWidth, r.FrameHeight);
        if (p.Z < 0) return;
        if (!labelTex.TryGetValue(text, out LoadedTexture? tex))
        {
            tex = capi.Gui.TextTexture.GenTextTexture(text, labelFont, new TextBackground { FillColor = new double[] { 0, 0, 0, 0.55 }, Padding = 2, Radius = 2 });
            labelTex[text] = tex;
        }
        float s = (float)Math.Min(1.0, 6.0 / Math.Max(1.0, p.Z));
        float w = tex.Width * s, h = tex.Height * s;
        r.Render2DTexture(tex.TextureId, (float)p.X - w / 2, r.FrameHeight - (float)p.Y - h / 2, w, h, 20f);
    }

    // ---------------------------------------------------------------- KEYPOINTS server data

    /// <summary>
    /// Asks the server for zone data of picked entities not yet known. At most 4 requests/s, only in KEYPOINTS,
    /// only for new ids. No channel (server lacks the mod) or no reply within 3 s: one chat line, then silence
    /// until KEYPOINTS is entered again.
    /// </summary>
    private void RequestMissing()
    {
        if (serverSilent) return;
        long now = capi.ElapsedMilliseconds;
        if (now - lastReqMs < RequestIntervalMs) return;
        foreach (KeyValuePair<long, long> p in pendingSince)
        {
            if (now - p.Value > ReplyTimeoutMs) { NoServerData(); return; }
        }
        reqIds.Clear();
        for (int n = 0; n < pickedCount; n++)
        {
            Entity? e = picked[n];
            if (e == null) continue;
            long id = e.EntityId;
            if (!zones.ContainsKey(id) && !pendingSince.ContainsKey(id)) reqIds.Add(id);
        }
        if (reqIds.Count == 0) return;
        lastReqMs = now;
        if (Channel == null || !Channel.Connected) { NoServerData(); return; }
        if (zones.Count > 512) zones.Clear();
        Channel.SendPacket(new HbvZoneRequest { EntityIds = reqIds.ToArray() });
        foreach (long id in reqIds) pendingSince[id] = now;
    }

    private void NoServerData()
    {
        serverSilent = true;
        pendingSince.Clear();
        if (noServerReported) return;
        noServerReported = true;
        capi.ShowChatMessage(NoServerLine);
        capi.Logger.Notification("[hitboxviewer] KEYPOINTS: no server data (server lacks hitboxviewer 0.2.x or did not reply)");
    }

    internal void OnZoneReply(HbvZoneReply reply)
    {
        if (reply?.Entities == null) return;
        foreach (HbvEntityZones z in reply.Entities)
        {
            if (z == null) continue;
            pendingSince.Remove(z.EntityId);
            string code = capi.World.GetEntityById(z.EntityId)?.Code?.ToShortString() ?? ("entity " + z.EntityId);
            zones[z.EntityId] = new ZoneData(z, code);
        }
    }

    internal static Vec4f KeypointColor(string zone, string part, bool strong)
    {
        if (zone == "Critical")
        {
            switch (OrganOf(part))
            {
                case "Heart": return strong ? KpHeart : KpHeartDim;
                case "Lungs": return strong ? KpLungs : KpLungsDim;
                case "Brain": return strong ? KpBrain : KpBrainDim;
                case "Eyes": return strong ? KpEyes : KpEyesDim;
            }
        }
        return strong ? KpStrong : KpStd;
    }

    // ---------------------------------------------------------------- dump

    public bool Dump(out string message)
    {
        Entity? e = AimedEntity();
        if (e == null) { message = "[hbv] dump: aim at an entity first"; return false; }
        var sb = new StringBuilder(8192);
        EntityPlayer? self = capi.World.Player?.Entity;
        sb.Append("{\n");
        J(sb, "tool", "hitboxviewer 0.2.1"); J(sb, "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        J(sb, "entityCode", e.Code?.ToString() ?? ""); Jn(sb, "entityId", e.EntityId);
        Jb(sb, "isRendered", e.IsRendered); Jb(sb, "alive", e.Alive);
        Jn(sb, "clientSize", e.Properties?.Client?.Size ?? 0);
        Jv(sb, "entityPos", e.Pos.X, e.Pos.Y, e.Pos.Z);
        Jn(sb, "posYaw", e.Pos.Yaw); Jn(sb, "posPitch", e.Pos.Pitch); Jn(sb, "posRoll", e.Pos.Roll);
        if (e is EntityAgent ea) Jn(sb, "bodyYaw", ea.BodyYaw);
        if (self != null)
        {
            Jv(sb, "playerPos", self.Pos.X, self.Pos.Y, self.Pos.Z);
            Jv(sb, "cameraPos", self.CameraPos.X, self.CameraPos.Y, self.CameraPos.Z);
        }
        Box(sb, "collisionBox", e.CollisionBox); Box(sb, "selectionBox", e.SelectionBox);
        if (CoAvailable) CoAccess.DumpColliders(sb, e);
        else { J(sb, "coBehavior", "lib-missing"); sb.Append("  \"colliders\": []\n"); }
        sb.Append("}\n");

        string code = Sanitize(e.Code?.ToShortString() ?? "entity");
        string name = $"hbv-dump-{code}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json";
        string? path = TryWrite(cfg.DumpDir, name, sb) ?? TryWrite(Path.Combine(GamePaths.DataPath, "hitboxviewer"), name, sb);
        if (path == null) { message = "[hbv] dump: could not write file"; return false; }
        message = "[hbv] dump written: " + path;
        capi.Logger.Notification("[hitboxviewer] " + message);
        return true;
    }

    private string? TryWrite(string? dir, string name, StringBuilder sb)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            File.WriteAllText(p, sb.ToString());
            return p;
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("[hitboxviewer] dump to " + dir + " failed: " + ex.Message);
            return null;
        }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        return sb.ToString();
    }

    internal static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    internal static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    internal static void J(StringBuilder sb, string k, string v) => sb.Append("  \"").Append(k).Append("\": \"").Append(Esc(v)).Append("\",\n");
    internal static void Jn(StringBuilder sb, string k, double v) => sb.Append("  \"").Append(k).Append("\": ").Append(F(v)).Append(",\n");
    internal static void Jb(StringBuilder sb, string k, bool v) => sb.Append("  \"").Append(k).Append("\": ").Append(v ? "true" : "false").Append(",\n");
    internal static void Jv(StringBuilder sb, string k, double x, double y, double z) =>
        sb.Append("  \"").Append(k).Append("\": [").Append(F(x)).Append(", ").Append(F(y)).Append(", ").Append(F(z)).Append("],\n");

    private static void Box(StringBuilder sb, string k, Cuboidf? b)
    {
        if (b == null) { sb.Append("  \"").Append(k).Append("\": null,\n"); return; }
        sb.Append("  \"").Append(k).Append("\": {\"from\": [").Append(F(b.X1)).Append(", ").Append(F(b.Y1)).Append(", ").Append(F(b.Z1))
          .Append("], \"to\": [").Append(F(b.X2)).Append(", ").Append(F(b.Y2)).Append(", ").Append(F(b.Z2)).Append("]},\n");
    }

    // ---------------------------------------------------------------- colours

    internal static Vec4f? OrganColor(string? organ) => organ switch
    {
        "Heart" => ColHeart,
        "Lungs" => ColLungs,
        "Brain" => ColBrain,
        "Eyes" => ColEyes,
        _ => null,
    };

    /// <summary>Organ by element-name prefix (CO has no organ type; organs are Critical elements named Heart/Lungs/Brain/Eye*).</summary>
    internal static string? OrganOf(string name)
    {
        if (name.StartsWith("heart", StringComparison.OrdinalIgnoreCase)) return "Heart";
        if (name.StartsWith("lung", StringComparison.OrdinalIgnoreCase)) return "Lungs";
        if (name.StartsWith("brain", StringComparison.OrdinalIgnoreCase)) return "Brain";
        if (name.StartsWith("eye", StringComparison.OrdinalIgnoreCase)) return "Eyes";
        return null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vec4f PartColor(string name)
    {
        uint h = 2166136261;
        foreach (char ch in name) { h ^= ch; h *= 16777619; }
        return Palette[h % (uint)Palette.Length];
    }

    public void Dispose()
    {
        cube?.Dispose();
        cube = null;
        foreach (LoadedTexture t in labelTex.Values) t.Dispose();
        labelTex.Clear();
    }
}
