using System;
using System.Collections.Generic;
using UnityEngine;
using Gymize;
using NumSharp;

/// <summary>
/// RL-агент оленя для Gymize/Unity ML — использует DeerVision, DeerCognitiveMap,
/// ManualRigidbodyController и HeadYaw (управление дочерним объектом-головой)
/// Адаптирован для FoodZone и NumSharp.NDArray
/// </summary>
[RequireComponent(typeof(DeerVision))]
[RequireComponent(typeof(ManualRigidbodyController))]
public class DeerAgentRL : Agent
{
    [Header("Компоненты")]
    public GameObject headObject; // Дочерний объект - голова (вращение по yaw)
    private DeerVision _vision;
    private ManualRigidbodyController _controller;

    [Header("Параметры RL управления")]
    public float headYawMin = -35f;
    public float headYawMax = 35f;
    public float headYawSpeed = 100f; // градусов в сек
    public float headPitchMin = -20f;
    public float headPitchMax = 20f;
    public float headPitchSpeed = 60f; // градусов в сек
    private float desiredHeadPitch = 0f;

    [Header("🍎 РАДИКАЛЬНАЯ СИСТЕМА НАГРАД - ТОЛЬКО ЕДА!")]
    // === ОГРОМНЫЕ НАГРАДЫ - ТОЛЬКО ЗА ЕДУ! ===
    public float rewardPerFoodEaten = 100.0f;            // ОГРОМНАЯ награда за съеденную еду!
    public float rewardPerMemoryFoodEaten = 120.0f;      // ЕЩЕ БОЛЬШЕ за еду через память!
    
    // === БОЛЬШИЕ НАГРАДЫ - ЗА ПРОГРЕСС К ЕДЕ ===
    public float rewardPerFoodApproach = 10.0f;          // БОЛЬШАЯ награда за приближение к еде!
    public float rewardPerFoodDiscovery = 15.0f;         // БОЛЬШАЯ награда за обнаружение новой еды
    public float rewardForSeeingFood = 0.5f;             // Постоянная награда за видение еды
    
    // === МИКРОСКОПИЧЕСКИЕ НАГРАДЫ - ЗА ВСЕ ОСТАЛЬНОЕ ===
    public float rewardPerMovement = 0.001f;             // КРОШЕЧНАЯ награда за движение
    public float rewardPerExploration = 0.0f;            // НЕТ наград за исследование!
    
    // === ЖЕСТОКИЕ ШТРАФЫ ===
    public float penaltyPerCollision = 0.0f;             // БЕЗ штрафов за столкновения!
    public float penaltyPerIdle = -0.1f;                 // Маленький штраф за бездействие
    public float penaltyForNoFood = -50.0f;               // ОГРОМНЫЙ штраф за отсутствие еды
    public float penaltyForJitter = 0.0f;                // БЕЗ штрафов за дерганость
    public float penaltyForAvoidingFood = -10.0f;        // НОВЫЙ: Штраф за избегание еды
    
    // === НАСТРОЙКИ ===
    public float timeWithoutFoodThreshold = 10f;         // Время до штрафа за голод
    public float foodApproachThreshold = 0.5f;           // Минимальное приближение для награды
    public float movementThreshold = 0.2f;               // Минимальная скорость для "движения"
    
    [Header("Анти-фарм система")]
    public float penaltyForIdleStaring = -1.3f;          // Штраф за долгое смотрение на еду без движения
    public int maxStepsLookingAtFood = 100;               // Максимум шагов смотрения на еду без штрафа
    public float foodDiscoveryRadius = 5f;               // Радиус для определения "той же" еды

    [Header("Action Smoothing & Delay")]
    public int actionRepeat = 3;
    [Range(0f, 1f)]
    public float actionSmoothing = 0.5f;

    private float desiredHeadYaw = 0f;
    private HashSet<Vector2Int> visitedCells = new HashSet<Vector2Int>();
    private Vector3 lastPosition;
    private DeerCognitiveMap cogMap => DeerCognitiveMap.Instance;

