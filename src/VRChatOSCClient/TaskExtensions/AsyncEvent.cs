using System.Collections.Immutable;

namespace VRChatOSCClient.TaskExtensions;

public class AsyncEvent<T> where T : class
{
    private readonly Lock _lock = new();
    private ImmutableArray<T> _subscriptions = [];

    public bool HasSubscribers => _subscriptions.Length != 0;
    public IReadOnlyList<T> Subscriptions => _subscriptions;

    public void Add(T subscriber) {
        lock (_lock) {
            _subscriptions = _subscriptions.Add(subscriber);
        }
    }

    public void Remove(T subscriber) {
        lock (_lock) {
            _subscriptions = _subscriptions.Remove(subscriber);
        }
    }
}

internal static class AsyncEventExtensions
{
    extension (AsyncEvent<Func<Task>> eventHandler) {
        public async Task InvokeAsync() {
            IReadOnlyList<Func<Task>> subscribers = eventHandler.Subscriptions;
            Task[] tasks = new Task[subscribers.Count];

            for (int i = 0; i < subscribers.Count; i++) {
                tasks[i] = subscribers[i].Invoke();
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    extension<T>(AsyncEvent<Func<T, Task>> eventHandler)
    {
        public async Task InvokeAsync(T arg) {
            IReadOnlyList<Func<T, Task>> subscribers = eventHandler.Subscriptions;
            Task[] tasks = new Task[subscribers.Count];

            for (int i = 0; i < subscribers.Count; i++) {
                tasks[i] = subscribers[i].Invoke(arg);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    extension<T1, T2>(AsyncEvent<Func<T1, T2, Task>> eventHandler)
    {
        public async Task InvokeAsync(T1 arg1, T2 arg2) {
            IReadOnlyList<Func<T1, T2, Task>> subscribers = eventHandler.Subscriptions;
            Task[] tasks = new Task[subscribers.Count];

            for (int i = 0; i < subscribers.Count; i++) {
                tasks[i] = subscribers[i].Invoke(arg1, arg2);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}