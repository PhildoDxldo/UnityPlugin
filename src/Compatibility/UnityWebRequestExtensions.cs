#if UNITY_5_4_OR_NEWER
using UnityWebRequest = UnityEngine.Networking.UnityWebRequest;
#else
using UnityWebRequest = UnityEngine.Experimental.Networking.UnityWebRequest;
#endif
// TODO(@jackson): Test versions
namespace ModIO
{
    public static class UnityWebRequestExtensions
    {
        public static bool IsError(this UnityWebRequest request)
        {
            // NOTE(@jackson): Presumably !2017_OR_NEWER
            return (request.isError);
        }

        // NOTE(@jackson): Presumably !2018_OR_NEWER
        public static UnityWebRequestAsyncOperation SendWebRequest(this UnityWebRequest request)
        {
            UnityEngine.AsyncOperation operation = request.Send();

            UnityWebRequestAsyncOperation operationWrapper = new UnityWebRequestAsyncOperation(request, operation);
            return operationWrapper;
        }
    }
}
