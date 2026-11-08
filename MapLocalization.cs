using System;
using System.Reflection;
using UnityEngine;

namespace StrandedDeepSplitMap
{
    public sealed partial class SplitMapPlugin
    {
        private bool _mapLocLanguageInitialized;
        private bool _mapLocUseRussian;
        private float _mapLocNextLanguageScanTime;
        private Type _mapLocTmpTextType;
        private PropertyInfo _mapLocTmpTextProperty;

        private string MapL(string russian, string english)
        {
            MapLocRefreshLanguage();

            string primary = _mapLocUseRussian ? russian : english;
            string secondary = _mapLocUseRussian ? english : russian;

            if (!String.IsNullOrEmpty(primary))
                return primary;

            if (!String.IsNullOrEmpty(secondary))
                return secondary;

            return String.Empty;
        }

        private void MapLocRefreshLanguage()
        {
            float now = Time.unscaledTime;

            if (_mapLocLanguageInitialized &&
                now < _mapLocNextLanguageScanTime)
            {
                return;
            }

            _mapLocNextLanguageScanTime = now + 0.75f;

            bool russian;
            if (MapLocTryDetectGameLanguage(out russian) ||
                MapLocTryDetectNativeOptionsLanguage(out russian))
            {
                _mapLocLanguageInitialized = true;
                _mapLocUseRussian = russian;
                return;
            }

            if (!_mapLocLanguageInitialized)
            {
                _mapLocUseRussian =
                    Application.systemLanguage ==
                    SystemLanguage.Russian;

                _mapLocLanguageInitialized = true;
            }
        }

