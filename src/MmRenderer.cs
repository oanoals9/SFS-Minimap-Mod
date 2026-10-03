using System;
using System.Collections.Generic;
using SFS.Cameras;
using SFS.World;
using SFS.World.Maps;
using SFS.WorldBase;
using UnityEngine;

namespace SFSMiniMap
{
    /// <summary>
    /// Draws the map into a render texture with a camera of its own.
    ///
    /// The drawing is intentionally independent from the game's map view: that one lives under
    /// MapManager.mapSystemHolder, which the game switches off whenever the map is closed, so it
    /// cannot be used while flying. What this class does instead is
    ///   * draw the planet, its atmosphere, its sphere of influence, the vehicle and the
    ///     apoapsis/periapsis markers itself, with the game's own sprites and colours, and
    ///   * copy the lines the game has just drawn into Map.solidLine / Map.dashedLine.
    ///
    /// The second point is what makes the aerodynamic trajectory of the "Aero Trajectory" mod show up
    /// in the minimap: that mod draws into those two pools, so its line is copied along with the
    /// orbit lines, and disappears again when the mod is uninstalled. Nothing is written back - the
    /// pools are only read.
    ///
    /// Units: 1 unit = 1 km, the same as the game's map view, and everything is relative to the
    /// centre of the planet the vehicle is at.
    /// </summary>
    public class MmRenderer
    {
        private const float Z = 1000f;

        private GameObject root;
        private Camera camera;
        private RenderTexture texture;
        private int layer;

        private SpriteRenderer planetDisc;
        private SpriteRenderer atmosphereDisc;
        private SpriteRenderer soiDisc;
        private SpriteRenderer shipMarker;
        private SpriteRenderer apMarker;
        private SpriteRenderer peMarker;
        private LineRenderer ownOrbitLine;

        /// <summary>One entry per body that can be drawn: its disc, its atmosphere and its sphere of
        /// influence. They are pooled, because the number of bodies on screen changes all the time.</summary>
        private class BodyEntry
        {
            public SpriteRenderer Disc;
            public SpriteRenderer Atmosphere;
            public SpriteRenderer Soi;
        }

        private readonly List<BodyEntry> planetPool = new List<BodyEntry>();
        private readonly List<SpriteRenderer> vesselPool = new List<SpriteRenderer>();

        private readonly List<Planet> bodyCandidates = new List<Planet>();
        private readonly List<float> bodyDistances = new List<float>();
        private readonly List<Vector3> bodyOffsets = new List<Vector3>();
        private readonly Dictionary<Planet, BodyInfo> bodyInfos = new Dictionary<Planet, BodyInfo>();
        private readonly List<MapPlayer> otherPlayers = new List<MapPlayer>();

        private Planet[] allPlanets;
        private int otherPlayerRefresh;
        private int bodiesDrawn;
        private int vesselsDrawn;
        private int bodiesCulled;

        /// <summary>What does not change while a flight lasts: how big a body is, what colour it is,
        /// whether it has an atmosphere and whether it has a sphere of influence at all.</summary>
        private struct BodyInfo
        {
            public float RadiusKm;
            public float AtmosphereKm;
            public float SoiKm;
            public Color Color;
            public bool HasRing;
        }

        private readonly List<LineRenderer> linePool = new List<LineRenderer>();
        private readonly List<MmLines.Source> sources = new List<MmLines.Source>();
        private readonly List<Vector3[]> buffers = new List<Vector3[]>();

        private int lastMapSize;

        public bool Ready { get; private set; }
        public string LastError { get; private set; }
        public int Layer { get { return layer; } }
        public string LayerChoice { get; private set; }
        public int HiddenFromCameras { get; private set; }
        public int CamerasSeen { get; private set; }
        public RenderTexture Texture { get { return texture; } }
        public int MirroredLines { get; private set; }
        public int MirroredPoints { get; private set; }
        public int LinesOffScreen { get; private set; }
        public string LineWidthReport = "not drawn yet";
        public bool OwnOrbitDrawn { get; private set; }
        public string OwnOrbitReason { get; private set; }

        private int cameraRefresh;
        private int frameCounter;
        private readonly List<Camera> otherCameras = new List<Camera>();

        public MmRenderer()
        {
            LastError = "";
            OwnOrbitReason = "never drawn";
            try
            {
                Build();
                Ready = true;
            }
            catch (Exception e)
            {
                Ready = false;
                LastError = e.GetType().Name + ": " + e.Message;
                MmLog.Error("the minimap camera could not be created: " + e);
                Destroy();
            }
        }

        // ------------------------------------------------------------------ construction

        private void Build()
        {
            layer = FindFreeLayer(out string how);
            LayerChoice = how;

            root = new GameObject("SFS Mini Map (renderer)");
            root.transform.position = Vector3.zero;
            root.transform.rotation = Quaternion.identity;
            root.layer = layer;

            int size = MmConfig.MapSize;
            lastMapSize = size;
            texture = CreateTexture(size);

            GameObject cameraObject = new GameObject("SFS Mini Map (camera)");
            cameraObject.transform.SetParent(root.transform, false);
            cameraObject.transform.localPosition = Vector3.zero;
            cameraObject.transform.localRotation = Quaternion.identity;
            cameraObject.layer = layer;

            camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.cullingMask = 1 << layer;
            camera.nearClipPlane = 1f;
            camera.farClipPlane = 1000000f;
            camera.depth = -100f;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.targetTexture = texture;

            // The game's own part icon camera sets exactly this before rendering into its texture
            // (PartIconCreator.Render), so the minimap does the same.
            try
            {
                camera.forceIntoRenderTexture = true;
            }
            catch (Exception e)
            {
                MmLog.Detail("forceIntoRenderTexture is not available: " + e.Message);
            }

            planetDisc = MakeSpriteObject("planet", 20);
            atmosphereDisc = MakeSpriteObject("atmosphere", 21);
            soiDisc = MakeSpriteObject("soi", 19);
            apMarker = MakeSpriteObject("apoapsis", 32);
            peMarker = MakeSpriteObject("periapsis", 32);
            shipMarker = MakeSpriteObject("vehicle", 40);

            // Bodies other than the one the vehicle is at, and the vehicles in the same sky.
            for (int i = 0; i < 16; i++)
            {
                BodyEntry entry = new BodyEntry();
                entry.Soi = MakeSpriteObject("body soi " + i, 19);
                entry.Disc = MakeSpriteObject("body " + i, 20);
                entry.Atmosphere = MakeSpriteObject("body atmosphere " + i, 21);
                planetPool.Add(entry);
            }

            for (int i = 0; i < 16; i++)
                vesselPool.Add(MakeSpriteObject("other vehicle " + i, 39));

            for (int i = 0; i < 24; i++)
            {
                LineRenderer line = MakeLine("line " + i, 10);
                linePool.Add(line);
                buffers.Add(new Vector3[0]);
            }

            // Drawn by this mod rather than copied, and on top of the copied ones.
            ownOrbitLine = MakeLine("own orbit", 11);

            MmLog.Detail("renderer ready (layer " + layer + " [" + LayerMask.LayerToName(layer) + "] - " +
                         LayerChoice + ", " + size + "px texture)");

            // The layer above is the only thing that keeps the game's cameras away from these
            // objects, so it is verified once right away and then kept verified in Update.
            HideFromOtherCameras();
        }

