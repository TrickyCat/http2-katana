﻿namespace Microsoft.Http2.Protocol.Framing
{
    internal enum GoAwayStatusCode : int
    {
        Ok = 0,
        ProtocolError = 1,
        InternalError = 2,
    }
}
