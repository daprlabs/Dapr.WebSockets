namespace Dapr.WebSockets
{
    using System;
    using System.Runtime.Serialization;

    /// <summary>
    /// The not connected exception.
    /// </summary>
    public class NotConnectedException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NotConnectedException"/> class.
        /// </summary>
        public NotConnectedException() : base("Not connected.")
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NotConnectedException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        public NotConnectedException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="NotConnectedException"/> class.
        /// </summary>
        /// <param name="message">
        /// The message.
        /// </param>
        /// <param name="innerException">
        /// The inner exception.
        /// </param>
        public NotConnectedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}