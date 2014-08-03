// --------------------------------------------------------------------------------------------------------------------
// <summary>
//   The connection status.
// </summary>
// --------------------------------------------------------------------------------------------------------------------

namespace Dapr.WebSockets
{
    /// <summary>
    /// The connection status.
    /// </summary>
    public enum ConnectionStatus
    {
        /// <summary>
        /// Connection is in progress.
        /// </summary>
        Connecting,

        /// <summary>
        /// The connection is opened.
        /// </summary>
        Opened,

        /// <summary>
        /// The connection is closed.
        /// </summary>
        Closed
    }
}