using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace ModIO.Compatibility
{
    public static class UnityWebRequestAsyncOperationExtensions
    {
        // NOTE(@jackson): Presumably !2018_OR_NEWER
        public static UnityWebRequestAsyncOperation SendWebRequest(this UnityWebRequest request)
        {
            UnityEngine.AsyncOperation operation = request.Send();

            UnityWebRequestAsyncOperation operationWrapper = new UnityWebRequestAsyncOperation(request, operation);
            return operationWrapper;
        }

    }

    public class UnityWebRequestAsyncOperation
    {
        // ---------[ FIELDS ]---------
        public UnityWebRequest webRequest { get; private set; }
        public event System.Action<UnityWebRequestAsyncOperation> completed;

        // ---------[ AsyncOperation Interface Duplication ]---------
        private AsyncOperation m_operation;

        public bool allowSceneActivation
        {
            get { return m_operation.allowSceneActivation; }
            set { m_operation.allowSceneActivation = value; }
        }
        public bool isDone
        {
            get { return m_operation.isDone; }
        }
        public int priority
        {
            get { return m_operation.priority; }
            set { m_operation.priority = value; }
        }
        public float progress
        {
            get { return m_operation.progress; }
        }

        // --------[ INITIALIZATION ]---------
        public UnityWebRequestAsyncOperation(UnityWebRequest webRequest, AsyncOperation operationToMonitor)
        {
            Debug.Assert(webRequest != null);
            Debug.Assert(operationToMonitor != null);

            this.webRequest = webRequest;
            this.m_operation = operationToMonitor;

            #if UNITY_EDITOR
            if(!Application.isPlaying)
            {
                UnityWebRequestAsyncOperation.AddCoroutineToUpdateList(MonitorProgress());
            }
            else
            #endif
            {
                UnityMainThreadDispatcher.Instance().Enqueue(MonitorProgress());
            }
        }

        private IEnumerator MonitorProgress()
        {
            while (!m_operation.isDone)
            {
                yield return null;
            }

            if (completed != null)
            {
                completed.Invoke(this);
            }
        }

        // ---------[ MONITOR PROGRESS WHILE EDIT-MODE ]---------
        #if UNITY_EDITOR
        private static bool m_isEditorUpdating = false;
        private static List<IEnumerator> m_coroutineList = new List<IEnumerator>();

        private static void AddCoroutineToUpdateList(IEnumerator monitorCoroutine)
        {
            Debug.Assert(!Application.isPlaying);

            if(!m_isEditorUpdating)
            {
                UnityEditor.EditorApplication.update -= UpdateCoroutines;
                UnityEditor.EditorApplication.update += UpdateCoroutines;

                m_isEditorUpdating = true;
            }

            m_coroutineList.Add(monitorCoroutine);
        }

        private static void UpdateCoroutines()
        {
            List<IEnumerator> completedCoroutines = new List<IEnumerator>();

            foreach(IEnumerator coroutine in m_coroutineList)
            {
                if(!coroutine.MoveNext())
                {
                    completedCoroutines.Add(coroutine);
                }
            }

            foreach(IEnumerator coroutine in completedCoroutines)
            {
                m_coroutineList.Remove(coroutine);
            }
        }
        #endif
    }
}
