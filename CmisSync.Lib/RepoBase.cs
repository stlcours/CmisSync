//   CmisSync, a collaboration and sharing tool.
//   Copyright (C) 2010  Hylke Bons <hylkebons@gmail.com>
//
//   This program is free software: you can redistribute it and/or modify
//   it under the terms of the GNU General Public License as published by
//   the Free Software Foundation, either version 3 of the License, or
//   (at your option) any later version.
//
//   This program is distributed in the hope that it will be useful,
//   but WITHOUT ANY WARRANTY; without even the implied warranty of
//   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//   GNU General Public License for more details.
//
//   You should have received a copy of the GNU General Public License
//   along with this program. If not, see <http://www.gnu.org/licenses/>.


using log4net;
using System;
using System.IO;
using Timers = System.Timers;
using CmisSync.Auth;

namespace CmisSync.Lib
{

    /// <summary>
    /// Synchronizes a remote folder.
    /// This class contains the loop that synchronizes every X seconds.
    /// </summary>
    public abstract class RepoBase : IDisposable
    {
        /// <summary>
        /// Log.
        /// </summary>
        private static readonly ILog Logger = LogManager.GetLogger(typeof(RepoBase));


        /// <summary>
        /// Perform a synchronization if one is not running already.
        /// </summary>
        public abstract void SyncInBackground();


        /// <summary>
        /// Local disk size taken by the repository.
        /// </summary>
        public abstract double Size { get; }


        /// <summary>
        /// Whether this folder's synchronization is running right now.
        /// </summary>
        public abstract bool isSyncing();


        /// <summary>
        /// Whether this folder's synchronization is suspended right now.
        /// </summary>
        public abstract bool isSuspended();


        /// <summary>
        /// Path of the local synchronized folder.
        /// </summary>
        public readonly string LocalPath;


        /// <summary>
        /// Name of the synchronized folder, as found in the CmisSync XML configuration file.
        /// </summary>
        public readonly string Name;


        /// <summary>
        /// URL of the remote CMIS endpoint.
        /// </summary>
        public readonly Uri RemoteUrl;


        /// <summary>
        /// Current status of the synchronization (paused or not).
        /// </summary>
        public bool Enabled { get; private set; }


        /// <summary>
        /// Return the synchronized folder's information.
        /// </summary>
        protected RepoInfo RepoInfo { get; set; }


        /// <summary>
        /// Listener we inform about activity (used by spinner).
        /// </summary>
        private IActivityListener activityListener;


        /// <summary>
        /// Watches the local filesystem for changes.
        /// </summary>
        public Watcher Watcher { get; private set; }


        /// <summary>
        /// Timer for watching the local and remote filesystems.
        /// It perfoms synchronization at regular intervals.
        /// </summary>
        private Timers.Timer periodicSynchronizationTimer = new Timers.Timer();


        /// <summary>
        /// Timer to delay syncing after local change is made.
        /// Often several local changes are made in a short interval,
        /// for instance MS Word sometimes deletes a file then rewrites it,
        /// so better wait a bit rather than start syncing immediately.
        /// </summary>
        private Timers.Timer watcherDelayTimer = new Timers.Timer();


        /// <summary>
        /// Time to wait after a local change is made.
        /// </summary>
        private readonly double delayAfterLocalChange = 15 * 1000; // 15 seconds.


        /// <summary>
        /// Track whether <c>Dispose</c> has been called.
        /// </summary>
        private bool disposed = false;


