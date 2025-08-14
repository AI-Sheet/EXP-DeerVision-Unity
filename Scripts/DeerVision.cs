
using UnityEngine;
using System.Collections.Generic;
using System;

public class DeerVision : MonoBehaviour
{
    // ====== КОНСТАНТЫ И НАСТРОЙКИ ======
    private const float LAZY_DELETE_CONFIDENCE_THRESHOLD = 0.15f;
    private const float RECENT_POSITIONS_WINDOW = 1.7f;
    private const float RECENT_POSITIONS_CLEANUP_INTERVAL = 30f; // seconds
    private const float KD_DELETED_REBUILD_THRESHOLD = 0.35f; // rebuild if >35% nodes deleted
    public List<ObjectFeature> foundFeatures { get; private set; }

    // ====== ПАРАМЕТРЫ НАСТРАИВАЕМЫЕ В ИНСПЕКТОРЕ ======

    [Header("Vision")]
    public float fieldOfViewAngle = 250f; // угол обзора в градусах
    public Transform headTransform;
    public int fovGridSize = 21;
    public float gridCellSize = 2f;
    public float visionRadius = 15f;
    public int maxMemoryItems = 512;

    [Header("Vision/Fear")]
    public float uncertaintyPenalty = 0.35f; // штраф при плохой видимости

    [Header("Raycast/Sensing")]
    public int senseRayPerRing = 16;
    public int ringCount = 6;
    public float objectSampleRadius = 0.45f;
    public LayerMask sensingMask;  // только нужные объекты
    public float scanInterval = 0.1f; // Интервал между сканами (сек), для оптимизации

    [Header("Memory/Semantics")]
    public float maxMemoryAge = 240f;
    public float minConfidence = 0.03f;
    public float forgivenessRate = 0.025f;
    public int maxEventsPerObject = 16;

    [Header("KD-Tree")]
    public int rebalanceEveryNUpdates = 400;
    public int maxDepth = 32; // ограничение глубины дерева (stack safe)
    public float kdQueryRange = 2.25f; // radius поиска похожих объектов
    public float cellQuantLow = 1.0f, cellQuantHigh = 3.0f; // динамика spatial bucket

    [Header("FeatureDistance")]
    public float[] featureWeights = new float[] { 1, 1.5f, 1, 2, 1.5f, 1, 1.3f, 1, 1, 1 };

    [Header("RL Head/Focus")]
    [Range(-180f, 180f)] public float headYaw = 0f;      // Вращение по горизонтали (лево/право)
    [Range(-20f, 20f)] public float headPitch = 0f;    // Вращение головы вверх/вниз (новое)
    [Range(10f, 60f)] public float focusAngle = 40f;
    [Range(60f, 180f)] public float peripheryAngle = 120f;
    public float sensorPitchMin = -30f;
    public float sensorPitchMax = 30f;
    public float peripheryDist = 15f;

    [Serializable]
    public class MemoryItem
    {
        public Vector3 position;
        public float confidence;
        public float lastSeen;
        public int type = 0;
    }
    [NonSerialized]
    public List<MemoryItem> rayMemory = new List<MemoryItem>();

    [Header("Perf")]
    public bool cacheObservation = true; // ускорение GetObservation

    // ====== ВНУТРЕННИЕ СТРУКТУРЫ ДАННЫХ ======

    [Serializable]
    public class SemanticStats
    {
        public int nearAttackCount = 0, nearWolfCount = 0;
        public float lastDangerousSeen = -1000, lastAttack = -1000;
        public List<float> attackTimestamps = new List<float>();
        public float safeExperience = 0f;
    }

    [Serializable]
    public class ObjectFeature
    {
        public Vector3 position;
        public float seenTime;
        public float confidence;
        public float[] features;
        public Bounds bounds;
        public float shapeComplexity;
        public SemanticStats semantic = new SemanticStats();
        public int motionSignatureHash;
        public int shapeSignatureHash;
        public bool isDeleted = false; // lazy deletion
        public int type = 0;

