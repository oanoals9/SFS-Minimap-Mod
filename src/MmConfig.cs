using System;
using SFS.IO;
using SFS.Variables;
using UITools;
using UnityEngine;

namespace SFSMiniMap
{
    /// <summary>
    /// The values the player can change in UITools' "Mods Settings" window. Every field is one of the
    /// game's serialisable observable types, so UITools' ModSettings base can load and save the whole
    /// class as JSON in the mod folder.
    /// </summary>
    [Serializable]
    public class MmConfigData
    {
        // ---- window -------------------------------------------------------------------------
        public Float_Local windowWidth = new Float_Local(MmConfig.DefaultWidth);
        public Float_Local windowHeight = new Float_Local(MmConfig.DefaultHeight);
        public Float_Local windowOpacity = new Float_Local(MmConfig.DefaultOpacity);

        /// <summary>The window is shown by default; the hotkey is an optional extra.</summary>
        public Bool_Local visible = new Bool_Local { Value = true };
        public Bool_Local useHotkey = new Bool_Local { Value = false };

        /// <summary>Name of a UnityEngine.KeyCode, for example "N". Only used when useHotkey is on.</summary>
        public String_Local hotkey = new String_Local { Value = "N" };

        // ---- map ----------------------------------------------------------------------------
        /// <summary>Resolution of the render texture the map is drawn into.</summary>
        public Float_Local mapSize = new Float_Local(MmConfig.DefaultMapSize);

        /// <summary>Zoom of the planet-relative view: half height = k * (radius + height above ground).</summary>
        public Float_Local zoomK = new Float_Local(MmConfig.DefaultZoomK);

        /// <summary>0 = keep the vehicle in the middle (arc mode), 1 = middle of the arc's chord.</summary>
        public Float_Local arcCenterMode = new Float_Local(0f);

        /// <summary>Extra margin on the arc's chord, 0.2 = 20% more than the launch-to-impact distance.</summary>
        public Float_Local arcMargin = new Float_Local(0.2f);

        public Float_Local arcMinHalf = new Float_Local(20f);
        public Float_Local arcMaxHalf = new Float_Local(40000f);

        /// <summary>Seconds the centre and the zoom take to move to a new mode.</summary>
        public Float_Local transitionTime = new Float_Local(0.6f);

        /// <summary>Thickness of the mirrored orbit lines, in pixels of the map.</summary>
        public Float_Local lineWidthPx = new Float_Local(2f);

        /// <summary>
        /// The game fades an orbit line out as the map view zooms away from it, and the alpha it
        /// writes into the line pool can end up very small (or exactly zero, in which case the line
        /// is not put into the pool at all). Anything below this value is raised to it, so that the
        /// minimap always shows the trajectory. 0 keeps the game's own fading.
        /// </summary>
        public Float_Local lineMinAlpha = new Float_Local(0.85f);

        /// <summary>How solid the map's own background is. 1 = opaque, which is also what keeps the
        /// transparent atmosphere and sphere of influence looking right.</summary>
        public Float_Local backgroundOpacity = new Float_Local(1f);

        // ---- what is drawn ------------------------------------------------------------------
        public Bool_Local showPlanet = new Bool_Local { Value = true };

        /// <summary>
        /// Off by default: the game draws planets without terrain as a flat disc of
        /// PlanetData.basics.mapColor, and that is what the minimap shows. With this on, the disc is
        /// shaded like a ball instead, which is what the reference picture shows. It is off by
        /// default so that a first test of the map itself is not mixed up with a test of this.
        /// </summary>
        public Bool_Local planetShading = new Bool_Local { Value = false };
        public Bool_Local showAtmosphere = new Bool_Local { Value = true };
        public Bool_Local showSoi = new Bool_Local { Value = true };
        public Bool_Local showOrbit = new Bool_Local { Value = true };
        public Bool_Local showMarkers = new Bool_Local { Value = true };
        public Bool_Local showShip = new Bool_Local { Value = true };

        /// <summary>The triangles of every other vehicle at the same planet.</summary>
        public Bool_Local showOtherVehicles = new Bool_Local { Value = true };

        /// <summary>The discs, atmospheres and spheres of influence of the other bodies.</summary>
        public Bool_Local showOtherBodies = new Bool_Local { Value = true };

        /// <summary>
        /// Draw the current orbit as well, on top of whatever was copied out of the game's line
        /// pools. The two overlap exactly (both come from Orbit.GetPoints over the same time range),
        /// so this is invisible when the game did draw the line - and it is what still shows the
        /// orbit when the game faded it to nothing, or when the game's map has not drawn it yet.
        /// </summary>
        public Bool_Local drawOwnOrbit = new Bool_Local { Value = true };

