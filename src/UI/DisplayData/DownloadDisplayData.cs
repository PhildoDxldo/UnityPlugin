using System;

#if UNITY_5_4_OR_NEWER
using UnityWebRequest = UnityEngine.Networking.UnityWebRequest;
#else
using UnityWebRequest = UnityEngine.Experimental.Networking.UnityWebRequest;
#endif

namespace ModIO.UI
{
    [Serializable]
    public struct DownloadDisplayData
    {
        public Int64 bytesReceived;
        public Int64 bytesPerSecond;
        public Int64 bytesTotal;
        public bool isActive;
    }

    public abstract class DownloadDisplayComponent : UnityEngine.MonoBehaviour
    {
        public abstract event Action<DownloadDisplayComponent> onClick;

        public abstract DownloadDisplayData data { get; set; }

        public abstract void Initialize();
        public abstract void DisplayDownload(FileDownloadInfo downloadInfo);
    }
}
