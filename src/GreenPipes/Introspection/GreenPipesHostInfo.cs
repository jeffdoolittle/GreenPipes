﻿// Copyright 2007-2016 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace GreenPipes.Introspection
{
    using System;
    using System.Diagnostics;
    using System.Reflection;


    [Serializable]
    public class GreenPipesHostInfo :
        ProbeHostInfo
    {
        public GreenPipesHostInfo()
        {
        }

        public GreenPipesHostInfo(bool initialize)
        {
            MachineName = Environment.MachineName;

            GreenPipesVersion = GetAssemblyFileVersion(typeof(Pipe).GetTypeInfo().Assembly);            
                        
            var currentProcess = Process.GetCurrentProcess();
            ProcessId = currentProcess.Id;
            ProcessName = currentProcess.ProcessName;

            var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();

            entryAssembly = entryAssembly ?? System.Reflection.Assembly.GetCallingAssembly();
            FrameworkVersion = Environment.Version.ToString();
            OperatingSystemVersion = Environment.OSVersion.ToString();

            var assemblyName = entryAssembly.GetName();
            Assembly = assemblyName.Name;
            AssemblyVersion = GetAssemblyFileVersion(entryAssembly);
        }

        public string MachineName { get; private set; }
        public string ProcessName { get; private set; }
        public int ProcessId { get; private set; }
        public string Assembly { get; private set; }
        public string AssemblyVersion { get; private set; }
        public string FrameworkVersion { get; private set; }
        public string GreenPipesVersion { get; private set; }
        public string OperatingSystemVersion { get; private set; }

        static string GetAssemblyFileVersion(Assembly assembly)
        {
            var attribute = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
            if (attribute != null)
            {
                return attribute.Version;
            }

            var assemblyLocation = assembly.Location;
            if (assemblyLocation != null)
                return FileVersionInfo.GetVersionInfo(assemblyLocation).FileVersion;

            return "Unknown";
        }
    }
}