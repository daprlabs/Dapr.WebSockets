namespace Dapr.WebSockets
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.IO;
    using System.Net.WebSockets;
    using System.Reactive;
    using System.Reactive.Disposables;
    using System.Reactive.Subjects;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A reactive WebSocket abstraction.
    /// </summary>
    public static class WebSocket
    {
        /// <summary>
        /// The <see cref="UTF8Encoding"/> without a byte order mark.
        /// </summary>
        private static readonly UTF8Encoding Utf8EncodingWithoutByteOrderMark = new UTF8Encoding(false);
        
        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning a subject used to send and receive messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The subject used to send and receive messages.</returns>
        public static ISubject<string> Connect(Uri uri, CancellationToken cancellationToken)
        {
            return new SocketSubject(uri, cancellationToken);
        }

        /// <summary>
        /// Represents an <see cref="ISubject{T}"/> backed by an observer and an observable.
        /// </summary>
        private class SocketSubject : ISubject<string>
        {
            /// <summary>
            /// The send actor.
            /// </summary>
            private readonly MutuallyExclusiveTaskExecutor sendActor;

            /// <summary>
            /// The receive actor.
            /// </summary>
            private readonly MutuallyExclusiveTaskExecutor receiveActor;

            /// <summary>
            /// The send memory stream.
            /// </summary>
            private readonly MemoryStream sendMemoryStream;

            /// <summary>
            /// The message encoder.
            /// </summary>
            private readonly StreamWriter sendEncoder;

            /// <summary>
            /// The cancellation token source.
            /// </summary>
            private readonly CancellationTokenSource cancellation;

            /// <summary>
            /// The socket.
            /// </summary>
            private readonly ClientWebSocket socket;

            /// <summary>
            /// The task that completes when this instance is subscribed to.
            /// </summary>
            private readonly TaskCompletionSource<IObserver<string>> observerTask = new TaskCompletionSource<IObserver<string>>();

            /// <summary>
            /// The task that completed when this instance has connected.
            /// </summary>
            private readonly TaskCompletionSource<Unit> connected = new TaskCompletionSource<Unit>();
            
            /// <summary>
            /// The connection address.
            /// </summary>
            private readonly Uri uri;

            /// <summary>
            /// Initializes a new instance of the <see cref="SocketSubject"/> class.
            /// </summary>
            /// <param name="uri">
            /// The uri.
            /// </param>
            /// <param name="cancellationToken">
            /// The cancellation token.
            /// </param>
            public SocketSubject(Uri uri, CancellationToken cancellationToken)
            {
                this.uri = uri;
                this.socket = new ClientWebSocket();
                this.socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                this.sendActor = new MutuallyExclusiveTaskExecutor();
                this.receiveActor = new MutuallyExclusiveTaskExecutor();
                
                this.cancellation = new CancellationTokenSource();
                this.cancellation.Token.Register(() => this.socket.Dispose());
                
                this.sendMemoryStream = new MemoryStream();
                this.sendEncoder = new StreamWriter(this.sendMemoryStream, Utf8EncodingWithoutByteOrderMark) { AutoFlush = true };
                this.sendActor.Schedule(
                    async () =>
                    {
                        try
                        {
                            await this.connected.Task;
                        }
                        catch
                        {
                            this.cancellation.Cancel();
                            throw;
                        }
                    });
                this.sendActor.Run(this.cancellation.Token).ContinueWith(
                _ =>
                {
                    Debug.WriteLine("Send Actor completed: Status: {0}, Exception: {1}", _.Status, _.Exception);
                    this.sendActor.Dispose();
                    this.sendMemoryStream.Dispose();
                    this.sendEncoder.Dispose();
                    this.cancellation.Cancel();
                });
                cancellationToken.Register(this.cancellation.Cancel);

                this.receiveActor.Schedule(this.ConnectAndRunReceiveLoop);
                this.receiveActor.Run(this.cancellation.Token)
                    .ContinueWith(
                        _ =>
                        {
                            Debug.WriteLine("Receive Actor completed: Status: {0}, Exception: {1}", _.Status, _.Exception);
                            this.receiveActor.Dispose();
                            this.cancellation.Cancel();
                        });
                this.cancellation.Token.Register(
                    () => this.observerTask.Task.ContinueWith(
                        _ =>
                        {
                            if (_.IsCompleted)
                            {
                                _.Result.OnError(new OperationCanceledException("Cancelled"));
                            }
                        }));
            }

            /// <summary>
            /// Notifies the observer that the provider has finished sending push-based notifications.
            /// </summary>
            public void OnCompleted()
            {
                this.sendActor.Schedule(
                    async () =>
                    {
                        await this.socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "OnCompleted", this.cancellation.Token);
                        this.cancellation.Cancel();
                    });
            }

            /// <summary>
            /// Notifies the observer that the provider has experienced an error condition.
            /// </summary>
            /// <param name="error">An object that provides additional information about the error.</param>
            public void OnError(Exception error)
            {
                this.sendActor.Schedule(
                    async () =>
                    {
                        await this.socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "OnError: " + error.Message, this.cancellation.Token);
                        this.cancellation.Cancel();
                    });
            }

            /// <summary>
            /// Provides the observer with new data.
            /// </summary>
            /// <param name="value">The current notification information.</param>
            public void OnNext(string value)
            {
                this.sendActor.Schedule(
                    async () =>
                    {
                        try
                        {
                            this.sendEncoder.Write(value);
                            this.sendMemoryStream.Seek(0, SeekOrigin.Begin);
                            var buffer = new ArraySegment<byte>(this.sendMemoryStream.GetBuffer(), 0, (int)this.sendMemoryStream.Length);
                           
                            await this.socket.SendAsync(buffer, WebSocketMessageType.Text, true, this.cancellation.Token);

                            this.sendMemoryStream.SetLength(0);
                        }
                        catch
                        {
                            this.cancellation.Cancel();
                            throw;
                        }
                    });
            }

            /// <summary>
            /// Notifies the provider that an observer is to receive notifications.
            /// </summary>
            /// <returns>
            /// A reference to an interface that allows observers to stop receiving notifications before the provider has finished sending them.
            /// </returns>
            /// <param name="observer">The object that is to receive notifications.</param>
            public IDisposable Subscribe(IObserver<string> observer)
            {
                if (this.connected.Task.IsFaulted)
                {
                    throw new Exception("Connection failed", this.connected.Task.Exception);
                }

                this.observerTask.SetResult(observer);
                return Disposable.Create(this.cancellation.Cancel);
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
                        if (received != null)
                        {
                            observer.OnNext(received);
                        }

                        if (socket.CloseStatus.HasValue)
                        {
                            observer.OnCompleted();
                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    observer.OnError(e);
                    throw;
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
                using (var ms = new MemoryStream())
                {
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(buffer, cancellationToken);
                        if (received.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        ms.Write(buffer.Array, buffer.Offset, received.Count);
                    }
                    while (!received.EndOfMessage && !cancellationToken.IsCancellationRequested);
                    ms.Seek(0, SeekOrigin.Begin);
                    using (var txt = new StreamReader(ms, Utf8EncodingWithoutByteOrderMark))
                    {
                        var final = txt.ReadToEnd();
                        return final;
                    }
                }
            }

            /// <summary>
            /// Connects to the remote endpoint and runs the receive loop.
            /// </summary>
            /// <returns>A <see cref="Task"/> which represents the work performed.</returns>
            private async Task ConnectAndRunReceiveLoop()
            {
                try
                {
                    await this.socket.ConnectAsync(this.uri, this.cancellation.Token);
                    var observer = await this.observerTask.Task;
                    this.connected.TrySetResult(Unit.Default);
                    await ReceivePump(observer, this.socket, this.cancellation.Token);
                }
                catch (Exception exception)
                {
                    this.connected.TrySetException(exception);
                    this.cancellation.Cancel();
                    throw;
                }
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
}