
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Глобальная когнитивная карта (3D) для стаи.
/// Поддержка совместного опыта, хранятся объекты, встреченные в каждой клетке (x, y, z).
/// </summary>
public class DeerCognitiveMap : MonoBehaviour
{
    public static DeerCognitiveMap Instance { get; private set; }

    [Header("Grid Settings")]
    public float cellSize = 4.0f;
    public int mapHistorySeconds = 300;
    public float minConfidenceToShare = 0.15f;

    // Сparse-карта: (x,y,z) -> инфа о ячейке (честное 3D)
    private Dictionary<Vector3Int, CellInfo> grid = new Dictionary<Vector3Int, CellInfo>(4096);

    [Serializable]
    public class CellInfo
    {
        public float lastSeen;
        public float confidence;
        public List<float[]> objectFeatures;
        public int observations;
    }

    [Serializable]
    private class SerializableCell
    {
        public int x, y, z;
        public CellInfo cell;
    }

    [Serializable]
    private class SerializableGrid
    {
        public List<SerializableCell> cells = new List<SerializableCell>();
    }

    private string SavePath => Path.Combine(Application.persistentDataPath, "deer_cognitive_map.json");

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        LoadOrCreateMap();
    }

    private void OnApplicationQuit()
    {
        SaveMap();
    }

    /// <summary>
    /// Записать наблюдение — 3D ячейка.
    /// </summary>
    public void RegisterObservation3D(Vector3 worldPos, IList<float[]> featuresList, float confidence)
    {
        Vector3Int idx = WorldToGrid3D(worldPos);
        if (!grid.TryGetValue(idx, out CellInfo cell))
        {
            cell = new CellInfo { lastSeen = Time.time, confidence = confidence, objectFeatures = new List<float[]>(), observations = 1 };
            grid[idx] = cell;
        }
        else
        {
            cell.lastSeen = Time.time;
            cell.observations++;
            cell.confidence = Mathf.Max(cell.confidence, confidence);
            // --- Исправление: защита от null ---
            if (cell.objectFeatures == null)
                cell.objectFeatures = new List<float[]>();
        }
        if (featuresList != null)
            foreach (var f in featuresList)
                if (f != null)
                    cell.objectFeatures.Add((float[])f.Clone());
        // Последние 6 признаков (очистка)
        if (cell.objectFeatures.Count > 6)
            cell.objectFeatures.RemoveRange(0, cell.objectFeatures.Count - 6);
    }

    /// <summary>
    /// Получить информацию по 3D ячейке.
    /// </summary>
    public CellInfo GetCellInfo(Vector3 worldPos)
    {
        grid.TryGetValue(WorldToGrid3D(worldPos), out var cell);
        return cell;
    }

    /// <summary>
    /// Перевести мировые координаты в 3D-индексы sparse-сетки.
    /// </summary>
    private Vector3Int WorldToGrid3D(Vector3 pos)
    {
        return new Vector3Int(
            Mathf.FloorToInt(pos.x / cellSize),
            Mathf.FloorToInt(pos.y / cellSize),
            Mathf.FloorToInt(pos.z / cellSize)
        );
    }

    /// <summary>
    /// Сохраняет карту на диск.
    /// </summary>
    public void SaveMap()
    {
        try
        {
            SerializableGrid sGrid = new SerializableGrid();
            foreach (var kv in grid)
            {
                sGrid.cells.Add(new SerializableCell
                {
                    x = kv.Key.x,
                    y = kv.Key.y,
                    z = kv.Key.z,
                    cell = kv.Value
                });
            }
            string json = JsonUtility.ToJson(sGrid, true);
            File.WriteAllText(SavePath, json);
            Debug.Log($"[DeerCognitiveMap] Карта сохранена: {SavePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[DeerCognitiveMap] Ошибка при сохранении карты: {ex}");
        }
    }

    /// <summary>
    /// Загружает карту с диска, если она есть, иначе создаёт новую.
    /// </summary>
    public void LoadOrCreateMap()
    {
        if (File.Exists(SavePath))
        {
            try
            {
                string json = File.ReadAllText(SavePath);
                SerializableGrid sGrid = JsonUtility.FromJson<SerializableGrid>(json);
                grid.Clear();
                foreach (var sCell in sGrid.cells)
                {
                    var idx = new Vector3Int(sCell.x, sCell.y, sCell.z);
                    if (sCell.cell.objectFeatures == null)
                        sCell.cell.objectFeatures = new List<float[]>();
                    grid[idx] = sCell.cell;
                }
                Debug.Log($"[DeerCognitiveMap] Карта загружена: {SavePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DeerCognitiveMap] Ошибка при загрузке карты, создаётся новая: {ex}");
                grid.Clear();
            }
        }
        else
        {
            grid.Clear();
            Debug.Log("[DeerCognitiveMap] Карта не найдена, создана новая.");
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Визуализация heatmap покрытия в 3D для дебага.
    /// </summary>
    public void DrawDebugGizmos(float yBase = 0.2f)
    {
        float now = Application.isPlaying ? Time.time : 0f;
        foreach (var kv in grid)
        {
            float tAgo = now - kv.Value.lastSeen;
            if (tAgo > mapHistorySeconds) continue;
            float conf = Mathf.Clamp01(kv.Value.confidence);
            float alpha = 0.12f + 0.20f * conf;
            Gizmos.color = new Color(1f - conf, conf, 0.3f, alpha);
            var pos = new Vector3(
                kv.Key.x * cellSize + cellSize / 2,
                kv.Key.y * cellSize + cellSize / 2,
                kv.Key.z * cellSize + cellSize / 2);
            Gizmos.DrawCube(pos, Vector3.one * cellSize * 0.85f);
        }
    }
#endif
}
