using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using Whisper;
using Debug = UnityEngine.Debug;

namespace AISA.Speech
{
    /// <summary>
    /// 마이크로 녹음한 음성을 로컬 Whisper(whisper.unity)로 텍스트 변환
    ///
    /// 사용법:
    ///  1) 씬에 빈 GameObject를 만들고 WhisperManager + 이 컴포넌트를 붙인다.
    ///  2) StartRecording() / StopRecording() 을 버튼 등에서 호출하거나,
    ///     usePushToTalkKey 를 켜고 지정 키를 누르고 있는 동안 말한다.
    ///  3) onTranscribed 이벤트 또는 Transcribed (C# event) 로 결과 텍스트를 받는다.
    
    [RequireComponent(typeof(WhisperManager))]
    public class WhisperSpeechToText : MonoBehaviour
    {
        // Whisper 모델 입력 샘플레이트
        private const int SampleRate = 16000;

        [Header("Whisper")]
        [Tooltip("인식 언어 코드 (ko, en ...). 비워두거나 auto 면 자동 감지")]
        [SerializeField] private string language = "ko";

        [Header("Microphone")]
        [Tooltip("사용할 마이크 장치 이름. 비우면 기본 장치")]
        [SerializeField] private string microphoneDevice = "";

        [Tooltip("한 번에 녹음할 수 있는 최대 길이(초). 초과하면 자동으로 녹음 종료 후 변환")]
        [SerializeField, Range(1, 60)] private int maxRecordSeconds = 20;

        [Header("Push-To-Talk (테스트용)")]
        [SerializeField] private bool usePushToTalkKey = true;
        [SerializeField] private Key pushToTalkKey = Key.Space;

        [Header("Events")]
        public UnityEvent onRecordingStarted;
        public UnityEvent onRecordingStopped;
        public UnityEvent<string> onTranscribed;

        /// 코드에서 구독할 때 사용 (UnityEvent 와 동일 시점에 호출)
        public event Action<string> Transcribed;

        public bool IsRecording { get; private set; }
        public bool IsTranscribing { get; private set; }
        public string LastResult { get; private set; } = "";

        private WhisperManager _whisper;
        private AudioClip _clip;
        private string _device;
        private float _recordStartTime;

        private void Awake()
        {
            _whisper = GetComponent<WhisperManager>();
            _whisper.language = language;
        }

        private void Update()
        {
            if (IsRecording)
            {
                // 최대 녹음 시간 도달 시 자동 종료
                if (Time.realtimeSinceStartup - _recordStartTime >= maxRecordSeconds)
                    StopRecording();
            }

            if (!usePushToTalkKey || Keyboard.current == null)
                return;

            var key = Keyboard.current[pushToTalkKey];
            if (key.wasPressedThisFrame)
                StartRecording();
            else if (key.wasReleasedThisFrame && IsRecording)
                StopRecording();
        }

        public void StartRecording()
        {
            // 변환 중이어도 녹음은 바로 시작한다. 새 음성이 들어오면 이전 결과는 버린다.
            if (IsRecording)
                return;

            if (Microphone.devices.Length == 0)
            {
                Debug.LogError("[Whisper] 사용 가능한 마이크가 없습니다.");
                return;
            }

            _device = string.IsNullOrEmpty(microphoneDevice) ? null : microphoneDevice;
            _clip = Microphone.Start(_device, false, maxRecordSeconds, SampleRate);
            if (_clip == null)
            {
                Debug.LogError("[Whisper] 마이크 녹음을 시작하지 못했습니다. (권한 확인)");
                return;
            }

            IsRecording = true;
            _recordStartTime = Time.realtimeSinceStartup;
            Debug.Log("[Whisper] 녹음 시작");
            onRecordingStarted?.Invoke();
        }

        public void StopRecording()
        {
            if (!IsRecording)
                return;

            // GetPosition 은 Microphone.End 전에 읽어야 한다
            var position = Microphone.GetPosition(_device);
            Microphone.End(_device);
            IsRecording = false;
            Debug.Log($"[Whisper] 녹음 끝 ({Time.realtimeSinceStartup - _recordStartTime:0.0}초)");
            onRecordingStopped?.Invoke();

            // 녹음이 최대 길이까지 꽉 차면 position 이 0 으로 돌아올 수 있음
            var sampleCount = position > 0 ? position : _clip.samples;
            if (sampleCount <= 0)
            {
                Debug.LogWarning("[Whisper] 녹음된 데이터가 없습니다.");
                return;
            }

            var samples = new float[sampleCount * _clip.channels];
            _clip.GetData(samples, 0);
            var channels = _clip.channels;
            var frequency = _clip.frequency;
            Destroy(_clip);
            _clip = null;

            Transcribe(samples, frequency, channels);
        }

        /// <summary>이미 가지고 있는 AudioClip 을 변환할 때 사용</summary>
        public async void Transcribe(AudioClip clip)
        {
            if (clip == null || IsTranscribing)
                return;

            IsTranscribing = true;
            try
            {
                _whisper.language = language;
                var result = await _whisper.GetTextAsync(clip);
                HandleResult(result);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                IsTranscribing = false;
            }
        }

        // 가장 최근 음성의 번호. 이 번호가 아닌 변환 결과는 버린다.
        private int _latestId;
        // 변환 중에 들어온 새 음성 (항상 가장 최근 것 하나만 기다린다)
        private (float[] samples, int frequency, int channels, int id)? _pending;

        private void Transcribe(float[] samples, int frequency, int channels)
        {
            var id = ++_latestId;
            if (IsTranscribing)
            {
                // Whisper 는 한 번에 하나만 돌리고, 지금 것이 끝나면 이 음성을 바로 변환한다
                _pending = (samples, frequency, channels, id);
                return;
            }
            RunTranscribe(samples, frequency, channels, id);
        }

        private async void RunTranscribe(float[] samples, int frequency, int channels, int id)
        {
            IsTranscribing = true;
            var sw = Stopwatch.StartNew();
            try
            {
                _whisper.language = language;
                var result = await _whisper.GetTextAsync(samples, frequency, channels);
                if (id != _latestId)
                {
                    Debug.Log($"[Whisper] 새 명령이 들어와 이전 결과는 버림: {result?.Result?.Trim()}");
                    return;
                }
                Debug.Log($"[Whisper] 변환 시간: {sw.ElapsedMilliseconds} ms");
                HandleResult(result);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                IsTranscribing = false;
                if (_pending is { } next)
                {
                    _pending = null;
                    RunTranscribe(next.samples, next.frequency, next.channels, next.id);
                }
            }
        }

        private void HandleResult(WhisperResult result)
        {
            if (result == null)
            {
                Debug.LogError("[Whisper] 변환 실패 (모델이 로드되었는지 확인하세요)");
                return;
            }

            LastResult = result.Result?.Trim() ?? "";
            Debug.Log($"[Whisper] ({result.Language}) {LastResult}");

            onTranscribed?.Invoke(LastResult);
            Transcribed?.Invoke(LastResult);
        }

        private void OnDisable()
        {
            if (IsRecording)
            {
                Microphone.End(_device);
                IsRecording = false;
            }
        }
    }
}
