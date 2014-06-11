namespace Dapr.WebSockets
{
    using System;
    using System.Reactive;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Reactive.Threading.Tasks;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Dataflow;

    using Windows.Foundation;
    using Windows.Networking.Sockets;
    using Windows.Storage.Streams;

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
            var socket = CreateSocket();
            var cancellation = new CancellationTokenSource();
            var result = SocketReceivePump(socket, cancellation.Token);
            try
            {
                await socket.ConnectAsync(uri).AsTask(cancellation.Token);
            }
            catch
            {
                cancellation.Cancel();
            }

            return result;
        }

        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning an observer used to send messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The observer used to send messages.</returns>
        public static async Task<IObserver<string>> ConnectInput(Uri uri, CancellationToken cancellationToken)
        {
            var cancellation = new CancellationTokenSource();
            cancellationToken.Register(cancellation.Cancel);
            var socket = CreateSocket();
            var result = SocketSendPump(socket, cancellation.Token);
            try
            {
                await socket.ConnectAsync(uri).AsTask(cancellation.Token);
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }

            return result;
        }

        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning a subject used to send and receive messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The subject used to send and receive messages.</returns>
        public static async Task<ISubject<string>> Connect(Uri uri, CancellationToken cancellationToken)
        {
            var socket = CreateSocket();
            var cancellation = new CancellationTokenSource();
            cancellationToken.Register(cancellation.Cancel);

            var subject = new CombinedSubject<string>(SocketReceivePump(socket, cancellation.Token), SocketSendPump(socket, cancellation.Token));
            try
            {
                await socket.ConnectAsync(uri).AsTask(cancellation.Token);
            }
            catch
            {
                cancellation.Cancel();
                throw;
            }

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
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        /// <returns>
        /// The observer used to send messages to the socket.
        /// </returns>
        private static IObserver<string> SocketSendPump(IWebSocket socket, CancellationToken cancellationToken)
        {
            var writer = new DataWriter(socket.OutputStream);
            var outgoing = new ActionBlock<string>[] { null };
            outgoing[0] = new ActionBlock<string>(
                async next =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        outgoing[0].Complete();
                        return;
                    }

                    writer.WriteString(next);
                    await writer.StoreAsync().AsTask(cancellationToken);
                });

            outgoing[0].Completion.ContinueWith(_ => socket.Dispose(), CancellationToken.None);

            return outgoing[0].AsObserver();
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The observable stream of messages.</returns>
        private static IObservable<string> SocketReceivePump(MessageWebSocket socket, CancellationToken cancellationToken)
        {
            var incoming = new BufferBlock<string>();
            TypedEventHandler<MessageWebSocket, MessageWebSocketMessageReceivedEventArgs> read = (sender, args) =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        incoming.Complete();
                        return;
                    }

                    incoming.Post(ReadString(args));
                }
                catch (Exception e)
                {
                    ((ITargetBlock<string>)incoming).Fault(e);
                }
            };
            TypedEventHandler<IWebSocket, WebSocketClosedEventArgs> closed = (sender, args) => incoming.Complete();
            socket.MessageReceived += read;
            socket.Closed += closed;

            var canceled = new Subject<Unit>();
            cancellationToken.Register(canceled.OnCompleted);
            return incoming.AsObservable().TakeUntil(canceled);
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
    }
}
