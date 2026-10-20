using W = System.Windows;
using WT = System.Windows.Threading;

namespace San14Tenkazu;

// A static resource notification may observe a visual only while it is loaded. Pending callbacks
// belong to that same lifetime; reloading reads the current resources even if a notification was missed.
internal sealed class VisualEventSubscription
{
    private readonly W.FrameworkElement owner;
    private readonly Action<Action> subscribe, unsubscribe;
    private readonly Action refresh;
    private readonly object gate = new();
    private WT.DispatcherOperation? pending;
    private bool active;

    internal static void Attach(W.FrameworkElement owner, Action<Action> subscribe, Action<Action> unsubscribe, Action refresh)
        => _ = new VisualEventSubscription(owner, subscribe, unsubscribe, refresh);

    private VisualEventSubscription(W.FrameworkElement owner, Action<Action> subscribe, Action<Action> unsubscribe, Action refresh)
    {
        this.owner = owner; this.subscribe = subscribe; this.unsubscribe = unsubscribe; this.refresh = refresh;
        owner.Loaded += Loaded;
        owner.Unloaded += Unloaded;
        if (owner.IsLoaded) Loaded(owner, new W.RoutedEventArgs());
    }

    private void Loaded(object sender, W.RoutedEventArgs e)
    {
        lock (gate)
        {
            if (active) return;
            active = true;
            subscribe(Changed);
        }
        refresh();
    }

    private void Unloaded(object sender, W.RoutedEventArgs e)
    {
        lock (gate)
        {
            if (!active) return;
            active = false;
            unsubscribe(Changed);
            pending?.Abort();
            pending = null;
        }
    }

    private void Changed()
    {
        lock (gate)
        {
            if (!active || pending is not null || owner.Dispatcher.HasShutdownStarted) return;
            pending = owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (gate)
                {
                    pending = null;
                    if (!active) return;
                }
                refresh();
            }));
        }
    }
}
