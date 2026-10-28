using System;
using System.Threading;

namespace CsCom.Runtime;

/// <summary>
/// A minimal hot observable that pushes the values it receives to its subscribers. The event sinks generated with the
/// <c>observable</c> event style expose an <see cref="EventSubject{T}"/> for each event.
/// </summary>
/// <typeparam name="T">The type of the values.</typeparam>
public sealed class EventSubject<T> : IObservable<T>
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
    /// Pushes a value to the subscribers, in the order of their subscriptions. An exception thrown by a subscriber is propagated.
    /// </summary>
    /// <param name="value">The value.</param>
    public void OnNext(T value)
    {
        foreach (var observer in Volatile.Read(ref _observers))
        {
            observer.OnNext(value);
        }
    }

    /// <summary>
    /// Notifies the subscribers that no more values will be pushed and removes them. Later subscribers are notified immediately.
    /// </summary>
    public void OnCompleted()
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
            observer.OnCompleted();
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
