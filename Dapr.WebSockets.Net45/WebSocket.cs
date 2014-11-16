// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   A reactive WebSocket abstraction.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    using System;
    using System.Threading;

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
            return new ObservableClientWebSocket(uri, cancellationToken);
        }
    }
}
