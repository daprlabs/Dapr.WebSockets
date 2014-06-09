namespace Dapr.WebSockets
{
    using System;
    using System.Reactive;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Reactive.Threading.Tasks;
    using System.Threading;
    using System.Threading.Tasks;

    using SuperSocket.ClientEngine;

    using WebSocket4Net;

    /// <summary>
    /// A reactive WebSocket abstraction.
    /// </summary>
    public static class ReactiveWebSocket
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
        /// Return a connected <see cref="WebSocket"/>.
        /// </summary>
        /// <param name="uri">
        /// The uri to connect the result to.
        /// </param>
        /// <returns>
        /// A connected <see cref="WebSocket"/>.
        /// </returns>
        private static async Task<WebSocket> ConnectSocket(Uri uri)
        {
            var socket = new WebSocket(uri.ToString());
            var opened = Observable.FromEventPattern(_ => socket.Opened += _, _ => socket.Opened -= _);
            socket.Open();
            await opened.ToTask();
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
        private static IObserver<string> SocketSendPump(WebSocket socket, CancellationToken cancellationToken)
        {
            return Observer.Create<string>(
                next =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        socket.Send(next);
                    }
                    else
                    {
                        throw new OperationCanceledException("Cancellation was requested.");
                    }
                });
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The observable stream of messages.</returns>
        private static IObservable<string> SocketReceivePump(WebSocket socket, CancellationToken cancellationToken)
        {
            var closed = Observable.FromEventPattern(_ => socket.Closed += _, _ => socket.Closed -= _);
            var error = Observable.FromEventPattern<ErrorEventArgs>(_ => socket.Error += _, _ => socket.Error -= _);
            var received = Observable.FromEventPattern<MessageReceivedEventArgs>(_ => socket.MessageReceived += _, _ => socket.MessageReceived -= _);
            var canceled = new Subject<Unit>();
            cancellationToken.Register(canceled.OnCompleted);

            return Observable.Create<string>(
                observer =>
                {
                    var subscription = received.Select(e => e.EventArgs.Message).TakeUntil(closed).TakeUntil(error).TakeUntil(canceled).Subscribe(observer);

                    return Disposable.Create(
                        () =>
                        {
                            socket.Close();
                            subscription.Dispose();
                        });
                }).Publish().RefCount();
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
