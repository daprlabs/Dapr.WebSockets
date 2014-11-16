// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   The observable socket.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// The observable socket.
    /// </summary>
    public class ObservableSocket : IObservableSocket
    {
        /// <summary>
        /// The send function.
        /// </summary>
        private readonly Func<string, Task> sendFunc;

        /// <summary>
        /// The send function.
        /// </summary>
        private readonly Task connected;

        /// <summary>
        /// Initializes a new instance of the <see cref="ObservableSocket"/> class.
        /// </summary>
        /// <param name="incoming">
        /// The incoming message stream.
        /// </param>
        /// <param name="sendFunc">The send function.</param>
        /// <param name="status">
        /// The status stream.
        /// </param>
        public ObservableSocket(IObservable<string> incoming, Func<string, Task> sendFunc, IObservable<ConnectionStatus> status, Task connected)
        {
            this.Incoming = incoming;
            this.Status = status;
            this.sendFunc = sendFunc;
            this.connected = connected;
        }

        /// <summary>
        /// Gets the incoming message stream.
        /// </summary>
        public IObservable<string> Incoming { get; private set; }

        /// <summary>
        /// Gets the status.
        /// </summary>
        public IObservable<ConnectionStatus> Status { get; private set; }

        /// <summary>
        /// Send <paramref name="message"/> to the socket.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the work performed.
        /// </returns>
        public Task Send(string message)
        {
            return this.sendFunc(message);
        }

        /// <summary>
        /// Attempts to connect to the remote endpoint.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        public Task Connect()
        {
            return connected;
        }
    }
}