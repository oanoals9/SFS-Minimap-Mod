using System;
using System.Reflection;
using SFS.World;
using SFS.World.Maps;
using UnityEngine;

namespace SFSMiniMap
{
    /// <summary>
    /// Drives the minimap: reads the game state once per frame, works out where the map should look
    /// and how far in, and hands both to the renderer and the window.
    ///
    /// It lives on the window's holder object, so it is created and destroyed together with the
    /// window.
    /// </summary>
    public class MmRuntime : MonoBehaviour
    {
        private const int AutoDumpAfterFrames = 180;
        private const int AnnounceAfterFrames = 300;
        private const int RepeatEveryFrames = 1800;      // about 30 seconds

        public static MmRuntime Current { get; private set; }

        private MmRenderer renderer;
        private int frames;
        private bool dumped;
        private bool announced;
        private int announcedFrame;
        private bool announcedInOrbit;
        private bool announcedRealtime;
        private int announcedWarp = -1;
        private bool shownByHotkey = true;

        private float blend;          // 0 = arc mode (vehicle in the middle), 1 = planet in the middle
        private float blendVelocity;
        private float viewAngle;
        private float viewAngleVelocity;
        private bool viewAngleReady;
        private Vector2 arcCenter;
        private Vector2 arcCenterVelocity;
        private float arcHalf;
        private float arcHalfVelocity;
        private Vector2 orbitCenter;
        private Vector2 orbitCenterVelocity;
        private bool viewReady;

        private Material appliedMaterial;
        private int gameMapFailures;
        private string lastState = "starting";

        private static MethodInfo drawMap;
        private static bool drawMapLooked;

        private void Awake()
        {
            Current = this;
            shownByHotkey = MmConfig.Visible;
            renderer = new MmRenderer();
            MmLog.Detail("runtime created, renderer " + renderer.Describe());
        }

        private void Update()
        {
            try
            {
                HandleHotkey();
            }
            catch (Exception e)
            {
                MmLog.Error("the hotkey handler failed: " + e);
            }
        }

        private void LateUpdate()
        {
            try
            {
                Step();
            }
            catch (Exception e)
            {
                MmLog.Error("the minimap step failed: " + e);
            }
        }

        private void OnDestroy()
        {
            if (Current == this)
                Current = null;

            if (renderer != null)
                renderer.Destroy();
            renderer = null;
        }

        // ------------------------------------------------------------------ hotkey

        private void HandleHotkey()
        {
            if (MmHotkey.Capturing)
                return;

            if (!MmConfig.UseHotkey)
                return;

            KeyCode key = MmConfig.Hotkey;
            if (key == KeyCode.None)
                return;

            if (Input.GetKeyDown(key))
            {
                shownByHotkey = !shownByHotkey;
                MmLog.Detail("the minimap was " + (shownByHotkey ? "shown" : "hidden"));
            }
        }

        // ------------------------------------------------------------------ the frame

        private void Step()
        {
            frames++;

            // The game's map view is switched off while flying, but its objects are kept up to date
            // every frame. They are also what carries the sprites and the line material, which is why
            // the assets are picked up late: on the very first frame the map may not exist yet.
            if (frames < 600 && frames % 30 == 1)
                MmAssets.TryReload();

            if (renderer == null || !renderer.Ready)
            {
                lastState = "camera " + (renderer == null ? "missing" : renderer.LastError);

                if (!announced && frames >= AnnounceAfterFrames)
                {
                    announced = true;
                    MmLog.Write("state: the minimap camera is not available (" +
                                (renderer == null ? "not created" : renderer.LastError) + ")");
                }
                return;
            }

            MmSnapshot snapshot = MmTarget.Capture();

            bool mapOpen = IsGameMapOpen();
            bool visible = shownByHotkey && !mapOpen;

            if (!snapshot.valid)
            {
                lastState = snapshot.failReason;
                renderer.Update(snapshot, Vector2.zero, 100f, false, this.viewAngle);
                MmWindow.SetTexture(renderer.Texture);
                MmWindow.SetVisible(visible);
                MmWindow.SetApoapsis("Apoapsis --");
                MmWindow.SetPeriapsis("Periapsis --");

                if (!announced && frames >= AnnounceAfterFrames)
                {
                    announced = true;
                    MmLog.Write("state: no usable vehicle to show (" + snapshot.failReason + ")");
                }
                return;
            }

            Vector2 center;
            float half;
            string mode;
            ComputeView(snapshot, out center, out half, out mode);

            float viewAngle = ComputeViewAngle(snapshot);

            if (appliedMaterial != MmAssets.LineMaterial)
            {
                appliedMaterial = MmAssets.LineMaterial;
                renderer.RefreshMaterial();
            }

            UpdateGameMapIfAsked(mapOpen);

            renderer.Update(snapshot, center, half, visible, viewAngle);
            MmWindow.SetTexture(renderer.Texture);
            MmWindow.SetVisible(visible);

            SetLabels(FormatApoapsis(snapshot), FormatPeriapsis(snapshot));
            MmWindow.SetApsidesVisible(snapshot.HasAp, snapshot.HasPe);

            lastState = mode + ", centre " + center.x.ToString("0.#") + "," + center.y.ToString("0.#") +
                        " km, half height " + half.ToString("0.#") + " km, " +
                        renderer.MirroredLines + " line(s)";

            MaybeAnnounce(snapshot, mode, center, half);
            MaybeAutoDump(snapshot);
        }