    private List<float> lastAction = new List<float>() { 0, 0, 0, 0, 0 };
    private List<float> smoothedAction = new List<float>() { 0, 0, 0, 0, 0 };
    private int actionRepeatCounter = 0;

    private List<float> prevAction = new List<float>() { 0, 0, 0, 0, 0 };
    private float jitterSum = 0f;

    [NonSerialized] public bool foodTakenByHead = false;
    [NonSerialized] public bool foodTakenByMemory = false;

    private float lastHeadYaw = 0f;
    private float lastBodyYaw = 0f;

    private float lastFoodTime = 0f;
    private int consecutiveIdleSteps = 0;
    
    // Простая система наград за еду
    private HashSet<Vector3> discoveredFoodPositions = new HashSet<Vector3>();
    private float lastDistanceToFood = -1f;

    public override void OnReset()
    {
        UnityEngine.SceneManagement.SceneManager.LoadScene(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
            UnityEngine.SceneManagement.LoadSceneMode.Single
        );
        visitedCells.Clear();
        lastPosition = transform.position;
        lastAction = new List<float>() { 0, 0, 0, 0, 0 };
        smoothedAction = new List<float>() { 0, 0, 0, 0, 0 };
        prevAction = new List<float>() { 0, 0, 0, 0, 0 };
        actionRepeatCounter = 0;
        jitterSum = 0f;
        foodTakenByHead = false;
        foodTakenByMemory = false;
        lastHeadYaw = _vision != null ? _vision.headYaw : 0f;
        lastBodyYaw = transform.eulerAngles.y;
        lastFoodTime = Time.time;
        consecutiveIdleSteps = 0;
        
        // Инициализация системы наград за еду
        discoveredFoodPositions.Clear();
        lastDistanceToFood = -1f;
    }

    void Awake()
    {
        _vision = GetComponent<DeerVision>();
        _controller = GetComponent<ManualRigidbodyController>();
        lastPosition = transform.position;
        if (headObject == null)
            Debug.LogWarning("HeadObject (голова) не задан! Добавьте ее в инспекторе как дочерний объект от тела агента.");
        lastHeadYaw = _vision != null ? _vision.headYaw : 0f;
        lastBodyYaw = transform.eulerAngles.y;
        lastFoodTime = Time.time;
        consecutiveIdleSteps = 0;
        
        // Инициализация системы наград за еду
        discoveredFoodPositions = new HashSet<Vector3>();
        lastDistanceToFood = -1f;
    }

    public override void OnAction(object action)
    {
        List<float> act = ParseAction(action);

        if (act.Count < 5)
            throw new ArgumentException($"Action for DeerAgentRL must have at least 5 elements, got {act.Count}");

        // --- Action Repeat ---
        if (actionRepeatCounter > 0)
        {
            act = new List<float>(lastAction); // повторяем последнее действие
            actionRepeatCounter--;
        }
        else
        {
            // Сглаживание действий
            for (int i = 0; i < act.Count; i++)
            {
                smoothedAction[i] = actionSmoothing * smoothedAction[i] + (1 - actionSmoothing) * act[i];
            }
            lastAction = new List<float>(smoothedAction);
            actionRepeatCounter = actionRepeat - 1;
            act = new List<float>(smoothedAction);
        }

        // --- Jitter (дерганость) ---
        float jitter = 0f;
        for (int i = 0; i < act.Count; i++)
        {
            jitter += Mathf.Abs(act[i] - prevAction[i]);
        }
        jitterSum += jitter;
        prevAction = new List<float>(act);

        float move = Mathf.Clamp(act[0], -1f, 1f);
        float turn = Mathf.Clamp(act[1], -1f, 1f);
        float speed = Mathf.Clamp01(act[2]);
        float headDeltaNorm = Mathf.Clamp(act[3], -1f, 1f);

        _controller.SetRLInputs(move, turn, false, speed);

        // --- Управление головой (yaw) ---
        float headYawDelta = headDeltaNorm * headYawSpeed * Time.deltaTime;
        desiredHeadYaw = Mathf.Clamp(_vision.headYaw + headYawDelta, headYawMin, headYawMax);
        _vision.headYaw = desiredHeadYaw;
        float headPitchDeltaNorm = Mathf.Clamp(act[4], -1f, 1f);
        float headPitchDelta = headPitchDeltaNorm * headPitchSpeed * Time.deltaTime;
        desiredHeadPitch = Mathf.Clamp(_vision.headPitch + headPitchDelta, headPitchMin, headPitchMax);
        _vision.headPitch = desiredHeadPitch;
        if (headObject != null)
        {
            Vector3 localRot = headObject.transform.localEulerAngles;
            localRot.y = desiredHeadYaw;
            localRot.x = desiredHeadPitch;
            headObject.transform.localEulerAngles = localRot;
        }
    }

