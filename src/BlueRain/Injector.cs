﻿// Copyright (C) 2013-2015 aevitas
// See the file LICENSE for copying permission.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BlueRain.Common;

namespace BlueRain
{
	/// <summary>
	/// This type provides injector support for both internal and external memory manipulators.
	/// When out of process, we call CreateRemoteThread to force the proc to load our module - when we're in-process,
	/// we just call LoadLibrary directly. This way we can support both types with a single class, depending on what type
	/// of NativeMemory implementation we're constructed with.
	/// </summary>
	public class Injector : IDisposable
	{
		private readonly NativeMemory _memory;
		private bool _ejectOnDispose;

		private readonly Dictionary<string, InjectedModule> _injectedModules;

		/// <summary>
		/// Gets the modules this injector has successfully injected.
		/// </summary>
		/// <value>
		/// The injected modules.
		/// </value>
		public IReadOnlyDictionary<string, InjectedModule> InjectedModules
		{
			get { return _injectedModules; }
		}

		// Make sure we hide the default constructor - this should only be initialized from a Memory instance.
		private Injector()
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="Injector" /> class.
		/// </summary>
		/// <param name="memory">The memory.</param>
		/// <param name="ejectOnDispose">if set to <c>true</c> library is freed from the target process when the injector is disposed.</param>
		internal Injector(NativeMemory memory, bool ejectOnDispose = false)
		{
			var epm = memory as ExternalProcessMemory;
			if (epm != null && epm.ProcessHandle.IsInvalid)
				throw new ArgumentException(
					"The specified ExternalProcessMemory has an invalid ProcessHandle - can not construct injector without a valid handle!");

			_memory = memory;
			_ejectOnDispose = ejectOnDispose;
			_injectedModules = new Dictionary<string, InjectedModule>();
		}

		public InjectedModule Inject(string libraryPath)
		{
			if (!File.Exists(libraryPath))
				throw new FileNotFoundException("Couldn't find the specified library to inject: " + libraryPath);

			if (_memory == null)
				throw new InvalidOperationException("Can not inject a library without a valid Memory instance!");

			// External requires some additional love to inject a library (CreateRemoteThread etc.)
			var epm = _memory as ExternalProcessMemory;
			if (epm != null)
				return InjectLibraryExternal(libraryPath);

			return InjectLibraryInternal(libraryPath);
		}

		private InjectedModule InjectLibraryExternal(string libraryPath)
		{
			// Injecting remotely consists of a few steps:
			// 1. GetProcAddress on kernel32 to get a pointer to LoadLibraryW
			// 2. Allocate memory to write the full path to our library to
			// 3. CreateRemoteThread that calls LoadLibraryW and pass it a pointer to our chunk
			// 4. Get thread's exit code
			// 5. ????
			// 6. Profit
			var memory = _memory as ExternalProcessMemory;

			// Realistically won't happen, but code analysis complains about it being null.
			if (memory == null)
				throw new Exception("A valid memory instance is required for InjectLibraryExternal!");

			if (memory.ProcessHandle.IsInvalid)
				throw new InvalidOperationException("Can not inject library with an invalid ProcessHandle in ExternalProcessMemory!");

			var path = Path.GetFullPath(libraryPath);
			var libraryName = Path.GetFileName(libraryPath);

			SafeMemoryHandle threadHandle = null;

			try
			{
				var loadLibraryPtr =
					UnsafeNativeMethods.GetProcAddress(
						UnsafeNativeMethods.GetModuleHandle(UnsafeNativeMethods.Kernel32).DangerousGetHandle(), "LoadLibraryW");

				if (loadLibraryPtr == IntPtr.Zero)
					throw new BlueRainInjectionException("Couldn't obtain handle to LoadLibraryW in remote process!");

				var pathBytes = Encoding.Unicode.GetBytes(path);

				using (var alloc = memory.Allocate((UIntPtr) pathBytes.Length))
				{
					alloc.WriteBytes(IntPtr.Zero, pathBytes);

					threadHandle = UnsafeNativeMethods.CreateRemoteThread(memory.ProcessHandle.DangerousGetHandle(), IntPtr.Zero, 0x0,
						loadLibraryPtr, alloc.Address, 0, IntPtr.Zero);

					if (threadHandle.IsInvalid)
						throw new BlueRainInjectionException("Couldn't obtain a handle to the remotely created thread for module injection!");
				}


			}
			finally
			{
				threadHandle.Close();
			}
		}

		private static InjectedModule InjectLibraryInternal(string libraryPath)
		{
			// It's hardly "injecting" when we're in-process, but for the sake of keeping the API streamlined we'll go with it.
			// All we have to do is call LoadLibrary on the local process and wrap it in an InjectedModule type.
			var lib = SafeLoadLibrary.LoadLibraryEx(libraryPath);

			if (lib == null)
				throw new Exception("LoadLibrary failed in local process!");

			var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().FirstOrDefault(s => s.FileName == libraryPath);

			if (module == null)
				throw new Exception("The injected library couldn't be found in the Process' module list!");

			return new InjectedModule(module);
		}

		#region Implementation of IDisposable

		/// <summary>
		/// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
		/// </summary>
		/// <exception cref="NotImplementedException"></exception>
		public void Dispose()
		{
		}

		#endregion
	}
}