        /// <summary>
        /// The report, printed whether or not the Debug setting is on.
        ///
        /// It used to be printed once, five seconds after the flight scene loads - which is when the
        /// vehicle is still sitting on the launch pad, so it never described the situations that
        /// matter (in orbit, under time warp). It is now printed again whenever one of those states
        /// changes, so a single flight produces a report for each of them.
        /// </summary>
        private void MaybeAnnounce(MmSnapshot snapshot, string mode, Vector2 centerKm, float halfKm)
        {
            bool first = !announced;

            if (first && frames < AnnounceAfterFrames)
                return;

            bool changed = !first &&
                           (snapshot.inOrbit != announcedInOrbit ||
                            snapshot.warpIndex != announcedWarp ||
                            snapshot.realtimePhysics != announcedRealtime);

            // A state change is not enough on its own: the report used to describe only the launch
            // pad, because that is where the vehicle is when the first one is printed and where a
            // flight often stays for a while. Repeating it now and then guarantees that a log taken
            // after the flight has moved on describes where the flight actually is.
            bool due = frames - announcedFrame >= RepeatEveryFrames;

            if (!first && !changed && !due)
                return;

            announced = true;
            announcedFrame = frames;
            announcedInOrbit = snapshot.inOrbit;
            announcedWarp = snapshot.warpIndex;
            announcedRealtime = snapshot.realtimePhysics;

            try
            {
                MmLog.Write((first ? "state: " : (changed ? "state changed: " : "state (periodic): ")) +
                            mode + ", centre " + centerKm.x.ToString("0.#") + "," +
                            centerKm.y.ToString("0.#") + " km, half height " + halfKm.ToString("0.#") +
                            " km, " + renderer.MirroredLines + " line(s) mirrored" +
                            (renderer.OwnOrbitDrawn ? ", own orbit drawn" : "") +
                            ", in orbit " + snapshot.inOrbit);
                MmLog.Write("map system: " + MapSystemState() + ", game map open: " + IsGameMapOpen());
                MmLog.Write("orbit: " + DescribeOrbit(snapshot) + " | own line: " +
                            (renderer.OwnOrbitDrawn ? "drawn" : "NOT drawn") + " (" + renderer.OwnOrbitReason + ")");
                MmLog.Write("output: " + MmWindow.Describe());
                MmLog.Write("text: lines " + MmWindow.LabelFontSize.ToString("0.#") + "px, title " +
                            (MmWindow.TitleFontSize > 0f
                                 ? MmWindow.TitleFontSize.ToString("0.#") + "px"
                                 : "unreadable") +
                            (snapshot.HasAp ? ", apoapsis line shown" : ", apoapsis line hidden") +
                            (snapshot.HasPe ? ", periapsis line shown" : ", periapsis line hidden"));
                MmLog.Write("lines: " + MmLines.Describe());
                MmLog.Write("drawn line: " + renderer.LineWidthReport);
                MmLog.Write("pixels: " + renderer.ReadBackSummary());
            }
            catch (Exception e)
            {
                MmLog.Error("the state report failed: " + e);
            }
        }