    private List<float> ParseAction(object action)
    {
        if (action is List<object> lo)
            return lo.ConvertAll(Convert.ToSingle);
        if (action is List<float> lf)
            return lf;
        if (action is float[] fa)
            return new List<float>(fa);
        if (action is double[] da)
        {
            var l = new List<float>(da.Length);
            foreach (var d in da) l.Add((float)d);
            return l;
        }
        if (action is NumSharp.NDArray nda)
        {
            var arr = nda.ToArray<float>();
            return new List<float>(arr);
        }
        if (action is object[] oa)
        {
            var l = new List<float>(oa.Length);
            foreach (var o in oa) l.Add(Convert.ToSingle(o));
            return l;
        }
        // Fallback: NDArray через reflection (если приходит как object)
        if (action != null && action.GetType().FullName.Contains("NumSharp.NDArray"))
        {
            var method = action.GetType().GetMethod("ToArray", new Type[] { typeof(Type) });
            if (method != null)
            {
                var arrObj = method.Invoke(action, new object[] { typeof(float) }) as Array;
                if (arrObj != null)
                {
                    var l = new List<float>(arrObj.Length);
                    for (int i = 0; i < arrObj.Length; i++)
                        l.Add(Convert.ToSingle(arrObj.GetValue(i)));
                    return l;
                }
            }
            method = action.GetType().GetMethod("ToArray", Type.EmptyTypes);
            if (method != null)
            {
                var arrObj = method.Invoke(action, null) as Array;
                if (arrObj != null)
                {
                    var l = new List<float>(arrObj.Length);
                    for (int i = 0; i < arrObj.Length; i++)
                        l.Add(Convert.ToSingle(arrObj.GetValue(i)));
                    return l;
                }
            }
            throw new ArgumentException("NumSharp NDArray could not be converted to float[]");
        }
        throw new ArgumentException($"Invalid action format for DeerAgentRL: {action?.GetType()}");
    }

    public object CollectObservations()
    {
        var result = new Dictionary<string, object>();

        result["obs_vision"] = _vision.GetMemoryObservation();
        result["head_yaw"] = _vision.headYaw / headYawMax;
        result["head_pitch"] = _vision.headPitch / headPitchMax;
        result["position"] = new float[] { transform.position.x, transform.position.z };

        if (cogMap != null)
        {
            Vector2Int cell = WorldToCell(transform.position, cogMap.cellSize);
            visitedCells.Add(cell);
            result["visited"] = visitedCells.Count;
        }
        else
        {
            result["visited"] = 0;
        }

        var foodMem = _vision.GetClosestMemory(2);
        if (foodMem != null)
        {
            float dist = Vector3.Distance(transform.position, foodMem.position);
            Vector3 toFood = foodMem.position - transform.position;
            float angle = Vector3.SignedAngle(
                Quaternion.Euler(0, _vision.headYaw, 0) * transform.forward,
                toFood, Vector3.up
            );
            result["food_in_memory"] = new float[] { 1, dist, angle / 180f };
        }
        else
        {
            result["food_in_memory"] = new float[] { 0, -1f, 0f };
        }

        var obs3d = _vision.GetObservation();
        result["obs_3d"] = obs3d;

        var focusObs = _vision.CollectFocusObservation();
        result["focus_obs"] = focusObs;

        if (_vision.foundFeatures != null)
        {
            List<float[]> featuresList = new List<float[]>();
            foreach (var f in _vision.foundFeatures)
                featuresList.Add(f.features);
            result["raw_features"] = featuresList;
        }

