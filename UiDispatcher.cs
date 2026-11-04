namespace TheWitness;

internal static class UiDispatcher
{
    private static Control? marshal;

    internal static void Initialize()
    {
        marshal = new Control();
        _ = marshal.Handle;
    }

    internal static T? Invoke<T>(Func<T> func) where T : class
    {
        if (marshal is not { IsHandleCreated: true })
        {
            return null;
        }

        try
        {
            return (T?)marshal.Invoke(func);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    internal static void InvokeAction(Action action)
    {
        if (marshal is not { IsHandleCreated: true })
        {
            return;
        }

        try
        {
            marshal.Invoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
