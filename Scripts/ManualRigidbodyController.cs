
using UnityEngine;

/// <summary>
/// Реалистичное ручное или RL-управление Rigidbody-персонажем.
/// Добавлена инерция, сглаживание смены направления, ограничение резких разворотов, движение назад медленнее, плавное торможение.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ManualRigidbodyController : MonoBehaviour
{
    public enum MoveState { Idle, Walk, Run, Custom }

    [Header("Manual Control")]
    public bool manualControl = false; // В инспекторе! Если true — управление с клавиатуры

    [Header("Ходьба")]
    public float walkForce = 60f;
    public float walkMaxSpeed = 3f;
    public float walkTurnSpeed = 160f;

    [Header("Бег")]
    public float runForce = 100f;
    public float runMaxSpeed = 7f;
    public float runTurnSpeed = 90f;

    [Header("Custom (RL)")]
    public float maxCustomForce = 120f;
    public float maxCustomSpeed = 10f;
    public float maxCustomTurnSpeed = 180f;

    [Header("Прыжок")]
    public float jumpForce = 10f;
    public float jumpCooldown = 5f;
    public float uprightTorque = 333f;
    public float uprightLerp = 0.17f;
    public float groundLinearDamping = 7f;
    public float airLinearDamping = 0.10f;
    public float groundCheckDistance = 0.65f;
    public float maxAirAngularSpeed = 8f;
    public float groundAngularDamping = 2.0f;
    public float airAngularDamping = 0.8f;

    [Header("Инерция и сглаживание")]
    [Tooltip("Время (сек), за которое персонаж полностью меняет направление движения")]
    public float directionSmoothTime = 0.23f;
    [Tooltip("Время (сек), за которое персонаж полностью останавливается")]
    public float stopSmoothTime = 0.32f;
    [Tooltip("Максимальный угол разворота за один кадр (градусы)")]
    public float maxTurnAnglePerStep = 60f;

    [Header("Движение назад")]
    [Tooltip("Во сколько раз движение назад медленнее")]
    public float backwardSpeedMultiplier = 0.45f;
    [Tooltip("Во сколько раз сила движения назад меньше")]
    public float backwardForceMultiplier = 0.4f;

    private Rigidbody rb;
    private MoveState currentState = MoveState.Idle;
    private float lastJumpTime = -10;
    private bool lastGrounded = false;

    // Управляющие параметры (RL или manual)
    private float moveInput, turnInput;
    private bool wantJump, wantRun;
    private float speedControl = 1f; // [0,1] — 0=walk, 1=run, между — интерполяция

    // Для инерции и сглаживания
    private Vector3 desiredMoveDir = Vector3.zero;
    private Vector3 currentMoveDir = Vector3.zero;
    private float lastMoveInput = 0f;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.mass = 1.13f;
        rb.linearDamping = groundLinearDamping;
        rb.angularDamping = groundAngularDamping;
        rb.useGravity = true;
        rb.isKinematic = false;
        rb.centerOfMass = new Vector3(0, -0.30f, 0);
        rb.constraints = RigidbodyConstraints.None;
    }

    private void FixedUpdate()
    {
        bool grounded = Physics.Raycast(transform.position + Vector3.up * 0.05f, -transform.up, groundCheckDistance + 0.08f);

        // Damping
        rb.linearDamping = grounded ? groundLinearDamping : airLinearDamping;
        rb.angularDamping = grounded ? groundAngularDamping : airAngularDamping;

        UpdateState();

        // --- Выбор параметров движения ---
        float force, maxSpeed, turnSpeed;
        switch (currentState)
        {
            case MoveState.Walk:
                force = walkForce;
                maxSpeed = walkMaxSpeed;
                turnSpeed = walkTurnSpeed;
                break;
            case MoveState.Run:
                force = runForce;
                maxSpeed = runMaxSpeed;
                turnSpeed = runTurnSpeed;
                break;
            case MoveState.Custom: // RL-режим: интерполяция между walk и run, либо абсолютные значения
                force = Mathf.Lerp(walkForce, maxCustomForce, speedControl);
                maxSpeed = Mathf.Lerp(walkMaxSpeed, maxCustomSpeed, speedControl);
                turnSpeed = Mathf.Lerp(walkTurnSpeed, maxCustomTurnSpeed, speedControl);
                break;
            default:
                force = 0f;
                maxSpeed = 0.1f;
                turnSpeed = walkTurnSpeed;
                break;
        }

        // --- Движение с инерцией и сглаживанием ---
        Vector3 fwd = transform.forward;
        Vector3 velocity = rb.linearVelocity;
        Vector3 horizontalVelocity = Vector3.ProjectOnPlane(velocity, Vector3.up);

        // Определяем желаемое направление движения (только вдоль forward)
        float targetMoveInput = Mathf.Clamp(moveInput, -1f, 1f);

        // Движение назад медленнее
        float speedMult = (targetMoveInput < -0.01f) ? backwardSpeedMultiplier : 1f;
        float forceMult = (targetMoveInput < -0.01f) ? backwardForceMultiplier : 1f;
        float maxSpeedThisFrame = maxSpeed * speedMult;
        float forceThisFrame = force * forceMult;

        // Сглаживаем смену направления (инерция)
        // Если резко меняем направление (например, с 1 на -1), ограничиваем скорость смены
        float maxDelta = maxTurnAnglePerStep / 90f * Time.fixedDeltaTime / directionSmoothTime;
        float moveInputSmoothed = Mathf.MoveTowards(lastMoveInput, targetMoveInput, maxDelta);
        lastMoveInput = moveInputSmoothed;

        // Если почти остановились — сглаживаем торможение
        if (Mathf.Abs(targetMoveInput) < 0.05f)
            moveInputSmoothed = Mathf.MoveTowards(lastMoveInput, 0f, Time.fixedDeltaTime / stopSmoothTime);

        // --- Прыжок ---
        if (wantJump && grounded && (Time.time - lastJumpTime) > jumpCooldown && moveInputSmoothed >= -0.01f)
        {
            rb.linearVelocity = new Vector3(rb.linearVelocity.x, 0, rb.linearVelocity.z);
            rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
            lastJumpTime = Time.time;
        }

        // --- Применяем силу движения ---
        if (grounded && Mathf.Abs(moveInputSmoothed) > 0.05f)
        {
            if (horizontalVelocity.magnitude < maxSpeedThisFrame)
            {
                rb.AddForce(fwd * moveInputSmoothed * forceThisFrame, ForceMode.Force);
            }
        }

        // Ограничиваем максимальную скорость (на земле и в воздухе)
        Vector3 newHorizontalVelocity = Vector3.ClampMagnitude(horizontalVelocity, maxSpeedThisFrame);
        rb.linearVelocity = new Vector3(newHorizontalVelocity.x, rb.linearVelocity.y, newHorizontalVelocity.z);

        // --- Поворот ---
        float effectiveTurnSpeed = grounded ? turnSpeed : turnSpeed * 0.35f;
        if (Mathf.Abs(turnInput) > 0.05f)
        {
            float angleDelta = turnInput * effectiveTurnSpeed * Time.fixedDeltaTime;
            rb.MoveRotation(rb.rotation * Quaternion.Euler(0f, angleDelta, 0f));
        }

        // --- Ограничение угловой скорости в воздухе ---
        if (!grounded)
        {
            Vector3 av = rb.angularVelocity;
            av.y = Mathf.Clamp(av.y, -maxAirAngularSpeed, maxAirAngularSpeed);
            rb.angularVelocity = av;
        }

        // --- Плавное торможение на земле ---
        if (Mathf.Abs(moveInputSmoothed) < 0.03f && grounded)
        {
            rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, new Vector3(0, rb.linearVelocity.y, 0), 0.09f);
        }

        // --- Балансировка (упрощённо) ---
        var cur = rb.rotation;
        var to = Quaternion.FromToRotation(transform.up, Vector3.up) * cur;
        var uprightRot = Quaternion.Slerp(cur, to, uprightLerp);
        var delta = uprightRot * Quaternion.Inverse(cur);
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        rb.AddTorque(axis * angle * Mathf.Deg2Rad * uprightTorque, ForceMode.Force);

        lastGrounded = grounded;
        wantJump = false; // сбрасываем после обработки
    }
    public Vector3 GetVelocity()
    {
        var rb = GetComponent<Rigidbody>();
        return rb != null ? rb.linearVelocity : Vector3.zero;
    }
    private void UpdateState()
    {
        // Если Custom — всегда Custom, иначе дискретно
        if (currentState == MoveState.Custom) return;

        if (Mathf.Abs(moveInput) > 0.13f && wantRun) currentState = MoveState.Run;
        else if (Mathf.Abs(moveInput) > 0.04f) currentState = MoveState.Walk;
        else currentState = MoveState.Idle;
    }

    // --- RL/Manual API ---
    /// <summary>
    /// Для RL: move [-1..1], turn [-1..1], jump, speed [0..1] (0=walk, 1=run, между — интерполяция)
    /// </summary>
    public void SetRLInputs(float move, float turn, bool jump, float speed = 1f)
    {
        moveInput = Mathf.Clamp(move, -1f, 1f);
        turnInput = Mathf.Clamp(turn, -1f, 1f);
        wantJump = jump;
        speedControl = Mathf.Clamp01(speed);
        currentState = MoveState.Custom;
    }

    /// <summary>
    /// Для ручного управления (клавиатура): move [-1..1], turn [-1..1], run, jump
    /// </summary>
    public void SetManualInputs(float move, float turn, bool run, bool jump)
    {
        moveInput = Mathf.Clamp(move, -1f, 1f);
        turnInput = Mathf.Clamp(turn, -1f, 1f);
        wantRun = run;
        wantJump = jump;
        currentState = MoveState.Idle; // сброс в UpdateState
    }

    private void Update()
    {
        if (!manualControl) return;

        float _move = 0, _turn = 0;
        if (Input.GetKey(KeyCode.W)) _move += 1f;
        if (Input.GetKey(KeyCode.S)) _move -= 1f;
        if (Input.GetKey(KeyCode.A)) _turn -= 1f;
        if (Input.GetKey(KeyCode.D)) _turn += 1f;
        bool _run = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool _jump = Input.GetKeyDown(KeyCode.Space);

        SetManualInputs(_move, _turn, _run, _jump);
    }
}