        return result;
    }

    public float GetReward()
    {
        float reward = 0f;
        Vector3 currentPosition = transform.position;
        Vector3 currentVelocity = (currentPosition - lastPosition) / Time.deltaTime;
        float currentSpeed = currentVelocity.magnitude;
        bool isMoving = currentSpeed > movementThreshold;
        
        // === 1. БАЗОВАЯ НАГРАДА ЗА ДВИЖЕНИЕ (анти-idle) ===
        // Проверяем, не ест ли олень в данный момент
        bool isCurrentlyEating = false;
        if (headObject != null)
        {
            var headCol = headObject.GetComponent<Collider>();
            if (headCol != null)
            {
                // Проверяем пересечение с любой FoodZone
                var foodZones = FindObjectsByType<FoodZone>(FindObjectsSortMode.None);
                foreach (var zone in foodZones)
                {
                    if (zone.GetComponent<Collider>().bounds.Intersects(headCol.bounds))
                    {
                        isCurrentlyEating = true;
                        break;
                    }
                }
            }
        }
        
        if (isMoving)
        {
            reward += rewardPerMovement * currentSpeed; // Больше движения = больше награды
            consecutiveIdleSteps = 0;
        }
        else
        {
            consecutiveIdleSteps++;
            // ШТРАФ за бездействие ТОЛЬКО если НЕ ест еду
            if (!isCurrentlyEating && consecutiveIdleSteps > 20) // ~2 секунды бездействия
            {
                float idlePenalty = penaltyPerIdle * (1f + consecutiveIdleSteps * 0.05f);
                reward += idlePenalty;
            }
            // Если ест, сбрасываем счетчик бездействия
            else if (isCurrentlyEating)
            {
                consecutiveIdleSteps = 0;
            }
        }
        
        // === 2. НЕТ НАГРАД ЗА ИССЛЕДОВАНИЕ! ТОЛЬКО ЕДА! ===
        // Убираем награды за исследование - олень должен сосредоточиться ТОЛЬКО на еде
        if (cogMap != null)
        {
            var cell = WorldToCell(currentPosition, cogMap.cellSize);
            visitedCells.Add(cell); // Просто отслеживаем, но НЕ награждаем
        }

        // === 3. СИСТЕМА НАГРАД ЗА ЕДУ - ТОЛЬКО ЗА РЕАЛЬНЫЙ ПРОГРЕСС! ===
        bool currentlySeesFood = false;
        Vector3 closestVisibleFood = Vector3.zero;
        float closestFoodDistance = float.MaxValue;
        
        // Находим ближайшую видимую еду
        if (_vision.foundFeatures != null)
        {
            foreach (var f in _vision.foundFeatures)
            {
                if (f.type == 2) // еда
                {
                    currentlySeesFood = true;
                    float dist = Vector3.Distance(currentPosition, f.position);
                    if (dist < closestFoodDistance)
                    {
                        closestFoodDistance = dist;
                        closestVisibleFood = f.position;
                    }
                }
            }
        }
        
        // НАГРАДЫ ЗА ЕДУ - БОЛЕЕ АГРЕССИВНАЯ МОТИВАЦИЯ!
        if (currentlySeesFood)
        {
            // 1. Награда за ПЕРВОЕ обнаружение новой еды (даже без движения)
            bool isNewFood = true;
            foreach (var discoveredPos in discoveredFoodPositions)
            {
                if (Vector3.Distance(closestVisibleFood, discoveredPos) < foodDiscoveryRadius)
                {
                    isNewFood = false;
                    break;
                }
            }
            
            if (isNewFood)
            {
                reward += rewardPerFoodDiscovery; // Награда за открытие новой еды
                discoveredFoodPositions.Add(closestVisibleFood);
                Debug.Log($"[DeerAgent] Обнаружена новая еда! Награда: +{rewardPerFoodDiscovery}");
            }
            
            // 2. Награда за ПРИБЛИЖЕНИЕ к еде (убираем ограничение на движение)
            if (lastDistanceToFood > 0)
            {
                float distanceChange = lastDistanceToFood - closestFoodDistance;
                if (distanceChange > 0.1f) // Любое приближение
                {
                    float approachReward = rewardPerFoodApproach * distanceChange;
                    reward += approachReward;
                    Debug.Log($"[DeerAgent] Приближение к еде: {distanceChange:F2}м, награда: +{approachReward:F2}");
                }
                else if (distanceChange < -0.3f) // Штраф за отдаление от еды
                {
                    reward += rewardPerFoodApproach * distanceChange * 0.5f; // Меньший штраф
                }
            }
            
            // 3. БОЛЬШАЯ постоянная награда за то, что видим еду
            reward += rewardForSeeingFood;
            
            lastDistanceToFood = closestFoodDistance;
        }
        else
        {
            lastDistanceToFood = -1f; // Сброс если еды не видим
            
            // ШТРАФ за то, что НЕ видим еду (должны активно искать!)
            reward += -0.1f;
        }
        
        // === 4. МАКСИМАЛЬНЫЕ НАГРАДЫ ЗА ПОЕДАНИЕ ЕДЫ! ===
        if (foodTakenByMemory)
        {
            reward += rewardPerMemoryFoodEaten; // ОГРОМНАЯ награда за еду через память!
            foodTakenByMemory = false;
            lastFoodTime = Time.time;
        }
        else if (foodTakenByHead)
        {
            reward += rewardPerFoodEaten; // ОГРОМНАЯ награда за съеденную еду!
            foodTakenByHead = false;
            lastFoodTime = Time.time;
        }
        
        // === 5. ШТРАФ ЗА ДОЛГОЕ ОТСУТСТВИЕ ЕДЫ ===
        float timeSinceFood = Time.time - lastFoodTime;
        if (timeSinceFood > timeWithoutFoodThreshold)
        {
            // Экспоненциально растущий штраф за голод
            float hungerPenalty = penaltyForNoFood * Mathf.Pow(1.2f, timeSinceFood - timeWithoutFoodThreshold);
            reward += hungerPenalty * Time.deltaTime;
        }
        
        // === 6. БЕЗ ШТРАФОВ ЗА ДЕРГАНОСТЬ - МОЖНО ДЕРГАТЬСЯ! ===
        // Убираем штраф за дерганость - пусть олень свободно ищет еду
        // if (jitterSum > 0.5f)
        // {
        //     reward += penaltyForJitter * (jitterSum / 5f);
        // }

        
        // === 7. ОБНОВЛЕНИЕ ПЕРЕМЕННЫХ ===
        
        // Обновление переменных для следующего кадра
        lastPosition = currentPosition;
        lastHeadYaw = _vision.headYaw;
        lastBodyYaw = transform.eulerAngles.y;
        jitterSum = 0f;
        
        return reward;
    }

