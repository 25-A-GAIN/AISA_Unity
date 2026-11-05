using System;

// 자연어 파서의 출력 형식. target이 있으면 target을 우선하고, 없으면 direction으로 이동한다.
[Serializable]
public class MoveCommand
{
    public string target;     // 오브젝트 이름 (예: "LeftSwitch")
    public string direction;  // "left" | "right" | "forward" | "back"
    public float distance;    // direction 이동 거리. 0 이하면 AIMove의 기본 거리 사용
    public float speedScale;  // 기본 속도 배율 (1 = 보통, 0.5 = 천천히). 0 이하면 1로 처리
}

// 한 문장에서 나온 이동 단계들. 순서대로 실행한다.
[Serializable]
public class MovePlan
{
    public MoveCommand[] steps;
}