        /// <summary>
        /// Constructor.
        /// <param name="perpetual">Whether to perpetually sync again and again at regular intervals. False means syncing just once then stopping.</param>
        /// </summary>
        public RepoBase(RepoInfo repoInfo, IActivityListener activityListener, bool enableWatcher, bool perpetual)
        {
            RepoInfo = repoInfo;
            LocalPath = repoInfo.TargetDirectory;
            Name = repoInfo.Name;
            RemoteUrl = repoInfo.Address;

            this.activityListener = activityListener;

            Enabled = ! repoInfo.IsSuspended;

            // Folder lock.
            // Disabled for now. Can be an interesting feature, but should be made opt-in, as
            // most users would be surprised to see this file appear.
            // folderLock = new FolderLock(LocalPath);

            if (enableWatcher)
            {
                Watcher = new Watcher(LocalPath);
                Watcher.EnableRaisingEvents = true;
            }

            if (perpetual)
            {
                // Main loop syncing every X seconds.
                periodicSynchronizationTimer.Elapsed += delegate
                {
                    // Synchronize.
                    SyncInBackground();
                };
                periodicSynchronizationTimer.AutoReset = true;
                Logger.Info("Repo " + repoInfo.Name + " - Set poll interval to " + repoInfo.PollInterval + "ms");
                periodicSynchronizationTimer.Interval = repoInfo.PollInterval;
                periodicSynchronizationTimer.Enabled = true;

                // Partial sync interval.
                watcherDelayTimer.Elapsed += delegate
                {
                    // Run partial sync.
                    SyncInBackground();
                };
                watcherDelayTimer.AutoReset = false;
                watcherDelayTimer.Interval = delayAfterLocalChange;
            }

            this.Watcher.ChangeEvent += OnFileActivity;
        }


        /// <summary>
        /// Destructor.
        /// </summary>
        ~RepoBase()
        {
            Dispose(false);
        }


        /// <summary>
        /// Implement IDisposable interface. 
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }


