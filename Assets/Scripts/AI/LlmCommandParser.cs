using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

namespace AISA.AI
{
    /// <summary>
    /// Colab 노트북 5번 셀을 옮긴 것.
    /// 긴급 판별 → llama-server 호출 → 검증(실패 시 재시도) → 대화 기억 → 로그(jsonl)
    ///
    /// 먼저 PC 에서 llama-server 를 켜 둬야 한다:
    ///   llama-server.exe -hf unsloth/Qwen3-4B-Instruct-2507-GGUF:Q4_K_M --port 8080 -ngl 99 -c 4096
    /// 모델을 바꿔도(파인튜닝 모델 포함) 이 코드는 그대로다. Model Label 만 바꿔 두면 로그에서 구분된다.
    /// </summary>
    public class LlmCommandParser : MonoBehaviour
    {
        [Header("llama-server")]
        [SerializeField] string serverUrl = "http://127.0.0.1:8080/v1/chat/completions";
        [Tooltip("로그에 남길 모델 이름. 모델 파일을 바꾸면 같이 바꿔 둘 것")]
        [SerializeField] string modelLabel = "Qwen3-4B-Instruct-2507-Q4_K_M";
        [SerializeField, Range(1, 60)] int timeoutSeconds = 15;
        [SerializeField] int maxTokens = 160;

        [Header("Parsing")]
        [Tooltip("이전 대화 기억 턴 수. 늘리면 느려지고 작은 모델은 헷갈려 함")]
        [SerializeField, Range(0, 10)] int maxHistoryTurns = 5;
        [SerializeField, Range(0, 3)] int retries = 1;
        [Tooltip("출력 형식을 서버에서 문법으로 강제 (JSON 형식 오류가 사실상 사라짐)")]
        [SerializeField] bool useJsonSchema = true;

        [Header("Log (파인튜닝 데이터용)")]
        [SerializeField] bool writeLog = true;

        // 새 명령이 들어오면 이전 요청이 끝나기 전에 또 호출될 수 있어서 개수로 센다
        int _busyCount;
        public bool IsBusy => _busyCount > 0;
        // 가장 최근 Parse 호출 번호. 더 새 호출이 있으면 이전 결과는 대화 기억에 넣지 않는다.
        int _latestParse;
        public string LogPath => Path.Combine(Application.persistentDataPath, "parse_log.jsonl");

        readonly List<(string input, string output)> _history = new();

        void Start()
        {
            Debug.Log($"[LLM] 프롬프트 버전 {CommandSchema.PromptVersion}, 로그: {LogPath}");
        }

        public void ResetHistory() => _history.Clear();

        /// <param name="text">LLM 에 넣을 문장</param>
        /// <param name="heard">Whisper 가 받아 적은 원문 (로그용, 필터 전 원문)</param>
        public async Task<(CommandResult result, List<string> errors)> Parse(string text, string heard = null)
        {
            var sw = Stopwatch.StartNew();
            var parseId = ++_latestParse;
            var historyUsed = _history.ToList();
            CommandResult result = null;
            List<string> errors;
            string raw = null;

            var emergency = CommandSchema.CheckEmergency(text);
            if (emergency != null)
            {
                result = new CommandResult { actions = emergency, emergency = true };
                errors = new List<string>();
                ResetHistory();   // 긴급 정지 뒤에는 이전 맥락을 이어가지 않음
            }
            else
            {
                _busyCount++;
                errors = new List<string> { "시도 안 함" };
                string hint = null;
                try
                {
                    for (int i = 0; i <= retries; i++)
                    {
                        raw = await Request(BuildMessages(text, hint));
                        try
                        {
                            result = Normalize(JsonUtility.FromJson<CommandResult>(raw));
                            errors = CommandSchema.Validate(result);
                        }
                        catch (Exception e)
                        {
                            result = null;
                            errors = new List<string> { $"JSON 파싱 실패: {e.Message}" };
                        }
                        if (errors.Count == 0)
                            break;
                        hint = string.Join("; ", errors);
                    }
                }
                catch (Exception e)
                {
                    errors = new List<string> { $"서버 요청 실패: {e.Message} (llama-server 가 켜져 있는지 확인)" };
                }
                finally
                {
                    _busyCount--;
                }

                // 성공했고, 기다리는 동안 새 명령에 밀려나지 않은 결과만 기억 (실제로 실행된 것만)
                if (errors.Count == 0 && parseId == _latestParse)
                {
                    _history.Add((text, CommandSchema.ToJson(result)));
                    if (_history.Count > maxHistoryTurns)
                        _history.RemoveRange(0, _history.Count - maxHistoryTurns);
                }
            }

            var ms = sw.ElapsedMilliseconds;
            Debug.Log($"[LLM] [{ms}ms] {text}\n   {CommandSchema.ToJson(result)}"
                      + (errors.Count > 0 ? $"\n   오류: {string.Join(", ", errors)}" : ""));
            if (writeLog)
                WriteLog(heard ?? text, text, historyUsed, result, raw, errors, ms);

            return (result, errors);
        }