        public void CopyFrom(ObjectFeature src)
        {
            position = src.position; seenTime = src.seenTime;
            confidence = src.confidence; bounds = src.bounds;
            shapeComplexity = src.shapeComplexity; isDeleted = src.isDeleted;
            if (features == null || features.Length != src.features.Length)
                features = new float[src.features.Length];
            Array.Copy(src.features, features, src.features.Length);
            semantic = src.semantic;
            motionSignatureHash = src.motionSignatureHash;
            shapeSignatureHash = src.shapeSignatureHash;
            type = src.type;
        }
    }

    public class KDTreeNode
    {
        public ObjectFeature data;
        public KDTreeNode left, right;
        public bool verticalSplit;
        public float splitValue;
        public bool isDeleted = false;
        public KDTreeNode(ObjectFeature d, bool v, float sv)
        {
            data = d; verticalSplit = v; splitValue = sv;
            isDeleted = false;
        }
    }

    private KDTreeNode kdRoot = null;
    private int kdCount = 0, kdDeleted = 0, updatesSinceRebalance = 0;
    private float cellQuant = 2.0f; // динамично регулируется

    private Dictionary<int, List<(Vector3, float)>> recentPositions = new Dictionary<int, List<(Vector3, float)>>();
    private float lastRecentPositionsCleanup = 0f;

    private float[,] obsCache = null;
    private bool obsDirty = true;

    private float lastScanTime = -1000f;

    // ====== ОСНОВНОЙ ЦИКЛ ======
    void Update()
    {
        float now = Time.time;

        AutoAdjustCellQuant();

        if (now - lastScanTime >= scanInterval)
        {
            ScanFieldOfView();
            lastScanTime = now;
        }

        KdAgingAndLazyDelete(kdRoot, now, 0);
        SemanticDecayAndForgiveness();

        updatesSinceRebalance++;
        if (updatesSinceRebalance >= rebalanceEveryNUpdates ||
            GetKDDepth(kdRoot) > maxDepth ||
            (kdCount > 0 && kdDeleted / (float)kdCount > KD_DELETED_REBUILD_THRESHOLD))
        {
            RebuildKDTree();
            updatesSinceRebalance = 0;
        }

        obsDirty = true;

        if (now - lastRecentPositionsCleanup > RECENT_POSITIONS_CLEANUP_INTERVAL)
        {
            CleanupRecentPositions(now);
            lastRecentPositionsCleanup = now;
        }
    }

    private void AutoAdjustCellQuant()
    {
        int count = kdCount - kdDeleted;
        float area = Mathf.PI * visionRadius * visionRadius + 1e-2f;
        float density = count / area;

        if (density > 1.5f)
            cellQuant = Mathf.Max(cellQuantLow, cellQuant - 0.08f);
        else if (density < 0.35f)
            cellQuant = Mathf.Min(cellQuantHigh, cellQuant + 0.04f);

        int depth = GetKDDepth(kdRoot);
        if (depth > maxDepth * 0.85f)
            cellQuant = Mathf.Max(cellQuantLow, cellQuant - 0.12f);
        else if (depth < maxDepth * 0.5f)
            cellQuant = Mathf.Min(cellQuantHigh, cellQuant + 0.07f);
    }

