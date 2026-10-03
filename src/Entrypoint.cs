using System;
using System.Collections.Generic;
using ModLoader;
using ModLoader.Helpers;
using UnityEngine;

namespace SFSMiniMap
{
    /// <summary>
    /// Mod entry point. The game's built-in loader (ModLoader.Mod in Assembly-CSharp.dll)
    /// creates this class and calls Early_Load() then Load().
    ///
    /// This mod only does something in the world (flight) scene, so there are no patches at all:
    /// everything is read through public game API and drawn with our own camera.
    /// </summary>
    public class Entrypoint : Mod
    {
        public static Entrypoint Instance { get; private set; }

        public Entrypoint()
        {
            Instance = this;
        }

        public override string ModNameID => "sfsminimap";
        public override string DisplayName => "Mini Map";
        public override string Author => "oanoals9";
        public override string MinimumGameVersionNecessary => "1.6.0.18";
        public override string ModVersion => "0.3.0";
        public override string Description =>
            "A small map window in flight: the planet, the current trajectory, apoapsis/periapsis and " +
            "the vehicle, with the zoom following the height above the ground.";
        public override string IconLink => null;
        public override Action LoadKeybindings => null;

        public override Dictionary<string, string> Dependencies
        {
            get
            {
                return new Dictionary<string, string>
                {
                    { "UITools", "1.1.6" }
                };
            }
        }

        public override void Early_Load()
        {
            MmLog.Detail("Early_Load");
        }

        public override void Load()
        {
            MmConfig.Initialise();
            MmSettingsPage.Register();
            MmSettingsAux.Ensure();

            SceneHelper.OnWorldSceneLoaded += MmScene.OnWorldLoaded;
            SceneHelper.OnWorldSceneUnloaded += MmScene.OnWorldUnloaded;

            MmLog.Always("loaded (v" + ModVersion + ")");
        }
    }

    /// <summary>
    /// "Is this missing?" for the game's observable types. They overload the equality operators (and
    /// the game marks the old ones obsolete), so a plain == would either not compile or silently ask
    /// the wrong question. Unity's own "destroyed object" test is applied as well.
    /// </summary>
    public static class MmLib
    {
        public static bool IsNull(object value)
        {
            if (ReferenceEquals(value, null))
                return true;

            UnityEngine.Object unity = value as UnityEngine.Object;
            if (ReferenceEquals(unity, null))
                return false;

            return unity == null;
        }

        public static bool IsSet(object value)
        {
            return !IsNull(value);
        }
    }

    /// <summary>
    /// Small logging helper, so every line of this mod can be found in the F1 console with one grep.
    ///
    /// The "Debug output" switch on the settings page decides how much of this is printed. With it
    /// off the console stays quiet apart from one line saying the mod loaded - the state report and
    /// everything else are debug output, and a mod that talks in the console when it has nothing to
    /// report is a nuisance.
    /// </summary>
    public static class MmLog
    {
        private const string Prefix = "[SFSMM] ";

        /// <summary>Debug output: printed only while the "Debug output" switch is on.</summary>
        public static void Write(string message)
        {
            if (!MmConfig.Debug)
                return;

            Debug.Log(Prefix + message);
        }

        /// <summary>Printed whether or not debug output is on.</summary>
        public static void Always(string message)
        {
            Debug.Log(Prefix + message);
        }

        public static void Detail(string message)
        {
            if (!MmConfig.Debug)
                return;

            Debug.Log(Prefix + message);
        }

        public static void Warn(string message)
        {
            Debug.LogWarning(Prefix + message);
        }

        public static void Error(string message)
        {
            Debug.LogError(Prefix + message);
        }

        /// <summary>Logs and swallows: used around every step that touches game objects, so that an
        /// unexpected state can never break the flight scene.</summary>
        public static void Guard(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                Error(what + " failed: " + e);
            }
        }
    }

    /// <summary>
    /// Tracks which scene the game is in. The minimap lives in the world scene only; the build scene
    /// is ignored on purpose.
    /// </summary>
    public static class MmScene
    {
        public static bool IsWorld { get; private set; }

        public static void OnWorldLoaded()
        {
            IsWorld = true;
            MmLog.Detail("world scene loaded");
            MmRuntime.Ensure();
        }

        public static void OnWorldUnloaded()
        {
            IsWorld = false;
            MmLog.Detail("world scene unloaded");
            MmRuntime.Destroy();
        }
    }
}
