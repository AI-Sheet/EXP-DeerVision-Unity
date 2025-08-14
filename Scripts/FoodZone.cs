using UnityEngine;

/// <summary>
/// Компонент для еды, которую можно поедать постепенно.
/// Агент должен сам научиться стоять в зоне, опускать голову и не двигаться.
/// </summary>
public class FoodZone : MonoBehaviour
{
    public float eatTimeRequired = 1.0f; // УМЕНЬШЕНО - проще съесть!
    public float eatRewardPerSecond = 15.0f; // ОГРОМНАЯ награда за процесс поедания
    public float maxEatSpeed = 0.5f; // УВЕЛИЧЕНО - можно двигаться быстрее
    
    [Header("Дополнительные награды")]
    public float startEatingBonus = 20.0f; // ОГРОМНЫЙ бонус за начало поедания
    public float progressBonus = 5.0f; // БОЛЬШОЙ дополнительный бонус за прогресс
    public float proximityReward = 2.0f; // Награда просто за близость к еде

    private float eatingTimer = 0f;
    private DeerAgentRL eatingAgent = null;

    void OnTriggerStay(Collider other)
    {
        var agent = other.GetComponentInParent<DeerAgentRL>();
        if (agent == null) return;
        var vision = agent.GetComponent<DeerVision>();
        var controller = agent.GetComponent<ManualRigidbodyController>();
        if (vision == null || controller == null) return;

        // Проверяем, что голова в зоне еды
        if (agent.headObject == null) return;
        var headCol = agent.headObject.GetComponent<Collider>();
        if (headCol == null) return;
        if (!GetComponent<Collider>().bounds.Intersects(headCol.bounds)) return;

        // ВСЕГДА награждаем за близость к еде, даже если не ест
        agent.AddReward(proximityReward * Time.deltaTime);

        // Проверяем признаки еды: скорость должна быть маленькой для поедания
        if (controller.GetVelocity().magnitude > maxEatSpeed)
        {
            // Если быстро движется - просто награда за близость, без поедания
            return;
        }

        // Начисляем ОГРОМНУЮ награду за поедание
        agent.AddReward(eatRewardPerSecond * Time.deltaTime);

        // Увеличиваем таймер поедания
        if (eatingAgent != agent)
        {
            eatingAgent = agent;
            eatingTimer = 0f;
            // Бонус за начало поедания
            agent.AddReward(startEatingBonus);
            Debug.Log($"[FoodZone] Олень начал есть! Бонус: +{startEatingBonus}");
        }
        
        float prevTimer = eatingTimer;
        eatingTimer += Time.deltaTime;
        
        // Дополнительные бонусы за прогресс каждые 25% завершения
        float progress = eatingTimer / eatTimeRequired;
        float prevProgress = prevTimer / eatTimeRequired;
        
        for (float milestone = 0.25f; milestone <= 1.0f; milestone += 0.25f)
        {
            if (prevProgress < milestone && progress >= milestone)
            {
                agent.AddReward(progressBonus);
                Debug.Log($"[FoodZone] Прогресс поедания: {(milestone * 100):F0}% (+{progressBonus})");
            }
        }

        // Если поедание завершено — уничтожаем еду
        if (eatingTimer >= eatTimeRequired)
        {
            agent.foodTakenByHead = true; // Для статистики/логов
            Destroy(gameObject);
        }
    }

    void OnTriggerExit(Collider other)
    {
        var agent = other.GetComponentInParent<DeerAgentRL>();
        if (agent != null && agent == eatingAgent)
        {
            eatingAgent = null;
            eatingTimer = 0f;
        }
    }
}