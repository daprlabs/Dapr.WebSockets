// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   Represents an observable WebSocket.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    using System;
    using System.Threading.Tasks;

    /// <summary>
    /// Represents an observable WebSocket.
    /// </summary>
    public interface IObservableSocket
    {
        /// <summary>
        /// Gets the incoming message stream.
        /// </summary>
        IObservable<string> Incoming { get; }

        /// <summary>
        /// Gets the status.
        /// </summary>
        IObservable<ConnectionStatus> Status { get; }

        /// <summary>
        /// Send <paramref name="message"/> to the socket.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the work performed.
        /// </returns>
        Task Send(string message);

        /// <summary>
        /// Attempts to connect to the remote endpoint.
        /// </summary>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task Connect();
    }
}