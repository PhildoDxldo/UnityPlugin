#if UNITY_5_4_OR_NEWER
using UnityWebRequest = UnityEngine.Networking.UnityWebRequest;
#else
using UnityWebRequest = UnityEngine.Experimental.Networking.UnityWebRequest;
#endif

namespace ModIO
{
    [System.Serializable]
    public class FileDownloadInfo
    {
        public UnityWebRequest request;
        public WebRequestError error;
        public string target;
        public System.Int64 fileSize;
        public bool isDone;
    }
}
