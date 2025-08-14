
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Collections.Generic;

/// <summary>
/// Визуализатор исследования и восприятия агента:
/// - Показывает зоны, которые агент реально "увидел" (heatmap, квадраты, размер зависит от частоты наблюдения)
/// - Показывает направление взгляда (луч, угол)
/// - Показывает угол тела (forward)
/// - Показывает, как агент определяет предметы (ярлыки, confidence, тип/ярлык)
/// - Показывает объекты из памяти (камни, деревья, еда) с формой, временем, уверенностью, ярлыком
/// </summary>
public class DeerVisionDebugVisualizer : MonoBehaviour
{
    [Header("Ссылки")]
    public DeerVision deerVision;
    public Transform agentBody; // основной transform агента (для forward)
    public float cellSize = 2.0f;
    public int maxCellVisits = 30;
    public Color zoneColor = new Color(0.2f, 0.7f, 1f, 0.18f);
    public Color zoneHotColor = new Color(1f, 0.5f, 0.1f, 0.32f);
    public Color lookRayColor = new Color(0.9f, 1f, 0.2f, 0.7f);
    public Color bodyRayColor = new Color(0.2f, 0.9f, 0.2f, 0.7f);

    // Карта реально увиденных зон: cell -> visits
    private Dictionary<Vector2Int, int> visitedCells = new Dictionary<Vector2Int, int>();

    void Update()
    {
        // Обновляем heatmap по позиции тела агента
        if (agentBody != null)
        {
            Vector2Int cell = WorldToCell(agentBody.position, cellSize);
            if (!visitedCells.ContainsKey(cell))
                visitedCells[cell] = 1;
            else
                visitedCells[cell] = Mathf.Min(visitedCells[cell] + 1, maxCellVisits);
        }

        // (Опционально) также можно учитывать найденные объекты, если нужно
        if (deerVision != null && deerVision.foundFeatures != null)
        {
            foreach (var f in deerVision.foundFeatures)
            {
                Vector2Int cell = WorldToCell(f.position, cellSize);
                if (!visitedCells.ContainsKey(cell))
                    visitedCells[cell] = 1;
                else
                    visitedCells[cell] = Mathf.Min(visitedCells[cell] + 1, maxCellVisits);
            }
        }
    }

    void OnDrawGizmos()
    {
        DrawVisitedZones();
        DrawLookDirection();
        DrawBodyDirection();
        DrawObjectPerception();
        DrawMemoryObjects();
        DrawVisionRays();   // Новое: рисуем все сенсорные лучи
        DrawVisionRings();  // Новое: рисуем круги зрения
    }

    // Рисуем зоны реально увиденных объектов (heatmap)
    void DrawVisitedZones()
    {
        foreach (var kv in visitedCells)
        {
            Vector3 center = CellToWorld(kv.Key, cellSize);
            float scale = 1.0f + 0.5f * Mathf.Log(1 + kv.Value);
            Color c = Color.Lerp(zoneColor, zoneHotColor, Mathf.Clamp01(kv.Value / (float)maxCellVisits));
            Gizmos.color = c;
            Gizmos.DrawCube(center + Vector3.up * 0.05f, new Vector3(cellSize, 0.1f, cellSize) * scale);
#if UNITY_EDITOR
            Handles.color = Color.white;
            Handles.Label(center + Vector3.up * 0.3f, $"v={kv.Value}", EditorStyles.miniLabel);
#endif
        }
    }

    // Визуализация памяти агента (например, камни, деревья, их форма, тип, уверенность, время)
    void DrawMemoryObjects()
    {
        if (deerVision == null || deerVision.rayMemory == null) return;

        // --- Фильтрация дубликатов: рисуем только уникальные объекты по позиции и типу ---
        var uniqueMemory = new List<DeerVision.MemoryItem>();
        foreach (var mem in deerVision.rayMemory)
        {
            bool duplicate = false;
            foreach (var u in uniqueMemory)
            {
                if (Vector3.Distance(u.position, mem.position) < 0.5f && u.type == mem.type)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate)
                uniqueMemory.Add(mem);
        }

        foreach (var mem in uniqueMemory)
        {
            // Цвет по типу объекта
            Color c = GetTypeColor(mem.type, mem.confidence);
            Gizmos.color = c;

            // Нарисуем сферу в позиции памяти
            Gizmos.DrawSphere(mem.position + Vector3.up * 0.1f, 0.25f);

#if UNITY_EDITOR
            Handles.color = c;
            string label = $"[{GetTypeLabel(mem.type)}]\nc:{mem.confidence:0.00}\nt:{Time.time - mem.lastSeen:0.0}s";
            Handles.Label(mem.position + Vector3.up * 0.3f, label, EditorStyles.miniLabel);
#endif

            // --- Исправление: ищем ObjectFeature с похожей позицией, чтобы взять bounds ---
            var feature = FindFeatureByPosition(deerVision.foundFeatures, mem.position, 1.0f);
            if (feature != null && feature.bounds.size != Vector3.zero)
            {
                DrawBox(feature.bounds, c);
            }
        }
    }

