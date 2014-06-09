// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   A reactive WebSocket abstraction.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    using System;
    using System.Reactive.Subjects;
    using System.Threading;
    using System.Threading.Tasks;

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
        public static Task<IObservable<string>> ConnectOutput(Uri uri, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning an observer used to send messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The observer used to send messages.</returns>
        public static Task<IObserver<string>> ConnectInput(Uri uri, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Connect to the provided WebSocket <paramref name="uri"/>, returning a subject used to send and receive messages.
        /// </summary>
        /// <param name="uri">The uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The subject used to send and receive messages.</returns>
        public static Task<ISubject<string>> Connect(Uri uri, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
