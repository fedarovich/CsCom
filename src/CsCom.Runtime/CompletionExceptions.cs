using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace CsCom.Runtime;

// Collects the exceptions of calls that all run whatever fails, e.g. the completions of a gate and the observers of the
// subjects of an event sink, and throws them once all of them ran: a single exception as it is, with its stack trace, so
// that an event call returns its HRESULT, and several in an AggregateException, like CancellationTokenSource.Cancel does.
internal static class CompletionExceptions
{
    public static void Add(ref List<Exception>? exceptions, Exception exception) => (exceptions ??= []).Add(exception);

    public static void ThrowIfAny(List<Exception>? exceptions)
    {
        if (exceptions is null)
        {
            return;
        }

        if (exceptions.Count == 1)
        {
            ExceptionDispatchInfo.Throw(exceptions[0]);
        }

        throw new AggregateException(exceptions);
    }
}
