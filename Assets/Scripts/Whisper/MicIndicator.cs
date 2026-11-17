using UnityEngine;
using UnityEngine.UI;

namespace AISA.Speech
{
    /// <summary>
    /// 녹음 중(Space 를 누르고 있는 동안)에만 마이크 아이콘을 보여준다.
    ///
    /// 사용법: 마이크 Image 오브젝트에 이 컴포넌트를 붙인다.
    /// (Image 의 enabled 만 끄고 켜므로 오브젝트 자체는 활성 상태로 둔다)
    /// </summary>
    [RequireComponent(typeof(Image))]
    public class MicIndicator : MonoBehaviour
    {
        [SerializeField] WhisperSpeechToText speech;
        [SerializeField] Image icon;

        void Reset()
        {
            icon = GetComponent<Image>();
            speech = FindAnyObjectByType<WhisperSpeechToText>();
        }

        void Awake()
        {
            if (icon == null)
                icon = GetComponent<Image>();
            if (speech == null)
                speech = FindAnyObjectByType<WhisperSpeechToText>();
            icon.enabled = false;
        }

        void Update()
        {
            var recording = speech != null && speech.IsRecording;
            if (icon.enabled != recording)
                icon.enabled = recording;
        }
    }
}
