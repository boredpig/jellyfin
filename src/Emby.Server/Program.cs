﻿using MediaBrowser.Model.Logging;
using MediaBrowser.Server.Implementations;
using MediaBrowser.Server.Startup.Common;
using MediaBrowser.ServerApplication.Native;
using MediaBrowser.ServerApplication.Splash;
using MediaBrowser.ServerApplication.Updates;
using Microsoft.Win32;
using System;
using System.Configuration.Install;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Emby.Common.Implementations.EnvironmentInfo;
using Emby.Common.Implementations.IO;
using Emby.Common.Implementations.Logging;
using Emby.Common.Implementations.Networking;
using Emby.Common.Implementations.Security;
using Emby.Server.Core;
using Emby.Server.Core.Browser;
using Emby.Server.Implementations.IO;
using ImageMagickSharp;
using MediaBrowser.Common.Net;
using MediaBrowser.Server.Startup.Common.IO;

namespace Emby.Server
{
    public class Program
    {
        private static ApplicationHost _appHost;

        private static ILogger _logger;

        private static bool _isRunningAsService = false;
        private static bool _canRestartService = false;
        private static bool _appHostDisposed;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetDllDirectory(string lpPathName);

        /// <summary>
        /// Defines the entry point of the application.
        /// </summary>
        public static void Main(string[] args)
        {
            var options = new StartupOptions();
            _isRunningAsService = options.ContainsOption("-service");

            if (_isRunningAsService)
            {
                //_canRestartService = CanRestartWindowsService();
            }

            var currentProcess = Process.GetCurrentProcess();

            var applicationPath = currentProcess.MainModule.FileName;
            var architecturePath = Path.Combine(Path.GetDirectoryName(applicationPath), Environment.Is64BitProcess ? "x64" : "x86");

            Wand.SetMagickCoderModulePath(architecturePath);

            var success = SetDllDirectory(architecturePath);

            var appPaths = CreateApplicationPaths(applicationPath, _isRunningAsService);

            var logManager = new NlogManager(appPaths.LogDirectoryPath, "server");
            logManager.ReloadLogger(LogSeverity.Debug);
            logManager.AddConsoleOutput();

            var logger = _logger = logManager.GetLogger("Main");

            ApplicationHost.LogEnvironmentInfo(logger, appPaths, true);

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            if (IsAlreadyRunning(applicationPath, currentProcess))
            {
                logger.Info("Shutting down because another instance of Emby Server is already running.");
                return;
            }

            if (PerformUpdateIfNeeded(appPaths, logger))
            {
                logger.Info("Exiting to perform application update.");
                return;
            }

            try
            {
                RunApplication(appPaths, logManager, _isRunningAsService, options);
            }
            finally
            {
                OnServiceShutdown();
            }
        }

