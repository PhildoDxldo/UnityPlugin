using UnityEngine.Networking;

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
            return new UnityWebRequestAsyncOperation();
        }
    }
}
