// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   A reactive WebSocket abstraction.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    using System;
    using System.Collections.Concurrent;
    using System.Reactive.Disposables;
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
        /// <param name="uri">
        /// The uri.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// The subject used to send and receive messages.
        /// </returns>
        public static IObservableSocket Connect(Uri uri, CancellationToken cancellationToken)
        {
            var cancellation = new CancellationTokenSource();
            cancellationToken.Register(cancellation.Cancel);
            return CreateObservableSocket(uri, cancellation);
        }

        /// <summary>
        /// Returns a new observable socket.
        /// </summary>
        /// <param name="uri">
        /// The uri.
        /// </param>
        /// <param name="cancellation">
        /// The cancellation.
        /// </param>
        /// <returns>
        /// The <see cref="ObservableSocket"/>.
        /// </returns>
        private static ObservableSocket CreateObservableSocket(Uri uri, CancellationTokenSource cancellation)
        {
            var status = new ReplaySubject<ConnectionStatus>(1);
            Func<string, Task> send = ThrowOnSend;
            var connected = new TaskCompletionSource<int>();
            var incoming = Observable.Create<string>(
                observer =>
                {
                    status.OnNext(ConnectionStatus.Connecting);
                    bool[] completed = { false };

                    try
                    {
                        var socket = CreateSocket();
                        SocketReceivePump(socket, cancellation.Token).Subscribe(
                            observer.OnNext,
                            exception =>
                            {
                                observer.OnError(exception);
                                completed[0] = true;
                                cancellation.Cancel();
                            },
                            cancellation.Cancel);

                        cancellation.Token.Register(
                            () =>
                            {
                                if (!completed[0])
                                {
                                    observer.OnCompleted();
                                }

                                try
                                {
                                    socket.Close(1000, string.Empty);
                                }
                                // ReSharper disable once EmptyGeneralCatchClause
                                catch
                                {
                                    // Ignore errors closing the socket.
                                }
                                finally
                                {
                                    status.OnNext(ConnectionStatus.Closed);
                                }
                            });

                        send = SocketSendPump(socket, uri, cancellation, connected);
                        status.OnNext(ConnectionStatus.Opened);
                    }
                    catch (Exception e)
                    {
                        observer.OnError(e);
                        completed[0] = true;
                        cancellation.Cancel();
                        throw;
                    }

                    return Disposable.Create(cancellation.Cancel);
                }).Publish().RefCount();

            return new ObservableSocket(incoming, send, status, connected.Task);
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
        private static Func<string, Task> SocketSendPump(IWebSocket socket, Uri uri, CancellationTokenSource cancellation, TaskCompletionSource<int> connected)
        {
            var sender = new MutuallyExclusiveTaskExecutor();
            sender.Schedule(
                async () =>
                {
                    try
                    {
                        await socket.ConnectAsync(uri).AsTask(cancellation.Token).ConfigureAwait(false);
                        connected.TrySetResult(0);
                    }
                    catch
                    {
                        cancellation.Cancel();
                    }
                });

            var writer = new DataWriter(socket.OutputStream);
            
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
            return next => sender.Schedule(
                async () =>
                    {
                        try
                        {
                            writer.WriteString(next);
                            await writer.StoreAsync().AsTask(cancellation.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            cancellation.Cancel();
                        }
                    });
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="socket">
        /// The socket.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token source.
        /// </param>
        /// <returns>
        /// The observable stream of messages.
        /// </returns>
        private static IObservable<string> SocketReceivePump(MessageWebSocket socket, CancellationToken cancellationToken)
        {
            return Observable.Create<string>(
                observer =>
                {
                    var cancellation = new CancellationTokenSource();
                    cancellationToken.Register(cancellation.Cancel);
                    TypedEventHandler<MessageWebSocket, MessageWebSocketMessageReceivedEventArgs> read = (sender, args) =>
                    {
                        try
                        {
                            if (cancellation.Token.IsCancellationRequested)
                            {
                                return;
                            }

                            observer.OnNext(ReadString(args));
                        }
                        catch (Exception e)
                        {
                            observer.OnError(e);
                            cancellation.Cancel();
                        }
                    };
                    TypedEventHandler<IWebSocket, WebSocketClosedEventArgs> closed = (sender, args) =>
                    {
                        observer.OnCompleted();
                        cancellation.Cancel();
                    };
                    socket.MessageReceived += read;
                    socket.Closed += closed;

                    cancellation.Token.Register(
                        () =>
                        {
                            try
                            {
                                socket.Closed -= closed;
                                socket.MessageReceived -= read;
                            }
                            catch (ObjectDisposedException)
                            {
                            }
                        });

                    return cancellation.Cancel;
                });
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
        /// Throws an exception when invoked.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        private static Task ThrowOnSend(string message)
        {
            throw new NotConnectedException();
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
            /// <returns>
            /// A <see cref="Task"/> representing the work performed.
            /// </returns>
            public Task Schedule(Func<Task> action)
            {
                var completion = new TaskCompletionSource<int>();
                this.tasks.Enqueue(
                    async () =>
                        {
                            try
                            {
                                await action().ConfigureAwait(false);
                                completion.SetResult(0);
                            }
                            catch (Exception e)
                            {
                                completion.SetException(e);
                            }
                        });
                this.semaphore.Release();
                return completion.Task;
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
                    await this.semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                    // Process all available items in the queue.
                    Func<Task> task;
                    while (this.tasks.TryDequeue(out task))
                    {
                        // Execute the task we pulled out of the queue.
                        await task().ConfigureAwait(false);
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
