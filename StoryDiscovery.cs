using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Beam;
using Beam.Intro;
using UnityEngine;
using UnityEngine.Events;

namespace StrandedDeepSplitMap
{
    public sealed partial class SplitMapPlugin
    {
        private sealed class StoryNote
        {
            public string Kind;
            public string ZoneKey;
            public float WorldX;
            public float WorldZ;
            public bool Completed;
        }

        private readonly List<StoryNote> _storyNotes =
            new List<StoryNote>();

        private readonly Dictionary<MissionControllerBase, Action<bool>>
            _storyMissionProximityHandlers =
            new Dictionary<MissionControllerBase, Action<bool>>();

        private readonly Dictionary<MissionControllerBase, Action<IMissionDetailsProvider>>
            _storyMissionCompletedHandlers =
            new Dictionary<MissionControllerBase, Action<IMissionDetailsProvider>>();

        private readonly Dictionary<AircraftProximity, UnityAction>
            _storyAircraftHandlers =
            new Dictionary<AircraftProximity, UnityAction>();

        private bool _storyLoaded;
        private string _storySeed = String.Empty;
        private float _storyNextScanTime;

        private GUIStyle _storyMarkerStyle;
        private GUIStyle _storyLabelStyle;
        private GUIStyle _storyCompletedLabelStyle;

        private Texture2D _storyLuscaTexture;
        private Texture2D _storyAbaiaTexture;
        private Texture2D _storyMegalodonTexture;
        private Texture2D _storyCarrierTexture;
        private Texture2D _storyUnknownTexture;

        private FieldInfo _storyMissionAssetLoaderField;
        private FieldInfo _storyBossTypeField;
        private FieldInfo _storyMissionCompletedField;
        private FieldInfo _storyAircraftEventField;
        private FieldInfo _storyZoneSavedObjectsLookupField;

        private void StoryInitialize()
        {
            try
            {
                BindingFlags instanceFlags =
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic;

                _storyMissionAssetLoaderField =
                    typeof(MissionControllerBase).GetField(
                        "_missionAssetLoader",
                        instanceFlags);

                _storyMissionCompletedField =
                    typeof(MissionControllerBase).GetField(
                        "_missionCompleted",
                        instanceFlags);

                if (_storyMissionAssetLoaderField != null)
                {
                    _storyBossTypeField =
                        _storyMissionAssetLoaderField.FieldType.GetField(
                            "_bossType",
                            instanceFlags);
                }

                _storyAircraftEventField =
                    typeof(AircraftProximity).GetField(
                        "EnteredProximity",
                        instanceFlags);

                _storyZoneSavedObjectsLookupField =
                    typeof(StrandedWorld).GetField(
                        "_zoneSavedObjectsLookup",
                        instanceFlags);

                LoadStoryIconAssets();

                Logger.LogInfo(
                    "Story Discovery event bridge initialized.");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Story Discovery initialization failed: " +
                    ex.Message);
            }
        }

