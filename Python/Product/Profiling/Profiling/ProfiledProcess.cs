// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using Microsoft.VisualStudioTools.Project;
using Microsoft.PythonTools.Infrastructure;

namespace Microsoft.PythonTools.Profiling {
    sealed class ProfiledProcess : IDisposable {
        private readonly string _exe, _args, _dir;
        private readonly ProcessorArchitecture _arch;
        private readonly Process _process;
        private readonly PythonToolsService _pyService;

        public ProfiledProcess(PythonToolsService pyService, string exe, string args, string dir, Dictionary<string, string> envVars, ProcessorArchitecture arch) {
            if (arch != ProcessorArchitecture.X86 && arch != ProcessorArchitecture.Amd64) {
                throw new InvalidOperationException(String.Format("Unsupported architecture: {0}", arch));
            }
            if (dir.EndsWith("\\")) {
                dir = dir.Substring(0, dir.Length - 1);
            }
            if (String.IsNullOrEmpty(dir)) {
                dir = ".";
            }
            _pyService = pyService;
            _exe = exe;
            _args = args;
            _dir = dir;
            _arch = arch;

            ProcessStartInfo processInfo;
            string pythonInstallDir = Path.GetDirectoryName(PythonToolsInstallPath.GetFile("VsPyProf.dll", typeof(ProfiledProcess).Assembly));
            string dll = _arch == ProcessorArchitecture.Amd64 ? "VsPyProf.dll" : "VsPyProfX86.dll";
            string arguments = "\"" + Path.Combine(pythonInstallDir, "proflaun.py") + "\" " +
                "\"" + Path.Combine(pythonInstallDir, dll) + "\" " +
                "\"" + dir + "\" " +
                _args;

            processInfo = new ProcessStartInfo(_exe, arguments);
            if (_pyService.DebuggerOptions.WaitOnNormalExit) {
                processInfo.EnvironmentVariables["VSPYPROF_WAIT_ON_NORMAL_EXIT"] = "1";
            }
            if (_pyService.DebuggerOptions.WaitOnAbnormalExit) {
                processInfo.EnvironmentVariables["VSPYPROF_WAIT_ON_ABNORMAL_EXIT"] = "1";
            }
            
            processInfo.CreateNoWindow = false;
            processInfo.UseShellExecute = false;
            processInfo.RedirectStandardOutput = false;
            processInfo.WorkingDirectory = _dir;

            if (envVars != null) {
                foreach (var keyValue in envVars) {
                    processInfo.EnvironmentVariables[keyValue.Key] = keyValue.Value;
                }
            }

            _process = new Process();
            _process.StartInfo = processInfo;
        }

        public void Dispose() {
            _process.Dispose();
        }

        public void StartProfiling(string filename) {
            StartPerfMon(filename);
            
            _process.EnableRaisingEvents = true;
            _process.Exited += (sender, args) => {
                try {
                    // Exited event is fired on a random thread pool thread, we need to handle exceptions.
                    StopPerfMon();
                } catch (InvalidOperationException e) {
                    MessageBox.Show(String.Format("Unable to stop performance monitor: {0}", e.Message), "Python Tools for Visual Studio");
                }
                var procExited = ProcessExited;
                if (procExited != null) {
                    procExited(this, EventArgs.Empty);
                }
            };

            _process.Start();
        }

        public event EventHandler ProcessExited;

        private void StartPerfMon(string filename) {
            string perfToolsPath = GetPerfToolsPath();

            string perfMonPath = Path.Combine(perfToolsPath, "VSPerfMon.exe");

            var psi = new ProcessStartInfo(perfMonPath, "/trace /output:" + ProcessOutput.QuoteSingleArgument(filename));
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            var process = Process.Start(psi);

            string perfCmdPath = Path.Combine(perfToolsPath, "VSPerfCmd.exe");

            psi = new ProcessStartInfo(perfCmdPath, "/waitstart");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            process = Process.Start(psi);
            process.WaitForExit();
            if (process.ExitCode != 0) {
                throw new InvalidOperationException("Starting perf cmd failed: " + process.StandardOutput.ReadToEnd());
            }
        }

        private void StopPerfMon() {
            string perfToolsPath = GetPerfToolsPath();

            string perfMonPath = Path.Combine(perfToolsPath, "VSPerfCmd.exe");

            var psi = new ProcessStartInfo(perfMonPath, "/shutdown");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardError = true;
            psi.RedirectStandardOutput = true;
            var process = Process.Start(psi);
            process.WaitForExit();
            if (process.ExitCode != 0) {
                throw new InvalidOperationException("Shutting down perf cmd failed: " + process.StandardOutput.ReadToEnd() + "\r\n" + process.StandardError.ReadToEnd());
            }
        }

        private string GetPerfToolsPath() {
            RegistryKey key = Registry.LocalMachine.OpenSubKey("Software\\Microsoft\\VisualStudio\\" + AssemblyVersionInfo.VSVersion);
            var shFolder = key.GetValue("ShellFolder") as string;
            if (shFolder == null) {
                throw new InvalidOperationException("Cannot find shell folder for Visual Studio");
            }

            string perfToolsPath;
            if (_arch == ProcessorArchitecture.Amd64) {
                perfToolsPath = @"Team Tools\Performance Tools\x64";
            } else {
                perfToolsPath = @"Team Tools\Performance Tools\";
            }
            perfToolsPath = Path.Combine(shFolder, perfToolsPath);
            return perfToolsPath;
        }


        internal void StopProfiling() {
            _process.Kill();
        }
    }
}
