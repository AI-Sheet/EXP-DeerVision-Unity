
using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Спавнер еды: поддерживает не больше maxFoodCount объектов еды на сцене.
/// Это предотвращает "лавину" Invoke() и гарантирует чёткий контроль респавна.
/// </summary>
public class FoodSpawner : MonoBehaviour
{
    [Header("Настройки генерации еды")]
    public GameObject foodPrefab;      // Префаб еды с Collider и тегом Food
    public int maxFoodCount = 3;       // Максимум еды на сцене
    public float foodMinDist = 0.5f;   // Не класть слишком близко
    public float yOffset = 0.2f;       // На сколько приподнять над Ground
    public float respawnDelay = 1f;    // Задержка перед респавном съеденной еды

    private Transform groundTransform;
    private Renderer groundRenderer;
    private List<GameObject> spawnedFood = new List<GameObject>();
    private bool respawnScheduled = false; // Чтобы не запланировать много респавнов

    void Start()
    {
        // Найти Ground Plane по тегу
        GameObject ground = GameObject.FindGameObjectWithTag("Ground");
        if (ground == null)
        {
            Debug.LogError("Ground (Plane) не найден! Присвойте Plane тег Ground.");
            return;
        }
        groundTransform = ground.transform;
        groundRenderer = ground.GetComponent<Renderer>();
        if (groundRenderer == null)
        {
            Debug.LogError("Ground должен иметь Renderer, чтобы узнать размеры!");
            return;
        }
        // Заполнить поле
        for (int i = 0; i < maxFoodCount; i++)
        {
            SpawnFood();
        }
    }

    void Update()
    {
        // Удалять null-еды из списка
        spawnedFood.RemoveAll(f => f == null);

        // Если еды стало меньше, чем maxFoodCount — (но только 1 раз планирую респавн)
        if (spawnedFood.Count < maxFoodCount && !respawnScheduled)
        {
            respawnScheduled = true;
            Invoke(nameof(SpawnFoodWithFlagReset), respawnDelay);
        }
    }

    // Вызов через Invoke гарантирует единичный респавн
    void SpawnFoodWithFlagReset()
    {
        SpawnFood();
        respawnScheduled = false;
    }

    public void SpawnFood()
    {
        if (groundRenderer == null || foodPrefab == null) return;

        Bounds area = groundRenderer.bounds;

        for (int attempt = 0; attempt < 30; attempt++)
        {
            float x = Random.Range(area.min.x, area.max.x);
            float z = Random.Range(area.min.z, area.max.z);
            Vector3 newPos = new Vector3(x, area.max.y + yOffset, z);

            // Проверяем, не слишком ли близко к другим
            bool tooClose = false;
            foreach (var f in spawnedFood)
            {
                if (f == null) continue;
                if ((f.transform.position - newPos).magnitude < foodMinDist)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose) continue;

            var go = Instantiate(foodPrefab, newPos, Quaternion.identity);
            go.tag = "Food"; // На всякий случай
            spawnedFood.Add(go);

            // Кускам еды сообщим, кто их спавнер (для автоматического респавна)
            var eater = go.GetComponent<FoodEatenNotifier>();
            if (eater != null) eater.spawner = this;

            return;
        }
        Debug.LogWarning("Не удалось разместить новую еду: слишком плотно!");
    }
}

// Новый скрипт — Component на foodPrefab, чтобы сигналить о поедании
public class FoodEatenNotifier : MonoBehaviour
{
    [HideInInspector] public FoodSpawner spawner;

    // Это вызывается из DeerAgent после Destroy(gameObject);
    private void OnDestroy()
    {
        if (spawner != null)
        {
            // Можно вызвать прямо SpawnFood или ничего не делать, тк FoodSpawner сам отслеживает кол-во еды
        }
    }
}
