namespace Dapr.WebSockets
{
    using System;
    using System.Collections.Concurrent;
    using System.Net.WebSockets;
    using System.Reactive;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Reactive.Threading.Tasks;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A reactive WebSocket abstraction.
    /// </summary>
    public static class WebSocket
    {
        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning an observable used to receive messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The subject used to send and receive messages.</returns>
        public static async Task<IObservable<string>> ConnectOutput(Uri uri, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, cancellationToken);
            return SocketReceivePump(socket, cancellationToken);
        }

        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning an observer used to send messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The observer used to send messages.</returns>
        public static async Task<IObserver<string>> ConnectInput(Uri uri, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, cancellationToken);
            var cancellation = new CancellationTokenSource();
            cancellationToken.Register(cancellation.Cancel);
            cancellation.Token.Register(socket.Dispose);
            return SocketSendPump(socket, cancellation);
        }

        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning a subject used to send and receive messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The subject used to send and receive messages.</returns>
        public static async Task<ISubject<string>> Connect(Uri uri, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, cancellationToken);

            var cancellation = new CancellationTokenSource();
            cancellationToken.Register(cancellation.Cancel);
            cancellation.Token.Register(socket.Dispose);
            return new CombinedSubject<string>(SocketReceivePump(socket, cancellation.Token), SocketSendPump(socket, cancellation));
        }

        /// <summary>
        /// The socket send pump.
        /// </summary>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <param name="cancellation">
        /// The cancellation token source.
        /// </param>
        /// <returns>
        /// The observer used to send messages to the socket.
        /// </returns>
        private static IObserver<string> SocketSendPump(ClientWebSocket socket, CancellationTokenSource cancellation)
        {
            var buffer = new byte[0];
            var subject = new Subject<string>();
            var actor = new MutuallyExclusiveTaskExecutor();
            var subscription = subject.Subscribe(
                next => actor.Enqueue(
                    async () =>
                    {
                        ArraySegment<byte> segment;
                        if (Encoding.UTF8.GetByteCount(next) > buffer.Length)
                        {
                            // The buffer is too small to hold the result, so it cannot be reused.
                            // Create a larger buffer for next time.
                            buffer = Encoding.UTF8.GetBytes(next);
                            segment = new ArraySegment<byte>(buffer);
                        }
                        else
                        {
                            // Reuse existing buffer.
                            var count = Encoding.UTF8.GetBytes(next, 0, next.Length, buffer, 0);
                            segment = new ArraySegment<byte>(buffer, 0, count);
                        }

                        await socket.SendAsync(segment, WebSocketMessageType.Text, true, cancellation.Token);
                    }));
            actor.Run(cancellation.Token).ContinueWith(_ => cancellation.Cancel());
            cancellation.Token.Register(subscription.Dispose);

            return subject.AsObserver();
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The observable stream of messages.</returns>
        private static IObservable<string> SocketReceivePump(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            return Observable.Create<string>(
                observer =>
                {
                    var cancellation = new CancellationTokenSource();
                    var subscription = ReceivePump(observer, socket, cancellation.Token).ToObservable().Subscribe(_ => { }, observer.OnError);
                    cancellation.Token.Register(subscription.Dispose);
                    cancellationToken.Register(cancellation.Cancel);
                    return Disposable.Create(cancellation.Cancel);
                }).Publish().RefCount();
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="observer">The observer.</param>
        /// <param name="socket">The socket.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        private static async Task ReceivePump(IObserver<string> observer, ClientWebSocket socket, CancellationToken cancellationToken)
        {
            try
            {
                var buffer = new ArraySegment<byte>(new byte[4096]);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var received = await ReceiveString(socket, buffer, cancellationToken);
                    if (socket.CloseStatus.HasValue)
                    {
                        observer.OnCompleted();
                        return;
                    }

                    observer.OnNext(received);
                }
            }
            catch (Exception e)
            {
                observer.OnError(e);
            }
        }

        /// <summary>
        /// Receive a single string from the provided <paramref name="socket"/>.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="buffer">The scratch buffer.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The received string.</returns>
        private static async Task<string> ReceiveString(ClientWebSocket socket, ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var result = new StringBuilder();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, cancellationToken);
                result.Append(Encoding.UTF8.GetString(buffer.Array, buffer.Offset, received.Count));
            }
            while (!cancellationToken.IsCancellationRequested && !received.EndOfMessage);

            return result.ToString();
        }

        /// <summary>
        /// Represents an <see cref="ISubject{T}"/> backed by an observer and an observable.
        /// </summary>
        /// <typeparam name="T">
        /// The underlying stream type.
        /// </typeparam>
        private struct CombinedSubject<T> : ISubject<T>
        {
            /// <summary>
            /// The observer.
            /// </summary>
            private readonly IObserver<T> internalObserver;

            /// <summary>
            /// The observable.
            /// </summary>
            private readonly IObservable<T> internalObservable;

            /// <summary>
            /// Initializes a new instance of the <see cref="CombinedSubject{T}"/> struct.
            /// </summary>
            /// <param name="observable">
            /// The observable.
            /// </param>
            /// <param name="observer">
            /// The observer.
            /// </param>
            public CombinedSubject(IObservable<T> observable, IObserver<T> observer)
            {
                this.internalObservable = observable;
                this.internalObserver = observer;
            }

            /// <summary>
            /// Notifies the observer that the provider has finished sending push-based notifications.
            /// </summary>
            public void OnCompleted()
            {
                this.internalObserver.OnCompleted();
            }

            /// <summary>
            /// Notifies the observer that the provider has experienced an error condition.
            /// </summary>
            /// <param name="error">An object that provides additional information about the error.</param>
            public void OnError(Exception error)
            {
                this.internalObserver.OnError(error);
            }

            /// <summary>
            /// Provides the observer with new data.
            /// </summary>
            /// <param name="value">The current notification information.</param>
            public void OnNext(T value)
            {
                this.internalObserver.OnNext(value);
            }

            /// <summary>
            /// Notifies the provider that an observer is to receive notifications.
            /// </summary>
            /// <returns>
            /// A reference to an interface that allows observers to stop receiving notifications before the provider has finished sending them.
            /// </returns>
            /// <param name="observer">The object that is to receive notifications.</param>
            public IDisposable Subscribe(IObserver<T> observer)
            {
                return this.internalObservable.Subscribe(observer);
            }
        }
                
        /// <summary>
        /// The mutually exclusive task executor.
        /// </summary>
        private class MutuallyExclusiveTaskExecutor
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
            public void Enqueue(Func<Task> action)
            {
                this.tasks.Enqueue(action);
                this.semaphore.Release();
            }

            /// <summary>
            /// Invokes the executor.
            /// </summary>
            /// <param name="cancellationToken">The cancellation task.</param>
            /// <returns>A <see cref="Task"/> representing the work performed</returns>
            public async Task Run(CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await this.semaphore.WaitAsync(cancellationToken);

                    // Process all available items in the queue.
                    Func<Task> task;
                    while (this.tasks.TryDequeue(out task))
                    {
                        // Execute the task we pulled out of the queue 
                        await task();
                    }
                }
            }
        }
    }
}
