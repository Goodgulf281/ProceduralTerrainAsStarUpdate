using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace Goodgulf.Building
{
    /// <summary>
    /// Dispatches actions from background threads to be executed on Unity's main thread.
    /// 
    /// Unity APIs are generally not thread-safe, so any code that interacts with
    /// GameObjects, Transforms, or other UnityEngine components must run on the main thread.
    /// This dispatcher provides a simple, thread-safe queue to schedule such actions.
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        // Thread-safe queue storing actions that need to be executed on the main thread
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        /// <summary>
        /// Enqueues an action to be executed on the Unity main thread.
        /// This method can safely be called from any background thread.
        /// </summary>
        /// <param name="action">The action to execute on the main thread</param>
        public static void Enqueue(Action action)
        {
            // Guard against null actions to avoid runtime errors
            if (action != null)
                _queue.Enqueue(action);
        }

        /// <summary>
        /// Unity Update loop.
        /// Executes all queued actions once per frame on the main thread.
        /// </summary>
        void Update()
        {
            // Dequeue and execute all pending actions
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }
}
