﻿using System;

namespace Anathema.MemoryManagement.Threading
{
    /// <summary>
    /// Class containing a frozen thread. If an instance of this class is disposed, its associated thread is resumed.
    /// </summary>
    public class FrozenThread : IDisposable
    {
        /// <summary>
        /// The frozen thread.
        /// </summary>
        public RemoteThread Thread { get; private set; }

        #region Constructor
        /// <summary>
        /// Initializes a new instance of the <see cref="FrozenThread"/> class.
        /// </summary>
        /// <param name="Thread">The frozen thread.</param>
        internal FrozenThread(RemoteThread Thread)
        {
            // Save the parameter
            this.Thread = Thread;
        }

        #endregion

        #region Methods
        #region Dispose (implementation of IDisposable)
        /// <summary>
        /// Releases all resources used by the <see cref="RemoteThread"/> object.
        /// </summary>
        public virtual void Dispose()
        {
            // Unfreeze the thread
            Thread.Resume();
        }

        #endregion
        #region ToString (override)
        /// <summary>
        /// Returns a string that represents the current object.
        /// </summary>
        public override String ToString()
        {
            return String.Format("Id = {0}", Thread.Id);
        }

        #endregion
        #endregion

    } // End class

} // End namespace