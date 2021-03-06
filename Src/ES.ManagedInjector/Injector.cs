﻿using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ES.ManagedInjector
{
    public class Injector
    {
        private readonly Int32 _pid;
        private readonly Byte[] _assemblyContent;
        private readonly String _methodName = null;
        private readonly Assembly _assembly = null;
        private readonly List<Byte[]> _dependency = new List<Byte[]>();
        private readonly Dictionary<String, Byte[]> _files = new Dictionary<String, Byte[]>();
        private Process _process = null;
        private IntPtr _processHandle = IntPtr.Zero;
        private String _lastErrorMessage = String.Empty;

        /// <summary>Inject the given assembly bytes into the process identified by the pid. You have to specify manually
        /// not standard dependencies since in this case the Assembly location is not specified.</summary>
        public Injector(Int32 pid, Byte[] assemblyContent) : this(pid, assemblyContent, null)
        { }

        /// <summary> Inject the given assembly into the process identified by the pid. The Assembly must
        /// exists on the filesystem.</summary>
        public Injector(Int32 pid, Assembly assembly) : this(pid, assembly, null)
        { }

        /// <summary>Inject the given assembly bytes into the process identified by the pid. 
        /// You have to specify manually not standard dependencies since in this case the Assembly 
        /// location is not specified. The invoked method is the one specified.
        /// </summary>
        public Injector(Int32 pid, Byte[] assemblyContent, String methodName)
        {
            _pid = pid;
            _assemblyContent = assemblyContent;
            _methodName = methodName;
        }

        /// <summary> Inject the given assembly into the process identified by the pid. The Assembly must
        /// exists on the filesystem. The invoked method is the one specified.</summary>
        public Injector(Int32 pid, Assembly assembly, String methodName)
        {            
            if (String.IsNullOrWhiteSpace(assembly.Location))
            {
                var errorMsg =
                    "Unable to inject an Assembly whih doesn't have a location. " +
                    "Use the contructor that take as input a byte array to inject a memory only assembly.";                    
                throw new ApplicationException(errorMsg);
            }

            _pid = pid;
            _assembly = assembly;
            _assemblyContent = File.ReadAllBytes(_assembly.Location);
            _methodName = methodName;

            // set assembly resolve method for dependencies
            AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += ResolveAssembly;
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
        }
        
        /// <summary>
        /// Execute the injection of the specified assembly
        /// </summary>
        /// <returns></returns>
        public InjectionResult Inject()
        {
            var result = InjectionResult.UnknownError;
            ResolveDependencies();

            try
            {
                _process = Process.GetProcessById(_pid);
            }
            catch
            {
                result = InjectionResult.PidNotValid;
            }


            if (_process != null)
            {
                try
                {
                    UInt32 threadId = 0;
                    foreach (var windowHandle in GetProcessWindows(_pid))
                    {
                        _processHandle = windowHandle;
                        threadId = Methods.GetWindowThreadProcessId(windowHandle, IntPtr.Zero);
                        if (threadId > 0)
                        {
                            Remote.hookHandle = InjectIntoThread(threadId);
                            if (Remote.hookHandle != IntPtr.Zero)
                            {
                                ActivateHook();                                
                                if (VerifyInjection())
                                {
                                    result = ActivateAssembly();
                                    break;
                                }
                                else
                                {
                                    result = InjectionResult.InjectionFailed;
                                }
                            }
                            else
                            {
                                result = InjectionResult.InjectionFailed;
                            }
                        }
                        else
                        {
                            result = InjectionResult.WindowThreadNotFound;
                        }
                    }
                }
                catch (Exception e) {
                    result = InjectionResult.InjectionFailed;
                    _lastErrorMessage = e.ToString();
                }
            }

            return result;
        }

        /// <summary>
        /// this method allows to specified additional Assembly that must be loaded into the remote
        /// process before to execute the injected Assembly. It is usefull to add non standard dependencies.
        /// </summary>
        /// <param name="assembly"></param>
        public void AddDependency(Assembly assembly)
        {
            if (String.IsNullOrWhiteSpace(assembly.Location))
            {
                var errorMsg =
                    "Unable to inject an Assembly that doesn't have a location." +
                    "Use the contructor that take the a byte buffer to inject a memory only assembly.";
                throw new ApplicationException(errorMsg);
            }

            AddDependency(File.ReadAllBytes(assembly.Location));
        }

        /// <summary>
        /// this method allows to specified additional Assembly that must be loaded into the remote
        /// process before to execute the injected Assembly. It is usefull to add non standard dependencies.
        /// </summary>
        /// <param name="assembly"></param>
        public void AddDependency(Byte[] assemblyContent)
        {
            _dependency.Add(assemblyContent);
        }

        /// <summary>
        /// Copy the content of the given filename to the folder of the injected process
        /// </summary>
        /// <param name="filename"></param>
        public void AddFile(String filename, Byte[] content)
        {
            var basename = Path.GetFileName(filename);
            if (!_files.ContainsKey(basename))
            {
                _files.Add(basename, content);
            }            
        }

        /// <summary>
        /// Copy the content of the given filename to the folder of the injected process. In this case, the
        /// file must exists on the filesystem.
        /// </summary>
        /// <param name="filename"></param>
        public void AddFile(String filename)
        {
            AddFile(filename, File.ReadAllBytes(filename));
        }

        /// <summary>
        /// Return a string which provides a description of the last raised error.
        /// If no error was raised this string is empty.
        /// </summary>
        /// <returns>A textual description of the error</returns>
        public String GetLastErrorMessage()
        {
            return _lastErrorMessage;
        }

        private Assembly TryLoadAssembly(String filename)
        {
            Assembly assembly = null;
            if (File.Exists(filename))
            {
                assembly = Assembly.LoadFile(filename);
            }
            return assembly;
        }

        private Assembly ResolveAssembly(Object sender, ResolveEventArgs e)
        {
            Assembly resolvedAssembly = null;

            if (_assembly != null)
            {
                var assemblyDir =
                    String.IsNullOrWhiteSpace(_assembly.Location) ?
                    String.Empty :
                    Path.GetDirectoryName(_assembly.Location);

                if (!String.IsNullOrWhiteSpace(assemblyDir))
                {
                    var fullAssemblyName = new AssemblyName(e.Name);
                    var assemblyFile = Path.Combine(assemblyDir, fullAssemblyName.Name);
                    resolvedAssembly = TryLoadAssembly(assemblyFile + ".dll") ?? TryLoadAssembly(assemblyFile + ".exe");
                }
            }
                
            return resolvedAssembly;
        }

        private void ResolveDependencies()
        {
            if (_assembly != null)
            {
                foreach (var assemblyName in _assembly.GetReferencedAssemblies())
                {
                    try
                    {
                        var assembly = Assembly.Load(assemblyName);
                        if (!Utility.IsBclAssembly(assembly))
                        {
                            if (!String.IsNullOrWhiteSpace(assembly.Location))
                            {
                                _dependency.Add(File.ReadAllBytes(assembly.Location));
                            }
                        }
                    }
                    catch { /* ignore exception */ }
                }
            }
        }

        private InjectionResult ActivateAssembly()
        {
            var client = new Client(_assemblyContent, _methodName, _dependency, _files);
            client.ActivateAssembly();
            _lastErrorMessage = client.GetLastErrorMessage();
            return client.GetLastError();
        }

        private IntPtr[] GetProcessWindows(Int32 pid)
        {
            // Yes, I copied this piece of code from StackOverFlow
            // src: https://stackoverflow.com/a/25152035/1422545
            var apRet = new List<IntPtr>();
            var pLast = IntPtr.Zero;
            var currentPid = 0;

            do
            {
                pLast = Methods.FindWindowEx(IntPtr.Zero, pLast, null, null);                
                Methods.GetWindowThreadProcessId(pLast, out currentPid);

                if (currentPid == pid)
                    apRet.Add(pLast);

            } while (pLast != IntPtr.Zero);

            return apRet.ToArray();
        }   

        private Boolean VerifyInjection()
        {
            _process.Refresh();
            var moduleName = typeof(Injector).Module.Name;
            var moduleFound = false;
            foreach (ProcessModule procModule in _process.Modules)
            {
                var fileName = Path.GetFileName(procModule.FileName);
                if (fileName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    moduleFound = true;
                    break;
                }
            }
            return moduleFound;
        }

        private IntPtr InjectIntoThread(UInt32 threadId)
        {
            var thisModule = typeof(Injector).Module;
            var moduleHandle = Methods.LoadLibrary(thisModule.Name);

            // get addr exported function
            var hookProc = Methods.GetProcAddress(moduleHandle, "HookProc");
            return Methods.SetWindowsHookEx(Constants.WH_CALLWNDPROC, hookProc, moduleHandle, threadId);
        }

        private void ActivateHook()
        {
            Methods.SendMessage(_processHandle, Constants.InjectorMessage, IntPtr.Zero, IntPtr.Zero);
        }
    }
}
