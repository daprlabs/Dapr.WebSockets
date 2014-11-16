// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   A reactive WebSocket abstraction.
// </summary>
// --------------------------------------------------------------------------------------------------------------------
#if false
extern alias ws;

namespace Dapr.WebSockets
{
    using System;
    using System.Reactive.Concurrency;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Threading;
    using System.Threading.Tasks;

    using WebSocket4Net = ws.WebSocket4Net;

    /// <summary>
    /// A reactive WebSocket abstraction.
    /// </summary>
    public static class WebSocket_Disabled
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
        public static IObservableSocket Connect(Uri uri, CancellationToken cancellationToken, IScheduler scheduler)
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
        private static IObservableSocket CreateObservableSocket(Uri uri, CancellationTokenSource cancellation, IScheduler scheduler)
        {
            var status = new ReplaySubject<ConnectionStatus>(1);
            var socket = new WebSocket4Net.WebSocket(uri.ToString());
            Func<string, Task> send = message =>
                {
                    var tcs = new TaskCompletionSource<int>();
                    
                    try
                    {
                        if (socket.State != WebSocket4Net.WebSocketState.Open)
                        {
                            socket.Open();
                        }

                        socket.Send(message);
                        tcs.SetResult(0);
                    }
                    catch (Exception e)
                    {
                        tcs.SetException(e);
                    }

                    return tcs.Task;
                };
            socket.Open();
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
                            catch
                            {
                                // Ignore errors closing the socket.
                            }
                        });

                    return Disposable.Create(cancellation.Cancel);
                }).SubscribeOn(scheduler).ObserveOn(scheduler).Publish().RefCount();

            return new ObservableSocket(incoming, send, status);
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
    }
}
#endif