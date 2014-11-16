// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   Represents an observable WebSocket.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Net.WebSockets;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Represents an observable WebSocket.
    /// </summary>
    internal class ObservableClientWebSocket : IObservableSocket
    {
        /// <summary>
        /// The <see cref="UTF8Encoding"/> without a byte order mark.
        /// </summary>
        private static readonly UTF8Encoding Utf8EncodingWithoutByteOrderMark = new UTF8Encoding(false);

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
        /// The connection address.
        /// </summary>
        private readonly Uri uri;

        /// <summary>
        /// The status.
        /// </summary>
        private readonly ISubject<ConnectionStatus> status;

        /// <summary>
        /// The task that completed when this instance has connected.
        /// </summary>
        private TaskCompletionSource<ClientWebSocket> socketTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObservableClientWebSocket"/> class.
        /// </summary>
        /// <param name="uri">
        /// The uri.
        /// </param>
        /// <param name="cancellationToken">
        /// The cancellation token.
        /// </param>
        public ObservableClientWebSocket(Uri uri, CancellationToken cancellationToken)
        {
            this.uri = uri;

            this.status = new ReplaySubject<ConnectionStatus>(1);
            this.Status = this.status.AsObservable();

            this.sendActor = new MutuallyExclusiveTaskExecutor();
            this.receiveActor = new MutuallyExclusiveTaskExecutor();

            this.cancellation = new CancellationTokenSource();
            cancellationToken.Register(this.cancellation.Cancel);

            this.sendMemoryStream = new MemoryStream();
            this.sendEncoder = new StreamWriter(this.sendMemoryStream, Utf8EncodingWithoutByteOrderMark);
            this.sendEncoder.AutoFlush = true;

            this.sendActor.Start(this.cancellation.Token).ContinueWith(
                _ =>
                    {
                        Debug.WriteLine("Send Actor completed: Status: {0}, Exception: {1}", _.Status, _.Exception);
                        this.sendActor.Dispose();
                        this.sendMemoryStream.Dispose();
                        this.sendEncoder.Dispose();
                    });

            this.receiveActor.Start(this.cancellation.Token).ContinueWith(
                _ =>
                    {
                        Debug.WriteLine("Receive Actor completed: Status: {0}, Exception: {1}", _.Status, _.Exception);
                        this.receiveActor.Dispose();
                    });

            this.Incoming = Observable.Create<string>(
                observer =>
                    {
                        if (this.cancellation.IsCancellationRequested)
                        {
                            throw new OperationCanceledException();
                        }
                        
                        var observerCancellation = new CancellationTokenSource();
                        this.cancellation.Token.Register(
                            () =>
                                {
                                    observerCancellation.Cancel();
                                    observer.OnCompleted();
                                });
                        this.receiveActor.Schedule(() => this.ReceivePump(observer, observerCancellation.Token));
                        return Disposable.Create(observerCancellation.Cancel);
                    }).Publish().RefCount();
        }

        /// <summary>
        /// Gets the incoming messages.
        /// </summary>
        public IObservable<string> Incoming { get; private set; }

        /// <summary>
        /// Gets the status.
        /// </summary>
        public IObservable<ConnectionStatus> Status { get; private set; }

        /// <summary>
        /// Sends a message over the socket.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public Task Send(string message)
        {
            if (this.cancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            var result = new TaskCompletionSource<int>();
            this.sendActor.Schedule(() => this.SendInternal(message).CompleteWith(result));
            return result.Task;
        }

        /// <summary>
        /// Attempts to connect to the remote endpoint.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public Task Connect()
        {
            return this.GetConnectedSocket();
        }

        /// <summary>
        /// Returns a value indicating whether or not a connection attempt should be made.
        /// </summary>
        /// <param name="socket">The socket task to examine.</param>
        /// <returns>A value indicating whether or not a connection attempt should be made.</returns>
        private static bool ShouldConnect(TaskCompletionSource<ClientWebSocket> socket)
        {
            if (socket == null)
            {
                return true;
            }

            var task = socket.Task;

            var connectionClosed = task.Status == TaskStatus.RanToCompletion && task.Result != null
                   && task.Result.State != WebSocketState.Open;

            var connectionFaulted = task.Status == TaskStatus.Faulted || task.Status == TaskStatus.Canceled;

            return connectionClosed || connectionFaulted;
        }

        /// <summary>
        /// Receive a single string from the provided <paramref name="socket"/>.
        /// </summary>
        /// <param name="socket">The socket.</param>
        /// <param name="buffer">The scratch buffer.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The received string.</returns>
        private static async Task<string> ReceiveString(
            ClientWebSocket socket,
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult received;
                do
                {
                    if (socket.State != WebSocketState.Open)
                    {
                        throw new InvalidOperationException("Socket is not open.");
                    }

                    received = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
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
                    return txt.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// Returns a connected socket.
        /// </summary>
        /// <returns>A connected socket.</returns>
        private async Task<ClientWebSocket> GetConnectedSocket()
        {
            while (true)
            {
                if (this.cancellation.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                var existing = this.socketTask;
                if (ShouldConnect(existing))
                {
                    // Replace the socket task.
                    var replacement = new TaskCompletionSource<ClientWebSocket>();
                    var original = Interlocked.CompareExchange(ref this.socketTask, replacement, existing);

                    if (original != existing)
                    {
                        if (original == null)
                        {
                            continue;
                        }

                        // Another thread is creating the replacement, return that.
                        return await original.Task.ConfigureAwait(false);
                    }

                    // Dispose the existing socket.
                    if (existing != null && existing.Task.Status == TaskStatus.RanToCompletion)
                    {
                        if (existing.Task.Result != null)
                        {
                            existing.Task.Result.Dispose();
                        }
                    }

                    // Create a new, connected socket.
                    var socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    try
                    {
                        this.status.OnNext(ConnectionStatus.Connecting);
                        await socket.ConnectAsync(this.uri, this.cancellation.Token).ConfigureAwait(false);
                        this.status.OnNext(ConnectionStatus.Opened);
                        replacement.TrySetResult(socket);
                    }
                    catch (Exception exception)
                    {
                        this.status.OnNext(ConnectionStatus.Closed);
                        replacement.TrySetException(exception);
                        throw;
                    }

                    return socket;
                }

                return await existing.Task.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sends a message over the socket.
        /// </summary>
        /// <param name="value">The message.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        private async Task SendInternal(string value)
        {
            try
            {
                var socket = await this.GetConnectedSocket().ConfigureAwait(false);
                this.sendEncoder.Write(value);
                this.sendMemoryStream.Seek(0, SeekOrigin.Begin);
                var buffer = new ArraySegment<byte>(
                    this.sendMemoryStream.GetBuffer(),
                    0,
                    (int)this.sendMemoryStream.Length);

#if __ANDROID__ || __IOS__
    // Mono bug, fixed in checkin https://github.com/mono/mono/pull/1115
            const bool EndOfMessage = false;
#else
                const bool EndOfMessage = true;
#endif
                await
                    socket.SendAsync(buffer, WebSocketMessageType.Text, EndOfMessage, this.cancellation.Token)
                        .ConfigureAwait(false);

                this.sendMemoryStream.SetLength(0);
            }
            catch (Exception e)
            {
                Debug.WriteLine("SendInternal failed with exception: " + e.ToDetailedString());
                throw;
            }
        }

        /// <summary>
        /// The pump for received messages.
        /// </summary>
        /// <param name="observer">The observer.</param>
        /// <param name="observerCancellation">The cancellation token.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        private async Task ReceivePump(IObserver<string> observer, CancellationToken observerCancellation)
        {
            try
            {
                var socket = await this.GetConnectedSocket().ConfigureAwait(false);
                var bytes = new byte[65536];
                while (!observerCancellation.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var buffer = new ArraySegment<byte>(bytes);
                    var received = await ReceiveString(socket, buffer, observerCancellation).ConfigureAwait(false);
                    if (received != null)
                    {
                        observer.OnNext(received);
                    }
                }

                observer.OnCompleted();
            }
            catch (Exception e)
            {
                Debug.WriteLine("ReceivePump failed with exception: " + e.ToDetailedString());
                observer.OnError(e);
                throw;
            }
        }
    }
}