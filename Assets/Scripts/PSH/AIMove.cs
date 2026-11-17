using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
public class AIMove : MonoBehaviour
{
    [SerializeField] Transform[] targets;
    [SerializeField] float defaultDistance = 3f;
    [SerializeField] float sampleRadius = 2f;
    // 방향의 기준. 비워두면 AI 자신을 기준으로 한다.
    [SerializeField] Transform directionReference;
    // 파서가 낸 속도 배율의 허용 범위.
    [SerializeField] float minSpeedScale = 0.1f;
    [SerializeField] float maxSpeedScale = 2f;

    readonly Queue<MoveCommand> pendingSteps = new Queue<MoveCommand>();
    NavMeshAgent agent;
    NavMeshPath path;
    float baseSpeed;
    bool isMoving;

    void Awake()
    {
        agent = GetComponent<NavMeshAgent>();
        path = new NavMeshPath();
        baseSpeed = agent.speed;
    }

    void Update()
    {
        if (!isMoving || agent.pathPending || agent.remainingDistance > agent.stoppingDistance)
            return;

        isMoving = false;
        Debug.Log($"[AIMove] 도착: {transform.position}");

        if (pendingSteps.Count > 0)
            StartNextStep();
    }

    // 진행 중인 계획을 버리고 단일 명령을 실행한다.
    public bool Execute(MoveCommand command)
    {
        pendingSteps.Clear();
        return StartStep(command);
    }

    // 진행 중인 계획을 버리고 새 계획을 처음부터 실행한다.
    public bool Execute(MovePlan plan)
    {
        pendingSteps.Clear();

        if (plan?.steps == null || plan.steps.Length == 0)
        {
            Debug.LogWarning("[AIMove] 실행할 단계가 없습니다.");
            return false;
        }

        foreach (MoveCommand step in plan.steps)
            pendingSteps.Enqueue(step);

        return StartNextStep();
    }

    // 목적지는 이전 단계가 끝난 뒤의 위치와 방향을 기준으로 이 시점에 계산한다.
    bool StartNextStep()
    {
        if (StartStep(pendingSteps.Dequeue()))
            return true;

        Debug.LogWarning($"[AIMove] 단계 실패로 남은 {pendingSteps.Count}단계를 취소합니다.");
        pendingSteps.Clear();
        return false;
    }

    bool StartStep(MoveCommand command)
    {
        if (!agent.isOnNavMesh)
        {
            Debug.LogError("[AIMove] AI가 NavMesh 위에 없습니다.");
            return false;
        }

        if (!TryGetDestination(command, out Vector3 destination))
            return false;

        if (!NavMesh.SamplePosition(destination, out NavMeshHit hit, sampleRadius, agent.areaMask))
        {
            Debug.LogWarning($"[AIMove] 목적지 근처에 NavMesh가 없습니다: {destination}");
            return false;
        }

        if (!agent.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete)
        {
            Debug.LogWarning($"[AIMove] 목적지까지 경로가 없습니다: {hit.position}");
            return false;
        }

        float speedScale = command.speedScale > 0f ? command.speedScale : 1f;
        agent.speed = baseSpeed * Mathf.Clamp(speedScale, minSpeedScale, maxSpeedScale);
        agent.SetPath(path);
        isMoving = true;
        Debug.Log($"[AIMove] 이동 시작: {hit.position}");
        return true;
    }

    bool TryGetDestination(MoveCommand command, out Vector3 destination)
    {
        destination = default;

        if (!string.IsNullOrEmpty(command.target))
        {
            foreach (Transform target in targets)
            {
                if (target != null && string.Equals(target.name, command.target, StringComparison.OrdinalIgnoreCase))
                {
                    destination = target.position;
                    return true;
                }
            }

            Debug.LogWarning($"[AIMove] 알 수 없는 target: {command.target}");
            return false;
        }

        if (!TryGetDirection(command.direction, out Vector3 direction))
        {
            Debug.LogWarning($"[AIMove] 알 수 없는 direction: {command.direction}");
            return false;
        }

        float distance = command.distance > 0f ? command.distance : defaultDistance;
        destination = transform.position + direction * distance;
        return true;
    }

    bool TryGetDirection(string name, out Vector3 direction)
    {
        Transform reference = directionReference != null ? directionReference : transform;

        switch (name?.ToLowerInvariant())
        {
            case "left": direction = -reference.right; break;
            case "right": direction = reference.right; break;
            case "forward": direction = reference.forward; break;
            case "back": direction = -reference.forward; break;
            default:
                direction = default;
                return false;
        }

        direction = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
        return true;
    }
}