        // 행동이 있으면 reply 는 버린다 (행동할 때는 대답하지 않음)
        static CommandResult Normalize(CommandResult cmd)
        {
            if (cmd == null)
                return null;
            if (cmd.HasActions)
                cmd.reply = null;
            else
                cmd.reply = cmd.reply?.Trim();
            return cmd;
        }

        List<(string role, string content)> BuildMessages(string text, string errorHint)
        {
            var msgs = new List<(string, string)> { ("system", CommandSchema.SystemPrompt) };
            foreach (var (input, output) in _history)
            {
                msgs.Add(("user", input));
                msgs.Add(("assistant", output));
            }
            if (errorHint != null)
                text = $"{text}\n(이전 출력이 틀렸다: {errorHint}. 규칙에 맞는 JSON만 다시 써.)";
            msgs.Add(("user", text));
            return msgs;
        }

        async Task<string> Request(List<(string role, string content)> messages)
        {
            var body = new StringBuilder("{\"messages\":[");
            body.Append(string.Join(",", messages.Select(m =>
                $"{{\"role\":{Json.Str(m.role)},\"content\":{Json.Str(m.content)}}}")));
            body.Append($"],\"temperature\":0,\"max_tokens\":{maxTokens},\"cache_prompt\":true");
            if (useJsonSchema)
                body.Append($",\"response_format\":{{\"type\":\"json_schema\",\"json_schema\":{{\"name\":\"command\",\"schema\":{CommandSchema.JsonSchema}}}}}");
            body.Append('}');

            using var req = new UnityWebRequest(serverUrl, "POST");
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body.ToString()));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = timeoutSeconds;

            await req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
                throw new Exception($"{req.error} {req.downloadHandler.text}");

            var response = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
            var content = response?.choices?.FirstOrDefault()?.message?.content;
            if (content == null)
                throw new Exception($"응답 형식이 이상함: {req.downloadHandler.text}");
            return content.Trim();
        }

        void WriteLog(string heard, string input, List<(string input, string output)> history,
                      CommandResult result, string raw, List<string> errors, long ms)
        {
            // label 은 나중에 사람이 정답을 채우는 칸 (출력이 맞으면 그대로 두고, 틀리면 고친 JSON 을 넣는다)
            var historyJson = string.Join(",", history.Select(h => $"{{\"input\":{Json.Str(h.input)},\"output\":{h.output}}}"));
            var line = "{"
                     + $"\"time\":{Json.Str(DateTime.Now.ToString("s"))},"
                     + $"\"model\":{Json.Str(modelLabel)},"
                     + $"\"prompt_version\":{Json.Str(CommandSchema.PromptVersion)},"
                     + $"\"heard\":{Json.Str(heard)},"
                     + $"\"input\":{Json.Str(input)},"
                     + $"\"history\":[{historyJson}],"
                     + $"\"output\":{CommandSchema.ToJson(result)},"
                     + $"\"raw\":{Json.Str(raw)},"
                     + $"\"errors\":[{string.Join(",", errors.Select(Json.Str))}],"
                     + $"\"ms\":{ms},"
                     + "\"label\":null}";
            try
            {
                File.AppendAllText(LogPath, line + "\n", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LLM] 로그 저장 실패: {e.Message}");
            }
        }

        [Serializable] class ChatResponse { public Choice[] choices; }
        [Serializable] class Choice { public Message message; }
        [Serializable] class Message { public string content; }
    }
}