        private LineRenderer MakeLine(string name, int sortingOrder)
        {
            GameObject lineObject = new GameObject(name);
            lineObject.transform.SetParent(root.transform, false);
            lineObject.layer = layer;

            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = 0;
            line.numCapVertices = 0;
            line.numCornerVertices = 0;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            line.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            LineRenderer template = MmAssets.LineTemplate;
            if (template != null)
            {
                try
                {
                    line.textureMode = template.textureMode;
                    line.widthCurve = template.widthCurve;
                    line.alignment = template.alignment;
                }
                catch (Exception e)
                {
                    MmLog.Detail("line template could not be copied: " + e.Message);
                }
            }

            SetMaterial(line);
            SetSorting(line, sortingOrder);
            lineObject.SetActive(false);
            return line;
        }

        private void SetMaterial(LineRenderer line)
        {
            if (MmAssets.LineMaterial != null)
            {
                try
                {
                    // Assigning to .material gives this renderer its own copy, which is what the
                    // per line texture scale below needs.
                    line.material = MmAssets.LineMaterial;
                    return;
                }
                catch (Exception e)
                {
                    MmLog.Detail("line material could not be set: " + e.Message);
                }
            }
        }

        /// <summary>Called when a better line material became available (the game's own map is not
        /// always there on the first frame).</summary>
        public void RefreshMaterial()
        {
            for (int i = 0; i < linePool.Count; i++)
                SetMaterial(linePool[i]);
        }

        private SpriteRenderer MakeSpriteObject(string name, int sortingOrder)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, Z);
            go.layer = layer;