    // ====== СКАНИРОВАНИЕ ПОЛЯ ЗРЕНИЯ ======
    void ScanFieldOfView()
    {
        int totalRays = ringCount * senseRayPerRing;
        foundFeatures = new List<ObjectFeature>(totalRays);
        float now = Time.time;
        Vector3 origin = headTransform != null ? headTransform.position : transform.position + Vector3.up * 1.1f;

        // Получаем ориентацию головы (или тела, если головы нет)
        Quaternion headRot = headTransform != null ? headTransform.rotation : transform.rotation;

        for (int ring = 1; ring <= ringCount; ++ring)
        {
            float ringFrac = ring / (float)ringCount;
            float radius = visionRadius * ringFrac;
            float elevationAngle = Mathf.Lerp(sensorPitchMin, sensorPitchMax, ringFrac);

            for (int i = 0; i < senseRayPerRing; ++i)
            {
                // Ограничиваем азимут сектором fieldOfViewAngle
                float azimuth = -fieldOfViewAngle / 2f + fieldOfViewAngle * i / (senseRayPerRing - 1);
                Quaternion localRayRot = Quaternion.Euler(elevationAngle, azimuth, 0);
                Vector3 dir = headRot * localRayRot * Vector3.forward;

                RaycastHit hit;
                if (Physics.SphereCast(origin, objectSampleRadius, dir, out hit, radius, sensingMask.value))
                {
                    GameObject obj = hit.collider.gameObject;
                    ObjectFeature feature = ExtractFeaturesForObject(obj, hit.point, hit.collider.bounds, now);

                    // --- Определение типа по признакам ---
                    // Сначала проверяем явные маркеры еды
                    bool isFood = obj.CompareTag("Food") || obj.GetComponent<FoodZone>() != null;
                    
                    if (isFood)
                    {
                        feature.type = 2; // ВСЕГДА еда, если есть тег или компонент
                        Debug.Log($"[DeerVision] Обнаружена еда по тегу/компоненту: {obj.name}");
                    }
                    else
                    {
                        // Иначе используем старую логику
                        bool isSmall = feature.bounds.size.x < 1.2f && feature.bounds.size.z < 1.2f && feature.bounds.size.y < 1.2f;
                        bool isBig = feature.bounds.size.x > 1.5f || feature.bounds.size.z > 1.5f;
                        bool isTallThin = feature.bounds.size.y > 3.0f && Mathf.Min(feature.bounds.size.x, feature.bounds.size.z) < 0.7f;
                        bool isStatic = feature.features != null && feature.features.Length > 7 && feature.features[7] < 0.15f;
                        bool isMoving = feature.features != null && feature.features.Length > 6 && feature.features[6] > 0.7f;
                        bool isFast = feature.features != null && feature.features.Length > 7 && feature.features[7] > 1.0f;
                        bool isComplex = feature.shapeComplexity > 1.5f;

                        if (isTallThin)
                            feature.type = 1;
                        else if (isBig && isStatic)
                            feature.type = 1;
                        else if (isBig && !isSmall)
                            feature.type = 1;
                        else if (isSmall && isComplex && isMoving)
                            feature.type = 3;
                        else if (isSmall && !isFast && !isBig)
                            feature.type = 2;
                        else
                            feature.type = 0;
                    }

                    feature.shapeSignatureHash = ShapeSignatureHash(feature.features);
                    feature.motionSignatureHash = MotionSignatureHash(obj, feature, feature.shapeSignatureHash);
                    foundFeatures.Add(feature);
                }
            }
        }
        // --- Новый блок: проверка исчезновения объектов из памяти ---
        float vanishAngle = focusAngle * 0.6f;
        float vanishRadius = visionRadius * 0.98f;
        List<MemoryItem> toRemove = new List<MemoryItem>();
        foreach (var m in rayMemory)
        {
            Vector3 toObj = m.position - origin;
            float dist = toObj.magnitude;
            float angle = Vector3.Angle(Quaternion.Euler(0, headYaw, 0) * Vector3.forward, toObj);
            if (dist < vanishRadius && angle < vanishAngle)
            {
                bool found = false;
                foreach (var f in foundFeatures)
                {
                    if (f.type == m.type && Vector3.Distance(f.position, m.position) < 1.0f)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    // Плавное экспоненциальное затухание уверенности
                    m.confidence *= 0.98f; // Было 0.25f, теперь медленнее
                    // Можно добавить минимальное время памяти:
                    if ((Time.time - m.lastSeen) > 3.0f && m.confidence < 0.05f)
                        toRemove.Add(m);
                }
            }
        }
        foreach (var m in toRemove)
            rayMemory.Remove(m);

        if (DeerCognitiveMap.Instance != null && foundFeatures != null && foundFeatures.Count > 0)
        {
            List<float[]> featureList = new List<float[]>(foundFeatures.Count);
            foreach (var f in foundFeatures)
                featureList.Add(f.features);
            float meanConf = 0f;
            foreach (var f in foundFeatures) meanConf += f.confidence;
            meanConf /= foundFeatures.Count;
            DeerCognitiveMap.Instance.RegisterObservation3D(transform.position, featureList, meanConf);
        }

        UpdateRayMemory(foundFeatures, now);

        foreach (var f in foundFeatures)
            AddOrUpdateMemoryKdTree(f, now);
    }

