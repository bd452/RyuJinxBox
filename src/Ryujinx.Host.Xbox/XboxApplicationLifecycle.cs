using Ryujinx.Common.Logging;
using System;
using System.Threading;

namespace Ryujinx.Host.Xbox
{
    /// <summary>
    /// Manages the Xbox application lifecycle events.
    /// Replaces the desktop main loop with Xbox-compatible lifecycle handling:
    /// OnActivated, OnSuspending, OnResuming.
    /// </summary>
    public sealed class XboxApplicationLifecycle : IDisposable
    {
        private readonly ManualResetEventSlim _suspendEvent;
        private readonly CancellationTokenSource _shutdownTokenSource;
        private volatile bool _isSuspended;
        private volatile bool _isDisposed;

        /// <summary>
        /// Fired when the application is activated on Xbox.
        /// </summary>
        public event Action OnActivated;

        /// <summary>
        /// Fired when the Xbox system requests suspension (e.g., user presses Guide button).
        /// </summary>
        public event Action OnSuspending;

        /// <summary>
        /// Fired when the application resumes from suspension.
        /// </summary>
        public event Action OnResuming;

        /// <summary>
        /// A token that is cancelled when the application is shutting down.
        /// </summary>
        public CancellationToken ShutdownToken => _shutdownTokenSource.Token;

        /// <summary>
        /// Whether the application is currently suspended.
        /// </summary>
        public bool IsSuspended => _isSuspended;

        public XboxApplicationLifecycle()
        {
            _suspendEvent = new ManualResetEventSlim(true);
            _shutdownTokenSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Called by the Xbox app shell when the application is activated.
        /// </summary>
        public void HandleActivated()
        {
            Logger.Info?.Print(LogClass.Application, "Xbox application activated.");
            OnActivated?.Invoke();
        }

        /// <summary>
        /// Called by the Xbox app shell when the system requests suspension.
        /// The emulator core should pause execution and release transient resources.
        /// </summary>
        public void HandleSuspending()
        {
            if (_isSuspended)
            {
                return;
            }

            Logger.Info?.Print(LogClass.Application, "Xbox application suspending.");
            _isSuspended = true;
            _suspendEvent.Reset();
            OnSuspending?.Invoke();
        }

        /// <summary>
        /// Called by the Xbox app shell when the application resumes.
        /// </summary>
        public void HandleResuming()
        {
            if (!_isSuspended)
            {
                return;
            }

            Logger.Info?.Print(LogClass.Application, "Xbox application resuming.");
            _isSuspended = false;
            _suspendEvent.Set();
            OnResuming?.Invoke();
        }

        /// <summary>
        /// Blocks the calling thread while the application is suspended.
        /// Should be called at the top of the emulation loop iteration.
        /// </summary>
        public void WaitIfSuspended()
        {
            _suspendEvent.Wait(ShutdownToken);
        }

        /// <summary>
        /// Initiates application shutdown.
        /// </summary>
        public void RequestShutdown()
        {
            Logger.Info?.Print(LogClass.Application, "Xbox application shutdown requested.");
            _shutdownTokenSource.Cancel();
            _suspendEvent.Set(); // Unblock any suspended wait
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _shutdownTokenSource.Cancel();
            _suspendEvent.Set();
            _suspendEvent.Dispose();
            _shutdownTokenSource.Dispose();
        }
    }
}
