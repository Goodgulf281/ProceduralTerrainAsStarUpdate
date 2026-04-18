using UnityEngine;
using System.Collections.Generic;
using System;

namespace Goodgulf.TerrainUtils
{
    /// <summary>
    /// Allows background threads to queue actions to be executed on Unity's main thread.
    /// Required because Unity APIs can only be called from the main thread.
    /// </summary>
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static UnityMainThreadDispatcher _instance;
        private static readonly Queue<Action> _executionQueue = new Queue<Action>();
        private static readonly object _queueLock = new object();
        
        /// <summary>
        /// Get or create the singleton instance.
        /// </summary>
        public static UnityMainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    // Try to find existing instance
                    _instance = FindAnyObjectByType<UnityMainThreadDispatcher>();
                    
                    if (_instance == null)
                    {
                        // Create new instance
                        GameObject go = new GameObject("UnityMainThreadDispatcher");
                        _instance = go.AddComponent<UnityMainThreadDispatcher>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// Ensure the dispatcher is initialized.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            // Access Instance to create it
            _ = Instance;
        }
        
        private void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
            }
        }
        
        private void Update()
        {
            // Process all queued actions on the main thread
            lock (_queueLock)
            {
                while (_executionQueue.Count > 0)
                {
                    Action action = _executionQueue.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Error executing main thread action: {e.Message}\n{e.StackTrace}");
                    }
                }
            }
        }
        
        /// <summary>
        /// Enqueue an action to be executed on the main thread.
        /// Thread-safe, can be called from any thread.
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            
            lock (_queueLock)
            {
                _executionQueue.Enqueue(action);
            }
        }
        
        /// <summary>
        /// Check if we're currently on the main thread.
        /// </summary>
        public static bool IsMainThread()
        {
            return System.Threading.Thread.CurrentThread.ManagedThreadId == 1;
        }
    }
}
