using System.Collections.Generic;
using System.Linq;
using AISA.Speech;
using UnityEngine;
using UnityEngine.Events;

namespace AISA.AI
{
    /// <summary>
    /// 음성 → 텍스트(Whisper) → 명령(LLM) → AI 이동(AIMove) 을 잇는 컴포넌트.
    ///
    /// 사용법:
    ///  1) 씬 아무 GameObject 에 LlmCommandParser + 이 컴포넌트를 붙인다.
    ///  2) Speech 에 WhisperSpeechToText, Mover 에 AI 의 AIMove 를 넣는다.
    ///  3) llama-server 를 켜고 Play → Space 를 누른 채 말하기.
    ///  마이크 없이 시험하려면 Test Input 에 문장을 쓰고 컴포넌트 메뉴(⋮) → "Send Test Input".
    /// </summary>
    public class VoiceCommandBridge : MonoBehaviour
    {
        [SerializeField] WhisperSpeechToText speech;
        [SerializeField] LlmCommandParser parser;
        [SerializeField] AIMove mover;

        [Header("Action → AIMove")]
        [SerializeField] float walkSpeedScale = 1f;
        [SerializeField] float runSpeedScale = 1.8f;

        [Header("Whisper 필터")]
        [Tooltip("조용할 때 Whisper 가 지어내는 문장. 이 글이 들어 있으면 버린다")]
        [SerializeField] string[] hallucinations =
        {
            "시청해주셔서 감사합니다", "시청해 주셔서 감사합니다", "구독과 좋아요", "MBC 뉴스", "자막 제공",
        };

        [Header("Debug")]
        [Tooltip("Qwen 이 문장을 어떻게 해석했는지 한 줄로 Console 에 찍는다")]
        [SerializeField] bool logParsing = true;

        [Header("Test")]
        [SerializeField] string testInput = "왼쪽으로 가서 왼쪽 스위치 눌러";

        [Header("Events")]
        [Tooltip("아이샤의 대답(되묻기, 잡담, 못 하는 일). UI 말풍선 등에 연결")]
        public UnityEvent<string> onReply;

        int _requestId;   // 긴급 정지 뒤에 늦게 도착한 LLM 결과를 버리기 위한 번호

        void Reset()
        {
            parser = GetComponent<LlmCommandParser>();
            speech = FindAnyObjectByType<WhisperSpeechToText>();
            mover = FindAnyObjectByType<AIMove>();
        }

        void OnEnable()
        {
            if (speech != null)
                speech.Transcribed += Submit;
        }

        void OnDisable()
        {
            if (speech != null)
                speech.Transcribed -= Submit;
        }

        [ContextMenu("Send Test Input")]
        void SendTestInput() => Submit(testInput);

        public async void Submit(string heard)
        {
            var text = heard?.Trim();
            if (string.IsNullOrEmpty(text))
                return;
            if (hallucinations.Any(h => text.Contains(h)))
            {
                Debug.Log($"[Bridge] Whisper 헛소리로 보고 버림: {text}");
                return;
            }

            // 이전 명령을 해석하는 중이어도 새 명령을 바로 보낸다. 이전 결과는 도착해도 버린다.
            if (parser.IsBusy)
                Debug.Log($"[Bridge] 새 명령이 들어와 이전 명령은 버림 → {text}");

            var id = ++_requestId;
            var (result, errors) = await parser.Parse(text, heard);
            if (logParsing)
                Debug.Log(Describe(text, result, errors));
            if (id != _requestId)
                return;   // 기다리는 사이에 더 새로운 명령(긴급 정지 등)이 들어옴

            if (errors.Count > 0 || result == null)
            {
                onReply?.Invoke("응? 다시 말해 줘.");
                return;
            }

            if (result.HasActions)
                Execute(result.actions);
            else
                onReply?.Invoke(result.reply);
        }

        // 예: [Parse] "왼쪽으로 가서 스위치 눌러" → Move(left) → PressSwitch(LeftSwitch)
        static string Describe(string text, CommandResult result, List<string> errors)
        {
            var head = $"[Parse] \"{text}\"";
            if (errors.Count > 0 || result == null)
                return $"{head} → 실패: {string.Join(", ", errors)}";
            if (result.HasActions)
            {
                var steps = result.actions.Select(a =>
                {
                    var arg = !string.IsNullOrEmpty(a.target) ? a.target : a.direction;
                    return string.IsNullOrEmpty(arg) ? a.action : $"{a.action}({arg})";
                });
                return $"{head} → {string.Join(" → ", steps)}" + (result.emergency ? "  [긴급]" : "");
            }
            return $"{head} → 대답: \"{result.reply}\"";
        }

        void Execute(ActionData[] actions)
        {
            if (actions.Any(a => a.action == "Stop"))
            {
                mover.Stop();
                return;
            }

            var steps = new List<MoveCommand>();
            foreach (var a in actions)
            {
                var step = ToMoveCommand(a);
                if (step != null)
                    steps.Add(step);
            }
            if (steps.Count > 0)
                mover.Execute(new MovePlan { steps = steps.ToArray() });
        }

        // 행동을 추가하면 여기에서 실제 게임 동작으로 연결한다.
        MoveCommand ToMoveCommand(ActionData a)
        {
            switch (a.action)
            {
                case "Move": return new MoveCommand { direction = a.direction, speedScale = walkSpeedScale };
                case "Run": return new MoveCommand { direction = a.direction, speedScale = runSpeedScale };
                case "GoTo": return new MoveCommand { target = a.target, speedScale = walkSpeedScale };
                // TODO: 스위치를 실제로 누르는 동작은 아직 없어서 스위치 앞까지 이동만 한다
                case "PressSwitch": return new MoveCommand { target = a.target, speedScale = walkSpeedScale };
                default:
                    Debug.LogWarning($"[Bridge] 게임에 연결되지 않은 행동: {a.action}");
                    return null;
            }
        }
    }
}
