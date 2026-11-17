using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AISA.AI
{
    // LLM 출력의 행동 하나. 쓰지 않는 값은 비워 둔다.
    [Serializable]
    public class ActionData
    {
        public string action;     // "Move" | "Run" | "PressSwitch" | "Stop"
        public string direction;  // PARAMS["direction"] 의 id
        public string target;     // PARAMS["target"] 의 id
    }

    // LLM 출력. actions 와 reply 중 하나만 채워진다.
    [Serializable]
    public class CommandResult
    {
        public ActionData[] actions;
        public string reply;
        public bool emergency;

        public bool HasActions => actions != null && actions.Length > 0;
        public bool HasReply => !string.IsNullOrWhiteSpace(reply);
    }

    /// <summary>
    /// Colab 노트북 3~4번 셀을 옮긴 것. ★ 행동을 늘릴 때는 여기만 고치면 된다 ★
    ///
    /// 행동 추가하는 법:
    ///  (1) 새 값 목록이 필요하면 Params 에 추가
    ///  (2) Actions 에 행동 한 줄 추가 (params 가 비어 있으면 인자 없는 행동)
    ///  (3) Examples 에 그 행동을 쓰는 예시를 1~2개 추가 (작은 모델은 예시가 있어야 잘 씀)
    ///  (4) VoiceCommandBridge.ToMoveCommand 에서 그 행동을 실제 게임 동작으로 연결
    /// 시스템 프롬프트, 출력 JSON 스키마(문법 강제), 검증은 아래 정의로 자동으로 만들어진다.
    ///
    /// 프롬프트 내용을 바꾸면 PromptVersion 이 바뀌어 로그에서 구분된다.
    /// </summary>
    public static class CommandSchema
    {
        // 인자로 쓸 수 있는 값 목록 (id: 한국어 설명). id 는 AIMove 가 쓰는 이름 그대로.
        public static readonly Dictionary<string, (string id, string desc)[]> Params = new()
        {
            ["direction"] = new[]
            {
                ("forward", "앞/위"), ("back", "뒤/아래"), ("left", "왼쪽"), ("right", "오른쪽"),
            },
            ["target"] = new[]
            {
                ("LeftSwitch", "왼쪽 스위치"), ("RightSwitch", "오른쪽 스위치"),
            },
        };

        // 행동 목록 (이름, 설명, 쓰는 Params 이름들)
        public static readonly (string name, string desc, string[] parameters)[] Actions =
        {
            ("Move", "그 방향으로 걷기", new[] { "direction" }),
            ("Run", "그 방향으로 달리기", new[] { "direction" }),
            ("GoTo", "그 대상 앞까지 걸어가기 (누르지는 않음)", new[] { "target" }),
            ("PressSwitch", "스위치 누르기 (스위치 앞까지 이동은 게임이 알아서 함)", new[] { "target" }),
        };

        // 긴급 명령: 발화 전체가 이 단어 하나일 때만. LLM 을 거치지 않고 바로 실행된다.
        public static readonly Dictionary<string, ActionData[]> Emergency = new()
        {
            ["멈춰"] = new[] { new ActionData { action = "Stop" } },
            ["그만"] = new[] { new ActionData { action = "Stop" } },
            ["스톱"] = new[] { new ActionData { action = "Stop" } },
            ["정지"] = new[] { new ActionData { action = "Stop" } },
        };

        // 예시 (few-shot). 각 예시는 (플레이어 말, 출력 JSON) 대화 하나.
        public static readonly (string user, string output)[][] Examples =
        {
            new[]
            {
                ("왼쪽으로 가서 왼쪽 스위치 눌러.",
                 "{\"actions\": [{\"action\": \"Move\", \"direction\": \"left\"}, {\"action\": \"PressSwitch\", \"target\": \"LeftSwitch\"}]}"),
            },
            new[] { ("앞으로 뛰어!", "{\"actions\": [{\"action\": \"Run\", \"direction\": \"forward\"}]}") },
            new[] { ("오른쪽 스위치 쪽으로 가 봐.", "{\"actions\": [{\"action\": \"GoTo\", \"target\": \"RightSwitch\"}]}") },
            new[]
            {
                ("저기 스위치 좀 눌러 봐.", "{\"reply\": \"어느 스위치? 왼쪽, 오른쪽?\"}"),
                ("오른쪽.", "{\"actions\": [{\"action\": \"PressSwitch\", \"target\": \"RightSwitch\"}]}"),
            },
            new[] { ("문 열어.", "{\"reply\": \"지금은 문을 열 수 없어.\"}") },
        };

        static string _systemPrompt;
        static string _jsonSchema;

        public static string SystemPrompt => _systemPrompt ??= BuildSystemPrompt();
        public static string JsonSchema => _jsonSchema ??= BuildJsonSchema();

        // 프롬프트 내용의 짧은 해시. 로그에서 프롬프트가 바뀐 시점을 구분하는 용도.
        public static string PromptVersion
        {
            get
            {
                using var sha = System.Security.Cryptography.SHA1.Create();
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(SystemPrompt));
                return BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }

        static string BuildSystemPrompt()
        {
            var actLines = Actions.Select(a =>
            {
                var args = string.Join(", ", a.parameters.Select(p => $"\"{p}\": {p} 값"));
                var shape = $"{{\"action\": \"{a.name}\"{(args.Length > 0 ? ", " + args : "")}}}";
                return $"- {shape}  : {a.desc}";
            });
            var paramLines = Params.Select(p =>
                $"- {p.Key}: {string.Join(", ", p.Value.Select(v => $"{v.id}({v.desc})"))}");

            var examples = Examples.Select((conv, i) =>
            {
                var sb = new StringBuilder($"[예시 {i + 1}]");
                foreach (var (user, output) in conv)
                    sb.Append($"\n플레이어: {user}\n출력: {output}");
                return sb.ToString();
            });

            return $@"너는 공포 게임 속 AI 동료 ""아이샤""의 명령 해석기다.
플레이어의 한국어 말을 아래 두 형식 중 하나의 JSON으로만 바꾼다. 다른 글은 쓰지 않는다.

A. 할 수 있는 행동이 있을 때:  {{""actions"": [행동, 행동, ...]}}
B. 행동을 만들 수 없을 때:      {{""reply"": ""아이샤의 짧은 반말 대답""}}

규칙:
- 행동이 하나라도 있으면 A만 쓴다. A에는 reply를 넣지 않는다.
- 여러 행동은 말한 순서대로 나열한다.
- 아래 목록에 있는 행동과 값만 쓴다. 목록에 없는 일을 시키면 B로 못 한다고 짧게 말한다.
- 무엇을 가리키는지 모호하면(예: 어느 스위치인지 모름) B로 되묻는다.
- 명령이 아닌 잡담이면 B로 짧게 대답한다.
- 직전에 네가 되물었다면, 이번 말은 그 질문에 대한 대답이다. 원래 명령과 합쳐서 A를 만든다.
- ""왼쪽으로 가""처럼 방향만 말하면 Move, ""왼쪽 스위치로 가""처럼 대상을 말하면 GoTo다. 누르라고 해야 PressSwitch다.
- ""앞으로""는 forward, ""뒤로""는 back이다. ""뛰어"", ""달려"", ""빨리 가""는 Run, 그냥 ""가"", ""걸어""는 Move다.
- 플레이어의 말은 음성 인식 결과라서 오타나 비슷한 발음이 섞일 수 있다. 뜻이 분명하면 그대로 따른다.

행동 목록:
{string.Join("\n", actLines)}

값 목록:
{string.Join("\n", paramLines)}

아래는 형식 예시일 뿐 실제 대화가 아니다.

{string.Join("\n\n", examples)}";
        }

        // llama-server 의 response_format 에 넣을 JSON 스키마. 서버가 이 형식 밖의 글자는 아예 못 쓰게 막는다.
        static string BuildJsonSchema()
        {
            var actionSchemas = Actions.Select(a =>
            {
                var props = new List<string> { $"\"action\":{{\"const\":\"{a.name}\"}}" };
                foreach (var p in a.parameters)
                    props.Add($"\"{p}\":{{\"enum\":[{string.Join(",", Params[p].Select(v => $"\"{v.id}\""))}]}}");
                var required = string.Join(",", new[] { "action" }.Concat(a.parameters).Select(r => $"\"{r}\""));
                return $"{{\"type\":\"object\",\"properties\":{{{string.Join(",", props)}}},\"required\":[{required}],\"additionalProperties\":false}}";
            });

            return "{\"oneOf\":["
                 + "{\"type\":\"object\",\"properties\":{\"actions\":{\"type\":\"array\",\"minItems\":1,\"items\":{\"oneOf\":["
                 + string.Join(",", actionSchemas)
                 + "]}}},\"required\":[\"actions\"],\"additionalProperties\":false},"
                 + "{\"type\":\"object\",\"properties\":{\"reply\":{\"type\":\"string\"}},\"required\":[\"reply\"],\"additionalProperties\":false}"
                 + "]}";
        }

        public static ActionData[] CheckEmergency(string text)
        {
            var key = Regex.Replace(text ?? "", @"[\s!.?~,…]+", "");
            return Emergency.TryGetValue(key, out var actions) ? actions : null;
        }

        // 검증. 문법 강제가 있어도 서버 설정이 빠졌을 때를 대비해 그대로 둔다.
        public static List<string> Validate(CommandResult cmd)
        {
            var errors = new List<string>();
            if (cmd == null)
            {
                errors.Add("결과 없음");
                return errors;
            }
            if (!cmd.HasActions)
            {
                if (!cmd.HasReply)
                    errors.Add("행동도 대답도 없음 (actions가 비었으면 reply를 써야 함)");
                return errors;
            }

            for (int i = 0; i < cmd.actions.Length; i++)
            {
                var a = cmd.actions[i];
                var spec = Actions.FirstOrDefault(s => s.name == a?.action);
                if (spec.name == null)
                {
                    errors.Add($"{i}번 행동 '{a?.action}'은 없는 행동");
                    continue;
                }
                foreach (var p in spec.parameters)
                {
                    var value = GetParam(a, p);
                    if (!Params[p].Any(v => v.id == value))
                        errors.Add($"{i}번 {a.action}의 {p} 값 '{value}'이 목록에 없음");
                }
            }
            return errors;
        }

        public static string GetParam(ActionData a, string param) => param switch
        {
            "direction" => a.direction,
            "target" => a.target,
            _ => throw new ArgumentException($"ActionData 에 '{param}' 필드를 추가하고 GetParam 에도 연결하세요."),
        };

        // 결과를 노트북과 같은 모양의 JSON 으로. 대화 기억과 로그에 쓴다.
        public static string ToJson(CommandResult cmd)
        {
            if (cmd == null)
                return "null";
            if (!cmd.HasActions)
                return $"{{\"reply\": {Json.Str(cmd.reply ?? "")}}}";

            var items = cmd.actions.Select(a =>
            {
                var fields = new List<string> { $"\"action\": {Json.Str(a.action)}" };
                if (!string.IsNullOrEmpty(a.direction)) fields.Add($"\"direction\": {Json.Str(a.direction)}");
                if (!string.IsNullOrEmpty(a.target)) fields.Add($"\"target\": {Json.Str(a.target)}");
                return "{" + string.Join(", ", fields) + "}";
            });
            var emergency = cmd.emergency ? "\"emergency\": true, " : "";
            return $"{{{emergency}\"actions\": [{string.Join(", ", items)}]}}";
        }
    }

    // 손으로 JSON 을 만들 때 쓰는 문자열 이스케이프
    public static class Json
    {
        public static string Str(string s)
        {
            if (s == null)
                return "null";
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
