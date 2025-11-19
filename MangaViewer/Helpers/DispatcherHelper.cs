using Microsoft.UI.Dispatching;
using System;
using System.Threading.Tasks;

namespace MangaViewer.Helpers
{
    /// <summary>
    /// DispatcherHelper
    /// Purpose: Centralized UI thread marshalling utilities to avoid code duplication.
    /// Thread Safety: Safe to call from any thread; uses TaskCompletionSource for async coordination.
    /// </summary>
    public static class DispatcherHelper
    {
        /// <summary>
        /// Execute a function on the UI thread and return its result asynchronously.
        /// </summary>
        /// <typeparam name="T">Return type of the function</typeparam>
        /// <param name="dispatcher">UI dispatcher queue</param>
        /// <param name="func">Function to execute on UI thread</param>
        /// <returns>Task with the result of the function, or default(T) if enqueue fails</returns>
        public static Task<T?> RunOnUiAsync<T>(DispatcherQueue dispatcher, Func<T?> func)
        {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (func == null) throw new ArgumentNullException(nameof(func));

            var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            
            if (!dispatcher.TryEnqueue(() =>
            {
                try 
                { 
                    tcs.TrySetResult(func()); 
                }
                catch (Exception ex) 
                { 
                    tcs.TrySetException(ex); 
                }
            }))
            {
                tcs.TrySetResult(default);
            }
            
            return tcs.Task;
        }

        /// <summary>
        /// Execute an action on the UI thread asynchronously.
        /// </summary>
        /// <param name="dispatcher">UI dispatcher queue</param>
        /// <param name="action">Action to execute on UI thread</param>
        /// <returns>Task that completes when the action finishes</returns>
        public static Task RunOnUiAsync(DispatcherQueue dispatcher, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            
            return RunOnUiAsync(dispatcher, () => 
            { 
                action(); 
                return (object?)null; 
            });
        }

        /// <summary>
        /// Check if the current thread has access to the dispatcher.
        /// </summary>
        /// <param name="dispatcher">UI dispatcher queue</param>
        /// <returns>True if current thread is the UI thread</returns>
        public static bool HasThreadAccess(DispatcherQueue dispatcher)
        {
            return dispatcher?.HasThreadAccess ?? false;
        }

        /// <summary>
        /// Execute action on UI thread if needed, otherwise execute immediately.
        /// </summary>
        /// <param name="dispatcher">UI dispatcher queue</param>
        /// <param name="action">Action to execute</param>
        public static void RunOnUi(DispatcherQueue dispatcher, Action action)
        {
            if (dispatcher == null || action == null) return;

            if (dispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                dispatcher.TryEnqueue(() => { try { action(); } catch { } });
            }
        }
    }
}
