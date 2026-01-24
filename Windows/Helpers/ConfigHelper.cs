using BrokenNes.Windows;

namespace BrokenNes.Windows.Helpers
{
    public static class ConfigHelper
    {
        public static void Update(EmulatorConfig config, System.Action<EmulatorConfig> update, bool save = true)
        {
            update(config);
            if (save)
            {
                config.Save();
            }
        }

        public static bool Toggle(EmulatorConfig config, System.Func<EmulatorConfig, bool> getter, System.Action<EmulatorConfig, bool> setter)
        {
            bool newValue = !getter(config);
            setter(config, newValue);
            config.Save();
            return newValue;
        }

        public static void Save(EmulatorConfig config)
        {
            config.Save();
        }

        public static bool HideMenuBarInFullscreen(EmulatorConfig config)
        {
            return config.HideMenuBarInFullscreen;
        }

        public static bool ShouldHideMenuBarNow(EmulatorConfig config, bool isFullscreen)
        {
            return isFullscreen && config.HideMenuBarInFullscreen;
        }
    }
}