        private void StoryUpdate()
        {
            float now = Time.unscaledTime;

            if (now < _storyNextScanTime)
                return;

            _storyNextScanTime = now + 1.0f;

            try
            {
                EnsureStoryLoadedForCurrentWorld();
                StorySubscribeMissionControllers();
                StorySubscribeAircraftProximity();
                ScanVisitedStoryZones(false);
                StoryReconcileCompletedMissions();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Story Discovery update failed: " +
                    ex.Message);
            }
        }

        private void StoryShutdown()
        {
            try
            {
                foreach (
                    KeyValuePair<MissionControllerBase, Action<bool>> pair
                    in _storyMissionProximityHandlers)
                {
                    MissionControllerBase mission = pair.Key;

                    if (mission != null)
                        mission.EnteredMissionProximity -= pair.Value;
                }

                foreach (
                    KeyValuePair<MissionControllerBase, Action<IMissionDetailsProvider>> pair
                    in _storyMissionCompletedHandlers)
                {
                    MissionControllerBase mission = pair.Key;

                    if (mission != null)
                        mission.MissionCompleted -= pair.Value;
                }

                foreach (
                    KeyValuePair<AircraftProximity, UnityAction> pair
                    in _storyAircraftHandlers)
                {
                    AircraftProximity proximity = pair.Key;

                    if (proximity == null)
                        continue;

                    UnityEvent evt =
                        GetAircraftEnteredProximityEvent(proximity);

                    if (evt != null)
                        evt.RemoveListener(pair.Value);
                }

                _storyMissionProximityHandlers.Clear();
                _storyMissionCompletedHandlers.Clear();
                _storyAircraftHandlers.Clear();

                ReleaseStoryIconAssets();

                if (_storyLoaded)
                    SaveStoryNotes();
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Story Discovery shutdown failed: " +
                    ex.Message);
            }
        }

        private void StorySubscribeMissionControllers()
        {
            UnityEngine.Object[] raw =
                Resources.FindObjectsOfTypeAll(
                    typeof(MissionControllerBase));

            int i;

            for (i = 0; i < raw.Length; i++)
            {
                MissionControllerBase mission =
                    raw[i] as MissionControllerBase;

                if (mission == null)
                    continue;

                if (!_storyMissionProximityHandlers.ContainsKey(mission))
                {
                    MissionControllerBase captured = mission;

                    Action<bool> proximityHandler =
                        delegate(bool entered)
                        {
                            if (entered)
                                RecordStoryMission(captured, false);
                        };

                    mission.EnteredMissionProximity +=
                        proximityHandler;

                    _storyMissionProximityHandlers.Add(
                        mission,
                        proximityHandler);
                }

                if (!_storyMissionCompletedHandlers.ContainsKey(mission))
                {
                    MissionControllerBase captured = mission;

                    Action<IMissionDetailsProvider> completedHandler =
                        delegate(IMissionDetailsProvider provider)
                        {
                            RecordStoryMission(captured, true);
                        };

                    mission.MissionCompleted +=
                        completedHandler;

                    _storyMissionCompletedHandlers.Add(
                        mission,
                        completedHandler);
                }
            }
        }

        private void StorySubscribeAircraftProximity()
        {
            UnityEngine.Object[] raw =
                Resources.FindObjectsOfTypeAll(
                    typeof(AircraftProximity));

            int i;

            for (i = 0; i < raw.Length; i++)
            {
                AircraftProximity proximity =
                    raw[i] as AircraftProximity;

                if (proximity == null ||
                    _storyAircraftHandlers.ContainsKey(proximity))
                {
                    continue;
                }

                UnityEvent evt =
                    GetAircraftEnteredProximityEvent(proximity);

                if (evt == null)
                    continue;

                AircraftProximity captured = proximity;

                UnityAction handler =
                    delegate()
                    {
                        RecordAircraftCarrier(captured);
                    };

                evt.AddListener(handler);

                _storyAircraftHandlers.Add(
                    proximity,
                    handler);
            }
        }

        private UnityEvent GetAircraftEnteredProximityEvent(
            AircraftProximity proximity)
        {
            if (proximity == null ||
                _storyAircraftEventField == null)
            {
                return null;
            }

            try
            {
                return
                    _storyAircraftEventField.GetValue(proximity)
                    as UnityEvent;
            }
            catch
            {
                return null;
            }
        }

        private void RecordStoryMission(
            MissionControllerBase mission,
            bool completed)
        {
            if (mission == null)
                return;

            try
            {
                EnsureStoryLoadedForCurrentWorld();

                string kind =
                    GetStoryKindFromMission(mission);

                if (String.IsNullOrEmpty(kind) ||
                    String.Equals(
                        kind,
                        "SHIP",
                        StringComparison.Ordinal))
                {
                    return;
                }

                Vector3 position =
                    mission.transform.position;

                Zone zone =
                    StrandedWorld.GetZone(
                        position,
                        false);

                string zoneKey =
                    zone != null
                    ? GetZoneKey(zone)
                    : String.Empty;

                if (UpsertStoryNote(
                    kind,
                    zoneKey,
                    position.x,
                    position.z,
                    completed || IsMissionCompleted(mission)))
                {
                    SaveStoryNotes();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Story mission discovery failed: " +
                    ex.Message);
            }
        }

        private void RecordAircraftCarrier(
            AircraftProximity proximity)
        {
            if (proximity == null)
                return;

            try
            {
                EnsureStoryLoadedForCurrentWorld();

                Vector3 position =
                    proximity.transform.position;

                Zone zone =
                    StrandedWorld.GetZone(
                        position,
                        false);

                string zoneKey =
                    zone != null
                    ? GetZoneKey(zone)
                    : String.Empty;

                if (UpsertStoryNote(
                    "CARRIER",
                    zoneKey,
                    position.x,
                    position.z,
                    false))
                {
                    SaveStoryNotes();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Aircraft Carrier discovery failed: " +
                    ex.Message);
            }
        }

        private string GetStoryKindFromMission(
            MissionControllerBase mission)
        {
            if (mission == null ||
                _storyMissionAssetLoaderField == null ||
                _storyBossTypeField == null)
            {
                return String.Empty;
            }

            try
            {
                object loader =
                    _storyMissionAssetLoaderField.GetValue(mission);

                if (loader == null)
                    return String.Empty;

                object rawBossType =
                    _storyBossTypeField.GetValue(loader);

                if (rawBossType == null)
                    return String.Empty;

                string bossType =
                    rawBossType.ToString();

                if (String.Equals(
                    bossType,
                    "Shark",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return "MEGALODON";
                }

                if (String.Equals(
                    bossType,
                    "Eel",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return "ABAIA";
                }

                if (String.Equals(
                    bossType,
                    "GiantSquid",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return "LUSCA";
                }

                if (String.Equals(
                    bossType,
                    "Ship",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return "SHIP";
                }
            }
            catch
            {
            }

            return String.Empty;
        }

        private bool IsMissionCompleted(
            MissionControllerBase mission)
        {
            if (mission == null ||
                _storyMissionCompletedField == null)
            {
                return false;
            }

            try
            {
                object raw =
                    _storyMissionCompletedField.GetValue(
                        mission);

                return raw is bool && (bool)raw;
            }
            catch
            {
                return false;
            }
        }

        private void StoryReconcileCompletedMissions()
        {
            UnityEngine.Object[] raw =
                Resources.FindObjectsOfTypeAll(
                    typeof(MissionControllerBase));

            int i;

            for (i = 0; i < raw.Length; i++)
            {
                MissionControllerBase mission =
                    raw[i] as MissionControllerBase;

                if (mission == null ||
                    !IsMissionCompleted(mission))
                {
                    continue;
                }

                string kind =
                    GetStoryKindFromMission(mission);

                if (String.IsNullOrEmpty(kind) ||
                    String.Equals(
                        kind,
                        "SHIP",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                StoryNote note =
                    FindStoryNote(kind, String.Empty);

                if (note != null &&
                    !note.Completed)
                {
                    RecordStoryMission(
                        mission,
                        true);
                }
            }
        }

        private void EnsureStoryLoadedForCurrentWorld()
        {
            string path =
                GetStoryPath();

            string seed =
                Path.GetFileNameWithoutExtension(path);

            if (_storyLoaded &&
                String.Equals(
                    _storySeed,
                    seed,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (_storyLoaded)
                SaveStoryNotes();

            _storyNotes.Clear();
            _storyLoaded = true;
            _storySeed = seed;

            try
            {
                if (File.Exists(path))
                {
                    string[] lines =
                        File.ReadAllLines(path);

                    int i;

                    for (i = 0; i < lines.Length; i++)
                    {
                        StoryNote note =
                            ParseStoryLine(lines[i]);

                        if (note != null)
                            _storyNotes.Add(note);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Story notes load failed: " +
                    ex.Message);
            }

            ScanVisitedStoryZones(true);
        }

        private string GetStoryPath()
        {
            string discoveryPath =
                GetDiscoveryPath();

            string fileName =
                Path.GetFileName(discoveryPath);

            if (String.IsNullOrEmpty(fileName))
                fileName = "unknown-world.txt";

            string directory =
                Path.Combine(
                    BepInEx.Paths.ConfigPath,
                    "StrandedDeepMap",
                    "Story");

            return Path.Combine(
                directory,
                fileName);
        }

        private StoryNote ParseStoryLine(
            string line)
        {
            if (String.IsNullOrWhiteSpace(line) ||
                line.StartsWith(
                    "#",
                    StringComparison.Ordinal))
            {
                return null;
            }

            string[] parts =
                line.Split('\t');

            if (parts.Length < 6 ||
                !String.Equals(
                    parts[0],
                    "STORY",
                    StringComparison.Ordinal))
            {
                return null;
            }

            float x;
            float z;

            if (!Single.TryParse(
                parts[3],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out x))
            {
                return null;
            }

            if (!Single.TryParse(
                parts[4],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out z))
            {
                return null;
            }

            StoryNote note =
                new StoryNote();

            note.Kind =
                parts[1] ?? String.Empty;

            note.ZoneKey =
                parts[2] ?? String.Empty;

            note.WorldX = x;
            note.WorldZ = z;
            note.Completed =
                parts[5] == "1";

            return note;
        }

        private void SaveStoryNotes()
        {
            if (!_storyLoaded)
                return;

            string path =
                GetStoryPath();

            string tmp =
                path + ".tmp";

            try
            {
                string directory =
                    Path.GetDirectoryName(path);

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                List<StoryNote> ordered =
                    new List<StoryNote>(_storyNotes);

                ordered.Sort(
                    delegate(
                        StoryNote a,
                        StoryNote b)
                    {
                        int kindCompare =
                            StringComparer.Ordinal.Compare(
                                a.Kind,
                                b.Kind);

                        if (kindCompare != 0)
                            return kindCompare;

                        return
                            StringComparer.Ordinal.Compare(
                                a.ZoneKey,
                                b.ZoneKey);
                    });

                List<string> lines =
                    new List<string>();

                lines.Add(
                    "# Stranded Deep Split Map story markers v1");

                lines.Add(
                    "# type\tkind\tzone\tx\tz\tcompleted");

                int i;

                for (i = 0; i < ordered.Count; i++)
                {
                    StoryNote note =
                        ordered[i];

                    lines.Add(
                        "STORY\t" +
                        (note.Kind ?? String.Empty) +
                        "\t" +
                        (note.ZoneKey ?? String.Empty) +
                        "\t" +
                        note.WorldX.ToString(
                            "R",
                            CultureInfo.InvariantCulture) +
                        "\t" +
                        note.WorldZ.ToString(
                            "R",
                            CultureInfo.InvariantCulture) +
                        "\t" +
                        (note.Completed ? "1" : "0"));
                }

                File.WriteAllLines(
                    tmp,
                    lines.ToArray(),
                    new System.Text.UTF8Encoding(false));

                File.Copy(
                    tmp,
                    path,
                    true);

                File.Delete(tmp);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Story notes save failed: " +
                    ex.Message);

                try
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
                catch
                {
                }
            }
        }

        private bool UpsertStoryNote(
            string kind,
            string zoneKey,
            float x,
            float z,
            bool completed)
        {
            StoryNote note =
                FindStoryNote(
                    kind,
                    zoneKey);

            if (note == null)
            {
                note =
                    new StoryNote();

                note.Kind =
                    kind ?? String.Empty;

                note.ZoneKey =
                    zoneKey ?? String.Empty;

                note.WorldX = x;
                note.WorldZ = z;
                note.Completed = completed;

                _storyNotes.Add(note);
                return true;
            }

            bool changed = false;

            if (!String.IsNullOrEmpty(kind) &&
                !String.Equals(
                    note.Kind,
                    kind,
                    StringComparison.Ordinal) &&
                (
                    String.Equals(
                        note.Kind,
                        "MISSION",
                        StringComparison.Ordinal) ||
                    String.IsNullOrEmpty(note.Kind)
                ))
            {
                note.Kind = kind;
                changed = true;
            }

            if (!String.IsNullOrEmpty(zoneKey) &&
                !String.Equals(
                    note.ZoneKey,
                    zoneKey,
                    StringComparison.Ordinal))
            {
                note.ZoneKey = zoneKey;
                changed = true;
            }

            if (Mathf.Abs(note.WorldX - x) > 0.1f ||
                Mathf.Abs(note.WorldZ - z) > 0.1f)
            {
                note.WorldX = x;
                note.WorldZ = z;
                changed = true;
            }

            if (completed &&
                !note.Completed)
            {
                note.Completed = true;
                changed = true;
            }

            return changed;
        }

        private StoryNote FindStoryNote(
            string kind,
            string zoneKey)
        {
            int i;

            if (!String.IsNullOrEmpty(zoneKey))
            {
                for (i = 0; i < _storyNotes.Count; i++)
                {
                    StoryNote note =
                        _storyNotes[i];

                    if (String.Equals(
                        note.ZoneKey,
                        zoneKey,
                        StringComparison.Ordinal))
                    {
                        return note;
                    }
                }
            }

            if (!String.IsNullOrEmpty(kind) &&
                !String.Equals(
                    kind,
                    "MISSION",
                    StringComparison.Ordinal))
            {
                for (i = 0; i < _storyNotes.Count; i++)
                {
                    StoryNote note =
                        _storyNotes[i];

                    if (String.Equals(
                        note.Kind,
                        kind,
                        StringComparison.Ordinal))
                    {
                        return note;
                    }
                }
            }

            return null;
        }

        private void ScanVisitedStoryZones(

            bool force)

        {

            // StoryUpdate already throttles this scan. Do not reuse

            // _storyNextScanTime here: StoryUpdate advances it before

            // calling this method, which would otherwise skip every

            // non-forced reconciliation.

            bool changed = false;



            UnityEngine.Object[] rawZones =

                Resources.FindObjectsOfTypeAll(

                    typeof(Zone));



            UnityEngine.Object[] rawMissions =

                Resources.FindObjectsOfTypeAll(

                    typeof(MissionControllerBase));



            IDictionary savedObjects =

                GetStorySavedObjectsLookup();



            int i;



            for (i = 0; i < rawZones.Length; i++)

            {

                Zone zone =

                    rawZones[i] as Zone;



                if (zone == null)

                    continue;



                string biome =

                    zone.Biome.ToString();



                bool isMission =

                    String.Equals(

                        biome,

                        "MISSION",

                        StringComparison.OrdinalIgnoreCase);



                bool isCarrier =

                    String.Equals(

                        biome,

                        "CARRIER",

                        StringComparison.OrdinalIgnoreCase);



                if (!isMission &&

                    !isCarrier)

                {

                    continue;

                }



                string savedKind;

                bool savedEvidence =

                    TryGetSavedStoryEvidence(

                        zone,

                        savedObjects,

                        out savedKind);



                if (!zone.HasVisited &&

                    !savedEvidence)

                {

                    continue;

                }



                string zoneKey =

                    GetZoneKey(zone);



                Vector3 zonePosition =

                    zone.transform.position;



                if (isCarrier)

                {

                    if (UpsertStoryNote(

                        "CARRIER",

                        zoneKey,

                        zonePosition.x,

                        zonePosition.z,

                        false))

                    {

                        changed = true;



                        if (savedEvidence)

                        {

                            Logger.LogInfo(

                                "Retroactive story discovery: CARRIER @ " +

                                zoneKey);

                        }

                    }



                    continue;

                }



                MissionControllerBase mission =

                    FindMissionControllerForZone(

                        zone,

                        rawMissions);



                if (mission != null)

                {

                    string runtimeKind =

                        GetStoryKindFromMission(

                            mission);



                    if (!String.IsNullOrEmpty(runtimeKind) &&

                        !String.Equals(

                            runtimeKind,

                            "SHIP",

                            StringComparison.Ordinal))

                    {

                        Vector3 position =

                            mission.transform.position;



                        if (UpsertStoryNote(

                            runtimeKind,

                            zoneKey,

                            position.x,

                            position.z,

                            IsMissionCompleted(mission)))

                        {

                            changed = true;

                        }



                        continue;

                    }

                }



                string kind =

                    !String.IsNullOrEmpty(savedKind)

                    ? savedKind

                    : GetStoryKindFromZoneId(zoneKey);



                if (String.IsNullOrEmpty(kind) ||

                    String.Equals(

                        kind,

                        "CARRIER",

                        StringComparison.Ordinal))

                {

                    kind = "MISSION";

                }



                if (UpsertStoryNote(

                    kind,

                    zoneKey,

                    zonePosition.x,

                    zonePosition.z,

                    false))

                {

                    changed = true;



                    if (savedEvidence)

                    {

                        Logger.LogInfo(

                            "Retroactive story discovery: " +

                            kind +

                            " @ " +

                            zoneKey);

                    }

                }

            }



            if (changed)

                SaveStoryNotes();

        }



        private IDictionary GetStorySavedObjectsLookup()

        {

            if (_storyZoneSavedObjectsLookupField == null)

                return null;



            try

            {

                UnityEngine.Object[] worlds =

                    Resources.FindObjectsOfTypeAll(

                        typeof(StrandedWorld));



                int i;



                for (i = 0; i < worlds.Length; i++)

                {

                    object world =

                        worlds[i];



                    if (world == null)

                        continue;



                    IDictionary dictionary =

                        _storyZoneSavedObjectsLookupField.GetValue(

                            world)

                        as IDictionary;



                    if (dictionary != null)

                        return dictionary;

                }

            }

            catch (Exception ex)

            {

                Logger.LogWarning(

                    "Story saved-state lookup failed: " +

                    ex.Message);

            }



            return null;

        }



        private bool TryGetSavedStoryEvidence(

            Zone zone,

            IDictionary savedObjects,

            out string kind)

        {

            kind = String.Empty;



            if (zone == null ||

                savedObjects == null)

            {

                return false;

            }



            string zoneKey =

                GetZoneKey(zone);



            if (String.IsNullOrEmpty(zoneKey))

                return false;



            object payload = null;



            try

            {

                if (savedObjects.Contains(zoneKey))

                {

                    payload =

                        savedObjects[zoneKey];

                }

                else

                {

                    foreach (

                        DictionaryEntry pair

                        in savedObjects)

                    {

                        string key =

                            pair.Key != null

                            ? pair.Key.ToString()

                            : String.Empty;



                        if (String.Equals(

                            key,

                            zoneKey,

                            StringComparison.OrdinalIgnoreCase))

                        {

                            payload =

                                pair.Value;



                            break;

                        }

                    }

                }

            }

            catch

            {

                return false;

            }



            if (payload == null)

                return false;



            string text =

                payload.ToString();



            if (String.IsNullOrEmpty(text))

                return false;



            if (ContainsStoryToken(

                    text,

                    "MISSION_SQUID") ||

                ContainsStoryToken(

                    text,

                    "GIANTSQUID") ||

                ContainsStoryToken(

                    text,

                    "LUSCA"))

            {

                kind = "LUSCA";

                return true;

            }



            if (ContainsStoryToken(

                    text,

                    "MISSION_EEL") ||

                ContainsStoryToken(

                    text,

                    "ABAIA"))

            {

                kind = "ABAIA";

                return true;

            }



            if (ContainsStoryToken(

                    text,

                    "MISSION_MEGALODON") ||

                ContainsStoryToken(

                    text,

                    "MISSION_SHARK") ||

                ContainsStoryToken(

                    text,

                    "MEGALODON"))

            {

                kind = "MEGALODON";

                return true;

            }



            if (ContainsStoryToken(

                    text,

                    "CARRIER") ||

                ContainsStoryToken(

                    text,

                    "AIRCRAFT_CARRIER"))

            {

                kind = "CARRIER";

                return true;

            }



            return false;

        }



        private static bool ContainsStoryToken(

            string text,

            string token)

        {

            return

                !String.IsNullOrEmpty(text) &&

                !String.IsNullOrEmpty(token) &&

                text.IndexOf(

                    token,

                    StringComparison.OrdinalIgnoreCase) >= 0;

        }



        private static string GetStoryKindFromZoneId(

            string zoneKey)

        {

            if (String.IsNullOrEmpty(zoneKey))

                return String.Empty;



            if (zoneKey.EndsWith(

                "_0",

                StringComparison.OrdinalIgnoreCase))

            {

                return "MEGALODON";

            }



            if (zoneKey.EndsWith(

                "_1",

                StringComparison.OrdinalIgnoreCase))

            {

                return "ABAIA";

            }



            if (zoneKey.EndsWith(

                "_2",

                StringComparison.OrdinalIgnoreCase))

            {

                return "LUSCA";

            }



            if (zoneKey.EndsWith(

                "_3",

                StringComparison.OrdinalIgnoreCase))

            {

                return "CARRIER";

            }



            return String.Empty;

        }


        private MissionControllerBase
            FindMissionControllerForZone(
                Zone zone,
                UnityEngine.Object[] rawMissions)
        {
            if (zone == null ||
                rawMissions == null)
            {
                return null;
            }

            int i;

            for (i = 0; i < rawMissions.Length; i++)
            {
                MissionControllerBase mission =
                    rawMissions[i]
                    as MissionControllerBase;

                if (mission == null)
                    continue;

                Zone missionZone =
                    StrandedWorld.GetZone(
                        mission.transform.position,
                        false);

                if (missionZone == zone)
                    return mission;
            }

            return null;
        }

        private void LoadStoryIconAssets()
        {
            _storyLuscaTexture =
                LoadStoryIconAsset(
                    "story_lusca.png");

            _storyAbaiaTexture =
                LoadStoryIconAsset(
                    "story_abaia.png");

            _storyMegalodonTexture =
                LoadStoryIconAsset(
                    "story_megalodon.png");

            _storyCarrierTexture =
                LoadStoryIconAsset(
                    "story_carrier.png");

            _storyUnknownTexture =
                LoadStoryIconAsset(
                    "story_unknown.png");

            int loaded = 0;

            if (_storyLuscaTexture != null) loaded++;
            if (_storyAbaiaTexture != null) loaded++;
            if (_storyMegalodonTexture != null) loaded++;
            if (_storyCarrierTexture != null) loaded++;
            if (_storyUnknownTexture != null) loaded++;

            Logger.LogInfo(
                "Story icon assets loaded: " +
                loaded +
                "/5");
        }

        private Texture2D LoadStoryIconAsset(
            string fileName)
        {
            try
            {
                string pluginDirectory =
                    Path.GetDirectoryName(
                        typeof(SplitMapPlugin)
                            .Assembly
                            .Location);

                string path =
                    Path.Combine(
                        pluginDirectory,
                        "assets",
                        "story",
                        fileName);

                if (!File.Exists(path))
                {
                    Logger.LogWarning(
                        "Story icon asset missing: " +
                        path);

                    return null;
                }

                byte[] bytes =
                    File.ReadAllBytes(path);

                Texture2D texture =
                    new Texture2D(
                        2,
                        2,
                        TextureFormat.RGBA32,
                        false);

                bool loaded =
                    UnityEngine.ImageConversion.LoadImage(
                        texture,
                        bytes,
                        false);

                if (!loaded)
                {
                    UnityEngine.Object.Destroy(texture);

                    Logger.LogWarning(
                        "Story icon asset could not be decoded: " +
                        path);

                    return null;
                }

                texture.name =
                    "SplitMap_" +
                    Path.GetFileNameWithoutExtension(
                        fileName);

                texture.filterMode =
                    FilterMode.Bilinear;

                texture.wrapMode =
                    TextureWrapMode.Clamp;

                return texture;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    "Story icon asset load failed: " +
                    fileName +
                    " :: " +
                    ex.Message);

                return null;
            }
        }

        private void ReleaseStoryIconAssets()
        {
            DestroyStoryIconTexture(
                ref _storyLuscaTexture);

            DestroyStoryIconTexture(
                ref _storyAbaiaTexture);

            DestroyStoryIconTexture(
                ref _storyMegalodonTexture);

            DestroyStoryIconTexture(
                ref _storyCarrierTexture);

            DestroyStoryIconTexture(
                ref _storyUnknownTexture);
        }

        private void DestroyStoryIconTexture(
            ref Texture2D texture)
        {
            if (texture == null)
                return;

            UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        private Texture2D GetStoryIcon(
            StoryNote note)
        {
            if (note == null)
                return _storyUnknownTexture;

            if (String.Equals(
                note.Kind,
                "LUSCA",
                StringComparison.Ordinal))
            {
                return
                    _storyLuscaTexture != null
                    ? _storyLuscaTexture
                    : _storyUnknownTexture;
            }

            if (String.Equals(
                note.Kind,
                "ABAIA",
                StringComparison.Ordinal))
            {
                return
                    _storyAbaiaTexture != null
                    ? _storyAbaiaTexture
                    : _storyUnknownTexture;
            }

            if (String.Equals(
                note.Kind,
                "MEGALODON",
                StringComparison.Ordinal))
            {
                return
                    _storyMegalodonTexture != null
                    ? _storyMegalodonTexture
                    : _storyUnknownTexture;
            }

            if (String.Equals(
                note.Kind,
                "CARRIER",
                StringComparison.Ordinal))
            {
                return
                    _storyCarrierTexture != null
                    ? _storyCarrierTexture
                    : _storyUnknownTexture;
            }

            return _storyUnknownTexture;
        }
        private void DrawStoryMarkers(

            Rect mapRect,

            WorldBounds bounds,

            MapViewState view,

            int viewIndex)

        {

            EnsureStoryLoadedForCurrentWorld();

            EnsureStoryStyles();



            if (IsRevealEntireMapEnabled())

            {

                DrawRevealAllStoryMarkers(

                    mapRect,

                    bounds,

                    view,

                    viewIndex);



                return;

            }



            int i;



            for (i = 0; i < _storyNotes.Count; i++)

            {

                DrawOneStoryMarker(

                    _storyNotes[i],

                    mapRect,

                    bounds,

                    view,

                    viewIndex);

            }

        }



        private void DrawRevealAllStoryMarkers(

            Rect mapRect,

            WorldBounds bounds,

            MapViewState view,

            int viewIndex)

        {

            UnityEngine.Object[] rawZones =

                Resources.FindObjectsOfTypeAll(

                    typeof(Zone));



            UnityEngine.Object[] rawMissions =

                Resources.FindObjectsOfTypeAll(

                    typeof(MissionControllerBase));



            IDictionary savedObjects =

                GetStorySavedObjectsLookup();



            int i;



            for (i = 0; i < rawZones.Length; i++)

            {

                Zone zone =

                    rawZones[i] as Zone;



                if (zone == null)

                    continue;



                string biome =

                    zone.Biome.ToString();



                bool isMission =

                    String.Equals(

                        biome,

                        "MISSION",

                        StringComparison.OrdinalIgnoreCase);



                bool isCarrier =

                    String.Equals(

                        biome,

                        "CARRIER",

                        StringComparison.OrdinalIgnoreCase);



                if (!isMission &&

                    !isCarrier)

                {

                    continue;

                }



                string zoneKey =

                    GetZoneKey(zone);



                Vector3 position =

                    zone.transform.position;



                string kind =

                    isCarrier

                    ? "CARRIER"

                    : GetStoryKindFromZoneId(

                        zoneKey);



                if (String.IsNullOrEmpty(kind))

                    kind = "MISSION";



                if (isMission)

                {

                    MissionControllerBase mission =

                        FindMissionControllerForZone(

                            zone,

                            rawMissions);



                    if (mission != null)

                    {

                        string runtimeKind =

                            GetStoryKindFromMission(

                                mission);



                        if (!String.IsNullOrEmpty(runtimeKind) &&

                            !String.Equals(

                                runtimeKind,

                                "SHIP",

                                StringComparison.Ordinal))

                        {

                            kind = runtimeKind;

                            position =

                                mission.transform.position;

                        }

                    }



                    string savedKind;



                    if (TryGetSavedStoryEvidence(

                            zone,

                            savedObjects,

                            out savedKind) &&

                        !String.IsNullOrEmpty(savedKind))

                    {

                        kind = savedKind;

                    }

                }



                StoryNote note =

                    new StoryNote();



                note.Kind = kind;

                note.ZoneKey = zoneKey;

                note.WorldX = position.x;

                note.WorldZ = position.z;

                note.Completed = false;



                StoryNote persisted =

                    FindStoryNote(

                        kind,

                        zoneKey);



                if (persisted != null)

                    note.Completed = persisted.Completed;



                DrawOneStoryMarker(

                    note,

                    mapRect,

                    bounds,

                    view,

                    viewIndex);

            }

        }



        private void DrawOneStoryMarker(

            StoryNote note,

            Rect mapRect,

            WorldBounds bounds,

            MapViewState view,

            int viewIndex)

        {

            if (note == null)

                return;



            Rect anchor =

                WorldAreaToMapRect(

                    note.WorldX - 0.5f,

                    note.WorldX + 0.5f,

                    note.WorldZ - 0.5f,

                    note.WorldZ + 0.5f,

                    bounds,

                    view,

                    mapRect);



            Vector2 center =

                anchor.center;



            if (!mapRect.Contains(center))

                return;



            Rect markerRect =

                new Rect(

                    center.x - 19.0f,

                    center.y - 19.0f,

                    38.0f,

                    38.0f);



            Rect labelRect =

                new Rect(

                    center.x + 23.0f,

                    center.y - 12.0f,

                    230.0f,

                    24.0f);



            Texture2D storyIcon =

                GetStoryIcon(note);



            if (storyIcon != null)

            {

                GUI.DrawTexture(

                    markerRect,

                    storyIcon,

                    ScaleMode.ScaleToFit,

                    true);

            }

            else

            {

                GUI.Label(

                    markerRect,

                    GetStorySymbol(note),

                    _storyMarkerStyle);

            }



            if (note.Completed)

            {

                GUI.Label(

                    markerRect,

                    "X",

                    _storyMarkerStyle);

            }



            GUI.Label(

                labelRect,

                GetStoryDisplayLabel(note),

                note.Completed

                ? _storyCompletedLabelStyle

                : _storyLabelStyle);

        }


        private void EnsureStoryStyles()
        {
            if (_storyMarkerStyle != null)
                return;

            Color ink =
                new Color(
                    0.32f,
                    0.18f,
                    0.09f,
                    1.0f);

            Color completedInk =
                new Color(
                    0.23f,
                    0.28f,
                    0.15f,
                    1.0f);

            _storyMarkerStyle =
                new GUIStyle(
                    GUI.skin.label);

            _storyMarkerStyle.alignment =
                TextAnchor.MiddleCenter;

            _storyMarkerStyle.fontStyle =
                FontStyle.Bold;

            _storyMarkerStyle.fontSize = 18;
            _storyMarkerStyle.normal.textColor = ink;

            _storyLabelStyle =
                new GUIStyle(
                    GUI.skin.label);

            _storyLabelStyle.alignment =
                TextAnchor.MiddleLeft;

            _storyLabelStyle.fontStyle =
                FontStyle.Bold;

            _storyLabelStyle.fontSize = 12;
            _storyLabelStyle.normal.textColor = ink;

            _storyCompletedLabelStyle =
                new GUIStyle(
                    _storyLabelStyle);

            _storyCompletedLabelStyle.normal.textColor =
                completedInk;
        }

        private string GetStorySymbol(
            StoryNote note)
        {
            if (note == null)
                return "?";

            if (note.Completed)
                return "X";

            if (String.Equals(
                note.Kind,
                "CARRIER",
                StringComparison.Ordinal))
            {
                return "A";
            }

            if (String.Equals(
                note.Kind,
                "MISSION",
                StringComparison.Ordinal))
            {
                return "?";
            }

            return "!";
        }

        private string GetStoryDisplayLabel(
            StoryNote note)
        {
            if (note == null)
                return String.Empty;

            string label;

            if (String.Equals(
                note.Kind,
                "MEGALODON",
                StringComparison.Ordinal))
            {
                label =
                    MapL(
                        "Мегалодон",
                        "Megalodon");
            }
            else if (String.Equals(
                note.Kind,
                "ABAIA",
                StringComparison.Ordinal))
            {
                label =
                    MapL(
                        "Великая Абайя",
                        "The Great Abaia");
            }
            else if (String.Equals(
                note.Kind,
                "LUSCA",
                StringComparison.Ordinal))
            {
                label =
                    MapL(
                        "Луска Великая",
                        "Lusca the Great");
            }
            else if (String.Equals(
                note.Kind,
                "CARRIER",
                StringComparison.Ordinal))
            {
                label =
                    MapL(
                        "Авианосец",
                        "Aircraft Carrier");
            }
            else
            {
                label =
                    MapL(
                        "Сюжетная точка",
                        "Story Location");
            }

            if (note.Completed &&
                !String.Equals(
                    note.Kind,
                    "CARRIER",
                    StringComparison.Ordinal))
            {
                label +=
                    MapL(
                        " — ПОБЕЖДЁН",
                        " — DEFEATED");
            }

            return label;
        }
    }
}