    // Поиск ObjectFeature по позиции (с допуском)
    DeerVision.ObjectFeature FindFeatureByPosition(List<DeerVision.ObjectFeature> features, Vector3 pos, float maxDist = 0.7f)
    {
        if (features == null) return null;
        DeerVision.ObjectFeature best = null;
        float minDist = maxDist;
        foreach (var f in features)
        {
            float d = Vector3.Distance(f.position, pos);
            if (d < minDist)
            {
                minDist = d;
                best = f;
            }
        }
        return best;
    }

    // Вспомогательная функция для отрисовки box по Bounds
    void DrawBox(Bounds b, Color color)
    {
        Vector3 center = b.center;
        Vector3 size = b.size;
        Vector3 ext = size * 0.5f;
        Vector3[] corners = new Vector3[8];
        corners[0] = center + new Vector3(-ext.x, -ext.y, -ext.z);
        corners[1] = center + new Vector3(ext.x, -ext.y, -ext.z);
        corners[2] = center + new Vector3(ext.x, -ext.y, ext.z);
        corners[3] = center + new Vector3(-ext.x, -ext.y, ext.z);
        corners[4] = center + new Vector3(-ext.x, ext.y, -ext.z);
        corners[5] = center + new Vector3(ext.x, ext.y, -ext.z);
        corners[6] = center + new Vector3(ext.x, ext.y, ext.z);
        corners[7] = center + new Vector3(-ext.x, ext.y, ext.z);

        Gizmos.color = color;
        // Нижняя грань
        Gizmos.DrawLine(corners[0], corners[1]);
        Gizmos.DrawLine(corners[1], corners[2]);
        Gizmos.DrawLine(corners[2], corners[3]);
        Gizmos.DrawLine(corners[3], corners[0]);
        // Верхняя грань
        Gizmos.DrawLine(corners[4], corners[5]);
        Gizmos.DrawLine(corners[5], corners[6]);
        Gizmos.DrawLine(corners[6], corners[7]);
        Gizmos.DrawLine(corners[7], corners[4]);
        // Вертикальные рёбра
        Gizmos.DrawLine(corners[0], corners[4]);
        Gizmos.DrawLine(corners[1], corners[5]);
        Gizmos.DrawLine(corners[2], corners[6]);
        Gizmos.DrawLine(corners[3], corners[7]);
    }

    // Рисуем направление взгляда (головы)
    void DrawLookDirection()
    {
        if (deerVision == null || deerVision.headTransform == null) return;
        Vector3 pos = deerVision.headTransform.position;
        Vector3 dir = deerVision.headTransform.forward;
        Gizmos.color = lookRayColor;
        Gizmos.DrawRay(pos, dir * deerVision.visionRadius * 0.95f);

#if UNITY_EDITOR
        Handles.color = lookRayColor;
        Handles.Label(pos + dir * 2.5f + Vector3.up * 0.2f, $"Look\nYaw:{deerVision.headYaw:0}\nPitch:{deerVision.headPitch:0}", EditorStyles.boldLabel);
#endif
    }

    // Рисуем направление тела (forward)
    void DrawBodyDirection()
    {
        if (agentBody == null) return;
        Vector3 pos = agentBody.position;
        Vector3 dir = agentBody.forward;
        Gizmos.color = bodyRayColor;
        Gizmos.DrawRay(pos, dir * 2.5f);

#if UNITY_EDITOR
        Handles.color = bodyRayColor;
        Handles.Label(pos + dir * 1.5f + Vector3.up * 0.1f, $"Body\nAngle:{agentBody.eulerAngles.y:0}", EditorStyles.miniLabel);
#endif
    }

