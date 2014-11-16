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
    using System.Threading.Tasks;

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
        /// <returns>
        /// The subject used to send and receive messages.
        /// </returns>
        public static IObservableSocket Connect(Uri uri, CancellationToken cancellationToken)
        {
            var cancellation = new CancellationTokenSource();
            
            var result = CreateObservableSocket(uri, cancellation);
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
        /// <returns>
        /// The observable stream of messages.
        /// </returns>
        private static IObservableSocket CreateObservableSocket(Uri uri, CancellationTokenSource cancellation)
        {
            var status = new ReplaySubject<ConnectionStatus>(1);
            var socket = new WebSocket4Net.WebSocket(uri.ToString());
            var connected = new TaskCompletionSource<int>();
            var dispatcher = new ConcurrentExclusiveSchedulerPair();
            var sendScheduler = new TaskPoolScheduler(new TaskFactory(dispatcher.ExclusiveScheduler));
            var receiveScheduler = new TaskPoolScheduler(new TaskFactory(dispatcher.ConcurrentScheduler));
            Func<string, Task> send = message =>
                {
                    var tcs = new TaskCompletionSource<int>();
                    sendScheduler.Schedule(
                        () =>
                        {
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
                        });
                    ;

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
                    socket.Opened += (sender, args) => connected.TrySetResult(0);
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

                            dispatcher.Complete();
                        });

                    return Disposable.Create(cancellation.Cancel);
                }).SubscribeOn(receiveScheduler).Publish().RefCount();
            
            return new ObservableSocket(incoming, send, status, connected.Task);
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