        /// <summary>
        /// The game switches its whole map scene off while flying (MapManager.mapSystemHolder is
        /// inactive, so MapManager.LateUpdate never runs DrawMap), which means it never puts anything
        /// into the line pools the trajectory is copied out of. With this on, the mod asks the game
        /// to draw its map once per frame anyway, so that those pools are filled - which is the only
        /// way a line another mod added to the map's own line pools (Aero Trajectory's aerodynamic
        /// trajectory) can be copied into the minimap.
        ///
        /// Off by default: it makes the game do the work of its map view during flight, including the
        /// aerodynamic simulation another mod may hook into, so it costs performance exactly when such
        /// a mod has something to compute (which is also exactly when its line is worth seeing).
        /// </summary>
        public Bool_Local liveGameMap = new Bool_Local { Value = true };

        /// <summary>
        /// On: while the vehicle is not in orbit - ascending, descending, landing - the map is turned
        /// so that "down" on the map is towards the ground, the way the game turns its own cameras.
        /// Off: the map always keeps the same orientation (0 degrees).
        /// </summary>
        public Bool_Local autoRight = new Bool_Local { Value = true };

        // ---- diagnostics --------------------------------------------------------------------
        public Bool_Local debug = new Bool_Local { Value = false };
    }

    /// <summary>
    /// Settings storage. Derives from UITools' ModSettings, which loads the file on startup, saves it
    /// again, and saves once more whenever one of the observable values changes.
    /// </summary>
    public class MmConfig : ModSettings<MmConfigData>
    {
        public const float DefaultWidth = 400f;
        public const float DefaultHeight = 520f;
        public const float DefaultOpacity = 0.9f;
        public const float DefaultZoomK = 1.3f;

        public const float MinWidth = 160f;
        public const float MaxWidth = 900f;
        public const float MinHeight = 160f;
        public const float MaxHeight = 900f;
        public const float MinOpacity = 0.2f;
        public const float MaxOpacity = 1f;

        public const float MinMapSize = 128f;
        public const float MaxMapSize = 512f;

        /// <summary>The default map resolution is the size the window is laid out around.</summary>
        public const float DefaultMapSize = 320f;

        public const float MinZoomK = 0.4f;
        public const float MaxZoomK = 4f;

        private static bool ready;

        public static bool Ready
        {
            get { return ready; }
        }

        /// <summary>Raised after any value changed, so an open window can follow along.</summary>
        public static event Action Changed;

        protected override FilePath SettingsFile
        {
            get { return new FolderPath(Entrypoint.Instance.ModFolder).ExtendToFile("settings.txt"); }
        }

        protected override void RegisterOnVariableChange(Action action)
        {
            settings.windowWidth.OnChange += action;
            settings.windowHeight.OnChange += action;
            settings.windowOpacity.OnChange += action;
            settings.visible.OnChange += action;
            settings.useHotkey.OnChange += action;
            settings.hotkey.OnChange += action;
            settings.mapSize.OnChange += action;
            settings.zoomK.OnChange += action;
            settings.arcCenterMode.OnChange += action;
            settings.arcMargin.OnChange += action;
            settings.arcMinHalf.OnChange += action;
            settings.arcMaxHalf.OnChange += action;
            settings.transitionTime.OnChange += action;
            settings.lineWidthPx.OnChange += action;
            settings.lineMinAlpha.OnChange += action;
            settings.backgroundOpacity.OnChange += action;
            settings.showPlanet.OnChange += action;
            settings.planetShading.OnChange += action;
            settings.showAtmosphere.OnChange += action;
            settings.showSoi.OnChange += action;
            settings.showOrbit.OnChange += action;
            settings.showMarkers.OnChange += action;
            settings.showShip.OnChange += action;
            settings.showOtherVehicles.OnChange += action;
            settings.showOtherBodies.OnChange += action;
            settings.drawOwnOrbit.OnChange += action;
            settings.liveGameMap.OnChange += action;
            settings.autoRight.OnChange += action;
            settings.debug.OnChange += action;
            Application.quitting += action;
        }

        /// <summary>Call once, from the mod's Load. Never throws: if anything goes wrong the mod simply
        /// keeps working with the built in defaults.</summary>
        public static void Initialise()
        {
            if (ready)
                return;

            try
            {
                MmConfig config = new MmConfig();
                config.Initialize();
                ready = settings != null;
                config.RegisterOnVariableChange(NotifyChanged);
                MmLog.Detail("settings loaded: " + Describe());
            }
            catch (Exception e)
            {
                ready = false;
                MmLog.Warn("settings could not be loaded, the defaults are used: " + e);
            }
        }

