using System;
using System.Runtime.Serialization;

namespace Intrinio_Realtime
{
    [Serializable]
    public class IntrinioRealtimeAuthorizationException : IntrinioRealtimeException
    {
        public IntrinioRealtimeAuthorizationException(string message= "Unable to Authorize") : base(message)
        {
            
        }

        public IntrinioRealtimeAuthorizationException(Exception innerException, string message = "Unable to Authorize") :base(message, innerException)
        {
        }

        protected IntrinioRealtimeAuthorizationException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}