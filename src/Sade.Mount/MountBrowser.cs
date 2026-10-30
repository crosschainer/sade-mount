using Sandbox;
using Sandbox.Mounting;
using System.Threading;

namespace Sade.Mount;

/// <summary>
/// Keeps the browser tree separate from the complete registry used by saved paths and dependencies.
/// </summary>
internal sealed class MountBrowser(BaseGameMount mount) : IDisposable
{
    private static readonly Sandbox.Diagnostics.Logger Log = new("SADE browser");
    private readonly object timerLock = new();
    private ResourceFolder? root;
    private ResourceLoader? last;
    private Timer? timer;
    private bool fallbackStopped;
    private long deadline;
    private int generation, scheduled, finished, disposed;

    internal void Schedule()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref scheduled, 1) != 0) return;
        root = mount.RootFolder;
        last = mount.Resources.LastOrDefault();
        if (root is null) { Complete(); return; }
        deadline = Environment.TickCount64 + 5000;
        var version = Interlocked.Increment(ref generation);

        // MountInternal builds RootFolder synchronously after our Mount override returns.
        // Post queues behind that call; MainThread.Queue / MainThread().OnCompleted can
        // instead execute inline when called from the engine's main-thread context.
        var context = ThreadSafe.IsMainThread ? SynchronizationContext.Current : null;
        if (context is not null)
        {
            try { context.Post(_ => TryApply(version), null); }
            catch (Exception) { /* The bounded dispatcher fallback uses the current context. */ }
        }
        ArmFallback();
    }

    private bool Current(int version) => Volatile.Read(ref disposed) == 0
        && Volatile.Read(ref finished) == 0 && version == Volatile.Read(ref generation);

    private void TryApply(int version)
    {
        if (!Current(version) || !ThreadSafe.IsMainThread) return;
        if (!ReferenceEquals(root, mount.RootFolder)) { Complete(); return; }
        if (!mount.IsMounted || root is null) return;

        // The last registered loader is also the last entry in the engine's tree build.
        // Check its ancestry too, so a reused loader cannot satisfy this with an old tree.
        if (last is not null)
        {
            var attachedRoot = last.Folder;
            if (attachedRoot is null) return;
            while (attachedRoot.Parent is not null) attachedRoot = attachedRoot.Parent;
            if (!ReferenceEquals(attachedRoot, root)) return;
        }
        try { Prune(root); }
        catch (Exception e) { Log.Warning(e, "SADE browser folder cleanup failed"); }
        finally { Complete(); }
    }

    private static void Prune(ResourceFolder folder)
    {
        folder.Files.RemoveAll(file => (file.Flags & ResourceFlags.DeveloperOnly) != 0);
        foreach (var child in folder.Folders) Prune(child);
        folder.Folders.RemoveAll(child => child.Files.Count == 0 && child.Folders.Count == 0);
    }

    private void ArmFallback()
    {
        lock (timerLock)
        {
            if (fallbackStopped || !Current(Volatile.Read(ref generation))) return;
            if (timer is null)
            {
                // Do not retain an editor/menu/game execution context across mount startup.
                if (ExecutionContext.IsFlowSuppressed()) CreateTimer();
                else { using (ExecutionContext.SuppressFlow()) CreateTimer(); }
            }
            timer!.Change(100, Timeout.Infinite);
        }

        void CreateTimer() => timer = new Timer(_ => Dispatch(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private void Dispatch()
    {
        var version = Volatile.Read(ref generation);
        if (!Current(version)) return;
        if (Environment.TickCount64 >= deadline)
        {
            // A large tree build can occupy the main thread beyond this deadline.
            // Bound timer retries without invalidating callbacks already waiting there.
            StopFallback();
            return;
        }
        try
        {
            // This callback runs on a timer thread, so the public dispatcher posts to the
            // main thread rather than taking its synchronous fast path.
            GameTask.MainThread().GetAwaiter().OnCompleted(() => TryApply(version));
        }
        catch (Exception) { /* Dispatcher may be unavailable during engine startup. */ }
        ArmFallback();
    }

    private void Complete()
    {
        Interlocked.Exchange(ref finished, 1);
        StopFallback();
    }

    private void StopFallback()
    {
        lock (timerLock)
        {
            fallbackStopped = true;
            timer?.Dispose();
            timer = null;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref disposed, 1);
        Interlocked.Increment(ref generation);
        Complete();
    }
}