    // Визуализация восприятия объектов (что агент "видит" и как определяет)
    void DrawObjectPerception()
    {
        if (deerVision == null || deerVision.foundFeatures == null) return;
        foreach (var f in deerVision.foundFeatures)
        {
            Vector3 pos = f.position + Vector3.up * 0.15f;
            Color c = GetTypeColor(f.type, f.confidence);
            Gizmos.color = c;
            Gizmos.DrawSphere(pos, 0.18f + 0.12f * f.confidence);

#if UNITY_EDITOR
            Handles.color = c;
            Handles.Label(
                pos + Vector3.up * 0.18f,
                GetTypeLabel(f.type), // <-- вот тут будет ярлык, который олень присвоил объекту
                EditorStyles.boldLabel
            );
#endif
        }
    }

    // --- Вспомогательные ---
    Vector2Int WorldToCell(Vector3 pos, float size)
    {
        return new Vector2Int(Mathf.FloorToInt(pos.x / size), Mathf.FloorToInt(pos.z / size));
    }
    Vector3 CellToWorld(Vector2Int cell, float size)
    {
        return new Vector3((cell.x + 0.5f) * size, 0, (cell.y + 0.5f) * size);
    }

    // Получить ярлык по типу объекта (0 - неизвестно, 1 - препятствие, 2 - еда, 3 - опасность и т.д.)
    string GetTypeLabel(int type)
    {
        switch (type)
        {
            case 1: return "Препятствие";
            case 2: return "Еда";
            case 3: return "Опасность";
            default: return "Неизвестно";
        }
    }
    void DrawVisionRays()
    {
        if (deerVision == null || deerVision.headTransform == null) return;

        Vector3 origin = deerVision.headTransform.position;
        Quaternion headRot = deerVision.headTransform.rotation;
        int ringCount = deerVision.ringCount;
        int senseRayPerRing = deerVision.senseRayPerRing;
        float visionRadius = deerVision.visionRadius;
        float sensorPitchMin = deerVision.sensorPitchMin;
        float sensorPitchMax = deerVision.sensorPitchMax;
        float fov = deerVision.fieldOfViewAngle > 0 ? deerVision.fieldOfViewAngle : 360f;

        Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.22f);

        for (int ring = 1; ring <= ringCount; ++ring)
        {
            float ringFrac = ring / (float)ringCount;
            float radius = visionRadius * ringFrac;
            float elevationAngle = Mathf.Lerp(sensorPitchMin, sensorPitchMax, ringFrac);

            for (int i = 0; i < senseRayPerRing; ++i)
            {
                float azimuth = -fov / 2f + fov * i / (senseRayPerRing - 1);
                Quaternion localRayRot = Quaternion.Euler(elevationAngle, azimuth, 0);
                Vector3 dir = headRot * localRayRot * Vector3.forward;
                Gizmos.DrawRay(origin, dir * radius);
            }
        }
    }

    void DrawVisionRings()
    {
        if (deerVision == null || deerVision.headTransform == null) return;

        Vector3 origin = deerVision.headTransform.position;
        Quaternion headRot = deerVision.headTransform.rotation;
        int ringCount = deerVision.ringCount;
        float visionRadius = deerVision.visionRadius;
        float sensorPitchMin = deerVision.sensorPitchMin;
        float sensorPitchMax = deerVision.sensorPitchMax;
        float fov = deerVision.fieldOfViewAngle > 0 ? deerVision.fieldOfViewAngle : 360f;

        Gizmos.color = new Color(0.1f, 0.6f, 1f, 0.13f);

        int segments = 64;
        for (int ring = 1; ring <= ringCount; ++ring)
        {
            float ringFrac = ring / (float)ringCount;
            float radius = visionRadius * ringFrac;
            float elevationAngle = Mathf.Lerp(sensorPitchMin, sensorPitchMax, ringFrac);

            Vector3[] points = new Vector3[segments + 1];
            for (int s = 0; s <= segments; ++s)
            {
                float azimuth = -fov / 2f + fov * s / segments;
                Quaternion localRayRot = Quaternion.Euler(elevationAngle, azimuth, 0);
                Vector3 dir = headRot * localRayRot * Vector3.forward;
                points[s] = origin + dir * radius;
            }
            for (int s = 0; s < segments; ++s)
            {
                Gizmos.DrawLine(points[s], points[s + 1]);
            }
        }
    }


    // Цвет по типу и уверенности
    Color GetTypeColor(int type, float confidence)
    {
        Color baseColor;
        switch (type)
        {
            case 1: baseColor = Color.gray; break;      // препятствие
            case 2: baseColor = Color.green; break;     // еда
            case 3: baseColor = Color.red; break;       // опасность
            default: baseColor = Color.yellow; break;   // неизвестно
        }
        baseColor.a = 0.25f + 0.35f * Mathf.Clamp01(confidence);
        return baseColor;
    }
}