        /// <summary>
        /// Determines whether [is already running] [the specified current process].
        /// </summary>
        /// <param name="applicationPath">The application path.</param>
        /// <param name="currentProcess">The current process.</param>
        /// <returns><c>true</c> if [is already running] [the specified current process]; otherwise, <c>false</c>.</returns>
        private static bool IsAlreadyRunning(string applicationPath, Process currentProcess)
        {
            var duplicate = Process.GetProcesses().FirstOrDefault(i =>
            {
                try
                {
                    if (currentProcess.Id == i.Id)
                    {
                        return false;
                    }
                }
                catch (Exception)
                {
                    return false;
                }

                try
                {
                    //_logger.Info("Module: {0}", i.MainModule.FileName);
                    if (string.Equals(applicationPath, i.MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    return false;
                }
                catch (Exception)
                {
                    return false;
                }
            });

            if (duplicate != null)
            {
                _logger.Info("Found a duplicate process. Giving it time to exit.");

                if (!duplicate.WaitForExit(30000))
                {
                    _logger.Info("The duplicate process did not exit.");
                    return true;
                }
            }

            if (!_isRunningAsService)
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// Creates the application paths.
        /// </summary>
        /// <param name="applicationPath">The application path.</param>
        /// <param name="runAsService">if set to <c>true</c> [run as service].</param>
        /// <returns>ServerApplicationPaths.</returns>
        private static ServerApplicationPaths CreateApplicationPaths(string applicationPath, bool runAsService)
        {
            var resourcesPath = Path.GetDirectoryName(applicationPath);

            if (runAsService)
            {
                var systemPath = Path.GetDirectoryName(applicationPath);

                var programDataPath = Path.GetDirectoryName(systemPath);

                return new ServerApplicationPaths(programDataPath, applicationPath, resourcesPath);
            }

            return new ServerApplicationPaths(ApplicationPathHelper.GetProgramDataPath(applicationPath), applicationPath, resourcesPath);
        }

        /// <summary>
        /// Gets a value indicating whether this instance can self restart.
        /// </summary>
        /// <value><c>true</c> if this instance can self restart; otherwise, <c>false</c>.</value>
        public static bool CanSelfRestart
        {
            get
            {
                if (_isRunningAsService)
                {
                    return _canRestartService;
                }
                else
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance can self update.
        /// </summary>
        /// <value><c>true</c> if this instance can self update; otherwise, <c>false</c>.</value>
        public static bool CanSelfUpdate
        {
            get
            {
                if (_isRunningAsService)
                {
                    return _canRestartService;
                }
                else
                {
                    return true;
                }
            }
        }

        private static readonly TaskCompletionSource<bool> ApplicationTaskCompletionSource = new TaskCompletionSource<bool>();

        /// <summary>
        /// Runs the application.
        /// </summary>
        /// <param name="appPaths">The app paths.</param>
        /// <param name="logManager">The log manager.</param>
        /// <param name="runService">if set to <c>true</c> [run service].</param>
        /// <param name="options">The options.</param>
        private static void RunApplication(ServerApplicationPaths appPaths, ILogManager logManager, bool runService, StartupOptions options)
        {
            var fileSystem = new WindowsFileSystem(logManager.GetLogger("FileSystem"));
            fileSystem.AddShortcutHandler(new LnkShortcutHandler());
            fileSystem.AddShortcutHandler(new MbLinkShortcutHandler(fileSystem));

            var nativeApp = new WindowsApp(fileSystem, _logger)
            {
                IsRunningAsService = runService
            };

            var imageEncoder = ImageEncoderHelper.GetImageEncoder(_logger, logManager, fileSystem, options, () => _appHost.HttpClient, appPaths);

            _appHost = new ApplicationHost(appPaths,
                logManager,
                options,
                fileSystem,
                nativeApp,
                new PowerManagement(),
                "emby.windows.zip",
                new EnvironmentInfo(),
                imageEncoder,
                new Server.Startup.Common.SystemEvents(logManager.GetLogger("SystemEvents")),
                new RecyclableMemoryStreamProvider(),
                new NetworkManager(logManager.GetLogger("NetworkManager")),
                GenerateCertificate,
                () => Environment.UserDomainName);

            var initProgress = new Progress<double>();

            if (!runService)
            {
                // Not crazy about this but it's the only way to suppress ffmpeg crash dialog boxes
                SetErrorMode(ErrorModes.SEM_FAILCRITICALERRORS | ErrorModes.SEM_NOALIGNMENTFAULTEXCEPT |
                             ErrorModes.SEM_NOGPFAULTERRORBOX | ErrorModes.SEM_NOOPENFILEERRORBOX);
            }

            var task = _appHost.Init(initProgress);
            Task.WaitAll(task);

            task = task.ContinueWith(new Action<Task>(a => _appHost.RunStartupTasks()), TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.AttachedToParent);

            if (runService)
            {
                StartService(logManager);
            }
            else
            {
                Task.WaitAll(task);

                task = InstallVcredist2013IfNeeded(_appHost, _logger);
                Task.WaitAll(task);

                Microsoft.Win32.SystemEvents.SessionEnding += SystemEvents_SessionEnding;
                Microsoft.Win32.SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;

                task = ApplicationTaskCompletionSource.Task;
                Task.WaitAll(task);
            }
        }

        private static void GenerateCertificate(string certPath, string certHost)
        {
            CertificateGenerator.CreateSelfSignCertificatePfx(certPath, certHost, _logger);
        }

        static void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLogon)
            {
                BrowserLauncher.OpenDashboard(_appHost);
            }
        }

        /// <summary>
        /// Starts the service.
        /// </summary>
        private static void StartService(ILogManager logManager)
        {
            var service = new BackgroundService(logManager.GetLogger("Service"));

            service.Disposed += service_Disposed;

            ServiceBase.Run(service);
        }

        /// <summary>
        /// Handles the Disposed event of the service control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        static void service_Disposed(object sender, EventArgs e)
        {
            ApplicationTaskCompletionSource.SetResult(true);
            OnServiceShutdown();
        }

        private static void OnServiceShutdown()
        {
            _logger.Info("Shutting down");

            DisposeAppHost();
        }

        /// <summary>
        /// Handles the SessionEnding event of the SystemEvents control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="SessionEndingEventArgs"/> instance containing the event data.</param>
        static void SystemEvents_SessionEnding(object sender, SessionEndingEventArgs e)
        {
            if (e.Reason == SessionEndReasons.SystemShutdown || !_isRunningAsService)
            {
                Shutdown();
            }
        }

        /// <summary>
        /// Handles the UnhandledException event of the CurrentDomain control.
        /// </summary>
        /// <param name="sender">The source of the event.</param>
        /// <param name="e">The <see cref="UnhandledExceptionEventArgs"/> instance containing the event data.</param>
        static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = (Exception)e.ExceptionObject;

            new UnhandledExceptionWriter(_appHost.ServerConfigurationManager.ApplicationPaths, _logger, _appHost.LogManager).Log(exception);

            if (!_isRunningAsService)
            {
                MessageBox.Show("Unhandled exception: " + exception.Message);
            }

            if (!Debugger.IsAttached)
            {
                Environment.Exit(Marshal.GetHRForException(exception));
            }
        }

        /// <summary>
        /// Performs the update if needed.
        /// </summary>
        /// <param name="appPaths">The app paths.</param>
        /// <param name="logger">The logger.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise</returns>
        private static bool PerformUpdateIfNeeded(ServerApplicationPaths appPaths, ILogger logger)
        {
            // Look for the existence of an update archive
            var updateArchive = Path.Combine(appPaths.TempUpdatePath, "MBServer" + ".zip");
            if (File.Exists(updateArchive))
            {
                logger.Info("An update is available from {0}", updateArchive);

                // Update is there - execute update
                try
                {
                    var serviceName = _isRunningAsService ? BackgroundService.GetExistingServiceName() : string.Empty;
                    new ApplicationUpdater().UpdateApplication(appPaths, updateArchive, logger, serviceName);

                    // And just let the app exit so it can update
                    return true;
                }
                catch (Exception e)
                {
                    logger.ErrorException("Error starting updater.", e);

                    MessageBox.Show(string.Format("Error attempting to update application.\n\n{0}\n\n{1}", e.GetType().Name, e.Message));
                }
            }

            return false;
        }

        public static void Shutdown()
        {
            if (_isRunningAsService)
            {
                ShutdownWindowsService();
            }
            else
            {
                DisposeAppHost();

                ShutdownWindowsApplication();
            }
        }

        public static void Restart()
        {
            DisposeAppHost();

            if (_isRunningAsService)
            {
                RestartWindowsService();
            }
            else
            {
                //_logger.Info("Hiding server notify icon");
                //_serverNotifyIcon.Visible = false;

                _logger.Info("Starting new instance");
                //Application.Restart();
                Process.Start(_appHost.ServerConfigurationManager.ApplicationPaths.ApplicationPath);

                ShutdownWindowsApplication();
            }
        }

        private static void DisposeAppHost()
        {
            if (!_appHostDisposed)
            {
                _logger.Info("Disposing app host");

                _appHostDisposed = true;
                _appHost.Dispose();
            }
        }

        private static void ShutdownWindowsApplication()
        {
            //_logger.Info("Calling Application.Exit");
            //Application.Exit();

            _logger.Info("Calling Environment.Exit");
            Environment.Exit(0);

            _logger.Info("Calling ApplicationTaskCompletionSource.SetResult");
            ApplicationTaskCompletionSource.SetResult(true);
        }

        private static void ShutdownWindowsService()
        {
        }

        private static void RestartWindowsService()
        {
        }

        private static bool CanRestartWindowsService()
        {
            return false;
        }

        private static async Task InstallVcredist2013IfNeeded(ApplicationHost appHost, ILogger logger)
        {
            // Reference 
            // http://stackoverflow.com/questions/12206314/detect-if-visual-c-redistributable-for-visual-studio-2012-is-installed

            try
            {
                var subkey = Environment.Is64BitProcess
                    ? "SOFTWARE\\WOW6432Node\\Microsoft\\VisualStudio\\12.0\\VC\\Runtimes\\x64"
                    : "SOFTWARE\\Microsoft\\VisualStudio\\12.0\\VC\\Runtimes\\x86";

                using (RegistryKey ndpKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default)
                    .OpenSubKey(subkey))
                {
                    if (ndpKey != null && ndpKey.GetValue("Version") != null)
                    {
                        var installedVersion = ((string)ndpKey.GetValue("Version")).TrimStart('v');
                        if (installedVersion.StartsWith("12", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.ErrorException("Error getting .NET Framework version", ex);
                return;
            }

            try
            {
                await InstallVcredist2013().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.ErrorException("Error installing Visual Studio C++ runtime", ex);
            }
        }

        private async static Task InstallVcredist2013()
        {
            var httpClient = _appHost.HttpClient;

            var tmp = await httpClient.GetTempFile(new HttpRequestOptions
            {
                Url = GetVcredist2013Url(),
                Progress = new Progress<double>()

            }).ConfigureAwait(false);

            var exePath = Path.ChangeExtension(tmp, ".exe");
            File.Copy(tmp, exePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,

                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Verb = "runas",
                ErrorDialog = false
            };

            _logger.Info("Running {0}", startInfo.FileName);

            using (var process = Process.Start(startInfo))
            {
                process.WaitForExit();
            }
        }

        private static string GetVcredist2013Url()
        {
            if (Environment.Is64BitProcess)
            {
                return "https://github.com/MediaBrowser/Emby.Resources/raw/master/vcredist2013/vcredist_x64.exe";
            }

            // TODO: ARM url - https://github.com/MediaBrowser/Emby.Resources/raw/master/vcredist2013/vcredist_arm.exe

            return "https://github.com/MediaBrowser/Emby.Resources/raw/master/vcredist2013/vcredist_x86.exe";
        }

        /// <summary>
        /// Sets the error mode.
        /// </summary>
        /// <param name="uMode">The u mode.</param>
        /// <returns>ErrorModes.</returns>
        [DllImport("kernel32.dll")]
        static extern ErrorModes SetErrorMode(ErrorModes uMode);

        /// <summary>
        /// Enum ErrorModes
        /// </summary>
        [Flags]
        public enum ErrorModes : uint
        {
            /// <summary>
            /// The SYSTE m_ DEFAULT
            /// </summary>
            SYSTEM_DEFAULT = 0x0,
            /// <summary>
            /// The SE m_ FAILCRITICALERRORS
            /// </summary>
            SEM_FAILCRITICALERRORS = 0x0001,
            /// <summary>
            /// The SE m_ NOALIGNMENTFAULTEXCEPT
            /// </summary>
            SEM_NOALIGNMENTFAULTEXCEPT = 0x0004,
            /// <summary>
            /// The SE m_ NOGPFAULTERRORBOX
            /// </summary>
            SEM_NOGPFAULTERRORBOX = 0x0002,
            /// <summary>
            /// The SE m_ NOOPENFILEERRORBOX
            /// </summary>
            SEM_NOOPENFILEERRORBOX = 0x8000
        }
    }
}
