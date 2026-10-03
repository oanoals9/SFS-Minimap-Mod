using System;
using SFS.UI.ModGUI;
using TMPro;
using UITools;
using UnityEngine;
using UnityEngine.Events;
using Type = SFS.UI.ModGUI.Type;
using Button = SFS.UI.ModGUI.Button;

namespace SFSMiniMap
{
    /// <summary>
    /// The mod's page in UITools' "Mods Settings" window. UITools creates that window during its own
    /// Early_Load, and the loader runs every mod's Early_Load before any mod's Load, so calling
    /// Register() from Load always finds it.
    /// </summary>
    public static class MmSettingsPage
    {
        private const string ModTitle = "Mini Map";

        private static bool registered;
        private static Label hotkeyLabel;
        private static Button hotkeyButton;

        public static void Register()
        {
            if (registered)
                return;

            try
            {
                ConfigurationMenu.Add(ModTitle,
                    new ValueTuple<string, Func<Transform, GameObject>>[]
                    {
                        new ValueTuple<string, Func<Transform, GameObject>>(
                            "Mini Map", new Func<Transform, GameObject>(CreateMapPage))
                    });

                registered = true;
                MmLog.Detail("settings page added to the Mods Settings window");
            }
            catch (Exception e)
            {
                MmLog.Warn("could not add the settings page yet: " + e.Message);
            }
        }

        /// <summary>Called every frame by the persistent helper, so the page can show what it is
        /// waiting for and spring back to its normal text afterwards.</summary>
        public static void Tick()
        {
            if (hotkeyButton == null && hotkeyLabel == null)
                return;

            if (MmHotkey.Capturing)
            {
                SetText(hotkeyButton, "Press any key...  (right click to cancel)");
                SetText(hotkeyLabel, "Hotkey: waiting for a key");
            }
            else
            {
                SetText(hotkeyButton, "Set hotkey");
                SetText(hotkeyLabel, "Hotkey: " + MmConfig.HotkeyName);
            }
        }

        // ------------------------------------------------------------------ pages

        /// <summary>
        /// The one page this mod adds to UITools' Mods Settings window.
        ///
        /// All that is left on it is the hotkey (and the switch that turns the hotkey on) and the
        /// debug switch. Everything else - size, zoom, colours, what is drawn, and whether the window
        /// is shown at all - is left at its defaults in settings.txt, which keeps the page short
        /// enough to be seen in one go: the settings window is the same height whatever is on the
        /// page, and its right hand column cannot be scrolled, so anything below the fold is
        /// unreachable.
        /// </summary>
        private static GameObject CreateMapPage(Transform parent)
        {
            Box box = MakePage(parent, "Mini Map Settings");
            int width = RowWidth();

            AddSection(box, width, "Window");
            AddToggle(box, width, "Show/hide with a hotkey",
                      delegate { return MmConfig.UseHotkey; },
                      delegate { MmConfig.SetUseHotkey(!MmConfig.UseHotkey); });

            hotkeyLabel = Builder.CreateLabel(box, width, 26, 0, 0, "Hotkey: " + MmConfig.HotkeyName);
            hotkeyLabel.TextAlignment = TextAlignmentOptions.Left;

            hotkeyButton = Builder.CreateButton(box, width, 36, 0, 0,
                delegate { MmHotkey.Begin(); }, "Set hotkey");

            Builder.CreateLabel(box, width, 46, 0, 0,
                "With the hotkey off the window is simply always there.\n" +
                "It hides itself while the game's own map (M) is open.");

            AddSection(box, width, "Debug");
            AddToggle(box, width, "Debug output",
                      delegate { return MmConfig.Debug; },
                      delegate { MmConfig.SetDebug(!MmConfig.Debug); });

            return box.gameObject;
        }

        // ------------------------------------------------------------------ building blocks

        private static Box MakePage(Transform parent, string title)
        {
            Vector2Int content = ConfigurationMenu.ContentSize;
            Box box = Builder.CreateBox(parent, content.x, content.y, 0, 0, 0.3f);
            box.CreateLayoutGroup(Type.Vertical, TextAnchor.UpperCenter, 16f,
                                  new RectOffset(0, 0, 5, 5), true);

            Builder.CreateLabel(box, RowWidth(), 40, 0, 0, title);
            return box;
        }