    public void RemoveMemoryByPosition(Vector3 pos, int type = 2, float maxDist = 1.0f)
    {
        rayMemory.RemoveAll(m => m.type == type && Vector3.Distance(m.position, pos) < maxDist);
    }

    public float[] GetMemoryObservation()
    {
        int memLimit = 4;
        float[] obs = new float[memLimit * 5];
        int idx = 0;
        foreach (var m in rayMemory)
        {
            if (idx >= memLimit) break;
            obs[idx * 5 + 0] = m.position.x;
            obs[idx * 5 + 1] = m.position.z;
            obs[idx * 5 + 2] = m.type;
            obs[idx * 5 + 3] = m.confidence;
            obs[idx * 5 + 4] = Mathf.Clamp01((Time.time - m.lastSeen) / 30f);
            idx++;
        }
        return obs;
    }

    ObjectFeature ExtractFeaturesForObject(GameObject obj, Vector3 hitPoint, Bounds bounds, float now)
    {
        Vector3 size = bounds.size;
        float volume = size.x * size.y * size.z;
        float maxDim = Mathf.Max(size.x, size.y, size.z);
        float compactness = volume / (maxDim * maxDim * maxDim + 1e-6f);
        float contour = Mathf.Max(size.x, size.z) / (size.y + 1e-3f);

        float meanVelocity = 0, meanDirX = 0, meanDirZ = 0;
        bool isMoving = false;
        int insID = obj.GetInstanceID();
        if (!recentPositions.ContainsKey(insID)) recentPositions[insID] = new List<(Vector3, float)>();
        var lst = recentPositions[insID];
        lst.Add((hitPoint, now));
        float window = RECENT_POSITIONS_WINDOW;
        lst.RemoveAll(e => now - e.Item2 > window);
        if (lst.Count > 3)
        {
            for (int i = 1; i < lst.Count; ++i)
            {
                Vector3 d = lst[i].Item1 - lst[i - 1].Item1;
                meanDirX += d.x;
                meanDirZ += d.z;
                meanVelocity += d.magnitude;
            }
            float count = lst.Count - 1;
            meanDirX /= count; meanDirZ /= count; meanVelocity /= count;
            isMoving = meanVelocity > 0.25f;
        }
        float movingF = isMoving ? 1f : 0f;

        float shapeCmplx = compactness * contour;
        float[] feats = new float[] {
            size.x, size.y, size.z,
            volume,
            compactness,
            shapeCmplx,
            movingF,
            meanVelocity,
            meanDirX,
            meanDirZ
        };

        var f = new ObjectFeature();
        f.position = hitPoint; f.seenTime = now; f.confidence = 1f;
        f.bounds = bounds; f.shapeComplexity = shapeCmplx; f.features = feats;
        return f;
    }

