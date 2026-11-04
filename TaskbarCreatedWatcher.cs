using System.Runtime.InteropServices;

namespace TheWitness;

internal sealed class TaskbarCreatedWatcher : NativeWindow, IDisposable
{
    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private readonly Action onTaskbarCreated;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string lpString);

    internal TaskbarCreatedWatcher(Action onTaskbarCreated)
    {
        this.onTaskbarCreated = onTaskbarCreated;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TaskbarCreatedMessage)
        {
            onTaskbarCreated();
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        DestroyHandle();
    }
}
