using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace AISA.Speech.Editor
{
    /// <summary>
    /// Whisper ggml 모델을 Assets/StreamingAssets/Whisper 에 내려받는 에디터 메뉴
    /// 모델 파일은 용량이 커서 git 에 올리지 않으므로 각자 이 메뉴로 받는다
    /// </summary>
    public static class WhisperModelDownloader
    {
        private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
        private const string TargetDir = "Assets/StreamingAssets/Whisper";

        [MenuItem("Tools/Whisper/Download Model/tiny (75MB, 빠름, 정확도 낮음)")]
        private static void DownloadTiny() => Download("ggml-tiny.bin");

        [MenuItem("Tools/Whisper/Download Model/base (142MB, 권장)")]
        private static void DownloadBase() => Download("ggml-base.bin");

        [MenuItem("Tools/Whisper/Download Model/small (466MB, 느림, 정확도 높음)")]
        private static void DownloadSmall() => Download("ggml-small.bin");

        [MenuItem("Tools/Whisper/Open Model Folder")]
        private static void OpenFolder()
        {
            Directory.CreateDirectory(TargetDir);
            EditorUtility.RevealInFinder(TargetDir);
        }

        private static void Download(string fileName)
        {
            Directory.CreateDirectory(TargetDir);
            var path = Path.Combine(TargetDir, fileName);

            if (File.Exists(path) &&
                !EditorUtility.DisplayDialog("Whisper", $"{fileName} 이(가) 이미 있습니다. 다시 받을까요?", "다시 받기", "취소"))
                return;

            var tmpPath = path + ".download";
            var request = new UnityWebRequest(BaseUrl + fileName, UnityWebRequest.kHttpVerbGET)
            {
                downloadHandler = new DownloadHandlerFile(tmpPath) { removeFileOnAbort = true }
            };
            var op = request.SendWebRequest();

            void Tick()
            {
                var cancel = EditorUtility.DisplayCancelableProgressBar(
                    "Whisper 모델 다운로드", $"{fileName}  {request.downloadProgress * 100f:0.0}%",
                    request.downloadProgress);

                if (cancel)
                    request.Abort();

                if (!op.isDone)
                    return;

                EditorApplication.update -= Tick;
                EditorUtility.ClearProgressBar();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    File.Move(tmpPath, path);
                    AssetDatabase.Refresh();
                    Debug.Log($"[Whisper] 모델 다운로드 완료: {path}\n" +
                              $"WhisperManager 의 Model Path 를 'Whisper/{fileName}' 로 설정하세요.");
                }
                else
                {
                    if (File.Exists(tmpPath))
                        File.Delete(tmpPath);
                    Debug.LogError($"[Whisper] 모델 다운로드 실패: {request.error}");
                }

                request.Dispose();
            }

            EditorApplication.update += Tick;
        }
    }
}