    void AddOrUpdateMemoryKdTree(ObjectFeature feature, float now)
    {
        List<KDTreeNode> nearNodes = new List<KDTreeNode>();
        KdKnnSearch(kdRoot, new Vector2(feature.position.x, feature.position.z), kdQueryRange, nearNodes, 0);

        KDTreeNode best = null;
        float minD = 1e6f, minF = 0.5f;
        foreach (var node in nearNodes)
        {
            if (node.isDeleted || node.data.isDeleted) continue;
            float d = (node.data.position - feature.position).sqrMagnitude;
            float fd = FeatureDistance(node.data, feature, featureWeights);
            if (fd < minF && d < minD && node.data.shapeSignatureHash == feature.shapeSignatureHash)
            {
                best = node; minF = fd; minD = d;
            }
        }
        if (best != null)
        {
            bool stable = (best.data.shapeSignatureHash == feature.shapeSignatureHash) && (best.data.type == feature.type);
            if (stable)
            {
                best.data.seenTime = now;
                best.data.confidence = Mathf.Clamp01(best.data.confidence + 0.12f);
            }
            else
            {
                feature.semantic = best.data.semantic;
                best.data.seenTime = now;
                best.data.confidence = Mathf.Clamp01(best.data.confidence + 0.26f);
                best.data.CopyFrom(feature);
            }
        }
        else
        {
            if (kdCount - kdDeleted > maxMemoryItems || GetKDDepth(kdRoot) > maxDepth)
                LazyDeleteOldest(kdRoot);
            KdInsert(ref kdRoot, feature, 0, false);
            kdCount++;
        }
    }

    void KdInsert(ref KDTreeNode node, ObjectFeature f, int depth, bool isDeleted)
    {
        if (node == null)
        {
            node = new KDTreeNode(f, depth % 2 == 0, (depth % 2 == 0) ? f.position.x : f.position.z) { isDeleted = isDeleted };
            return;
        }
        float key = node.verticalSplit ? f.position.x : f.position.z;
        if (key < node.splitValue) KdInsert(ref node.left, f, depth + 1, isDeleted);
        else KdInsert(ref node.right, f, depth + 1, isDeleted);
    }

    void KdKnnSearch(KDTreeNode node, Vector2 pos, float range, List<KDTreeNode> found, int depth)
    {
        if (node == null) return;
        if (!node.isDeleted)
        {
            Vector2 pt = new Vector2(node.data.position.x, node.data.position.z);
            if ((pt - pos).sqrMagnitude < range * range) found.Add(node);
        }
        float key = node.verticalSplit ? pos.x : pos.y;
        if (key - range < node.splitValue) KdKnnSearch(node.left, pos, range, found, depth + 1);
        if (key + range > node.splitValue) KdKnnSearch(node.right, pos, range, found, depth + 1);
    }

    void KdCollectAll(KDTreeNode node, List<ObjectFeature> res)
    {
        if (node == null) return;
        if (!node.isDeleted) res.Add(node.data);
        if (node.left != null) KdCollectAll(node.left, res);
        if (node.right != null) KdCollectAll(node.right, res);
    }

    int GetKDDepth(KDTreeNode node)
    {
        if (node == null) return 0;
        int l = GetKDDepth(node.left), r = GetKDDepth(node.right);
        return 1 + Mathf.Max(l, r);
    }

    void KdAgingAndLazyDelete(KDTreeNode node, float now, int depth)
    {
        if (node == null) return;
        if (!node.isDeleted)
        {
            float dt = now - node.data.seenTime;
            if (dt > maxMemoryAge || node.data.confidence < minConfidence)
            {
                node.isDeleted = node.data.isDeleted = true;
                kdDeleted++;
            }
        }
        if (node.left != null) KdAgingAndLazyDelete(node.left, now, depth + 1);
        if (node.right != null) KdAgingAndLazyDelete(node.right, now, depth + 1);
    }

    void LazyDeleteOldest(KDTreeNode node)
    {
        if (node == null) return;
        if (!node.isDeleted)
        {
            if (node.data.confidence < LAZY_DELETE_CONFIDENCE_THRESHOLD)
            {
                node.isDeleted = node.data.isDeleted = true; kdDeleted++;
                return;
            }
        }
        if (node.left != null) LazyDeleteOldest(node.left);
        if (node.right != null) LazyDeleteOldest(node.right);
    }

    void RebuildKDTree()
    {
        List<ObjectFeature> all = new List<ObjectFeature>();
        KdCollectAll(kdRoot, all);
        kdRoot = BuildKDFromSorted(all, 0);
        kdCount = all.Count;
        kdDeleted = 0;
    }

