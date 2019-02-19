namespace ModIO
{
    public class UnityWebRequestAsyncOperation
    {
        public event System.Action<UnityWebRequestAsyncOperation> completed;

        public UnityEngine.Networking.UnityWebRequest webRequest;
    }
}