        private bool MapLocTryDetectGameLanguage(
            out bool russian)
        {
            russian = false;

            try
            {
                Type localizationType =
                    MapLocFindLoadedType(
                        "Beam.Language.Localization");

                if (localizationType == null)
                    return false;

                MethodInfo getHandler =
                    localizationType.GetMethod(
                        "GetLanguageHandler",
                        BindingFlags.Static |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (getHandler == null)
                    return false;

                object handler =
                    getHandler.Invoke(
                        null,
                        null);

                if (handler == null)
                    return false;

                MethodInfo getCurrentLanguage =
                    handler.GetType().GetMethod(
                        "GetCurrentLanguage",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (getCurrentLanguage == null)
                    return false;

                object raw =
                    getCurrentLanguage.Invoke(
                        handler,
                        null);

                if (!(raw is SystemLanguage))
                    return false;

                SystemLanguage language =
                    (SystemLanguage)raw;

                if (language == SystemLanguage.Unknown)
                    return false;

                russian =
                    language == SystemLanguage.Russian;

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Map UI game-language detection failed: " +
                    ex.Message);
            }

            return false;
        }
        private bool MapLocTryDetectNativeOptionsLanguage(
            out bool russian)
        {
            russian = false;

            try
            {
                if (_mapLocTmpTextType == null)
                {
                    _mapLocTmpTextType =
                        MapLocFindLoadedType("TMPro.TMP_Text");

                    if (_mapLocTmpTextType == null)
                        return false;

                    _mapLocTmpTextProperty =
                        _mapLocTmpTextType.GetProperty(
                            "text",
                            BindingFlags.Instance |
                            BindingFlags.Public);
                }

                if (_mapLocTmpTextProperty == null)
                    return false;

                UnityEngine.Object[] objects =
                    Resources.FindObjectsOfTypeAll(
                        _mapLocTmpTextType);

                int activeRussian = 0;
                int activeEnglish = 0;
                int anyRussian = 0;
                int anyEnglish = 0;

                foreach (UnityEngine.Object obj in objects)
                {
                    if (obj == null)
                        continue;

                    object raw =
                        _mapLocTmpTextProperty.GetValue(
                            obj,
                            null);

                    string text = raw as string;
                    if (String.IsNullOrEmpty(text))
                        continue;

                    string normalized =
                        text.Trim().ToUpperInvariant();

                    bool isRussian =
                        MapLocIsRussianOptionsLabel(normalized);

                    bool isEnglish =
                        MapLocIsEnglishOptionsLabel(normalized);

                    if (!isRussian && !isEnglish)
                        continue;

                    Component component = obj as Component;
                    bool active =
                        component != null &&
                        component.gameObject != null &&
                        component.gameObject.activeInHierarchy;

                    if (isRussian)
                    {
                        anyRussian++;
                        if (active)
                            activeRussian++;
                    }

                    if (isEnglish)
                    {
                        anyEnglish++;
                        if (active)
                            activeEnglish++;
                    }
                }

                if (activeRussian != activeEnglish)
                {
                    russian = activeRussian > activeEnglish;
                    return true;
                }

                if (anyRussian != anyEnglish)
                {
                    russian = anyRussian > anyEnglish;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Map UI language detection failed: " +
                    ex.Message);
            }

            return false;
        }

        private static Type MapLocFindLoadedType(
            string fullName)
        {
            Assembly[] assemblies =
                AppDomain.CurrentDomain.GetAssemblies();

            foreach (Assembly assembly in assemblies)
            {
                if (assembly == null)
                    continue;

                Type type =
                    assembly.GetType(
                        fullName,
                        false);

                if (type != null)
                    return type;
            }

            return null;
        }

        private static bool MapLocIsRussianOptionsLabel(
            string value)
        {
            return
                value == "ОБЩИЕ" ||
                value == "ГРАФИКА" ||
                value == "ЗВУК" ||
                value == "ВВОД" ||
                value == "ОБ ИГРЕ";
        }

        private static bool MapLocIsEnglishOptionsLabel(
            string value)
        {
            return
                value == "GENERAL" ||
                value == "GRAPHICS" ||
                value == "AUDIO" ||
                value == "SOUND" ||
                value == "INPUT" ||
                value == "CONTROLS" ||
                value == "ABOUT";
        }

        private string MapLocManualMenuTitle(
            string russianTitle)
        {
            switch (russianTitle ?? String.Empty)
            {
                case "Добавить отметку": return MapL("Добавить отметку", "Add Marker");
                case "Материалы": return MapL("Материалы", "Materials");
                case "Глина": return MapL("Глина", "Clay");
                case "Камни": return MapL("Камни", "Stones");
                case "Брёвна": return MapL("Брёвна", "Logs");
                case "Палки": return MapL("Палки", "Sticks");
                case "Доски": return MapL("Доски", "Planks");
                case "Металлолом": return MapL("Металлолом", "Scrap");
                case "Волокнистые листья": return MapL("Волокнистые листья", "Fibrous Leaves");
                case "Ткань / кожа": return MapL("Ткань / кожа", "Cloth / Leather");
                case "Прочее": return MapL("Прочее", "Other");

                case "Еда": return MapL("Еда", "Food");
                case "Кокосы": return MapL("Кокосы", "Coconuts");
                case "Фрукты": return MapL("Фрукты", "Fruit");
                case "Рыба": return MapL("Рыба", "Fish");
                case "Мясо": return MapL("Мясо", "Meat");
                case "Провизия": return MapL("Провизия", "Supplies");

                case "Вода": return MapL("Вода", "Water");
                case "Водосборник": return MapL("Водосборник", "Water Still");
                case "Запас воды": return MapL("Запас воды", "Water Reserve");

                case "Растения": return MapL("Растения", "Plants");
                case "Пипи": return MapL("Пипи", "Pipi");
                case "Алоэ": return MapL("Алоэ", "Aloe");
                case "Юкка": return MapL("Юкка", "Yucca");
                case "Картофель": return MapL("Картофель", "Potato");
                case "Кура": return MapL("Кура", "Kura");
                case "Квувава": return MapL("Квувава", "Quwawa");
                case "Вавулавула": return MapL("Вавулавула", "Wavulavula");

                case "Животные": return MapL("Животные", "Animals");
                case "Кабан": return MapL("Кабан", "Boar");
                case "Огромный краб": return MapL("Огромный краб", "Giant Crab");
                case "Змея": return MapL("Змея", "Snake");
                case "Акула": return MapL("Акула", "Shark");

                case "Снаряжение": return MapL("Снаряжение", "Equipment");
                case "Инструменты": return MapL("Инструменты", "Tools");
                case "Оружие": return MapL("Оружие", "Weapons");
                case "Топливо": return MapL("Топливо", "Fuel");
                case "Навигация": return MapL("Навигация", "Navigation");

                case "Рельеф": return MapL("Рельеф", "Terrain");
                case "Скала": return MapL("Скала", "Cliff");
                case "Гора": return MapL("Гора", "Mountain");
                case "Каменная арка": return MapL("Каменная арка", "Stone Arch");
                case "Пещера": return MapL("Пещера", "Cave");

                case "Укрытие": return MapL("Укрытие", "Shelter");
                case "Ящик": return MapL("Ящик", "Crate");
                case "Костёр": return MapL("Костёр", "Campfire");

                case "Важное / прочее": return MapL("Важное / прочее", "Important / Other");
                case "Важно": return MapL("Важно", "Important");
                case "Точка": return MapL("Точка", "Dot");
            }

            return russianTitle ?? String.Empty;
        }

        private string MapLocManualMarkerLabel(
            string category,
            string subtype,
            string fallback)
        {
            string key =
                (category ?? String.Empty) +
                "/" +
                (subtype ?? String.Empty);

            switch (key)
            {
                case "materials/clay": return MapL("Глина", "Clay");
                case "materials/stones": return MapL("Камни", "Stones");
                case "materials/logs": return MapL("Брёвна", "Logs");
                case "materials/sticks": return MapL("Палки", "Sticks");
                case "materials/planks": return MapL("Доски", "Planks");
                case "materials/scrap": return MapL("Металл", "Scrap");
                case "materials/fibrous": return MapL("Волокно", "Fiber");
                case "materials/cloth": return MapL("Ткань", "Cloth");
                case "materials/other": return MapL("Материалы", "Materials");

                case "food/coconuts": return MapL("Кокосы", "Coconuts");
                case "food/fruit": return MapL("Фрукты", "Fruit");
                case "food/fish": return MapL("Рыба", "Fish");
                case "food/meat": return MapL("Мясо", "Meat");
                case "food/supplies": return MapL("Еда", "Food");

                case "water/still": return MapL("Вода", "Water");
                case "water/reserve": return MapL("Вода", "Water");

                case "plants/pipi": return MapL("Пипи", "Pipi");
                case "plants/aloe": return MapL("Алоэ", "Aloe");
                case "plants/yucca": return MapL("Юкка", "Yucca");
                case "plants/potato": return MapL("Картофель", "Potato");
                case "plants/kura": return MapL("Кура", "Kura");
                case "plants/quwawa": return MapL("Квувава", "Quwawa");
                case "plants/wavulavula": return MapL("Вавула", "Wavulavula");

                case "animals/boar": return MapL("Кабан", "Boar");
                case "animals/giant_crab": return MapL("Краб", "Giant Crab");
                case "animals/snake": return MapL("Змея", "Snake");
                case "animals/shark": return MapL("Акула", "Shark");

                case "equipment/tools": return MapL("Инструменты", "Tools");
                case "equipment/weapons": return MapL("Оружие", "Weapons");
                case "equipment/fuel": return MapL("Топливо", "Fuel");
                case "equipment/nav": return MapL("Навигация", "Navigation");
                case "equipment/other": return MapL("Снаряжение", "Equipment");

                case "terrain/cliff": return MapL("Скала", "Cliff");
                case "terrain/mountain": return MapL("Гора", "Mountain");
                case "terrain/stone_arch": return MapL("Арка", "Arch");
                case "terrain/cave": return MapL("Пещера", "Cave");

                case "basic/shelter": return MapL("Укрытие", "Shelter");
                case "basic/crate": return MapL("Ящик", "Crate");
                case "basic/campfire": return MapL("Костёр", "Campfire");

                case "other/important": return MapL("Важно", "Important");
                case "other/dot": return MapL("Точка", "Dot");
            }

            return fallback ?? String.Empty;
        }
    }
}