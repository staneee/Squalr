﻿using System;
using System.Diagnostics;
using System.Linq;
using Anathema.MemoryManagement.Internals;
using Anathema.MemoryManagement.Threading;

namespace Anathema.MemoryManagement.Modules
{
    /// <summary>
    /// Class representing an injected module in a remote process.
    /// </summary>
    public class InjectedModule : RemoteModule, IDisposableState
    {
        /// <summary>
        /// Gets a value indicating whether the element is disposed.
        /// </summary>
        public Boolean IsDisposed { get; private set; }
        /// <summary>
        /// Gets a value indicating whether the element must be disposed when the Garbage Collector collects the object.
        /// </summary>
        public Boolean MustBeDisposed { get; set; }

        #region Constructor/Destructor
        /// <summary>
        /// Initializes a new instance of the <see cref="InjectedModule"/> class.
        /// </summary>
        /// <param name="MemorySharp">The reference of the <see cref="MemoryEditor"/> object.</param>
        /// <param name="Module">The native <see cref="ProcessModule"/> object corresponding to the injected module.</param>
        /// <param name="MustBeDisposed">The module will be ejected when the finalizer collects the object.</param>
        internal InjectedModule(MemoryEditor MemorySharp, ProcessModule Module, Boolean MustBeDisposed = true) : base(MemorySharp, Module)
        {
            // Save the parameter
            this.MustBeDisposed = MustBeDisposed;
        }

        /// <summary>
        /// Frees resources and perform other cleanup operations before it is reclaimed by garbage collection.
        /// </summary>
        ~InjectedModule()
        {
            if(MustBeDisposed)
                Dispose();
        }

        #endregion

        #region Methods
        #region Dispose (implementation of IDisposableState)
        /// <summary>
        /// Releases all resources used by the <see cref="InjectedModule"/> object.
        /// </summary>
        public virtual void Dispose()
        {
            if (!IsDisposed)
            {
                // Set the flag to true
                IsDisposed = true;

                // Eject the module
                MemorySharp.Modules.Eject(this);

                // Avoid the finalizer 
                GC.SuppressFinalize(this);
            }
        }

        #endregion
        #region InternalInject (internal)
        /// <summary>
        /// Injects the specified module into the address space of the remote process.
        /// </summary>
        /// <param name="MemorySharp">The reference of the <see cref="MemoryEditor"/> object.</param>
        /// <param name="Path">The path of the module. This can be either a library module (a .dll file) or an executable module (an .exe file).</param>
        /// <returns>A new instance of the <see cref="InjectedModule"/>class.</returns>
        internal static InjectedModule InternalInject(MemoryEditor MemorySharp, String Path)
        {
            // Call LoadLibraryA remotely
            RemoteThread Thread = MemorySharp.Threads.CreateAndJoin(MemorySharp["kernel32"]["LoadLibraryA"].BaseAddress, Path);

            // Get the inject module
            if (Thread.GetExitCode<IntPtr>() != IntPtr.Zero)
                return new InjectedModule(MemorySharp, MemorySharp.Modules.NativeModules.First(m => m.BaseAddress == Thread.GetExitCode<IntPtr>()));

            return null;
        }

        #endregion
        #endregion

    } // End class

} // End namespace