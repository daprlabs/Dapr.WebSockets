namespace Dapr.WebSockets
{
    using System;
    using System.Collections.Concurrent;
    using System.Reactive;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Threading;
    using System.Threading.Tasks;

    using Windows.Foundation;
    using Windows.Networking.Sockets;
    using Windows.Storage.Streams;

    /// <summary>
    /// A reactive WebSocket abstraction.
    /// </summary>
    public static class WebSocket
    {
        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning a subject used to send and receive messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The subject used to send and receive messages.</returns>
        public static ISubject<string> Connect(Uri uri, CancellationToken cancellationToken)
        {
            var socket = CreateSocket();
            var cancellation = new CancellationTokenSource();
            cancellationToken.Register(cancellation.Cancel);

            var subject = new CombinedSubject<string>(SocketReceivePump(socket, cancellation.Token), SocketSendPump(socket, uri, cancellation));
            cancellation.Token.Register(socket.Dispose);

            return subject;
        }

        /// <summary>
        /// Return a <see cref="MessageWebSocket"/>.
        /// </summary>
        /// <returns>
        /// A <see cref="MessageWebSocket"/>.
        /// </returns>
        private static MessageWebSocket CreateSocket()
        {
            var socket = new MessageWebSocket();
            socket.Control.MessageType = SocketMessageType.Utf8;
            return socket;
        }

        /// <summary>
        /// The socket send pump.
        /// </summary>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <param name="uri">
        /// The uri.
        /// </param>
        /// <param name="cancellation">
        /// The cancellation.
        /// </param>
        /// <returns>
        /// The observer used to send messages to the socket.
        /// </returns>
        private static IObserver<string> SocketSendPump(IWebSocket socket, Uri uri, CancellationTokenSource cancellation)
        {
            var sender = new MutuallyExclusiveTaskExecutor();
            sender.Schedule(
                async () =>
                {
                    try
                    {
                        await socket.ConnectAsync(uri);
                    }
                    catch
                    {
                        cancellation.Cancel();
                    }
                });

            var writer = new DataWriter(socket.OutputStream);
            var subject = new Subject<string>();
            cancellation.Token.Register(
                subject.Subscribe(
                    next => sender.Schedule(
                        async () =>
                        {
                            try
                            {
                                writer.WriteString(next);
                                await writer.StoreAsync().AsTask(cancellation.Token);
                            }
                            catch
                            {
                                cancellation.Cancel();
                            }
                        }),
                    e => cancellation.Cancel(),
                    cancellation.Cancel).Dispose);
            
            sender.Run(cancellation.Token).ContinueWith(
                _ =>
                {
                    if (_.Status != TaskStatus.RanToCompletion)
                    {
                        cancellation.Cancel();
                    }

                    sender.Dispose();
                },
                CancellationToken.None);
            return subject.AsObserver();
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The observable stream of messages.</returns>
        private static IObservable<string> SocketReceivePump(MessageWebSocket socket, CancellationToken cancellationToken)
        {
            var subject = new Subject<string>();
            TypedEventHandler<MessageWebSocket, MessageWebSocketMessageReceivedEventArgs> read = (sender, args) =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        subject.OnCompleted();
                        return;
                    }

                    subject.OnNext(ReadString(args));
                }
                catch (Exception e)
                {
                    subject.OnError(e);
                }
            };
            TypedEventHandler<IWebSocket, WebSocketClosedEventArgs> closed = (sender, args) => subject.OnCompleted();
            socket.MessageReceived += read;
            socket.Closed += closed;

            cancellationToken.Register(subject.OnCompleted);
            return subject.AsObservable();
        }

        /// <summary>
        /// Read a single string and return it.
        /// </summary>
        /// <param name="args">
        /// The args.
        /// </param>
        /// <returns>
        /// The string which was read.
        /// </returns>
        private static string ReadString(MessageWebSocketMessageReceivedEventArgs args)
        {
            using (var stream = args.GetDataReader())
            {
                stream.UnicodeEncoding = UnicodeEncoding.Utf8;
                return stream.ReadString(stream.UnconsumedBufferLength);
            }
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
        private class MutuallyExclusiveTaskExecutor : IDisposable
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
            /// <returns>A <see cref="Task"/> representing the work performed</returns>
            public async Task Run(CancellationToken cancellationToken)
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
                        // Execute the task we pulled out of the queue.
                        await task();
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
}
