namespace Dapr.WebSockets
{
    using System;
    using System.Reactive;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
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
            var socket = await ConnectSocket(uri);
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
            var socket = await ConnectSocket(uri);
            return SocketSendPump(socket, cancellationToken);
        }

        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning a subject used to send and receive messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The subject used to send and receive messages.</returns>
        public static async Task<ISubject<string>> Connect(Uri uri, CancellationToken cancellationToken)
        {
            var socket = await ConnectSocket(uri);

            return new CombinedSubject<string>(SocketReceivePump(socket, cancellationToken), SocketSendPump(socket, cancellationToken));
        }

        /// <summary>
        /// Return a connected <see cref="MessageWebSocket"/>.
        /// </summary>
        /// <param name="uri">
        /// The uri to connect the result to.
        /// </param>
        /// <returns>
        /// A connected <see cref="MessageWebSocket"/>.
        /// </returns>
        private static async Task<MessageWebSocket> ConnectSocket(Uri uri)
        {
            var socket = new MessageWebSocket();
            socket.Control.MessageType = SocketMessageType.Utf8;
            await socket.ConnectAsync(uri);
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
            var process = new ActionBlock<string>[] { null };
            process[0] = new ActionBlock<string>(
                async next =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        process[0].Complete();
                        return;
                    }

                    writer.WriteString(next);
                    await writer.StoreAsync();
                });

            process[0].Completion.ContinueWith(_ => socket.Dispose(), CancellationToken.None);

            return process[0].AsObserver();
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The observable stream of messages.</returns>
        private static IObservable<string> SocketReceivePump(MessageWebSocket socket, CancellationToken cancellationToken)
        {
            var received =
                Observable
                    .FromEventPattern<TypedEventHandler<MessageWebSocket, MessageWebSocketMessageReceivedEventArgs>, MessageWebSocket, MessageWebSocketMessageReceivedEventArgs>(
                        _ => socket.MessageReceived += _,
                        _ => socket.MessageReceived -= _);

            var canceled = new Subject<Unit>();
            cancellationToken.Register(canceled.OnCompleted);

            var closed =
                Observable.FromEventPattern<TypedEventHandler<IWebSocket, WebSocketClosedEventArgs>, IWebSocket, WebSocketClosedEventArgs>(
                    _ => socket.Closed += _,
                    _ => socket.Closed -= _);
            return
                received
                    .Select(ReadString)
                    .TakeUntil(closed)
                    .TakeUntil(canceled)
                    .Publish()
                    .RefCount();
        }

        /// <summary>
        /// Read a single string and return it.
        /// </summary>
        /// <param name="e">The event the string is being read from.</param>
        /// <returns>The string which was read.</returns>
        private static string ReadString(EventPattern<MessageWebSocket, MessageWebSocketMessageReceivedEventArgs> e)
        {
            using (var stream = e.EventArgs.GetDataReader())
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