    // --- БЕЗ ШТРАФОВ ЗА КОЛЛИЗИИ - ПУСТЬ ОЛЕНЬ СВОБОДНО ДВИЖЕТСЯ! ---
    void OnTriggerEnter(Collider other)
    {
        // Просто игнорируем все коллизии - никаких штрафов!
        // Олень должен сосредоточиться только на поиске еды
    }

    void OnCollisionEnter(Collision col)
    {
        // Просто игнорируем все столкновения - никаких штрафов!
        // Олень должен сосредоточиться только на поиске еды
    }

    private Vector2Int WorldToCell(Vector3 pos, float cellSize)
    {
        return new Vector2Int(
            Mathf.FloorToInt(pos.x / cellSize),
            Mathf.FloorToInt(pos.z / cellSize)
        );
    }

    public override void OnInfo(object info)
    {
        if (info is Dictionary<string, object> d)
        {
            if (d.ContainsKey("stage"))
            {
                // stage = 1, 2, 3 и т.д. (пример)
            }
        }
    }

    void Update()
    {
#if UNITY_EDITOR
        if (Input.GetKeyDown(KeyCode.F12))
        {
            Debug.Log($"[RLAgent] headYaw={_vision.headYaw} visited {visitedCells.Count} cells");
        }
#endif
    }
}