        /// <summary>
        /// Dispose pattern implementation.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
                    this.periodicSynchronizationTimer.Stop();
                    this.periodicSynchronizationTimer.Dispose();
                    this.watcherDelayTimer.Stop();
                    this.watcherDelayTimer.Dispose();
                    if (Watcher != null)
                    {
                        this.Watcher.Dispose();
                    }
                }
                this.disposed = true;
            }
        }


        /// <summary>
        /// Synchronize at startup if configured to do so.
        /// </summary>
        public void SyncAtStartupIfConfiguredToDoSo()
        {
            // Sync up everything that changed
            // since we've been offline
            if (RepoInfo.SyncAtStartup)
            {
                SyncInBackground();
                Logger.Info(String.Format("Repo {0} - sync launch at startup", RepoInfo.Name));
            }
            else
            {
                Logger.Info(String.Format("Repo {0} - sync not launch at startup", RepoInfo.Name));
                // if LastSuccessSync + pollInterval >= DateTime.Now => Sync
                DateTime tm = RepoInfo.LastSuccessedSync.AddMilliseconds(RepoInfo.PollInterval);
                // http://msdn.microsoft.com/fr-fr/library/system.datetime.compare(v=vs.110).aspx
                if (DateTime.Compare(DateTime.Now, tm) >= 0)
                {
                    SyncInBackground();
                    Logger.Info(String.Format("Repo {0} - sync launch based on last success time sync + poll interval", RepoInfo.Name));
                }
                else
                {
                    Logger.Info(String.Format("Repo {0} - sync not launch based on last success time sync + poll interval - Next sync at {1}", RepoInfo.Name, tm));
                }
            }
        }


        /// <summary>
        /// Update repository settings.
        /// </summary>
        public virtual void UpdateSettings(string password, int pollInterval, bool syncAtStartup)
        {
            //Get configuration
            Config config = ConfigManager.CurrentConfig;
            CmisSync.Lib.Config.SyncConfig.Folder syncConfig = config.GetFolder(this.Name);

            //Pause sync
            this.periodicSynchronizationTimer.Stop();
            if (Enabled)
            {
                Disable();
            }

            //Update password...
            if (!String.IsNullOrEmpty(password))
            {
                this.RepoInfo.Password = new Password(password.TrimEnd());
                syncConfig.ObfuscatedPassword = RepoInfo.Password.ObfuscatedPassword;
                Logger.Debug("Updated \"" + this.Name + "\" password");
            }

            // Sync at startup
            syncConfig.SyncAtStartup = syncAtStartup;

            //Update poll interval
            this.RepoInfo.PollInterval = pollInterval;
            this.periodicSynchronizationTimer.Interval = pollInterval;
            syncConfig.PollInterval = pollInterval;
            Logger.Debug("Updated \"" + this.Name + "\" poll interval: " + pollInterval);

            //Save configuration
            config.Save();

            //Always resume sync...
            Enable();
            this.periodicSynchronizationTimer.Start();
        }


        /// <summary>
        /// Stop syncing momentarily.
        /// </summary>
        public void Disable()
        {
            Enabled = false;
            RepoInfo.IsSuspended = true;

            //Get configuration
            Config config = ConfigManager.CurrentConfig;
            CmisSync.Lib.Config.SyncConfig.Folder syncConfig = config.GetFolder(this.Name);
            syncConfig.IsSuspended = true;
            config.Save();
        }


        /// <summary>
        /// Restart syncing.
        /// </summary>
        public virtual void Enable()
        {
            Enabled = true;
            RepoInfo.IsSuspended = false;

            //Get configuration
            Config config = ConfigManager.CurrentConfig;
            CmisSync.Lib.Config.SyncConfig.Folder syncConfig = config.GetFolder(this.Name);
            syncConfig.IsSuspended = false;
            config.Save();
        }


        /// <summary>
        /// Will send message the currently running sync thread (if one exists) to stop syncing as soon as the next
        /// blockign operation completes.
        /// </summary>
        public abstract void CancelSync();


        /// <summary>
        /// Manual sync.
        /// </summary>
        public void ManualSync()
        {
            SyncInBackground();
        }


        /// <summary>
        /// Some file activity has been detected, sync changes.
        /// </summary>
        public void OnFileActivity(object sender, FileSystemEventArgs args)
        {
            watcherDelayTimer.Stop();
            watcherDelayTimer.Start(); //Restart the local timer...
        }


        /// <summary>
        /// A conflict has been resolved.
        /// </summary>
        protected internal void OnConflictResolved()
        {
            // ConflictResolved(); TODO
        }


        /// <summary>
        /// Called when sync starts.
        /// </summary>
        public void OnSyncStart()
        {
            Logger.Info(" Sync Started: " + LocalPath);
        }


        /// <summary>
        /// Called when sync completes.
        /// </summary>
        public void OnSyncComplete(bool success)
        {
            periodicSynchronizationTimer.Start();

            if (Watcher != null)
            {
                if (Watcher.GetChangeCount() > 0)
                {
                    //Watcher was stopped (due to error) so clear and restart sync
                    Watcher.Clear();
                }
                Watcher.EnableRaisingEvents = true;
                Watcher.EnableEvent = true;
            }

            Logger.Info("Sync Complete: " + LocalPath + " , success=" + success);

            // Save last sync
            RepoInfo.LastSuccessedSync = DateTime.Now;
            // TODO write it to database.
        }


        /// <summary>
        /// Called when sync encounters an error.
        /// </summary>
        public void OnSyncError(Exception exception)
        {
            Logger.Info("Sync Error: " + exception.GetType() + ", " + exception.Message);
            activityListener.ActivityError(new Tuple<string, Exception>(Name, exception));
        }


        /// <summary>
        /// Recursively gets a folder's size in bytes.
        /// </summary>
        private double CalculateSize(DirectoryInfo parent)
        {
            if (!Directory.Exists(parent.ToString()))
                return 0;

            double size = 0;

            try
            {
                // All files at this level.
                foreach (FileInfo file in parent.GetFiles())
                {
                    if (!file.Exists)
                        return 0;

                    size += file.Length;
                }

                // Recurse.
                foreach (DirectoryInfo directory in parent.GetDirectories())
                    size += CalculateSize(directory);

            }
            catch (Exception)
            {
                return 0;
            }

            return size;
        }
    }
}
