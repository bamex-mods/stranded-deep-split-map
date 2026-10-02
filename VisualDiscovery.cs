using System;
using System.Collections.Generic;
using UnityEngine;

namespace StrandedDeepSplitMap
{
    public sealed partial class SplitMapPlugin
    {
        // v0.12b: visual discovery now requires real line-of-sight evidence.
        // Existing Discovery/*.txt files remain authoritative and are never cleared.
        private const float NormalVisualDiscoveryDistance = 950.0f;
        private const float BinocularVisualDiscoveryDistance = 1550.0f;
        private const float CloseRangeDiscoveryDistance = 185.0f;
        private const float NormalVisualHoldSeconds = 0.65f;
        private const float BinocularVisualHoldSeconds = 0.50f;
        private const float BinocularFovRatio = 0.78f;
        private const float BinocularMinFovDelta = 6.0f;
        private const float NormalViewportInset = 0.055f;
        private const float BinocularRaycastExtraDistance = 120.0f;
        private const int BinocularRequiredRayHits = 2;

        // 3x3 ray grid in the central part of the binocular image. This deliberately
        // does not scan the full camera frustum: the player must actually aim at the island.
        private static readonly float[] BinocularViewportSamples =
        {
            0.38f,
            0.50f,
            0.62f
        };

        private readonly Dictionary<string, VisualDiscoveryProgress>[] _visualDiscoveryProgress =
        {
            new Dictionary<string, VisualDiscoveryProgress>(StringComparer.Ordinal),
            new Dictionary<string, VisualDiscoveryProgress>(StringComparer.Ordinal)
        };

        private readonly float[] _maxObservedCameraFov = { 0.0f, 0.0f };
        private string _visualDiscoverySeed = String.Empty;

        private void UpdateVisualDiscoveryFromPlayers()
        {
            if (_world == null || _players == null || _players.Length == 0)
                return;

            EnsureDiscoveryLoadedForCurrentWorld();
            ResetVisualDiscoveryTrackersIfWorldChanged();

            Zone[] zones = GetZones();
            if (zones == null || zones.Length == 0)
                return;

            int playerCount = Math.Min(_players.Length, 2);
            int p;

            for (p = 0; p < playerCount; p++)
            {
                Beam.Player player = _players[p];
                if (player == null || player.PlayerCamera == null || player.PlayerCamera.Camera == null)
                    continue;

                Camera camera = player.PlayerCamera.Camera;
                bool binocularActive = IsPlayerUsingBinoculars(player, camera, p);
                float maxVisualDistance = binocularActive
                    ? BinocularVisualDiscoveryDistance
                    : NormalVisualDiscoveryDistance;

                // Never discover geometry beyond what this camera could render at all.
                // This is an additional guard against physics colliders existing farther
                // away than the currently visible world.
                if (camera.farClipPlane > 10.0f)
                    maxVisualDistance = Mathf.Min(maxVisualDistance, camera.farClipPlane * 0.98f);

                float holdSeconds = binocularActive
                    ? BinocularVisualHoldSeconds
                    : NormalVisualHoldSeconds;


                Vector3 playerPosition = player.transform.position;
                bool worldVisibleToPlayer = p >= _views.Length || !_views[p].Open;

                int z;
                for (z = 0; z < zones.Length; z++)
                {
                    Zone zone = zones[z];
                    if (zone == null || IsHiddenBiome(zone) || IsZoneDiscovered(zone))
                        continue;

                    // Hard safety fallback: if the game itself considers the zone visited,
                    // the survivors certainly know that the island exists.
                    if (zone.HasVisited)
                    {
                        MarkZoneDiscovered(zone);
                        RemoveVisualDiscoveryProgress(p, zone);
                        continue;
                    }

                    float horizontalDistance = HorizontalDistance(playerPosition, zone.transform.position);

                    // Very-close fallback only. This prevents a landed-on island from
                    // remaining unknown if its terrain/colliders behave unusually.
                    if (horizontalDistance <= CloseRangeDiscoveryDistance)
                    {
                        MarkZoneDiscovered(zone);
                        RemoveVisualDiscoveryProgress(p, zone);
                        continue;
                    }

                    // Do not count the hidden world behind the paper map as "seen".
                    if (!worldVisibleToPlayer || horizontalDistance > maxVisualDistance)
                    {
                        RemoveStaleVisualDiscoveryProgress(p, zone);
                        continue;
                    }

                    Vector3 visibleProbe;
                    int evidenceCount;
                    bool actuallyVisible = binocularActive
                        ? IsZoneVisibleThroughBinoculars(zone, camera, player, maxVisualDistance, out visibleProbe, out evidenceCount)
                        : IsZoneVisibleNormally(zone, camera, player, out visibleProbe, out evidenceCount);

                    if (!actuallyVisible)
                    {
                        RemoveStaleVisualDiscoveryProgress(p, zone);
                        continue;
                    }

                    string key = GetZoneKey(zone);
                    VisualDiscoveryProgress progress;
                    if (!_visualDiscoveryProgress[p].TryGetValue(key, out progress))
                    {
                        progress = new VisualDiscoveryProgress();
                        progress.LastSeenAt = Time.unscaledTime;
                        progress.SeenSeconds = DiscoverySampleInterval;
                        progress.Binocular = binocularActive;
                        _visualDiscoveryProgress[p][key] = progress;
                    }
                    else
                    {
                        float gap = Time.unscaledTime - progress.LastSeenAt;
                        if (gap <= DiscoverySampleInterval * 2.25f)
                            progress.SeenSeconds += Mathf.Max(DiscoverySampleInterval, gap);
                        else
                            progress.SeenSeconds = DiscoverySampleInterval;

                        progress.LastSeenAt = Time.unscaledTime;
                        progress.Binocular = binocularActive;
                    }


                    if (progress.SeenSeconds >= holdSeconds)
                    {
                        MarkZoneDiscovered(zone);
                        _visualDiscoveryProgress[p].Remove(key);

                        Logger.LogInfo(
                            "Island visually discovered by P" + (p + 1) +
                            ": " + key +
                            " | " + (binocularActive ? "binocular LOS" : "normal LOS") +
                            " | evidence " + evidenceCount +
                            " | distance " + horizontalDistance.ToString("0") + " m");
                    }
                }
            }
        }

