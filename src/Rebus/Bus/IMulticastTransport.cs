using System;

namespace Rebus.Bus
{
    /// <summary>
    /// Adds to <see cref="IDuplexTransport"/> the ability to send to multiple recipients at
    /// once by using pub sub messaging.
    /// </summary>
    public interface IMulticastTransport : IDuplexTransport
    {
        /// <summary>
        /// Indicates whether this multicast-capable transport IS in fact supposed to do multicast
        /// </summary>
        bool ManagesSubscriptions { get; }

        /// <summary>
        /// Subscribes the specified input queue address to messages of the specified type
        /// </summary>
        void Subscribe(Type messageType, string inputQueueAddress);

        /// <summary>
        /// Unsubscribes the specified input queue address from messages of the specified type
        /// </summary>
        void Unsubscribe(Type messageType, string inputQueueAddress);
    }
}