    KDTreeNode BuildKDFromSorted(List<ObjectFeature> arr, int depth)
    {
        if (arr == null || arr.Count == 0) return null;
        int n = arr.Count;
        int axis = depth % 2;
        arr.Sort((a, b) => axis == 0 ? a.position.x.CompareTo(b.position.x) : a.position.z.CompareTo(b.position.z));
        int median = n / 2;
        var node = new KDTreeNode(arr[median], axis == 0, axis == 0 ? arr[median].position.x : arr[median].position.z);
        node.left = BuildKDFromSorted(arr.GetRange(0, median), depth + 1);
        if (median + 1 < n)
            node.right = BuildKDFromSorted(arr.GetRange(median + 1, n - (median + 1)), depth + 1);
        return node;
    }

    public List<float> CollectFocusObservation()
    {
        List<float> obs = new List<float>();
        foreach (var m in rayMemory)
        {
            Vector3 toObj = m.position - transform.position;
            float dist = toObj.magnitude;
            float angle = Vector3.Angle(Quaternion.Euler(0, headYaw, 0) * transform.forward, toObj);
            if (angle < focusAngle * 0.5f && dist < peripheryDist)
            {
                obs.Add(m.type);
                obs.Add(dist / peripheryDist);
                obs.Add(angle / focusAngle);
            }
        }
        int focusRayLimit = 8;
        while (obs.Count < focusRayLimit * 3)
            obs.Add(0f);
        return obs;
    }

    public MemoryItem GetClosestMemory(int type)
    {
        MemoryItem best = null;
        float minDist = float.MaxValue;
        foreach (var m in rayMemory)
        {
            if (m.type != type) continue;
            float d = Vector3.Distance(transform.position, m.position);
            if (d < minDist)
            {
                minDist = d;
                best = m;
            }
        }
        return best;
    }

    public void UpdateRayMemory(List<ObjectFeature> foundFeatures, float now)
    {
        rayMemory.RemoveAll(m => now - m.lastSeen > 60f || m.confidence < 0.01f);

        foreach (var f in foundFeatures)
        {
            int type = f.type;

            var existing = rayMemory.Find(m => Vector3.Distance(m.position, f.position) < 0.7f && m.type == type);
            if (existing != null)
            {
                existing.confidence = Mathf.Clamp01(existing.confidence + 0.2f);
                existing.lastSeen = now;
            }
            else
            {
                rayMemory.Add(new MemoryItem
                {
                    position = f.position,
                    type = type,
                    confidence = f.confidence,
                    lastSeen = now
                });
            }
        }
    }

    void SemanticDecayAndForgiveness()
    {
        List<ObjectFeature> allObjs = new List<ObjectFeature>();
        KdCollectAll(kdRoot, allObjs);
        float now = Time.time;
        foreach (var obj in allObjs)
        {
            if (obj.semantic.nearAttackCount > 0)
            {
                float sinceAttack = now - obj.semantic.lastAttack;
                if (sinceAttack > 24)
                {
                    obj.semantic.nearAttackCount = Mathf.Max(0, obj.semantic.nearAttackCount - 1);
                    obj.confidence = Mathf.Clamp01(obj.confidence + forgivenessRate);
                }
            }
            else
            {
                obj.semantic.safeExperience += Time.deltaTime;
                obj.confidence = Mathf.Clamp01(obj.confidence + forgivenessRate * (1f - obj.confidence));
            }
        }
    }

    public void RegisterDangerEvent(Vector3 pos, bool isAttack, float eventTime = -1f)
    {
        List<KDTreeNode> found = new List<KDTreeNode>();
        KdKnnSearch(kdRoot, new Vector2(pos.x, pos.z), kdQueryRange, found, 0);
        float now = eventTime > 0 ? eventTime : Time.time;
        foreach (var node in found)
        {
            var obj = node.data;
            if (node.isDeleted) continue;
            obj.semantic.nearWolfCount += 1;
            if (isAttack)
            {
                obj.semantic.nearAttackCount += 1;
                obj.semantic.lastAttack = now;
                obj.semantic.attackTimestamps.Add(now);
                obj.confidence = Mathf.Max(minConfidence, obj.confidence * 0.57f);
            }
            obj.semantic.lastDangerousSeen = now;
            while (obj.semantic.attackTimestamps.Count > maxEventsPerObject)
                obj.semantic.attackTimestamps.RemoveAt(0);
        }
    }