            SpriteRenderer renderer = go.AddComponent<SpriteRenderer>();
            renderer.enabled = true;
            renderer.color = Color.white;
            SetSorting(renderer, sortingOrder);
            go.SetActive(false);
            return renderer;
        }

        private static void SetSorting(Renderer renderer, int order)
        {
            if (renderer == null)
                return;

            // The default sorting layer is enough: the minimap camera only renders the layer this mod
            // owns, so the sorting order below is the only thing that decides what is on top.
            renderer.sortingOrder = order;
        }

        /// <summary>
        /// A layer of our own, so that the minimap camera can never pick up part of the game and the
        /// game's cameras can never pick up part of the minimap.
        ///
        /// The first eight layers are built in and are skipped. A layer that some camera of the game
        /// already renders would make a planet sized sprite appear in the flight view, so unnamed
        /// layers are preferred, and one that no current camera renders even more so; the bit is also
        /// cleared from every other camera in <see cref="HideFromOtherCameras"/>, which is what makes
        /// this safe even when a camera is created later.
        /// </summary>
        private static int FindFreeLayer(out string how)
        {
            int gameMask = GameCameraMask();

            for (int i = 8; i < 32; i++)
            {
                if ((gameMask & (1 << i)) != 0)
                    continue;
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                    continue;

                how = "free, and no camera of the game rendered it at startup";
                return i;
            }

            for (int i = 8; i < 32; i++)
            {
                if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                {
                    how = "free (the bit is cleared from the game's cameras)";
                    return i;
                }
            }

            int map = LayerMask.NameToLayer("Map");
            how = "WARNING: no free layer, the game's Map layer is reused";
            if (map >= 0)
                return map;

            how = "WARNING: no usable layer found, layer 0 is reused";
            return 0;
        }

        /// <summary>Every layer any camera of the game renders, whether that camera is switched on at
        /// this moment or not.</summary>
        private static int GameCameraMask()
        {
            int mask = 0;

            try
            {
                Camera[] cameras = Camera.allCameras;
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (cameras[i] != null)
                        mask |= cameras[i].cullingMask;
                }
            }
            catch
            {
            }

            try
            {
                GameCamerasManager manager = GameCamerasManager.main;
                if (manager != null)
                {
                    Camera world = Manager(manager.world_Camera);
                    Camera scaled = Manager(manager.scaledWorld_Camera);
                    Camera map = Manager(manager.map_Camera);

                    if (world != null)
                        mask |= world.cullingMask;
                    if (scaled != null)
                        mask |= scaled.cullingMask;
                    if (map != null)
                        mask |= map.cullingMask;
                }
            }
            catch
            {
            }

            return mask;
        }

        /// <summary>Every layer the game gave a name to, so that the free ones can be told apart from
        /// the ones the game uses silently.</summary>
        public static string DescribeNamedLayers()
        {
            string text = "";

            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name))
                    continue;

                if (text.Length > 0)
                    text += ", ";

                text += i + "=" + name;
            }

            return text.Length > 0 ? text : "none";
        }

        /// <summary>The unnamed layers and whether any camera of the game renders them, one character
        /// each: "8:- 9:- 10:+" means layer 9 is rendered by the game and 8 and 10 are not.</summary>
        public static string DescribeLayers()
        {
            int gameMask = GameCameraMask();
            string text = "";

            for (int i = 8; i < 32; i++)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i)))
                    continue;

                text += i + ((gameMask & (1 << i)) != 0 ? ":+" : ":-") + " ";
            }

            return text.Length > 0 ? text.TrimEnd() : "none";
        }

        /// <summary>
        /// Clears this mod's layer from every camera that is not ours, so that the minimap can never
        /// leak into the flight view, into scaled space or into the game's map view.
        ///
        /// Camera.allCameras only lists the cameras that are switched on right now, and the game
        /// switches its three cameras on and off depending on the view (ActiveCamera does exactly
        /// that), so the three are cleared through GameCamerasManager as well - otherwise the first
        /// time the player zooms out into scaled space the minimap would appear there as a planet
        /// sized sprite. This is repeated rather than done once, because cameras come and go with the
        /// scene.
        /// </summary>
        private void HideFromOtherCameras()
        {
            if (layer <= 0 || camera == null)
                return;

            int cleared = 0;
            int seen = 0;

            try
            {
                Camera[] cameras = Camera.allCameras;
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (Clear(cameras[i]))
                    {
                        seen++;
                        cleared++;
                    }
                    else if (cameras[i] != null && cameras[i] != camera)
                    {
                        seen++;
                    }
                }
            }
            catch (Exception e)
            {
                MmLog.Detail("the camera list could not be read: " + e.Message);
            }

            // The three views of the game, whether they are switched on at this moment or not.
            try
            {
                GameCamerasManager manager = GameCamerasManager.main;
                if (manager != null)
                {
                    if (Clear(Manager(manager.world_Camera))) { seen++; cleared++; }
                    if (Clear(Manager(manager.scaledWorld_Camera))) { seen++; cleared++; }
                    if (Clear(Manager(manager.map_Camera))) { seen++; cleared++; }
                }
            }
            catch (Exception e)
            {
                MmLog.Detail("the game's cameras could not be reached: " + e.Message);
            }

            HiddenFromCameras = cleared;
            CamerasSeen = seen;
        }

        private static Camera Manager(CameraManager manager)
        {
            if (manager == null)
                return null;

            try
            {
                return manager.camera;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Clears this mod's bit from one camera. True when it actually changed something.</summary>
        private bool Clear(Camera other)
        {
            if (other == null || other == camera)
                return false;

            try
            {
                int mask = other.cullingMask & ~(1 << layer);
                if (mask == other.cullingMask)
                    return false;

                other.cullingMask = mask;
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ per frame

        public void Update(MmSnapshot snapshot, Vector2 centerKm, float halfKm, bool show,
                           float viewAngleDegrees)
        {
            if (!Ready)
                return;

            try
            {
                if (--cameraRefresh <= 0)
                {
                    HideFromOtherCameras();

                    // Normally nothing is left to clear after the first pass, so a check now and then
                    // is enough. If something put the bit back, the check has to happen every frame -
                    // and it also does for the first seconds, in case the game's cameras are not there
                    // yet.
                    cameraRefresh = (HiddenFromCameras > 0 || frameCounter < 180) ? 1 : 30;
                }

                frameCounter++;
                EnsureSize();
            }            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                MmLog.Error("the minimap camera could not be kept ready: " + e);
                return;
            }

            if (!show)
            {
                if (root.activeSelf)
                    root.SetActive(false);
                if (camera.enabled)
                    camera.enabled = false;
                return;
            }

            if (!root.activeSelf)
                root.SetActive(true);
            if (!camera.enabled)
                camera.enabled = true;

            try
            {
                Draw(snapshot, centerKm, halfKm, viewAngleDegrees);
                LastError = "";
            }
            catch (Exception e)
            {
                LastError = e.GetType().Name + ": " + e.Message;
                MmLog.Error("drawing the minimap failed: " + e);
            }
        }

        /// <summary>
        /// The render texture, set up the way the game sets up its own part icon texture
        /// (PartIconCreator.Render): the same colour format and, usefully, a 24 bit depth buffer.
        /// </summary>
        private static RenderTexture CreateTexture(int size)
        {
            RenderTexture created = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32);
            created.name = "SFSMiniMapRT";
            created.filterMode = FilterMode.Bilinear;
            created.wrapMode = TextureWrapMode.Clamp;
            created.Create();
            return created;
        }

        private void EnsureSize()
        {
            int size = MmConfig.MapSize;
            if (size == lastMapSize && texture != null && texture.IsCreated())
                return;

            lastMapSize = size;
            if (texture != null)
            {
                camera.targetTexture = null;
                texture.Release();
                UnityEngine.Object.Destroy(texture);
            }

            texture = CreateTexture(size);
            if (camera != null)
                camera.targetTexture = texture;
        }

        private void Draw(MmSnapshot snapshot, Vector2 centerKm, float halfKm, float viewAngleDegrees)
        {
            int mapPx = Mathf.Max(1, lastMapSize);
            float worldPerPixel = (2f * halfKm) / mapPx;
            Vector3 center = new Vector3(centerKm.x, centerKm.y, 0f);

            if (camera != null)
            {
                camera.orthographicSize = Mathf.Max(0.001f, halfKm);
                camera.aspect = 1f;
                camera.transform.localPosition = Vector3.zero;

                // Turning the camera about its own view axis turns the whole picture, which is what
                // "keeping the ground at the bottom" amounts to. It leaves every object exactly as
                // far away as before, so nothing else has to change.
                camera.transform.localRotation = Quaternion.Euler(0f, 0f, viewAngleDegrees);

                // The map has a background of its own: an opaque one is what the reference picture
                // shows, and it is also what keeps the half transparent atmosphere and sphere of
                // influence looking right once the picture is put on an ordinary UI image.
                camera.backgroundColor = new Color(0.016f, 0.024f, 0.071f,
                                                   Mathf.Clamp01(MmConfig.BackgroundOpacity));
            }

            // Where the middle of the planet is, in the same space the game's map draws in.
            Vector3 planetOrigin = Vector3.zero;
            if (snapshot != null && snapshot.planet != null && snapshot.planet.mapHolder != null)
                planetOrigin = snapshot.planet.mapHolder.position;

            // ---- bodies -----------------------------------------------------------------------

            bool ok = snapshot != null && snapshot.valid;

            if (ok)
            {
                DrawBodies(snapshot, planetOrigin, center, halfKm);
            }
            else
            {
                HideBodies();
            }

            // ---- lines (the game's own, including the ones other mods add) --------------------

            if (ok && MmConfig.ShowOrbit)
                MirrorLines(planetOrigin, center, worldPerPixel, mapPx, halfKm);
            else
                HideAllLines();
            // ---- markers ----------------------------------------------------------------------

            Vector3 orbitOffset = Vector3.zero;
            if (ok && snapshot.orbit != null && snapshot.orbit.Planet != null &&
                snapshot.orbit.Planet.mapHolder != null)
            {
                orbitOffset = snapshot.orbit.Planet.mapHolder.position - planetOrigin;
            }

            if (ok && MmConfig.ShowOrbit && MmConfig.DrawOwnOrbit)
                DrawOwnOrbit(snapshot, orbitOffset, center, worldPerPixel);
            else
            {
                HideLine(ownOrbitLine);
                OwnOrbitDrawn = false;
            }

            if (ok && MmConfig.ShowMarkers)
            {
                ShowMarker(apMarker, snapshot.HasAp, snapshot.orbit, Math.PI, orbitOffset, center,
                           worldPerPixel, 9f);
                ShowMarker(peMarker, snapshot.HasPe, snapshot.orbit, 0.0, orbitOffset, center,
                           worldPerPixel, 9f);
            }
            else
            {
                Hide(apMarker);
                Hide(peMarker);
            }

            if (ok && MmConfig.ShowShip)
            {
                Vector3 position = new Vector3(snapshot.shipKm.x, snapshot.shipKm.y, 0f) - center;
                SetSprite(shipMarker, MmAssets.ShipSprite, Color.white, position, worldPerPixel, 16f);

                // The game's own map turns the vehicle icon to the vehicle's *attitude*
                // (Rocket.UpdateMapIconRotation calls mapIcon.SetRotation(GetRotation())), not to the
                // direction of travel - which is why a rocket on the launch pad, with no velocity at
                // all, still shows which way it is pointing.
                float angle = 90f;
                if (snapshot.rocket != null)
                {
                    try
                    {
                        angle = snapshot.rocket.GetRotation();
                    }
                    catch (Exception e)
                    {
                        MmLog.Detail("the vehicle rotation could not be read: " + e.Message);
                    }
                }
                else if (snapshot.velocity.sqrMagnitude > 0.01f)
                {
                    angle = Mathf.Atan2(snapshot.velocity.y, snapshot.velocity.x) * Mathf.Rad2Deg;
                }

                shipMarker.transform.localRotation = Quaternion.Euler(0f, 0f, angle - 90f);
            }
            else
            {
                Hide(shipMarker);
            }

            if (ok)
                DrawOtherVehicles(snapshot, planetOrigin, center, worldPerPixel, halfKm);
            else
                HideVessels();
        }

        /// <summary>
        /// Every body there is, the one the vehicle is at included, drawn from its distance to that
        /// planet and its own size. Anything whose nearest edge is nowhere near the view is skipped
        /// before a single renderer is touched, which is what keeps this cheap even though a solar
        /// system holds dozens of bodies.
        /// </summary>
        private void DrawBodies(MmSnapshot snapshot, Vector3 planetOrigin, Vector3 center, float halfKm)
        {
            bodyCandidates.Clear();
            bodyDistances.Clear();
            bodyOffsets.Clear();
            bodiesCulled = 0;

            Planet current = snapshot.planet;
            Planet[] planets = AllPlanets();
            float reach = Mathf.Abs(halfKm) * 1.5f;

            for (int i = 0; i < planets.Length; i++)
            {
                Planet planet = planets[i];
                if (MmLib.IsNull(planet))
                    continue;

                Transform holder = planet.mapHolder;
                if (MmLib.IsNull(holder))
                    continue;

                bool isCurrent = planet == current;
                if (!isCurrent && !MmConfig.ShowOtherBodies)
                    continue;

                BodyInfo info = InfoFor(planet);
                if (info.RadiusKm <= 0.0001f)
                    continue;

                Vector3 offset = holder.position - planetOrigin;
                float distance = offset.magnitude;

                float extent = info.RadiusKm + info.AtmosphereKm;
                if (info.HasRing && MmSnapshot.IsFinite(info.SoiKm))
                    extent = Mathf.Max(extent, info.SoiKm);

                if (distance - extent > reach)
                {
                    bodiesCulled++;
                    continue;
                }

                bodyCandidates.Add(planet);
                bodyDistances.Add(distance);
                bodyOffsets.Add(offset);
            }

            // Nearest first, so that the pool always holds the bodies that matter most.
            SortByDistance();

            bool shaded = MmConfig.PlanetShading && MmAssets.ShadedSprite != null;
            Sprite discSprite = shaded ? MmAssets.ShadedSprite : MmAssets.WhiteSprite;

            int used = 0;
            for (int i = 0; i < bodyCandidates.Count && used < planetPool.Count; i++)
            {
                Planet planet = bodyCandidates[i];
                BodyInfo info = bodyInfos[planet];
                Vector3 offset = bodyOffsets[i];
                bool isCurrent = planet == current;
                BodyEntry entry = planetPool[used];

                if (MmConfig.ShowPlanet)
                    SetDisc(entry.Disc, discSprite, info.Color, offset, center, info.RadiusKm * 2f);
                else
                    Hide(entry.Disc);

                if (MmConfig.ShowAtmosphere && info.AtmosphereKm > 0.01f)
                {
                    SetDisc(entry.Atmosphere, MmAssets.WhiteSprite, new Color(1f, 1f, 1f, 0.1f),
                            offset, center, (info.RadiusKm + info.AtmosphereKm) * 2f);
                }
                else
                {
                    Hide(entry.Atmosphere);
                }

                if (MmConfig.ShowSoi && info.HasRing && MmSnapshot.IsFinite(info.SoiKm) && info.SoiKm > 0.01f)
                {
                    SetDisc(entry.Soi, MmAssets.SoiSprite, new Color(1f, 1f, 1f, 0.08f),
                            offset, center, info.SoiKm * 2f);
                }
                else
                {
                    Hide(entry.Soi);
                }

                used++;
            }

            for (int i = used; i < planetPool.Count; i++)
            {
                Hide(planetPool[i].Disc);
                Hide(planetPool[i].Atmosphere);
                Hide(planetPool[i].Soi);
            }

            bodiesDrawn = used;
        }

        private void HideBodies()
        {
            for (int i = 0; i < planetPool.Count; i++)
            {
                Hide(planetPool[i].Disc);
                Hide(planetPool[i].Atmosphere);
                Hide(planetPool[i].Soi);
            }

            bodiesDrawn = 0;
        }

        private void SortByDistance()
        {
            // An insertion sort on three parallel lists: the counts are small (a handful of bodies
            // are ever near enough) and it allocates nothing.
            for (int i = 1; i < bodyDistances.Count; i++)
            {
                float distance = bodyDistances[i];
                Planet planet = bodyCandidates[i];
                Vector3 offset = bodyOffsets[i];
                int j = i - 1;

                while (j >= 0 && bodyDistances[j] > distance)
                {
                    bodyDistances[j + 1] = bodyDistances[j];
                    bodyCandidates[j + 1] = bodyCandidates[j];
                    bodyOffsets[j + 1] = bodyOffsets[j];
                    j--;
                }

                bodyDistances[j + 1] = distance;
                bodyCandidates[j + 1] = planet;
                bodyOffsets[j + 1] = offset;
            }
        }

        private Planet[] AllPlanets()
        {
            if (allPlanets != null)
                return allPlanets;

            try
            {
                PlanetLoader loader = SFS.Base.planetLoader;
                if (loader != null && loader.planets != null)
                {
                    allPlanets = new Planet[loader.planets.Count];
                    loader.planets.Values.CopyTo(allPlanets, 0);
                }
            }
            catch (Exception e)
            {
                MmLog.Detail("the list of planets could not be read: " + e.Message);
            }

            if (allPlanets == null)
                allPlanets = new Planet[0];

            MmLog.Detail("bodies: " + allPlanets.Length + " planet(s) known");
            return allPlanets;
        }

        /// <summary>How a body looks. Worked out once per body, not once per frame.</summary>
        private BodyInfo InfoFor(Planet planet)
        {
            BodyInfo info;
            if (bodyInfos.TryGetValue(planet, out info))
                return info;

            info = new BodyInfo();

            try
            {
                info.RadiusKm = (float)(planet.Radius / 1000.0);
                info.SoiKm = MmSnapshot.IsFinite(planet.SOI) ? (float)(planet.SOI / 1000.0) : float.PositiveInfinity;
                info.HasRing = planet.HasParent;

                if (planet.data != null && planet.data.basics != null)
                    info.Color = planet.data.basics.mapColor;

                if (planet.HasAtmospherePhysics)
                    info.AtmosphereKm = (float)(planet.AtmosphereHeightPhysics / 1000.0);
            }
            catch (Exception e)
            {
                MmLog.Detail("a body's appearance could not be read (" + planet.name + "): " + e.Message);
            }

            bodyInfos[planet] = info;
            return info;
        }

        /// <summary>
        /// The triangles of the other vehicles, at whatever body they are at - so a rocket on another
        /// planet shows up in the right place relative to the planet the camera is looking at.
        /// </summary>
        private void DrawOtherVehicles(MmSnapshot snapshot, Vector3 planetOrigin, Vector3 center,
                                       float worldPerPixel, float halfKm)
        {
            if (!MmConfig.ShowOtherVehicles)
            {
                HideVessels();
                return;
            }

            if (--otherPlayerRefresh <= 0)
            {
                otherPlayerRefresh = 30;
                RefreshOtherPlayers(snapshot);
            }

            float reach = Mathf.Abs(halfKm) * 1.5f;
            int used = 0;

            for (int i = 0; i < otherPlayers.Count && used < vesselPool.Count; i++)
            {
                MapPlayer player = otherPlayers[i];
                if (MmLib.IsNull(player))
                    continue;

                Location location;
                try
                {
                    location = player.Location;
                }
                catch
                {
                    continue;
                }

                if (MmLib.IsNull(location.planet) || MmLib.IsNull(location.planet.mapHolder))
                    continue;

                Vector3 offset = location.planet.mapHolder.position - planetOrigin;
                Vector3 local = MmTarget.ToKm3(location.position) + offset - center;

                if (Mathf.Abs(local.x) > reach || Mathf.Abs(local.y) > reach)
                    continue;

                float angle = 90f;
                MapRocket rocket = player as MapRocket;
                if (rocket != null && MmLib.IsSet(rocket.rocket))
                {
                    try
                    {
                        angle = rocket.rocket.GetRotation();
                    }
                    catch
                    {
                        angle = 90f;
                    }
                }

                SpriteRenderer marker = vesselPool[used];
                SetSprite(marker, MmAssets.ShipSprite, new Color(1f, 1f, 1f, 0.85f), local,
                          worldPerPixel, 13f);
                marker.transform.localRotation = Quaternion.Euler(0f, 0f, angle - 90f);
                used++;
            }

            for (int i = used; i < vesselPool.Count; i++)
                Hide(vesselPool[i]);

            vesselsDrawn = used;
        }

        private void HideVessels()
        {
            for (int i = 0; i < vesselPool.Count; i++)
                Hide(vesselPool[i]);

            vesselsDrawn = 0;
        }

        private void RefreshOtherPlayers(MmSnapshot snapshot)
        {
            otherPlayers.Clear();

            try
            {
                List<SelectableObject> objects = SelectableObject.mapObjects;
                if (objects == null)
                    return;

                MapPlayer mine = null;
                try
                {
                    PlayerController controller = PlayerController.main;
                    if (MmLib.IsSet(controller) && MmLib.IsSet(controller.player))
                    {
                        Player player = controller.player.Value;
                        if (MmLib.IsSet(player))
                            mine = player.mapPlayer;
                    }
                }
                catch
                {
                }

                for (int i = 0; i < objects.Count; i++)
                {
                    MapPlayer candidate = objects[i] as MapPlayer;
                    if (candidate == null)
                        continue;
                    if (mine != null && candidate == mine)
                        continue;

                    otherPlayers.Add(candidate);
                }
            }
            catch (Exception e)
            {
                MmLog.Detail("the other vehicles could not be listed: " + e.Message);
            }
        }

        /// <summary>
        /// Places a body that is drawn as a disc.
        ///
        /// <paramref name="planetRelative"/> is the centre of that body measured from the centre of
        /// the planet the vehicle is at, in kilometres - NOT a position in the game's map space. The
        /// two are only the same when the game's map view happens to be centred on this planet, which
        /// is why passing a map space position here made the planet fly off screen as soon as the map
        /// was opened and focused on something else. The planet the vehicle is at is at (0,0,0).
        /// </summary>
        private void SetDisc(SpriteRenderer renderer, Sprite sprite, Color color,
                             Vector3 planetRelative, Vector3 center, float diameterKm)
        {
            if (renderer == null || sprite == null)
            {
                Hide(renderer);
                return;
            }

            float spriteWidth = SpriteWidth(sprite);
            float scale = spriteWidth > 0.0001f ? diameterKm / spriteWidth : diameterKm;

            renderer.gameObject.SetActive(true);
            renderer.sprite = sprite;
            renderer.color = color;
            renderer.transform.localPosition = planetRelative - center + new Vector3(0f, 0f, Z);
            renderer.transform.localScale = new Vector3(scale, scale, 1f);
        }

        private void ShowMarker(SpriteRenderer renderer, bool valid, Orbit orbit, double trueAnomaly,
                                Vector3 orbitOffset, Vector3 center, float worldPerPixel, float pixels)
        {
            if (!valid || orbit == null)
            {
                Hide(renderer);
                return;
            }

            Double2 position = orbit.GetPositionAtTrueAnomaly(trueAnomaly);
            if (!MmSnapshot.IsFinite(position.x) || !MmSnapshot.IsFinite(position.y))
            {
                Hide(renderer);
                return;
            }

            Vector3 local = MmTarget.ToKm3(position) + orbitOffset - center;
            SetSprite(renderer, MmAssets.WhiteSprite, Color.white, local, worldPerPixel, pixels);
        }

        /// <summary>
        /// Draws the vehicle's current orbit from the game's own orbit maths.
        ///
        /// This is not a luxury. The game fades an orbit line out as its own map view zooms away
        /// (TrajectoryDrawer.GetOrbitAlpha) and returns early - without putting anything into the line
        /// pool - the moment that fade reaches zero, which happens as soon as the map is zoomed out
        /// past the size of the orbit. There is then nothing for the mirroring above to copy, so the
        /// orbit is computed here instead.
        ///
        /// A closed orbit is drawn whole, the way the reference picture shows it; anything else is
        /// drawn over the same time range the game would have used.
        /// </summary>
        private void DrawOwnOrbit(MmSnapshot snapshot, Vector3 orbitOffset, Vector3 center,
                                  float worldPerPixel)
        {
            OwnOrbitDrawn = false;
            OwnOrbitReason = "no orbit";

            if (ownOrbitLine == null)
                return;

            Orbit orbit = snapshot.orbit;
            if (orbit == null)
            {
                HideLine(ownOrbitLine);
                return;
            }

            double a0;
            double a1;
            bool haveRange = false;

            if (orbit.ecc < 1.0 && MmSnapshot.IsFinite(orbit.sma) && orbit.sma > 0.0)
            {
                a0 = 0.0;
                a1 = Math.PI * 2.0;
                haveRange = true;
                OwnOrbitReason = "whole orbit";
            }
            else
            {
                double start = orbit.PathStartTime;
                double end = orbit.PathEndTime;

                try
                {
                    double now = WorldTime.main.worldTime;
                    if (MmSnapshot.IsFinite(now) && now > start)
                        start = now;
                }
                catch
                {
                }

                if (MmSnapshot.IsFinite(start) && MmSnapshot.IsFinite(end) && end > start)
                {
                    a0 = orbit.GetTrueAnomaly(start);
                    a1 = orbit.GetTrueAnomaly(end);
                    haveRange = MmSnapshot.IsFinite(a0) && MmSnapshot.IsFinite(a1);
                    OwnOrbitReason = haveRange ? "open path" : "no usable true anomaly";
                }
                else
                {
                    a0 = 0.0;
                    a1 = 0.0;
                    OwnOrbitReason = "the open path has no usable time range";
                }
            }

            Vector3[] points = null;

            if (haveRange)
            {
                points = orbit.GetPoints(a0, a1, 96, 0.001);
                if (points == null || points.Length < 2)
                    points = null;
            }

            // Last resort: walk the orbit's own conic equation, which needs neither the path's time
            // range nor GetPoints - and it is what the game uses for the apoapsis/periapsis markers,
            // so it is known to answer for every orbit the game can produce.
            if (points == null && orbit.ecc < 1.0)
            {
                points = SampleConic(orbit, 128);
                if (points != null)
                    OwnOrbitReason = "sampled from the conic";
            }

            if (points == null)
            {
                if (OwnOrbitReason == "whole orbit" || OwnOrbitReason == "open path")
                    OwnOrbitReason = "the orbit produced no usable points";
                HideLine(ownOrbitLine);
                return;
            }

            for (int i = 0; i < points.Length; i++)
            {
                Vector3 point = points[i];
                if (float.IsNaN(point.x) || float.IsNaN(point.y))
                {
                    OwnOrbitReason = "GetPoints returned NaN";
                    HideLine(ownOrbitLine);
                    return;
                }

                point = point + orbitOffset - center;
                points[i] = new Vector3(point.x, point.y, Z);
            }

            float curveMid = 1f;
            try
            {
                curveMid = ownOrbitLine.widthCurve.Evaluate(0.5f);
            }
            catch
            {
                curveMid = 1f;
            }
            if (curveMid <= 0.0001f)
                curveMid = 1f;

            ownOrbitLine.gameObject.SetActive(true);
            ownOrbitLine.positionCount = points.Length;
            ownOrbitLine.SetPositions(points);
            ownOrbitLine.startColor = Color.white;
            ownOrbitLine.endColor = Color.white;
            ownOrbitLine.widthMultiplier = (worldPerPixel * MmConfig.LineWidthPx) / curveMid;

            ScaleTexture(ownOrbitLine, points, worldPerPixel);
            OwnOrbitDrawn = true;
        }

        /// <summary>
        /// Builds the closed orbit by asking the orbit itself where it is at each true anomaly, for
        /// one full revolution. Unlike GetPoints this needs no time range, which is what makes it the
        /// fallback that always answers for a closed orbit.
        /// </summary>
        private static Vector3[] SampleConic(Orbit orbit, int samples)
        {
            List<Vector3> collected = new List<Vector3>(samples + 1);

            for (int i = 0; i < samples; i++)
            {
                double anomaly = (Math.PI * 2.0) * i / samples;

                Double2 position;
                try
                {
                    position = orbit.GetPositionAtTrueAnomaly(anomaly);
                }
                catch
                {
                    continue;
                }

                if (!MmSnapshot.IsFinite(position.x) || !MmSnapshot.IsFinite(position.y))
                    continue;

                Vector3 point = MmTarget.ToKm3(position);
                if (float.IsNaN(point.x) || float.IsNaN(point.y))
                    continue;

                collected.Add(point);
            }

            if (collected.Count < 8)
                return null;

            // Close the ring.
            collected.Add(collected[0]);
            return collected.ToArray();
        }

        private static void HideLine(LineRenderer line)
        {
            if (line != null && line.gameObject.activeSelf)
                line.gameObject.SetActive(false);
        }

        /// <summary>
        /// Keeps a dash pattern at a constant size on screen instead of a constant size in
        /// kilometres, which would stretch a single dash over the whole line.
        /// </summary>
        private static void ScaleTexture(LineRenderer line, Vector3[] points, float worldPerPixel)
        {
            try
            {
                Material material = line.material;
                if (material == null || material.mainTexture == null)
                    return;

                float length = 0f;
                for (int i = 1; i < points.Length; i++)
                {
                    length += Vector2.Distance(new Vector2(points[i - 1].x, points[i - 1].y),
                                               new Vector2(points[i].x, points[i].y));
                }

                float pixels = worldPerPixel > 0.0001f ? length / worldPerPixel : 1f;
                material.mainTextureScale = new Vector2(Mathf.Max(1f, pixels / 16f), 1f);
            }
            catch (Exception e)
            {
                MmLog.Detail("the line texture could not be scaled: " + e.Message);
            }
        }

        private void SetSprite(SpriteRenderer renderer, Sprite sprite, Color color, Vector3 position,
                               float worldPerPixel, float pixels)
        {
            if (renderer == null || sprite == null)
            {
                Hide(renderer);
                return;
            }

            float wanted = worldPerPixel * pixels;
            float spriteWidth = SpriteWidth(sprite);
            float scale = spriteWidth > 0.0001f ? wanted / spriteWidth : wanted;

            renderer.gameObject.SetActive(true);
            renderer.sprite = sprite;
            renderer.color = color;
            renderer.transform.localPosition = position + new Vector3(0f, 0f, Z);
            renderer.transform.localScale = new Vector3(scale, scale, 1f);
        }

        private static float SpriteWidth(Sprite sprite)
        {
            if (sprite == null)
                return 0f;
            try
            {
                return sprite.bounds.size.x;
            }
            catch
            {
                return 0f;
            }
        }

        private static void Hide(SpriteRenderer renderer)
        {
            if (renderer != null && renderer.gameObject.activeSelf)
                renderer.gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ mirroring the game's lines

        private void MirrorLines(Vector3 planetOrigin, Vector3 center, float worldPerPixel, int mapPx,
                                 float halfKm)
        {
            sources.Clear();
            MirroredLines = 0;
            MirroredPoints = 0;
            LinesOffScreen = 0;
            LineWidthReport = "none within the view";

            MmLines.Collect(Map.solidLine, sources);
            MmLines.Collect(Map.dashedLine, sources);

            int used = 0;
            for (int i = 0; i < sources.Count; i++)
            {
                MmLines.Source source = sources[i];
                int count = source.Line.positionCount;
                if (count < 2)
                    continue;

                if (buffers.Count <= used)
                    buffers.Add(new Vector3[0]);

                if (buffers[used].Length != count)
                    buffers[used] = new Vector3[count];

                Vector3[] buffer = buffers[used];
                source.Line.GetPositions(buffer);

                // Positions of a line are relative to the transform the game parents it to (the
                // planet's map holder), unless the prefab was built in world space.
                Vector3 lineOrigin = source.Line.useWorldSpace ? Vector3.zero : source.Line.transform.position;

                float minX = float.MaxValue;
                float maxX = float.MinValue;
                float minY = float.MaxValue;
                float maxY = float.MinValue;

                for (int p = 0; p < count; p++)
                {
                    Vector3 point = lineOrigin + buffer[p] - planetOrigin - center;
                    buffer[p] = new Vector3(point.x, point.y, Z);

                    if (point.x < minX) minX = point.x;
                    if (point.x > maxX) maxX = point.x;
                    if (point.y < minY) minY = point.y;
                    if (point.y > maxY) maxY = point.y;
                }

                // Most of what the game draws into those pools are the orbits of the planets, which
                // are astronomical distances away and can never be inside a minimap showing a few
                // hundred kilometres. Skipping them keeps the pool for the lines that are actually
                // on screen (the vehicle's own, first of all) and saves uploading thousands of
                // points that nobody would see.
                if (!OverlapsView(minX, maxX, minY, maxY, halfKm))
                {
                    LinesOffScreen++;
                    continue;
                }

                if (used >= linePool.Count)
                    continue;

                LineRenderer target = linePool[used];

                target.gameObject.SetActive(true);
                target.positionCount = count;
                target.SetPositions(buffer);
                target.startColor = Boost(source.Line.startColor);
                target.endColor = Boost(source.Line.endColor);

                float curveMid = 1f;
                try
                {
                    curveMid = target.widthCurve.Evaluate(0.5f);
                }
                catch
                {
                    curveMid = 1f;
                }
                if (curveMid <= 0.0001f)
                    curveMid = 1f;

                float width = worldPerPixel * MmConfig.LineWidthPx;
                target.widthMultiplier = width / curveMid;

                if (MirroredLines == 0)
                {
                    LineWidthReport = "width " + MmConfig.LineWidthPx.ToString("0.0") + "px -> " +
                                      width.ToString("0.####") + " km, multiplier " +
                                      target.widthMultiplier.ToString("0.####") +
                                      ", width curve " + curveMid.ToString("0.####") +
                                      ", material " + (MmAssets.LineSource ?? "?");
                }

                // Keep a dash pattern at a constant size on screen instead of a constant size in
                // kilometres, otherwise the texture would be stretched over the whole line.
                ScaleTexture(target, buffer, worldPerPixel);

                used++;
                MirroredLines++;
                MirroredPoints += count;
            }

            for (int i = used; i < linePool.Count; i++)
            {
                if (linePool[i].gameObject.activeSelf)
                    linePool[i].gameObject.SetActive(false);
            }
        }

        /// <summary>Does a line's bounding box come anywhere near what the camera can see?</summary>
        private static bool OverlapsView(float minX, float maxX, float minY, float maxY, float halfKm)
        {
            float limit = Mathf.Abs(halfKm) * 1.5f;
            if (limit < 0.001f)
                limit = 0.001f;

            return maxX >= -limit && minX <= limit && maxY >= -limit && minY <= limit;
        }

        /// <summary>
        /// The game fades an orbit line out as its map view zooms away, so the colour it hands over
        /// can be nearly - or exactly - transparent. The minimap always wants to show the trajectory,
        /// so the alpha is raised to the player's minimum. The colour itself is left alone, which is
        /// what keeps the red of another mod's entry path red.
        /// </summary>
        private static Color Boost(Color color)
        {
            float minimum = MmConfig.LineMinAlpha;
            if (minimum <= 0f || color.a >= minimum)
                return color;

            color.a = minimum;
            return color;
        }

        private void HideAllLines()        {
            for (int i = 0; i < linePool.Count; i++)
            {
                if (linePool[i].gameObject.activeSelf)
                    linePool[i].gameObject.SetActive(false);
            }

            MirroredLines = 0;
            MirroredPoints = 0;
        }

        // ------------------------------------------------------------------ teardown

        public void Destroy()
        {
            try
            {
                if (camera != null)
                    camera.targetTexture = null;
            }
            catch
            {
            }

            try
            {
                if (texture != null)
                {
                    texture.Release();
                    UnityEngine.Object.Destroy(texture);
                }
            }
            catch
            {
            }

            try
            {
                if (root != null)
                    UnityEngine.Object.Destroy(root);
            }
            catch
            {
            }

            texture = null;
            camera = null;
            root = null;
            linePool.Clear();
            buffers.Clear();
            sources.Clear();
        }

        public string Describe()
        {
            if (!Ready)
                return "not created (" + LastError + ")";

            string cameraState = camera == null
                ? "no camera"
                : ("orthographicSize " + camera.orthographicSize.ToString("0.###") +
                   ", cullingMask " + camera.cullingMask +
                   ", target " + (camera.targetTexture != null ? camera.targetTexture.width + "px" : "none") +
                   ", enabled " + camera.enabled);

            return "layer " + layer + " [" + LayerMask.LayerToName(layer) + "] (" + LayerChoice + ")" +
                   ", hidden from " + HiddenFromCameras + " other camera(s), " + cameraState +
                   ", mirrored lines " + MirroredLines + " (" + MirroredPoints + " points, " +
                   LinesOffScreen + " off screen)" +
                   ", bodies " + bodiesDrawn + " (" + bodiesCulled + " too far)" +
                   ", other vehicles " + vesselsDrawn +
                   ", own orbit " + (OwnOrbitDrawn ? "drawn (" + OwnOrbitReason + ")" : "off: " + OwnOrbitReason);
        }

        /// <summary>
        /// Reads the render texture back and says how much of it is not the background colour. This
        /// is what tells "the camera rendered nothing" apart from "the camera rendered fine but the
        /// picture never reached the window", which a screenshot alone cannot do. Reading the texture
        /// back stalls the GPU for a moment, so it only happens when a dump is asked for.
        /// </summary>
        public string ReadBackSummary()
        {
            if (texture == null || !texture.IsCreated())
                return "no render texture";

            Texture2D read = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = texture;
                read = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0f, 0f, texture.width, texture.height), 0, 0);
                read.Apply();

                Color32[] pixels = read.GetPixels32();
                if (pixels == null || pixels.Length == 0)
                    return "the read back returned no pixels";

                // What counts as "background" is worked out from the picture itself rather than from
                // camera.backgroundColor: with a linear colour space the value that ends up in the
                // texture is gamma encoded, so comparing against the colour that was set counted every
                // single pixel as "different" and said nothing.
                Color32 background = MostCommon(pixels);

                int differing = 0;
                int opaque = 0;
                byte maxR = 0;
                byte maxG = 0;
                byte maxB = 0;
                byte maxA = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 pixel = pixels[i];
                    if (pixel.r != background.r || pixel.g != background.g || pixel.b != background.b)
                        differing++;
                    if (pixel.a > 0)
                        opaque++;
                    if (pixel.r > maxR) maxR = pixel.r;
                    if (pixel.g > maxG) maxG = pixel.g;
                    if (pixel.b > maxB) maxB = pixel.b;
                    if (pixel.a > maxA) maxA = pixel.a;
                }

                return texture.width + "x" + texture.height + ": " + differing + " of " + pixels.Length +
                       " pixel(s) differ from the most common colour (" +
                       background.r + "," + background.g + "," + background.b + "," + background.a + "), " +
                       opaque + " with alpha > 0, brightest (" + maxR + "," + maxG + "," + maxB + "," + maxA + ")";
            }
            catch (Exception e)
            {
                return "the read back failed: " + e.Message;
            }
            finally
            {
                RenderTexture.active = previous;
                if (read != null)
                    UnityEngine.Object.Destroy(read);
            }
        }

        /// <summary>The colour that covers the most pixels, counted on a sampled grid so that this is
        /// cheap even for a large texture.</summary>
        private static Color32 MostCommon(Color32[] pixels)
        {
            int stride = Mathf.Max(1, pixels.Length / 4096);

            Dictionary<int, int> counts = new Dictionary<int, int>();
            for (int i = 0; i < pixels.Length; i += stride)
            {
                Color32 pixel = pixels[i];
                int key = (pixel.r << 24) | (pixel.g << 16) | (pixel.b << 8) | pixel.a;
                int count;
                counts.TryGetValue(key, out count);
                counts[key] = count + 1;

                if (counts.Count > 256)
                {
                    // A very busy picture: fall back to the first pixel and stop growing the table.
                    return pixels[0];
                }
            }

            int bestKey = 0;
            int bestCount = -1;
            foreach (KeyValuePair<int, int> pair in counts)
            {
                if (pair.Value > bestCount)
                {
                    bestCount = pair.Value;
                    bestKey = pair.Key;
                }
            }

            return new Color32((byte)((bestKey >> 24) & 0xFF), (byte)((bestKey >> 16) & 0xFF),
                               (byte)((bestKey >> 8) & 0xFF), (byte)(bestKey & 0xFF));
        }

        /// <summary>The cameras of the game and the layers they render, so that the layer choice above
        /// can be checked against reality. The three views are listed even while they are switched
        /// off, because that is what they will look like the moment they are switched on.</summary>
        public string DescribeOtherCameras()
        {
            string text = "";

            try
            {
                GameCamerasManager manager = GameCamerasManager.main;
                if (manager != null)
                {
                    text += Named("world", Manager(manager.world_Camera)) + ", ";
                    text += Named("scaled", Manager(manager.scaledWorld_Camera)) + ", ";
                    text += Named("map", Manager(manager.map_Camera));
                }
                else
                {
                    text += "GameCamerasManager.main is null";
                }
            }
            catch (Exception e)
            {
                text += "the game's cameras could not be listed: " + e.Message;
            }

            try
            {
                Camera[] cameras = Camera.allCameras;
                text += "  |  switched on now (" + cameras.Length + "): ";
                for (int i = 0; i < cameras.Length; i++)
                {
                    Camera other = cameras[i];
                    if (other == null)
                        continue;

                    if (i > 0)
                        text += ", ";

                    text += other.name + (other == camera ? " (ours)" : "") +
                            " mask=" + other.cullingMask +
                            (other.targetTexture != null ? "->rt" : "");
                }
            }
            catch (Exception e)
            {
                text += "  |  could not list the camera array: " + e.Message;
            }

            return text;
        }

        private static string Named(string label, Camera camera)
        {
            if (camera == null)
                return label + "=none";

            string state = camera.gameObject != null
                ? (camera.gameObject.activeInHierarchy ? "on" : "off")
                : "?";

            return label + "=" + camera.name + " mask=" + camera.cullingMask + " (" + state + ")";
        }
    }

    /// <summary>
    /// Reading the game's own line pools, without touching them.
    /// </summary>
    public static class MmLines
    {
        public struct Source
        {
            public LineRenderer Line;
            public bool Dashed;
        }

        public static void Collect(LineDrawer drawer, List<Source> into)
        {
            if (drawer == null)
                return;

            if (!MmLines.TryGetItems(drawer, into))
                MmLog.Detail("the line pool could not be read");
        }

        private static bool TryGetItems(LineDrawer drawer, List<Source> into)
        {
            try
            {
                if (drawer.pool == null)
                    return false;

                List<LineRenderer> items = drawer.pool.Items;
                if (items == null)
                    return false;

                for (int i = 0; i < items.Count; i++)
                {
                    LineRenderer line = items[i];
                    if (line == null)
                        continue;

                    // A pooled line is only meaningful while the game has switched it on for this
                    // frame; the rest are leftovers from an earlier frame.
                    if (!line.gameObject.activeSelf)
                        continue;

                    if (line.positionCount < 2)
                        continue;

                    Source source = new Source();
                    source.Line = line;
                    source.Dashed = drawer == Map.dashedLine;
                    into.Add(source);
                }

                return true;
            }
            catch (Exception e)
            {
                MmLog.Detail("reading a line pool failed: " + e.Message);
                return false;
            }
        }

        public static string Describe()
        {
            return DescribeOne("solid", Map.solidLine) + " " + DescribeOne("dashed", Map.dashedLine);
        }

        /// <summary>
        /// One line per pooled line that the game drew this frame: which body it belongs to, how many
        /// points it has and what colour (including the alpha) the game gave it. This is what tells
        /// apart "the game did not draw a line at all" from "it drew it and we copied it badly".
        /// </summary>
        public static string DescribeLines()
        {
            string text = "";
            try
            {
                text = Detail("solid", Map.solidLine);
                string dashed = Detail("dashed", Map.dashedLine);
                if (dashed.Length > 0)
                    text += (text.Length > 0 ? " | " : "") + dashed;
            }
            catch (Exception e)
            {
                text = "error " + e.Message;
            }

            return text.Length > 0 ? text : "none";
        }

        private static string Detail(string name, LineDrawer drawer)
        {
            if (drawer == null || drawer.pool == null)
                return "";

            List<LineRenderer> items = drawer.pool.Items;
            if (items == null)
                return "";

            string text = "";
            for (int i = 0; i < items.Count; i++)
            {
                LineRenderer line = items[i];
                if (line == null || !line.gameObject.activeSelf || line.positionCount < 2)
                    continue;

                string owner = "?";
                try
                {
                    Transform parent = line.transform.parent;
                    if (parent != null)
                        owner = parent.name;
                }
                catch
                {
                }

                if (text.Length > 0)
                    text += ", ";

                text += name + "[" + owner + " n=" + line.positionCount +
                        " a=" + line.startColor.a.ToString("0.###") + "]";

                if (text.Length > 700)
                {
                    text += ", ...";
                    break;
                }
            }

            return text;
        }

        private static string DescribeOne(string name, LineDrawer drawer)
        {
            try
            {
                if (drawer == null)
                    return name + "=none";

                if (drawer.pool == null)
                    return name + "=no pool";

                List<LineRenderer> items = drawer.pool.Items;
                if (items == null)
                    return name + "=no items";

                int active = 0;
                int points = 0;
                bool worldSpace = false;
                for (int i = 0; i < items.Count; i++)
                {
                    LineRenderer line = items[i];
                    if (line == null)
                        continue;

                    if (line.gameObject.activeSelf)
                    {
                        active++;
                        points += line.positionCount;
                        worldSpace = line.useWorldSpace;
                    }
                }

                return name + "=" + items.Count + " item(s), " + active + " active, " + points +
                       " points, useWorldSpace " + worldSpace;
            }
            catch (Exception e)
            {
                return name + "=error " + e.Message;
            }
        }
    }
}
