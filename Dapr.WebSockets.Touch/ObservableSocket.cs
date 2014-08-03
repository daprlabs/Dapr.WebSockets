// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   The observable socket.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    using System;

    /// <summary>
    /// The observable socket.
    /// </summary>
    public class ObservableSocket
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ObservableSocket"/> class.
        /// </summary>
        /// <param name="incoming">
        /// The incoming message stream.
        /// </param>
        /// <param name="outgoing">
        /// The outgoing message observer.
        /// </param>
        /// <param name="status">
        /// The status stream.
        /// </param>
        public ObservableSocket(IObservable<string> incoming, IObserver<string> outgoing, IObservable<ConnectionStatus> status)
        {
            this.Incoming = incoming;
            this.Outgoing = outgoing;
            this.Status = status;
        }

        /// <summary>
        /// Gets the incoming message stream.
        /// </summary>
        public IObservable<string> Incoming { get; private set; }

        /// <summary>
        /// Gets the outgoing message observer.
        /// </summary>
        public IObserver<string> Outgoing { get; private set; }

        /// <summary>
        /// Gets the status.
        /// </summary>
        public IObservable<ConnectionStatus> Status { get; private set; }
    }
}