        private static void NotifyChanged()
        {
            try
            {
                Action handler = Changed;
                if (handler != null)
                    handler();
            }
            catch (Exception e)
            {
                MmLog.Error("a settings change handler failed: " + e);
            }
        }

        // ------------------------------------------------------------------ helpers

        private static float Number(Float_Local value, float fallback, float min, float max)
        {
            if (settings == null || MmLib.IsNull(value))
                return fallback;
            return Mathf.Clamp(value.Value, min, max);
        }

        private static bool Flag(Bool_Local value, bool fallback)
        {
            if (settings == null || MmLib.IsNull(value))
                return fallback;
            return value.Value;
        }

        private static void SetNumber(Float_Local value, float v, float min, float max)
        {
            if (settings == null || MmLib.IsNull(value))
                return;
            value.Value = Mathf.Clamp(v, min, max);
        }

        private static void SetFlag(Bool_Local value, bool v)
        {
            if (settings == null || MmLib.IsNull(value))
                return;
            value.Value = v;
        }

        // ------------------------------------------------------------------ window

        public static float Width { get { return Number(settings == null ? null : settings.windowWidth, DefaultWidth, MinWidth, MaxWidth); } }
        public static float Height { get { return Number(settings == null ? null : settings.windowHeight, DefaultHeight, MinHeight, MaxHeight); } }
        public static float Opacity { get { return Number(settings == null ? null : settings.windowOpacity, DefaultOpacity, MinOpacity, MaxOpacity); } }

        public static void SetWidth(float v) { SetNumber(settings == null ? null : settings.windowWidth, v, MinWidth, MaxWidth); }
        public static void SetHeight(float v) { SetNumber(settings == null ? null : settings.windowHeight, v, MinHeight, MaxHeight); }
        public static void SetOpacity(float v) { SetNumber(settings == null ? null : settings.windowOpacity, v, MinOpacity, MaxOpacity); }

        public static bool Visible { get { return Flag(settings == null ? null : settings.visible, true); } }
        public static void SetVisible(bool v) { SetFlag(settings == null ? null : settings.visible, v); }

        public static bool UseHotkey { get { return Flag(settings == null ? null : settings.useHotkey, false); } }
        public static void SetUseHotkey(bool v) { SetFlag(settings == null ? null : settings.useHotkey, v); }

        public static KeyCode Hotkey
        {
            get
            {
                string name = settings == null || MmLib.IsNull(settings.hotkey) ? "N" : settings.hotkey.Value;
                if (string.IsNullOrEmpty(name))
                    return KeyCode.None;
                try
                {
                    return (KeyCode)Enum.Parse(typeof(KeyCode), name, true);
                }
                catch
                {
                    return KeyCode.None;
                }
            }
        }

        public static string HotkeyName
        {
            get
            {
                if (settings == null || MmLib.IsNull(settings.hotkey))
                    return "N";
                return settings.hotkey.Value;
            }
        }

        public static void SetHotkey(KeyCode key)
        {
            if (settings == null || MmLib.IsNull(settings.hotkey))
                return;
            settings.hotkey.Value = key.ToString();
        }

        // ------------------------------------------------------------------ map

        public static int MapSize { get { return Mathf.RoundToInt(Number(settings == null ? null : settings.mapSize, 256f, MinMapSize, MaxMapSize)); } }
        public static float ZoomK { get { return Number(settings == null ? null : settings.zoomK, DefaultZoomK, MinZoomK, MaxZoomK); } }
        public static float ArcCenterMode { get { return Number(settings == null ? null : settings.arcCenterMode, 0f, 0f, 1f); } }
        public static float ArcMargin { get { return Number(settings == null ? null : settings.arcMargin, 0.2f, 0f, 3f); } }
        public static float ArcMinHalf { get { return Number(settings == null ? null : settings.arcMinHalf, 20f, 1f, 100000f); } }
        public static float ArcMaxHalf { get { return Number(settings == null ? null : settings.arcMaxHalf, 40000f, 10f, 10000000f); } }
        public static float TransitionTime { get { return Number(settings == null ? null : settings.transitionTime, 0.6f, 0f, 5f); } }
        public static float LineWidthPx { get { return Number(settings == null ? null : settings.lineWidthPx, 2f, 0.5f, 8f); } }
        public static float LineMinAlpha { get { return Number(settings == null ? null : settings.lineMinAlpha, 0.85f, 0f, 1f); } }
        public static float BackgroundOpacity { get { return Number(settings == null ? null : settings.backgroundOpacity, 1f, 0f, 1f); } }

