using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace Bamex.StrandedDeep.ModSettings
{
    public static class ModSettingsClient
    {
        private const string HostGuid =
            "com.bamex.strandeddeep.modsettings";

        private const string ApiTypeName =
            "StrandedDeepModSettings.ModSettingsApi";

        public static bool IsAvailable()
        {
            return ResolveApiType() != null;
        }

        public static bool RegisterMod(
            string modId,
            string displayName,
            int order)
        {
            return Invoke(
                "RegisterMod",
                new Type[]
                {
                    typeof(string),
                    typeof(string),
                    typeof(int)
                },
                new object[]
                {
                    modId,
                    displayName,
                    order
                });
        }

        public static bool AddSlider(
            string modId,
            string settingId,
            string label,
            int order,
            float min,
            float max,
            float step,
            float displayMultiplier,
            string suffix,
            int decimals,
            Func<float> getter,
            Action<float> setter)
        {
            return Invoke(
                "AddSlider",
                new Type[]
                {
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(int),
                    typeof(float),
                    typeof(float),
                    typeof(float),
                    typeof(float),
                    typeof(string),
                    typeof(int),
                    typeof(Func<float>),
                    typeof(Action<float>)
                },
                new object[]
                {
                    modId,
                    settingId,
                    label,
                    order,
                    min,
                    max,
                    step,
                    displayMultiplier,
                    suffix,
                    decimals,
                    getter,
                    setter
                });
        }

        public static bool AddToggle(
            string modId,
            string settingId,
            string label,
            int order,
            string onText,
            string offText,
            Func<bool> getter,
            Action<bool> setter)
        {
            return Invoke(
                "AddToggle",
                new Type[]
                {
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(int),
                    typeof(string),
                    typeof(string),
                    typeof(Func<bool>),
                    typeof(Action<bool>)
                },
                new object[]
                {
                    modId,
                    settingId,
                    label,
                    order,
                    onText,
                    offText,
                    getter,
                    setter
                });
        }

        public static bool AddChoice(
            string modId,
            string settingId,
            string label,
            int order,
            string[] choices,
            Func<int> getter,
            Action<int> setter)
        {
            return Invoke(
                "AddChoice",
                new Type[]
                {
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(int),
                    typeof(string[]),
                    typeof(Func<int>),
                    typeof(Action<int>)
                },
                new object[]
                {
                    modId,
                    settingId,
                    label,
                    order,
                    choices,
                    getter,
                    setter
                });
        }

        public static bool AddButton(
            string modId,
            string settingId,
            string label,
            int order,
            string actionText,
            Action action)
        {
            return Invoke(
                "AddButton",
                new Type[]
                {
                    typeof(string),
                    typeof(string),
                    typeof(string),
                    typeof(int),
                    typeof(string),
                    typeof(Action)
                },
                new object[]
                {
                    modId,
                    settingId,
                    label,
                    order,
                    actionText,
                    action
                });
        }

        public static bool RemoveMod(
            string modId)
        {
            return Invoke(
                "RemoveMod",
                new Type[]
                {
                    typeof(string)
                },
                new object[]
                {
                    modId
                });
        }

        private static bool Invoke(
            string methodName,
            Type[] parameterTypes,
            object[] arguments)
        {
            try
            {
                Type apiType =
                    ResolveApiType();

                if (apiType == null)
                    return false;

                MethodInfo method =
                    apiType.GetMethod(
                        methodName,
                        BindingFlags.Public |
                        BindingFlags.Static,
                        null,
                        parameterTypes,
                        null);

                if (method == null)
                    return false;

                method.Invoke(
                    null,
                    arguments);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Type ResolveApiType()
        {
            try
            {
                if (
                    !Chainloader.PluginInfos.ContainsKey(
                        HostGuid)
                )
                {
                    return null;
                }

                BepInEx.BaseUnityPlugin plugin =
                    Chainloader
                        .PluginInfos[HostGuid]
                        .Instance;

                if (plugin == null)
                    return null;

                Assembly assembly =
                    plugin
                        .GetType()
                        .Assembly;

                return assembly.GetType(
                    ApiTypeName,
                    false);
            }
            catch
            {
                return null;
            }
        }
    }
}