        private void ResetVisualDiscoveryTrackersIfWorldChanged()
        {
            string seed = ReadWorldSeed();
            if (String.Equals(seed, _visualDiscoverySeed, StringComparison.Ordinal))
                return;

            _visualDiscoverySeed = seed;
            _visualDiscoveryProgress[0].Clear();
            _visualDiscoveryProgress[1].Clear();
        }

        private void RemoveVisualDiscoveryProgress(int playerIndex, Zone zone)
        {
            if (playerIndex < 0 || playerIndex >= _visualDiscoveryProgress.Length || zone == null)
                return;

            _visualDiscoveryProgress[playerIndex].Remove(GetZoneKey(zone));
        }

        private void RemoveStaleVisualDiscoveryProgress(int playerIndex, Zone zone)
        {
            if (playerIndex < 0 || playerIndex >= _visualDiscoveryProgress.Length || zone == null)
                return;

            string key = GetZoneKey(zone);
            VisualDiscoveryProgress progress;
            if (!_visualDiscoveryProgress[playerIndex].TryGetValue(key, out progress))
                return;

            // A brief one-sample miss does not destroy progress, but losing the island
            // for more than roughly half a second requires a fresh observation.
            if (Time.unscaledTime - progress.LastSeenAt > DiscoverySampleInterval * 2.25f)
                _visualDiscoveryProgress[playerIndex].Remove(key);
        }

        private bool IsPlayerUsingBinoculars(
            Beam.Player player,
            Camera camera,
            int playerIndex)
        {
            if (player == null || camera == null || playerIndex < 0 || playerIndex >= _maxObservedCameraFov.Length)
                return false;

            // Prefer an explicit active hierarchy name if the held binocular model is
            // parented under the player/camera on this game build.
            if (ContainsActiveBinocularObject(player.transform))
            {
                UpdateObservedFovBaseline(playerIndex, camera.fieldOfView);
                return true;
            }

            float currentFov = camera.fieldOfView;
            UpdateObservedFovBaseline(playerIndex, currentFov);
            float baseline = _maxObservedCameraFov[playerIndex];

            if (baseline > 0.01f &&
                currentFov <= baseline * BinocularFovRatio &&
                baseline - currentFov >= BinocularMinFovDelta)
            {
                return true;
            }

            return false;
        }