    void AddPhantomDanger(Vector3 pos, float now, float visibleFraction)
    {
        ObjectFeature fake = new ObjectFeature
        {
            position = pos,
            seenTime = now,
            confidence = uncertaintyPenalty * (1f - visibleFraction),
            features = new float[featureWeights.Length],
            bounds = new Bounds(pos, Vector3.one),
            shapeComplexity = 0,
            semantic = new SemanticStats()
        };
        KdInsert(ref kdRoot, fake, 0, false);
        kdCount++;
    }

    float FeatureDistance(ObjectFeature a, ObjectFeature b, float[] weights)
    {
        int n = Mathf.Min(a.features.Length, b.features.Length);
        float sum = 0f;
        for (int i = 0; i < n; ++i)
        {
            float d = (a.features[i] - b.features[i]);
            d *= weights[Mathf.Min(i, weights.Length - 1)];
            sum += d * d;
        }
        return Mathf.Sqrt(sum / n);
    }

    int ShapeSignatureHash(float[] features)
    {
        unchecked
        {
            int h = 5381;
            for (int i = 0; i < 6 && i < features.Length; ++i)
                h = ((h << 5) + h) ^ Mathf.RoundToInt(features[i] * 1000f);
            return h;
        }
    }

    int MotionSignatureHash(GameObject obj, ObjectFeature feature, int shapeHash)
    {
        int insID = obj.GetInstanceID();
        if (!recentPositions.ContainsKey(insID))
            recentPositions[insID] = new List<(Vector3, float)>();
        var lst = recentPositions[insID];
        lst.Add((feature.position, Time.time));
        float window = RECENT_POSITIONS_WINDOW;
        lst.RemoveAll(e => Time.time - e.Item2 > window);

        Vector3 avgDelta = Vector3.zero;
        if (lst.Count >= 2)
        {
            for (int i = 1; i < lst.Count; ++i)
                avgDelta += (lst[i].Item1 - lst[i - 1].Item1);
            avgDelta /= (lst.Count - 1);
        }
        int h = shapeHash;
        h ^= Mathf.RoundToInt(avgDelta.x * 1000f);
        h ^= Mathf.RoundToInt(avgDelta.z * 1000f);
        h ^= Mathf.RoundToInt(window * 1000f);
        return h;
    }

    void CleanupRecentPositions(float now)
    {
        List<int> toRemove = new List<int>();
        foreach (var kvp in recentPositions)
        {
            kvp.Value.RemoveAll(e => now - e.Item2 > RECENT_POSITIONS_WINDOW);
            if (kvp.Value.Count == 0)
                toRemove.Add(kvp.Key);
        }
        foreach (var key in toRemove)
            recentPositions.Remove(key);
    }

    public float[,] GetObservation()
    {
        if (cacheObservation && !obsDirty && obsCache != null)
            return obsCache;

        List<ObjectFeature> found = new List<ObjectFeature>();
        KdCollectAll(kdRoot, found);
        int fdim = found.Count > 0 ? found[0].features.Length + 5 : featureWeights.Length + 5;
        float[,] obs = new float[found.Count, fdim];
        for (int i = 0; i < found.Count; ++i)
        {
            obs[i, 0] = found[i].position.x;
            obs[i, 1] = found[i].position.z;
            int k = 2;
            for (int j = 0; j < found[i].features.Length; ++j) obs[i, k++] = found[i].features[j];
            obs[i, k++] = found[i].confidence;
            obs[i, k++] = found[i].shapeComplexity;
            obs[i, k++] = found[i].semantic.nearAttackCount;
            obs[i, k++] = found[i].semantic.nearWolfCount;
            obs[i, k++] = found[i].semantic.safeExperience;
        }
        if (cacheObservation)
        {
            obsCache = obs;
            obsDirty = false;
        }
        return obs;
    }
}
