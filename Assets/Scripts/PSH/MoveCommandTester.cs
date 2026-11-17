using UnityEngine;
using UnityEngine.InputSystem;

// 파서가 만들어지기 전까지 mock 명령은 숫자 키(1~9), mock 계획은 F1~F12로 실행한다.
[RequireComponent(typeof(AIMove))]
public class MoveCommandTester : MonoBehaviour
{
    [SerializeField] MoveCommand[] mockCommands =
    {
        new MoveCommand { target = "LeftSwitch" },
        new MoveCommand { target = "RightSwitch" },
        new MoveCommand { direction = "left" },
        new MoveCommand { direction = "right" },
        new MoveCommand { direction = "forward" },
        new MoveCommand { direction = "back" },
        new MoveCommand { target = "Door" },  // 실패 케이스: 없는 target
    };

    [SerializeField] MovePlan[] mockPlans =
    {
        // F1: 같은 방향 반복 + 속도 변화
        new MovePlan
        {
            steps = new[]
            {
                new MoveCommand { direction = "forward", speedScale = 0.5f },
                new MoveCommand { direction = "forward", speedScale = 0.5f },
                new MoveCommand { direction = "left" },
                new MoveCommand { direction = "forward", speedScale = 1.5f },
                new MoveCommand { direction = "right" },
            },
        },
        // F2: LeftSwitch로 갔다가 방향 이동 후 RightSwitch로
        new MovePlan
        {
            steps = new[]
            {
                new MoveCommand { target = "LeftSwitch" },
                new MoveCommand { direction = "back" },
                new MoveCommand { direction = "back" },
                new MoveCommand { direction = "right", distance = 2f },
                new MoveCommand { target = "RightSwitch" },
            },
        },
        // F3: 거리, 속도, target 혼합
        new MovePlan
        {
            steps = new[]
            {
                new MoveCommand { direction = "forward", distance = 1f },
                new MoveCommand { direction = "forward", distance = 1f, speedScale = 2f },
                new MoveCommand { target = "LeftSwitch", speedScale = 0.3f },
                new MoveCommand { target = "RightSwitch", speedScale = 1.5f },
                new MoveCommand { target = "LeftSwitch" },
            },
        },
    };

    AIMove aiMove;

    void Awake()
    {
        aiMove = GetComponent<AIMove>();
    }

    void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
            return;

        for (int i = 0; i < mockCommands.Length && i < 9; i++)
        {
            if (keyboard[Key.Digit1 + i].wasPressedThisFrame)
            {
                MoveCommand command = mockCommands[i];
                Debug.Log($"[Tester] {i + 1}번 실행: {JsonUtility.ToJson(command)}");
                aiMove.Execute(command);
            }
        }

        for (int i = 0; i < mockPlans.Length && i < 12; i++)
        {
            if (keyboard[Key.F1 + i].wasPressedThisFrame)
            {
                MovePlan plan = mockPlans[i];
                Debug.Log($"[Tester] F{i + 1} 실행: {JsonUtility.ToJson(plan)}");
                aiMove.Execute(plan);
            }
        }
    }
}
