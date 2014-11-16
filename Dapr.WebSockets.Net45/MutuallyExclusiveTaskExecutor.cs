// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   The mutually exclusive task executor.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// The mutually exclusive task executor.
    /// </summary>
    internal class MutuallyExclusiveTaskExecutor : IDisposable
    {
        /// <summary>
        /// The tasks.
        /// </summary>
        private readonly ConcurrentQueue<Func<Task>> tasks = new ConcurrentQueue<Func<Task>>();

        /// <summary>
        /// The semaphore.
        /// </summary>
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(0);

        /// <summary>
        /// Enqueues the provided <paramref name="action"/> for execution.
        /// </summary>
        /// <param name="action">
        /// The action being invoked.
        /// </param>
        public void Schedule(Func<Task> action)
        {
            this.tasks.Enqueue(action);
            this.semaphore.Release();
        }

        /// <summary>
        /// Invokes the executor.
        /// </summary>
        /// <param name="cancellationToken">The cancellation task.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public Task Start(CancellationToken cancellationToken)
        {
            return Task.Run(() => this.Run(cancellationToken), cancellationToken);
        }

        /// <summary>
        /// Invokes the executor.
        /// </summary>
        /// <param name="cancellationToken">The cancellation task.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        private async Task Run(CancellationToken cancellationToken)
        {
            var cancelled = cancellationToken.IsCancellationRequested;
            while (!cancelled)
            {
                cancelled = cancellationToken.IsCancellationRequested;
                await this.semaphore.WaitAsync(cancellationToken);

                // Process all available items in the queue.
                Func<Task> task;
                while (this.tasks.TryDequeue(out task))
                {
                    try
                    {
                        // Execute the task we pulled out of the queue
                        await task();
                    }
                    catch
                    {
                        // Suppress all errors.
                    }
                }
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            if (this.semaphore != null)
            {
                this.semaphore.Dispose();
            }
        }
    }
}