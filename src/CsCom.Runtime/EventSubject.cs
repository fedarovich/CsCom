using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CsCom.Runtime;

/// <summary>
/// The base of <see cref="EventSubject{T}"/>, which completes the observables of an event sink together.
/// </summary>
/// <remarks>
/// The completion of a subject is final and removes its observers, so an observer that missed it would never be completed,
/// e.g. one that awaits the last value of an observable. So the exception of an observer stops neither the other observers
/// nor the other subjects: once all of them are completed, the exception is thrown as it is, or an
/// <see cref="AggregateException"/> of them when several observers threw, like the completions of
/// <see cref="EventConnection.DisposeWithCompletion"/>.
/// </remarks>
public abstract class EventSubject
{
    // Only EventSubject<T> derives from it.
    private protected EventSubject()
    {
    }

    /// <summary>
    /// Completes subjects like <see cref="EventSubject{T}.OnCompleted"/>, in their order: every one of them, also when an
    /// observer throws. A subject that another call has completed is skipped, also while that call still notifies its
    /// observers on another thread.
    /// </summary>
    /// <param name="subjects">The subjects.</param>
    /// <exception cref="ArgumentNullException">A subject is <see langword="null"/>; then no subject is completed.</exception>
    public static void Complete(params ReadOnlySpan<EventSubject> subjects)
    {
        ThrowIfNull(subjects);
        List<Exception>? exceptions = null;
        CompleteAll(subjects, ref exceptions);
        CompletionExceptions.ThrowIfAny(exceptions);
    }

    /// <summary>
    /// Completes subjects like <see cref="Complete"/> once a task has completed, also when it failed, e.g. the
    /// <see cref="EventConnection.DisposeAsync"/> of the connection of an event sink. The subjects are completed in the
    /// synchronization context of the caller, e.g. on the thread of a user interface, like the code after an
    /// <c>await</c>.
    /// </summary>
    /// <param name="disconnection">The task.</param>
    /// <param name="subjects">The subjects.</param>
    /// <returns>
    /// A task that completes once the subjects are completed, but not one that another call is still completing on another
    /// thread (see <see cref="Complete"/>). It fails with the exception of <paramref name="disconnection"/> as it is, e.g.
    /// the <see cref="COMException"/> of a sink that stays connected, or with that of an observer. When several failed, it
    /// fails with an <see cref="AggregateException"/> of them, whose first exception is that of
    /// <paramref name="disconnection"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="subjects"/> or a subject is <see langword="null"/>; then <paramref name="disconnection"/> is not
    /// awaited, and no subject is completed.
    /// </exception>
    public static ValueTask CompleteAfterAsync(ValueTask disconnection, params EventSubject[] subjects)
    {
        // The arguments are checked before the task is created, and the subjects are copied, so that the caller cannot
        // replace one with null afterwards. A null subject is an error of the caller, which is thrown at once and does
        // nothing else, like Task.WhenAll with a null task, which neither awaits nor observes the others: the caller has
        // started the disconnection, which closed the gate already, and its exception reaches
        // TaskScheduler.UnobservedTaskException, while awaiting it here would swallow it; completing the other subjects
        // would leave the caller unsure what a call that failed has done. The copy is one more array per call, once per
        // generated sink, which a params ReadOnlySpan would not save without accepting a null array (see
        // docs/known-issues.md).
        ArgumentNullException.ThrowIfNull(subjects);
        EventSubject[] checkedSubjects = [.. subjects];
        ThrowIfNull(checkedSubjects);
        return CompleteAsync(disconnection, checkedSubjects);

        static async ValueTask CompleteAsync(ValueTask disconnection, EventSubject[] subjects)
        {
            List<Exception>? exceptions = null;
            try
            {
                // Unlike the rest of the support code, it resumes in the synchronization context of the caller: there the
                // observers are completed.
                await disconnection.ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                CompletionExceptions.Add(ref exceptions, exception);
            }

            CompleteAll(subjects, ref exceptions);
            CompletionExceptions.ThrowIfAny(exceptions);
        }
    }

    /// <summary>
    /// Notifies every observer that no more values will be pushed, also when one throws, and adds the exceptions to the
    /// list. Nothing happens when the subject is completed already.
    /// </summary>
    private protected abstract void CompleteObservers(ref List<Exception>? exceptions);

    private static void ThrowIfNull(ReadOnlySpan<EventSubject> subjects)
    {
        foreach (var subject in subjects)
        {
            ArgumentNullException.ThrowIfNull(subject, nameof(subjects));
        }
    }

    private static void CompleteAll(ReadOnlySpan<EventSubject> subjects, ref List<Exception>? exceptions)
    {
        foreach (var subject in subjects)
        {
            subject.CompleteObservers(ref exceptions);
        }
    }
}

/// <summary>
/// A minimal hot observable that pushes the values it receives to its subscribers. The event sinks generated with the
/// <c>observable</c> event style expose an <see cref="EventSubject{T}"/> for each event.
/// </summary>
/// <typeparam name="T">The type of the values.</typeparam>
public sealed class EventSubject<T> : EventSubject, IObservable<T>
{
    private readonly Lock _lock = new();
    private IObserver<T>[] _observers = [];
    private bool _isCompleted;

    /// <inheritdoc/>
    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_lock)
        {
            if (!_isCompleted)
            {
                _observers = [.. _observers, observer];
                return new Subscription(this, observer);
            }
        }

        observer.OnCompleted();
        return new Subscription(null, observer);
    }

    /// <summary>
    /// Pushes a value to the subscribers, in the order of their subscriptions. An exception thrown by a subscriber is
    /// propagated, and the later subscribers do not get the value, like the handlers of a .NET event.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="OnCompleted"/>, which notifies every subscriber: a subscriber that missed a value still gets the
    /// next ones, but one that missed the completion would never be completed.
    /// </remarks>
    /// <param name="value">The value.</param>
    public void OnNext(T value)
    {
        foreach (var observer in Volatile.Read(ref _observers))
        {
            observer.OnNext(value);
        }
    }

    /// <summary>
    /// Notifies the subscribers that no more values will be pushed and removes them: every one of them, also when one
    /// throws. Once all of them are notified, the exception is thrown as it is, or an <see cref="AggregateException"/> of
    /// them when several threw. Later subscribers are notified immediately.
    /// </summary>
    public void OnCompleted() => Complete(this);

    /// <inheritdoc/>
    private protected override void CompleteObservers(ref List<Exception>? exceptions)
    {
        IObserver<T>[] observers;
        lock (_lock)
        {
            if (_isCompleted)
            {
                return;
            }

            _isCompleted = true;
            observers = _observers;
            _observers = [];
        }

        foreach (var observer in observers)
        {
            try
            {
                observer.OnCompleted();
            }
            catch (Exception exception)
            {
                CompletionExceptions.Add(ref exceptions, exception);
            }
        }
    }

    private void Unsubscribe(IObserver<T> observer)
    {
        lock (_lock)
        {
            int index = Array.IndexOf(_observers, observer);
            if (index >= 0)
            {
                _observers = [.. _observers.AsSpan(0, index), .. _observers.AsSpan(index + 1)];
            }
        }
    }

    private sealed class Subscription(EventSubject<T>? subject, IObserver<T> observer) : IDisposable
    {
        private EventSubject<T>? _subject = subject;

        public void Dispose()
        {
            Interlocked.Exchange(ref _subject, null)?.Unsubscribe(observer);
        }
    }
}
