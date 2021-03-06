﻿// --------------------------------------------------------
// Copyright:      Toni Kalajainen
// Date:           21.12.2018
// Url:            http://lexical.fi
// --------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Threading;

namespace Lexical.FileProvider.Common
{
    /// <summary>
    /// A disposable that manages a list of disposable objects.
    /// It will dispose them all at Dispose().
    /// </summary>
    public class DisposeList : IDisposeList
    {
        /// <summary>
        /// List of disposables that has been attached with this object.
        /// </summary>
        protected List<IDisposable> disposeList = new List<IDisposable>(2);

        /// <summary>
        /// State that is set when disposing starts and finalizes.
        /// Is changed with Interlocked. 
        ///  0 - not disposed
        ///  1 - disposing
        ///  2 - disposed
        ///  
        /// When disposing starts, new objects cannot be added to the object, instead they are disposed right at away.
        /// </summary>
        protected long disposing;

        /// <summary>
        /// Property that checks thread-synchronously whether disposing has started or completed.
        /// </summary>
        public bool IsDisposing => Interlocked.Read(ref disposing) != 0L;

        /// <summary>
        /// Property that checks thread-synchronously whether disposing has started.
        /// </summary>
        public bool IsDisposed => Interlocked.Read(ref disposing) == 2L;

        /// <summary>
        /// 
        /// </summary>
        /// <exception cref="AggregateException">thrown if disposing threw errors</exception>
        public virtual void Dispose()
        {
            // Is disposing
            Interlocked.CompareExchange(ref disposing, 1L, 0L);

            // Extract snapshot, clear array
            IDisposable[] toDispose = null;
            lock (this) { toDispose = disposeList?.ToArray(); disposeList.Clear(); }

            // Captured errors
            List<Exception> disposeErrors = null;

            // Dispose disposables
            DisposeAndCapture(toDispose, ref disposeErrors);

            // Call innerDispose(). Capture errors to compose it with others.
            try
            {
                innerDispose(ref disposeErrors);
            } catch (Exception e)
            {
                // Capture error
                (disposeErrors ?? (disposeErrors = new List<Exception>())).Add(e);
            }

            // Is disposed
            Interlocked.CompareExchange(ref disposing, 2L, 1L);

            // Throw captured errors
            if (disposeErrors != null) throw new AggregateException(disposeErrors);
        }

        /// <summary>
        /// Override this to add more dispose mechanism in subtypes
        /// </summary>
        /// <param name="disposeErrors">list that can be instantiated and where errors can be added</param>
        /// <exception cref="Exception">any exception is captured and aggregated with other errors</exception>
        protected virtual void innerDispose(ref List<Exception> disposeErrors)
        {
        }

        /// <summary>
        /// Add <paramref name="disposable"/> to be disposed with the file provider.
        /// 
        /// If parent object is disposed or being disposed, the disposable will be disposed immedialy.
        /// </summary>
        /// <param name="disposable"></param>
        /// <returns>true if was added to list, false if was disposed right away</returns>
        bool IDisposeList.AddDisposable(IDisposable disposable)
        {
            // Argument error
            if (disposable == null) throw new ArgumentNullException(nameof(disposable));

            // Parent is disposed/ing
            if (IsDisposing) { disposable.Dispose(); return false; }

            // Add to list
            lock (disposeList) disposeList.Add(disposable);

            // Check parent again
            if (IsDisposing) { lock (disposeList) disposeList.Remove(disposable); disposable.Dispose(); return false; }

            // OK
            return true;
        }

        bool IDisposeList.AddDisposables(IEnumerable<IDisposable> disposables)
        {
            // Argument error
            if (disposables == null) throw new ArgumentNullException(nameof(disposables));

            // Parent is disposed/ing
            if (IsDisposing) {
                // Captured errors
                List<Exception> disposeErrors = null;
                // Dispose now
                DisposeAndCapture(disposables, ref disposeErrors);
                // Throw captured errors
                if (disposeErrors != null) throw new AggregateException(disposeErrors);
                return false;
            }

            // Add to list
            lock (disposeList) disposeList.AddRange(disposables);

            // Check parent again
            if (IsDisposing) {
                // Captured errors
                List<Exception> disposeErrors = null;
                // Dispose now
                DisposeAndCapture(disposables, ref disposeErrors);
                // Remove
                lock (disposeList) foreach (IDisposable d in disposables) disposeList.Remove(d);
                // Throw captured errors
                if (disposeErrors != null) throw new AggregateException(disposeErrors);
                return false;
            }

            // OK
            return true;
        }

        /// <summary>
        /// Remove <paramref name="disposable"/> from list of attached disposables.
        /// </summary>
        /// <param name="disposable"></param>
        /// <returns>true if an item of <paramref name="disposable"/> was removed, false if it wasn't there</returns>
        bool IDisposeList.RemoveDisposable(IDisposable disposable)
        {
            // Argument error
            if (disposable == null) throw new ArgumentNullException(nameof(disposable));

            lock (this)
            {
                if (disposable == null) return false;
                return disposeList.Remove(disposable);
            }
        }

        /// <summary>
        /// Remove <paramref name="disposables"/> from the list. 
        /// </summary>
        /// <param name="disposables"></param>
        /// <returns>true if was removed, false if it wasn't in the list.</returns>
        bool IDisposeList.RemoveDisposables(IEnumerable<IDisposable> disposables)
        {
            // Argument error
            if (disposables == null) throw new ArgumentNullException(nameof(disposables));

            bool ok = true;
            lock (this)
            {
                if (disposables == null) return false;
                foreach(IDisposable d in disposables)   
                    ok &= disposeList.Remove(d);
                return ok;
            }
        }

        /// <summary>
        /// Dispose enumerable and capture errors
        /// </summary>
        /// <param name="disposables">list of disposables</param>
        /// <param name="disposeErrors">list to be created if errors occur</param>
        public static void DisposeAndCapture(IEnumerable<IDisposable> disposables, ref List<Exception> disposeErrors)
        {
            if (disposables == null) return;

            // Dispose disposables
            foreach (IDisposable disposable in disposables)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (AggregateException ae)
                {
                    (disposeErrors ?? (disposeErrors = new List<Exception>())).AddRange(ae.InnerExceptions);
                }
                catch (Exception e)
                {
                    // Capture error
                    (disposeErrors ?? (disposeErrors = new List<Exception>())).Add(e);
                }
            }
        }

    }
}
