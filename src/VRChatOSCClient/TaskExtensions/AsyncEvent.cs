using System.Collections.Immutable;

namespace VRChatOSCClient.TaskExtensions;

public class AsyncEvent<T> where T : class
{
    private readonly Lock _lock = new();
    private ImmutableArray<T> _subscriptions = [];

    public bool HasSubscribers => _subscriptions.Length != 0;
    public IReadOnlyList<T> Subscriptions => _subscriptions;

    public void Add(T subscriber) {
        using (_lock.EnterScope()) {
            _subscriptions = _subscriptions.Add(subscriber);
        }
    }

    public void Remove(T subscriber) {
        using (_lock.EnterScope()) {
            _subscriptions = _subscriptions.Remove(subscriber);
        }
    }
}

internal static class AsyncEventExtensions
{
    public static async Task InvokeAsync(this AsyncEvent<Func<Task>> eventHandler) {
        IReadOnlyList<Func<Task>> subscribers = eventHandler.Subscriptions;

        for (int i = 0; i < subscribers.Count; i++) {
            await subscribers[i].Invoke().ConfigureAwait(false);
        }
    }
    public static async Task InvokeAsync<T>(this AsyncEvent<Func<T, Task>> eventHandler, T arg) {
        IReadOnlyList<Func<T, Task>> subscribers = eventHandler.Subscriptions;

        for (int i = 0; i < subscribers.Count; i++) {
            await subscribers[i].Invoke(arg).ConfigureAwait(false);
        }
    }

    public static async Task InvokeAsync<T1, T2>(this AsyncEvent<Func<T1, T2, Task>> eventHandler, T1 arg1, T2 arg2) {
        IReadOnlyList<Func<T1, T2, Task>> subscribers = eventHandler.Subscriptions;

        for (int i = 0; i < subscribers.Count; i++) {
            await subscribers[i].Invoke(arg1, arg2).ConfigureAwait(false);
        }
    }
}