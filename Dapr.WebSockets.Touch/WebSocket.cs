// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   A reactive WebSocket abstraction.
// </summary>
// --------------------------------------------------------------------------------------------------------------------
extern alias ws;

namespace Dapr.WebSockets
{
    using System;
    using System.Reactive.Concurrency;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Threading;

    using WebSocket4Net = ws.WebSocket4Net;

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
        /// <param name="scheduler">
        /// The scheduler.
        /// </param>
        /// <returns>
        /// The subject used to send and receive messages.
        /// </returns>
        public static ObservableSocket Connect(Uri uri, CancellationToken cancellationToken, IScheduler scheduler)
        {
            var cancellation = new CancellationTokenSource();
            
            var result = CreateObservableSocket(uri, cancellation, scheduler);
            cancellationToken.Register(cancellation.Cancel);
            return result;
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="uri">
        /// The uri.
        /// </param>
        /// <param name="cancellation">
        /// The cancellation token source.
        /// </param>
        /// <param name="scheduler">
        /// The scheduler.
        /// </param>
        /// <returns>
        /// The observable stream of messages.
        /// </returns>
        private static ObservableSocket CreateObservableSocket(Uri uri, CancellationTokenSource cancellation, IScheduler scheduler)
        {
            var status = new ReplaySubject<ConnectionStatus>(1);
            var outgoing = new Subject<string>();
            var incoming = Observable.Create<string>(
                observer =>
                {
                    status.OnNext(ConnectionStatus.Connecting);

                    var completed = false;
                    EventHandler<WebSocket4Net.MessageReceivedEventArgs> onMessage = (sender, args) => observer.OnNext(args.Message);
                    EventHandler<ws::SuperSocket.ClientEngine.ErrorEventArgs> onError = (sender, args) =>
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
                        var opened = Observable.FromEventPattern(_ => socket.Opened += _, _ => socket.Opened -= _).FirstAsync().Subscribe(_ => status.OnNext(ConnectionStatus.Opened));
                        var closed = Observable.FromEventPattern(_ => socket.Closed += _, _ => socket.Closed -= _).FirstAsync().Subscribe(_ => status.OnNext(ConnectionStatus.Closed));

                        // While the socket is opened, send outgoing messages to it.
                        var sendSubscription =
                            status.SkipWhile(_ => _ == ConnectionStatus.Connecting)
                                .TakeWhile(_ => _ == ConnectionStatus.Opened)
                                .SelectMany(_ => outgoing)
                                .SubscribeOn(scheduler)
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
                        cancellation.Token.Register(
                            () =>
                            {
                                sendSubscription.Dispose();
                                opened.Dispose();
                                closed.Dispose();
                                status.OnCompleted();
                            });
                    }
                    catch (Exception e)
                    {
                        observer.OnError(e);
                        completed = true;
                        cancellation.Cancel();
                        throw;
                    }

                    return Disposable.Create(cancellation.Cancel);
                }).SubscribeOn(scheduler).Publish().RefCount();

            return new ObservableSocket(incoming, outgoing, status);
        }
    }
}