        /// <summary>
        /// Asks the game to draw its own map even though it is switched off, so that the line pools
        /// this mod copies from are filled during flight.
        ///
        /// The game only runs MapManager.DrawMap while its map scene is active (MapManager lives
        /// inside mapSystemHolder, which the game switches off in flight), so without this call those
        /// pools stay empty and there is nothing to copy - which also means a trajectory another mod
        /// added to them can never show up here. The map stays invisible either way, because its
        /// objects are inside the same switched off holder.
        ///
        /// Skipped while the game's map is open (the game draws it by itself then), and switched off
        /// for good after repeated failures so that a game update cannot fill the log.
        /// </summary>
        private void UpdateGameMapIfAsked(bool mapOpen)
        {
            if (!MmConfig.LiveGameMap || mapOpen || gameMapFailures >= 3)
                return;

            if (!MmConfig.ShowOrbit && !MmConfig.ShowMarkers)
                return;

            try
            {
                MapManager manager = Map.manager;
                if (MmLib.IsNull(manager))
                    return;

                MethodInfo method = FindDrawMap();
                if (method == null)
                {
                    gameMapFailures = 3;
                    return;
                }

                method.Invoke(manager, null);
            }
            catch (Exception e)
            {
                gameMapFailures++;
                MmLog.Warn("the game's map could not be drawn on demand (" + gameMapFailures +
                           "/3, after that it is not tried again): " + e);
            }
        }

