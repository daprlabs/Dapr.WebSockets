// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   A reactive WebSocket abstraction.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    using System;
    using System.Reactive.Concurrency;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Threading;
    using System.Threading.Tasks;
    
    using SuperSocket.ClientEngine;

    using WebSocket4Net;

    /// <summary>
    /// A reactive WebSocket abstraction.
    /// </summary>
    public static class WebSocket
    {
        /// <summary>
        /// The task scheduler.
        /// </summary>
        private static readonly IScheduler Scheduler = new TaskPoolScheduler(Task.Factory);
        
        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning a subject used to send and receive messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The subject used to send and receive messages.</returns>
        public static ISubject<string> Connect(Uri uri, CancellationToken cancellationToken)
        {
            var cancellation = new CancellationTokenSource();
            
            var result = MessagePump(uri, cancellation);
            cancellationToken.Register(cancellation.Cancel);
            return result;
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellation">The cancellation token source.</param>
        /// <returns>The observable stream of messages.</returns>
        private static CombinedSubject<string> MessagePump(Uri uri, CancellationTokenSource cancellation)
        {
            var outgoing = new Subject<string>();
            var incoming = Observable.Create<string>(
                observer =>
                {
                    var completed = false;
                    EventHandler<MessageReceivedEventArgs> onMessage = (sender, args) => observer.OnNext(args.Message);
                    EventHandler<ErrorEventArgs> onError = (sender, args) =>
                    {
                        observer.OnError(args.Exception);
                        completed = true;
                        cancellation.Cancel();
                    };
                    EventHandler onClosed = (sender, args) => cancellation.Cancel();

                    var socket = new WebSocket4Net.WebSocket(uri.ToString());
                    socket.MessageReceived += onMessage;
                    socket.Error += onError;
                    socket.Closed += onClosed;

                    cancellation.Token.Register(
                        () =>
                        {
                            if (!completed)
                            {
                                observer.OnCompleted();
                            }

                            socket.MessageReceived -= onMessage;
                            socket.Error -= onError;
                            socket.Closed -= onClosed;
                            try
                            {
                                socket.Close();
                            }
                            catch (Exception exception)
                            {
                                // Ignore errors closing the socket, but write them to debug output.
                                System.Diagnostics.Debug.WriteLine("Exception closing connection: {0}", exception);
                            }
                        });

                    // Connect to the socket and subscribe to the 'outgoing' observable while the connection remains opened.
                    try
                    {
                        var opened = Observable.FromEventPattern(_ => socket.Opened += _, _ => socket.Opened -= _).FirstAsync();
                        var closed = Observable.FromEventPattern(_ => socket.Closed += _, _ => socket.Closed -= _).FirstAsync();

                        var sendSubscription =
                            opened.SelectMany(_ => outgoing)
                                .TakeUntil(closed)
                                .SubscribeOn(Scheduler)
                                .ObserveOn(Scheduler)
                                .Subscribe(
                                    _ =>
                                    {
                                        try
                                        {
                                            socket.Send(_);
                                        }
                                        catch (Exception e)
                                        {
                                            observer.OnError(e);
                                            completed = true;
                                            cancellation.Cancel();
                                            throw;
                                        }
                                    });
                        socket.Open();
                        cancellation.Token.Register(sendSubscription.Dispose);
                    }
                    catch (Exception e)
                    {
                        observer.OnError(e);
                        completed = true;
                        cancellation.Cancel();
                        throw;
                    }

                    return Disposable.Create(cancellation.Cancel);
                }).SubscribeOn(Scheduler).ObserveOn(Scheduler).Publish().RefCount();
            return new CombinedSubject<string>(incoming, outgoing);
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