        private static int RowWidth()
        {
            int width = ConfigurationMenu.ContentSize.x - 50;
            return width < 100 ? 100 : width;
        }

        private static void AddSection(Box box, int width, string title)
        {
            Builder.CreateSeparator(box, width, 0, 0);
            Label label = Builder.CreateLabel(box, width, 34, 0, 0, title);
            label.TextAlignment = TextAlignmentOptions.Left;
        }

        private static void AddSlider(Box box, int width, string title, float min, float max,
                                      float current, bool wholeNumbers,
                                      Action<float> apply, Func<float, string> show)
        {
            Label label = Builder.CreateLabel(box, width, 28, 0, 0, title);
            label.TextAlignment = TextAlignmentOptions.Left;

            Builder.CreateSlider(box, width, Mathf.Clamp(current, min, max),
                                 new ValueTuple<float, float>(min, max), wholeNumbers,
                                 new UnityAction<float>(delegate (float changed) { apply(changed); }),
                                 show);
        }

        private static void AddToggle(Box box, int width, string title, Func<bool> get, Action toggle)
        {
            Builder.CreateToggleWithLabel(box, width, 34, get, toggle, 0, 0, title);
        }

        private static void SetText(Label label, string text)
        {
            if (label == null)
                return;

            try
            {
                // Only when it actually changed: this runs every frame while the page is open, and
                // writing the same text back would rebuild the text mesh each time.
                if (label.Text != text)
                    label.Text = text;
            }
            catch
            {
            }
        }

        private static void SetText(Button button, string text)
        {
            if (button == null)
                return;

            try
            {
                if (button.Text != text)
                    button.Text = text;
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Waiting for the player to press the new hotkey. This is driven by <see cref="MmSettingsAux"/>,
    /// which lives in every scene, because the settings window can also be opened from the main menu,
    /// where there is no minimap at all.
    /// </summary>
    public static class MmHotkey
    {
        public static bool Capturing { get; private set; }

        public static void Begin()
        {
            Capturing = true;
            MmLog.Detail("waiting for the new hotkey");
        }

        public static void Cancel()
        {
            Capturing = false;
        }

        public static void Poll()
        {
            if (!Capturing)
                return;

            try
            {
                Array keys = Enum.GetValues(typeof(KeyCode));
                for (int i = 0; i < keys.Length; i++)
                {
                    KeyCode key = (KeyCode)keys.GetValue(i);
                    int code = (int)key;

                    // Keyboard only: the mouse and gamepad codes start at Mouse0. Escape is skipped so
                    // that closing the settings window with it does not take the hotkey.
                    if (code <= 0 || code >= (int)KeyCode.Mouse0 || key == KeyCode.Escape)
                        continue;

                    if (Input.GetKeyDown(key))
                    {
                        MmConfig.SetHotkey(key);
                        Capturing = false;
                        MmLog.Write("the minimap hotkey is now " + key);
                        return;
                    }
                }

                if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1))
                    Capturing = false;
            }
            catch (Exception e)
            {
                Capturing = false;
                MmLog.Error("waiting for a hotkey failed: " + e);
            }
        }
    }

    /// <summary>
    /// A tiny object that survives scene changes. It is the only thing this mod keeps alive outside a
    /// flight; everything else is created and destroyed with the world scene.
    /// </summary>
    public class MmSettingsAux : MonoBehaviour
    {
        private static MmSettingsAux instance;

        public static void Ensure()
        {
            if (instance != null)
                return;

            try
            {
                GameObject holder = new GameObject("SFS Mini Map (aux)");
                UnityEngine.Object.DontDestroyOnLoad(holder);
                instance = holder.AddComponent<MmSettingsAux>();
            }
            catch (Exception e)
            {
                MmLog.Warn("the settings helper could not be created: " + e.Message);
            }
        }

        private void Update()
        {
            try
            {
                MmHotkey.Poll();
                MmSettingsPage.Tick();
            }
            catch (Exception e)
            {
                MmLog.Error("the settings helper failed: " + e);
            }
        }
    }
}
