using Microsoft.Extensions.Logging;

namespace YawVR.Link;

/// <summary>
/// Runs user callbacks: on the network thread by default, or on a host <see cref="SynchronizationContext"/>
/// (WPF, WinForms) when one is supplied. A throwing callback is logged and never kills a receive loop.
/// </summary>
internal sealed class CallbackInvoker
{
    private readonly SynchronizationContext? context;
    private readonly ILogger log;

    public CallbackInvoker(SynchronizationContext? context, ILogger log)
    {
        this.context = context;
        this.log = log;
    }

    /// <summary>Fire-and-forget notification.</summary>
    public void Post(string name, Action action)
    {
        if (context is null) { Run(name, action); return; }
        context.Post(_ => Run(name, action), null);
    }

    /// <summary>Blocking call whose result is needed (a reply). Returns <paramref name="fallback"/> if the callback throws.</summary>
    public T Call<T>(string name, Func<T> func, T fallback)
    {
        T result = fallback;
        if (context is null) Run(name, () => result = func());
        else context.Send(_ => Run(name, () => result = func()), null);
        return result;
    }

    public void Call(string name, Action action)
    {
        if (context is null) Run(name, action);
        else context.Send(_ => Run(name, action), null);
    }

    private void Run(string name, Action action)
    {
        try { action(); }
        catch (Exception ex) { log.LogError(ex, "Callback {Callback} threw", name); }
    }
}
