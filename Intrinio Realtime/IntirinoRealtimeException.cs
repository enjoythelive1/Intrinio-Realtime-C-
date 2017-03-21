using System;
using System.Runtime.Serialization;

namespace IntrinioRealtime
{
    [Serializable]
    public class IntrinioRealtimeException : Exception
    {
        public IntrinioRealtimeException()
        {
        }

        public IntrinioRealtimeException(string message) : base(message)
        {
        }

        public IntrinioRealtimeException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected IntrinioRealtimeException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}