        /// <summary>MapManager.DrawMap is private, so it is reached by reflection - looked up once, and
        /// only when the player actually asked for it.</summary>
        private static MethodInfo FindDrawMap()
        {
            if (drawMapLooked)
                return drawMap;

            drawMapLooked = true;

            try
            {
                drawMap = typeof(MapManager).GetMethod("DrawMap",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (drawMap == null)
                    MmLog.Warn("MapManager.DrawMap was not found, so the game's map cannot be updated on " +
                               "demand and only this mod's own lines will be drawn");
            }
            catch (Exception e)
            {
                MmLog.Warn("MapManager.DrawMap could not be looked up: " + e.Message);
            }

            return drawMap;
        }

        private void SetLabels(string apoapsis, string periapsis)
        {
            MmWindow.SetApoapsis(apoapsis);
            MmWindow.SetPeriapsis(periapsis);
        }

        // ------------------------------------------------------------------ where to look

        /// <summary>
        /// Two views are built every frame and mixed with a smoothed weight, so that switching
        /// between them is an animation instead of a jump:
        ///
        ///   * below orbit: the vehicle is in the middle and the map is as wide as the distance
        ///     from the launch point to the impact point, plus a margin;
        ///   * in orbit: the planet is in the middle and the half height is
        ///     zoomK * (planet radius + height above the ground).
        ///
        /// Inside one mode nothing is lagged: the smoothed weight is the only thing that takes time.
        /// </summary>
        private void ComputeView(MmSnapshot snapshot, out Vector2 center, out float half, out string mode)
        {
            // ---- view A: the arc ------------------------------------------------------------------

            Vector2 arcTargetCenter = snapshot.shipKm;
            float chord;

            if (snapshot.hasArcEnd)
            {
                chord = Vector2.Distance(snapshot.shipKm, snapshot.arcEndKm);
                if (MmConfig.ArcCenterMode > 0.001f)
                {
                    Vector2 midpoint = (snapshot.shipKm + snapshot.arcEndKm) * 0.5f;
                    arcTargetCenter = Vector2.Lerp(snapshot.shipKm, midpoint, MmConfig.ArcCenterMode);
                }
            }
            else
            {
                // Nothing to frame (the game has not produced a trajectory yet, or it could not be
                // sampled): fall back to the height of the arc, so that the map still zooms out with
                // the vehicle instead of sitting at the smallest range.
                double apoapsisKm = snapshot.hasOrbit ? snapshot.apoapsisM / 1000.0 : 0.0;
                chord = apoapsisKm > snapshot.radiusKm
                    ? (float)(2.0 * (apoapsisKm - snapshot.radiusKm))
                    : (float)(snapshot.radiusKm * 0.05);
            }

            float arcTargetHalf = MmConfig.ArcMinHalf;
            if (chord > 0.001f)
                arcTargetHalf = 0.5f * chord * (1f + MmConfig.ArcMargin);
            arcTargetHalf = Mathf.Clamp(arcTargetHalf, MmConfig.ArcMinHalf, MmConfig.ArcMaxHalf);

            // ---- view B: the planet ---------------------------------------------------------------

            // Once in orbit the map is centred on the middle of the apoapsis and the periapsis - the
            // centre of the ellipse - so that the whole orbit sits around the middle of the window
            // instead of the planet. Only a closed orbit has both points, so anything else keeps the
            // planet's centre.
            Vector2 orbitTargetCenter = snapshot.hasOrbitCentre ? snapshot.orbitCentreKm : Vector2.zero;

            // The zoom follows the two apsis points as well: half the distance between them is the
            // semi-major axis, so "Zoom" times that always keeps both of them (and the vehicle, which
            // is somewhere on the same ellipse) inside the window. If the two points could not be
            // worked out - so the view stays on the planet instead of the centre of the ellipse - the
            // apoapsis radius is used instead: with the planet in the middle, the apoapsis is the
            // furthest point of the orbit from the centre of the window.
            float orbitTargetHalf;
            if (snapshot.hasOrbitCentre && snapshot.orbitSemiMajorKm > 0.01f)
            {
                orbitTargetHalf = MmConfig.ZoomK * snapshot.orbitSemiMajorKm;
            }
            else
            {
                double apoapsisKm = snapshot.hasOrbit ? snapshot.apoapsisM / 1000.0 : 0.0;
                bool usable = MmSnapshot.IsFinite(apoapsisKm) && apoapsisKm > snapshot.radiusKm;

                orbitTargetHalf = MmConfig.ZoomK * (usable
                    ? (float)apoapsisKm
                    : (float)(snapshot.radiusKm + Math.Max(0.0, snapshot.heightM / 1000.0)));
            }

            orbitTargetHalf = Mathf.Clamp(orbitTargetHalf, 0.01f, 1e9f);

            // ---- mixing --------------------------------------------------------------------------

            float wanted = snapshot.inOrbit ? 1f : 0f;

            if (!viewReady)
            {
                viewReady = true;
                blend = wanted;
                arcCenter = arcTargetCenter;
                arcHalf = arcTargetHalf;
                orbitCenter = orbitTargetCenter;
            }

            float smooth = Mathf.Max(0.02f, MmConfig.TransitionTime);
            blend = Mathf.SmoothDamp(blend, wanted, ref blendVelocity, smooth);

            // A short smoothing only, so that a jittery trajectory does not make the map shake.
            arcCenter = Vector2.SmoothDamp(arcCenter, arcTargetCenter, ref arcCenterVelocity, 0.12f);
            arcHalf = Mathf.SmoothDamp(arcHalf, arcTargetHalf, ref arcHalfVelocity, 0.12f);
            orbitCenter = Vector2.SmoothDamp(orbitCenter, orbitTargetCenter, ref orbitCenterVelocity, 0.12f);

            center = Vector2.Lerp(arcCenter, orbitCenter, blend);
            half = Mathf.Lerp(arcHalf, orbitTargetHalf, blend);

            if (blend < 0.02f)
                mode = "arc";
            else if (blend > 0.98f)
                mode = "planet";
            else
                mode = "arc->planet " + blend.ToString("0.00");
        }

        /// <summary>One line that says whether the minimap had an orbit to draw at all, and what kind
        /// of orbit it was - which is what decides whether an orbit line should be visible.</summary>
        private string DescribeOrbit(MmSnapshot snapshot)
        {
            if (snapshot == null)
                return "no reading";

            string text = snapshot.hasOrbit
                ? "found (" + snapshot.firstPathType + "), ecc " + snapshot.ecc.ToString("0.####") +
                  ", apo " + (snapshot.apoapsisM / 1000.0).ToString("0.#") + " km" +
                  ", peri " + (snapshot.periapsisM / 1000.0).ToString("0.#") + " km"
                : "none";

            text += ", " + snapshot.pathCount + " path(s)";
            if (snapshot.pathCount > 0)
                text += " [" + snapshot.pathTypes + "]";

            text += ", " + snapshot.arc.Count + " arc point(s)";
            text += ", height " + (snapshot.heightM / 1000.0).ToString("0.#") + " km";
            text += ", atmosphere " + snapshot.atmoKm.ToString("0.#") + " km" +
                    (snapshot.heightM / 1000.0 < snapshot.atmoKm ? " (inside it)" : "");
            text += ", timewarp " + snapshot.warpIndex + " (" + snapshot.warpSpeed.ToString("0.#") + "x)";
            text += snapshot.realtimePhysics ? ", real-time physics" : ", on rails";
            text += ", map turned by " + viewAngle.ToString("0.#") + " deg";
            text += ", in orbit by geometry " + snapshot.inOrbitGeometric;

            if (snapshot.hasOrbit && snapshot.apoapsisM - snapshot.periapsisM < 0.5)
                text += " (degenerate: the whole orbit is one point)";

            return text;
        }

        /// <summary>
        /// The angle the map is turned by.
        ///
        /// While the vehicle is not in orbit - climbing, coming down, landing - the map is turned so
        /// that the vehicle sits above the planet's centre and the ground therefore comes out at the
        /// bottom. The angle is the vehicle's own direction from the planet's centre minus 90, which
        /// is the same expression the game uses for its own cameras (GameCamerasManager.
        /// GetTargetCameraAngle), and it is smoothed the same way the game smooths it.
        ///
        /// In orbit the map stays upright, and the angle is blended in and out rather than switched.
        /// </summary>
        private float ComputeViewAngle(MmSnapshot snapshot)
        {
            float wanted = 0f;

            if (MmConfig.AutoRight && !snapshot.inOrbit)
                wanted = (float)snapshot.shipAngleDegrees - 90f;

            if (!viewAngleReady)
            {
                viewAngleReady = true;
                viewAngle = wanted;
            }
            else
            {
                viewAngle = Mathf.SmoothDampAngle(viewAngle, wanted, ref viewAngleVelocity, 0.5f);
            }

            return viewAngle;
        }

        public static bool IsGameMapOpen()
        {
            try
            {
                MapManager manager = Map.manager;
                if (MmLib.IsNull(manager) || MmLib.IsNull(manager.mapMode))
                    return false;
                return manager.mapMode.Value;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Whether the game's map scene is switched on. It decides whether MapManager.LateUpdate runs
        /// at all, and therefore whether the line pools the trajectory is copied out of are filled -
        /// which is the thing that decides whether another mod's aerodynamic trajectory can appear
        /// here.
        /// </summary>
        public static string MapSystemState()
        {
            try
            {
                MapManager manager = Map.manager;
                if (MmLib.IsNull(manager))
                    return "no MapManager";

                GameObject holder = manager.mapSystemHolder;
                if (MmLib.IsNull(holder))
                    return "no mapSystemHolder";

                return "mapSystemHolder " + (holder.activeSelf ? "on" : "off") +
                       ", map manager " + (manager.gameObject.activeInHierarchy ? "on" : "off");
            }
            catch (Exception e)
            {
                return "unreadable: " + e.Message;
            }
        }

        // ------------------------------------------------------------------ text

        private static string FormatApoapsis(MmSnapshot snapshot)
        {
            if (!snapshot.HasAp)
                return "Apoapsis --";
            return "Apoapsis " + Distance(snapshot.ApoapsisAltitudeKm);
        }

        private static string FormatPeriapsis(MmSnapshot snapshot)
        {
            if (!snapshot.HasPe)
                return "Periapsis --";
            return "Periapsis " + Distance(snapshot.PeriapsisAltitudeKm);
        }

        private static string Distance(double kilometres)
        {
            return SFSMiniMap.MmUnits.Distance(kilometres);
        }

        // ------------------------------------------------------------------ diagnostics

        private void MaybeAutoDump(MmSnapshot snapshot)
        {
            if (!MmConfig.Debug || dumped || frames < AutoDumpAfterFrames)
                return;

            dumped = true;
            MmLog.Detail("automatic minimap dump - see below");
            Dump();
        }

        public static void Dump()
        {
            try
            {
                MmLog.Write("---- minimap state ----");
                MmRuntime runtime = Current;
                MmSnapshot snapshot = MmTarget.Capture();

                MmLog.Write("scene: " + (MmScene.IsWorld ? "world" : "other") +
                            ", game map open: " + IsGameMapOpen() +
                            ", " + MapSystemState());
                MmLog.Write("assets: " + MmAssets.Describe());
                MmLog.Write("line pools: " + MmLines.Describe());
                MmLog.Write("lines in the pool: " + MmLines.DescribeLines());
                MmLog.Write("runtime: " + (runtime != null ? runtime.lastState : "no runtime"));
                MmLog.Write("window: " + MmWindow.Describe());
                MmLog.Write("renderer: " + (runtime != null && runtime.renderer != null
                    ? runtime.renderer.Describe()
                    : "none"));
                MmLog.Write("cameras of the game: " + (runtime != null && runtime.renderer != null
                    ? runtime.renderer.DescribeOtherCameras()
                    : "none"));
                MmLog.Write("layers 8-31 (+ = a camera of the game renders it): " + MmRenderer.DescribeLayers());
                MmLog.Write("named layers: " + MmRenderer.DescribeNamedLayers());
                MmLog.Write("render texture: " + (runtime != null && runtime.renderer != null
                    ? runtime.renderer.ReadBackSummary()
                    : "none"));

                if (!snapshot.valid)
                {
                    MmLog.Write("vehicle: not usable (" + snapshot.failReason + ")");
                    return;
                }

                MmLog.Write("vehicle: " + snapshot.rocketName + " at " + snapshot.planetName +
                            ", height " + snapshot.heightM.ToString("0") + " m");
                MmLog.Write("planet: radius " + snapshot.radiusKm.ToString("0.#") + " km, soi " +
                            (MmSnapshot.IsFinite(snapshot.soiKm) ? snapshot.soiKm.ToString("0.#") + " km" : "infinite") +
                            ", atmosphere " + snapshot.atmoKm.ToString("0.#") + " km, terrain " + snapshot.hasTerrain);
                MmLog.Write("position: " + snapshot.shipKm.x.ToString("0.##") + ", " +
                            snapshot.shipKm.y.ToString("0.##") + " km, speed " +
                            snapshot.velocity.magnitude.ToString("0.#") + " m/s");
                MmLog.Write("in orbit: " + snapshot.inOrbit +
                            ", paths " + (snapshot.trajectory != null && snapshot.trajectory.paths != null
                                ? snapshot.trajectory.paths.Count.ToString() : "?") +
                            ", arc points " + snapshot.arc.Count + ", arc end " +
                            (snapshot.hasArcEnd
                                ? snapshot.arcEndKm.x.ToString("0.##") + ", " + snapshot.arcEndKm.y.ToString("0.##") + " km"
                                : "none"));

                if (snapshot.hasOrbit)
                {
                    MmLog.Write("orbit: apoapsis " + (snapshot.apoapsisM / 1000.0).ToString("0.##") +
                                " km, periapsis " + (snapshot.periapsisM / 1000.0).ToString("0.##") +
                                " km (radius), ecc " + snapshot.ecc.ToString("0.####") +
                                ", type " + snapshot.pathType);
                }
            }
            catch (Exception e)
            {
                MmLog.Error("the dump failed: " + e);
            }
        }

        // ------------------------------------------------------------------ lifetime helpers

        public static void Ensure()
        {
            if (MmLib.IsNull(Current) || !MmWindow.Created)
                MmWindow.Create();
        }

        public static void Destroy()
        {
            MmWindow.Destroy();
            MmAssets.Reset();
        }
    }

    /// <summary>
    /// Distance formatting. Uses the game's own formatter when it is reachable, so the numbers look
    /// exactly like the ones in the game's map view ("210.5km").
    /// </summary>
    public static class MmUnits
    {
        private static bool reported;

        /// <summary>Metres in, game style text out.</summary>
        public static string Distance(double kilometres)
        {
            double metres = kilometres * 1000.0;

            try
            {
                string text = Units.ToDistanceString(metres, true);
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
            catch (Exception e)
            {
                if (!reported)
                {
                    reported = true;
                    MmLog.Detail("the game's distance formatter is not available: " + e.Message);
                }
            }

            return Fallback(kilometres);
        }

        private static string Fallback(double kilometres)
        {
            double value = Math.Abs(kilometres);
            if (value >= 1e6)
                return (kilometres / 1e6).ToString("0.##") + "Gm";
            if (value >= 1e3)
                return (kilometres / 1e3).ToString("0.##") + "Mm";
            if (value >= 1.0)
                return kilometres.ToString("0.#") + "km";
            return (kilometres * 1000.0).ToString("0") + "m";
        }
    }
}