        private void UpdateObservedFovBaseline(int playerIndex, float currentFov)
        {
            if (playerIndex < 0 || playerIndex >= _maxObservedCameraFov.Length)
                return;

            if (currentFov <= 1.0f || currentFov >= 179.0f)
                return;

            if (_maxObservedCameraFov[playerIndex] <= 0.01f ||
                currentFov > _maxObservedCameraFov[playerIndex])
            {
                _maxObservedCameraFov[playerIndex] = currentFov;
            }
        }

        private static bool ContainsActiveBinocularObject(Transform root)
        {
            if (root == null)
                return false;

            try
            {
                Transform[] children = root.GetComponentsInChildren<Transform>(true);
                int i;
                for (i = 0; i < children.Length; i++)
                {
                    Transform child = children[i];
                    if (child == null || child.gameObject == null || !child.gameObject.activeInHierarchy)
                        continue;

                    string name = child.name ?? String.Empty;
                    if (name.IndexOf("BINOC", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private bool IsZoneVisibleThroughBinoculars(
            Zone zone,
            Camera camera,
            Beam.Player player,
            float maxVisualDistance,
            out Vector3 visibleProbe,
            out int evidenceCount)
        {
            visibleProbe = Vector3.zero;
            evidenceCount = 0;

            if (zone == null || camera == null || player == null)
                return false;

            float maxRayDistance = maxVisualDistance + BinocularRaycastExtraDistance;
            int yi;
            int xi;

            for (yi = 0; yi < BinocularViewportSamples.Length; yi++)
            {
                for (xi = 0; xi < BinocularViewportSamples.Length; xi++)
                {
                    Vector3 viewportPoint = new Vector3(
                        BinocularViewportSamples[xi],
                        BinocularViewportSamples[yi],
                        0.0f);

                    Ray ray = camera.ViewportPointToRay(viewportPoint);
                    RaycastHit hit;
                    if (!TryGetFirstWorldRaycastHit(ray, maxRayDistance, player, out hit))
                        continue;

                    if (!DoesRaycastHitBelongToZone(hit, zone))
                        continue;

                    evidenceCount++;
                    visibleProbe = hit.point;

                    if (evidenceCount >= BinocularRequiredRayHits)
                        return true;
                }
            }

            return false;
        }

        private bool IsZoneVisibleNormally(
            Zone zone,
            Camera camera,
            Beam.Player player,
            out Vector3 visibleProbe,
            out int evidenceCount)
        {
            visibleProbe = Vector3.zero;
            evidenceCount = 0;

            if (zone == null || camera == null || player == null)
                return false;

            UnityEngine.Terrain terrain = null;
            try
            {
                terrain = zone.Terrain;
            }
            catch
            {
                terrain = null;
            }

            // If the terrain is not loaded there is no trustworthy visual evidence.
            // Close-range/HasVisited fallbacks handle that case without phantom reveals.
            if (terrain == null || terrain.terrainData == null)
                return false;

            if (TryTerrainProbeVisible(zone, terrain, camera, player, 0.50f, 0.50f, out visibleProbe)) { evidenceCount = 1; return true; }
            if (TryTerrainProbeVisible(zone, terrain, camera, player, 0.32f, 0.50f, out visibleProbe)) { evidenceCount = 1; return true; }
            if (TryTerrainProbeVisible(zone, terrain, camera, player, 0.68f, 0.50f, out visibleProbe)) { evidenceCount = 1; return true; }
            if (TryTerrainProbeVisible(zone, terrain, camera, player, 0.50f, 0.32f, out visibleProbe)) { evidenceCount = 1; return true; }
            if (TryTerrainProbeVisible(zone, terrain, camera, player, 0.50f, 0.68f, out visibleProbe)) { evidenceCount = 1; return true; }
            if (TryTerrainProbeVisible(zone, terrain, camera, player, 0.35f, 0.35f, out visibleProbe)) { evidenceCount = 1; return true; }
            if (TryTerrainProbeVisible(zone, terrain, camera, player, 0.65f, 0.35f, out visibleProbe)) { evidenceCount = 1; return true; }
            if (TryTerrainProbeVisible(zone, terrain, camera, player, 0.35f, 0.65f, out visibleProbe)) { evidenceCount = 1; return true; }
            if (TryTerrainProbeVisible(zone, terrain, camera, player, 0.65f, 0.65f, out visibleProbe)) { evidenceCount = 1; return true; }

            return false;
        }

        private bool TryTerrainProbeVisible(
            Zone zone,
            UnityEngine.Terrain terrain,
            Camera camera,
            Beam.Player player,
            float u,
            float v,
            out Vector3 visibleProbe)
        {
            visibleProbe = Vector3.zero;
            if (zone == null || terrain == null || terrain.terrainData == null || camera == null || player == null)
                return false;

            try
            {
                Vector3 origin = terrain.transform.position;
                Vector3 size = terrain.terrainData.size;
                float worldY = terrain.terrainData.GetInterpolatedHeight(u, v) + origin.y;

                Vector3 surfacePoint = new Vector3(
                    origin.x + size.x * u,
                    worldY + 0.75f,
                    origin.z + size.z * v);

                Vector3 screenProbe = surfacePoint + Vector3.up * 5.0f;
                if (!IsWorldProbeInsideNormalCamera(screenProbe, camera))
                    return false;

                Vector3 cameraPosition = camera.transform.position;
                Vector3 direction = surfacePoint - cameraPosition;
                float distance = direction.magnitude;
                if (distance <= 0.01f)
                    return false;

                Ray ray = new Ray(cameraPosition, direction / distance);
                RaycastHit hit;
                if (!TryGetFirstWorldRaycastHit(ray, distance + 20.0f, player, out hit))
                    return false;

                if (!DoesRaycastHitBelongToZone(hit, zone))
                    return false;

                visibleProbe = hit.point;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetFirstWorldRaycastHit(
            Ray ray,
            float maxDistance,
            Beam.Player player,
            out RaycastHit firstWorldHit)
        {
            firstWorldHit = new RaycastHit();

            RaycastHit[] hits;
            try
            {
                hits = Physics.RaycastAll(
                    ray,
                    maxDistance,
                    ~0,
                    QueryTriggerInteraction.Ignore);
            }
            catch
            {
                return false;
            }

            if (hits == null || hits.Length == 0)
                return false;

            Array.Sort(
                hits,
                delegate(RaycastHit a, RaycastHit b)
                {
                    return a.distance.CompareTo(b.distance);
                });

            int i;
            for (i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null)
                    continue;

                Transform hitTransform = collider.transform;
                if (player != null && IsTransformUnder(hitTransform, player.transform))
                    continue;

                firstWorldHit = hits[i];
                return true;
            }

            return false;
        }

        private bool DoesRaycastHitBelongToZone(RaycastHit hit, Zone zone)
        {
            if (hit.collider == null || zone == null)
                return false;

            Transform hitTransform = hit.collider.transform;
            if (IsTransformUnder(hitTransform, zone.transform))
                return true;

            UnityEngine.Terrain terrain = null;
            try
            {
                terrain = zone.Terrain;
            }
            catch
            {
                terrain = null;
            }

            if (terrain == null || terrain.terrainData == null)
                return false;

            if (IsTransformUnder(hitTransform, terrain.transform))
                return true;

            // Some spawned island props are not parented directly below the Zone.
            // Their hit point still lies inside this Zone's actual terrain footprint.
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            const float margin = 8.0f;

            return hit.point.x >= origin.x - margin &&
                   hit.point.x <= origin.x + size.x + margin &&
                   hit.point.z >= origin.z - margin &&
                   hit.point.z <= origin.z + size.z + margin;
        }

        private static bool IsTransformUnder(Transform child, Transform root)
        {
            if (child == null || root == null)
                return false;

            Transform current = child;
            int safety = 0;
            while (current != null && safety < 128)
            {
                if (current == root)
                    return true;

                current = current.parent;
                safety++;
            }

            return false;
        }

        private static bool IsWorldProbeInsideNormalCamera(Vector3 probe, Camera camera)
        {
            if (camera == null)
                return false;

            Vector3 viewport = camera.WorldToViewportPoint(probe);
            if (viewport.z <= 0.0f)
                return false;

            return viewport.x >= NormalViewportInset &&
                   viewport.x <= 1.0f - NormalViewportInset &&
                   viewport.y >= NormalViewportInset &&
                   viewport.y <= 1.0f - NormalViewportInset;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private sealed class VisualDiscoveryProgress
        {
            public float SeenSeconds;
            public float LastSeenAt;
            public bool Binocular;
        }
    }
}