        public static void SetMapSize(float v) { SetNumber(settings == null ? null : settings.mapSize, v, MinMapSize, MaxMapSize); }
        public static void SetZoomK(float v) { SetNumber(settings == null ? null : settings.zoomK, v, MinZoomK, MaxZoomK); }
        public static void SetArcCenterMode(float v) { SetNumber(settings == null ? null : settings.arcCenterMode, v, 0f, 1f); }
        public static void SetArcMargin(float v) { SetNumber(settings == null ? null : settings.arcMargin, v, 0f, 3f); }
        public static void SetArcMinHalf(float v) { SetNumber(settings == null ? null : settings.arcMinHalf, v, 1f, 100000f); }
        public static void SetArcMaxHalf(float v) { SetNumber(settings == null ? null : settings.arcMaxHalf, v, 10f, 10000000f); }
        public static void SetTransitionTime(float v) { SetNumber(settings == null ? null : settings.transitionTime, v, 0f, 5f); }
        public static void SetLineWidthPx(float v) { SetNumber(settings == null ? null : settings.lineWidthPx, v, 0.5f, 8f); }
        public static void SetLineMinAlpha(float v) { SetNumber(settings == null ? null : settings.lineMinAlpha, v, 0f, 1f); }
        public static void SetBackgroundOpacity(float v) { SetNumber(settings == null ? null : settings.backgroundOpacity, v, 0f, 1f); }

        // ------------------------------------------------------------------ layers drawn

        public static bool ShowPlanet { get { return Flag(settings == null ? null : settings.showPlanet, true); } }
        public static bool PlanetShading { get { return Flag(settings == null ? null : settings.planetShading, false); } }
        public static bool ShowAtmosphere { get { return Flag(settings == null ? null : settings.showAtmosphere, true); } }
        public static bool ShowSoi { get { return Flag(settings == null ? null : settings.showSoi, true); } }
        public static bool ShowOrbit { get { return Flag(settings == null ? null : settings.showOrbit, true); } }
        public static bool ShowMarkers { get { return Flag(settings == null ? null : settings.showMarkers, true); } }
        public static bool ShowShip { get { return Flag(settings == null ? null : settings.showShip, true); } }
        public static bool ShowOtherVehicles { get { return Flag(settings == null ? null : settings.showOtherVehicles, true); } }
        public static bool ShowOtherBodies { get { return Flag(settings == null ? null : settings.showOtherBodies, true); } }
        public static bool DrawOwnOrbit { get { return Flag(settings == null ? null : settings.drawOwnOrbit, true); } }
        public static bool LiveGameMap { get { return Flag(settings == null ? null : settings.liveGameMap, false); } }
        public static bool AutoRight { get { return Flag(settings == null ? null : settings.autoRight, true); } }

        public static void SetShowPlanet(bool v) { SetFlag(settings == null ? null : settings.showPlanet, v); }
        public static void SetPlanetShading(bool v) { SetFlag(settings == null ? null : settings.planetShading, v); }
        public static void SetShowAtmosphere(bool v) { SetFlag(settings == null ? null : settings.showAtmosphere, v); }
        public static void SetShowSoi(bool v) { SetFlag(settings == null ? null : settings.showSoi, v); }
        public static void SetShowOrbit(bool v) { SetFlag(settings == null ? null : settings.showOrbit, v); }
        public static void SetShowMarkers(bool v) { SetFlag(settings == null ? null : settings.showMarkers, v); }
        public static void SetShowShip(bool v) { SetFlag(settings == null ? null : settings.showShip, v); }
        public static void SetShowOtherVehicles(bool v) { SetFlag(settings == null ? null : settings.showOtherVehicles, v); }
        public static void SetShowOtherBodies(bool v) { SetFlag(settings == null ? null : settings.showOtherBodies, v); }
        public static void SetDrawOwnOrbit(bool v) { SetFlag(settings == null ? null : settings.drawOwnOrbit, v); }
        public static void SetLiveGameMap(bool v) { SetFlag(settings == null ? null : settings.liveGameMap, v); }
        public static void SetAutoRight(bool v) { SetFlag(settings == null ? null : settings.autoRight, v); }

        // ------------------------------------------------------------------ diagnostics

        public static bool Debug { get { return Flag(settings == null ? null : settings.debug, false); } }

        public static void SetDebug(bool v)
        {
            SetFlag(settings == null ? null : settings.debug, v);

            if (v)
                MmLog.Always("debug output enabled");
        }

        private static string Describe()
        {
            return "window " + Width.ToString("0") + "x" + Height.ToString("0") +
                   ", visible " + (Visible ? "on" : "off") +
                   ", hotkey " + (UseHotkey ? HotkeyName : "off") +
                   ", map " + MapSize + "px" +
                   ", zoomK " + ZoomK.ToString("0.00") +
                   ", transition " + TransitionTime.ToString("0.00") + "s" +
                   ", debug " + (Debug ? "on" : "off");
        }
    }
}
