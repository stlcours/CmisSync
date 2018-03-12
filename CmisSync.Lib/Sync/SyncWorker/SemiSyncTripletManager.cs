﻿using log4net;

using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Concurrent;

using CmisSync.Lib.Sync.SyncTriplet;
using CmisSync.Lib.Sync.SyncWorker.Crawler;

namespace CmisSync.Lib.Sync.SyncWorker
{
    /*
     *  In this very first version. We do noet consider about
     *  concurrent sync triplet construction from local and remote:
     *     local first, then remote
     */
    public class SemiSyncTripletManager : IDisposable
    {

        //private static readonly ILog Logger = LogManager.GetLogger (typeof (SemiSyncTripletManager));

        public BlockingCollection<SyncTriplet.SyncTriplet> semiSyncTriplets = new BlockingCollection<SyncTriplet.SyncTriplet> ();

        private CmisSyncFolder.CmisSyncFolder cmisSyncFolder;

        private LocalCrawlWorker localCrawlWorker;

        public SemiSyncTripletManager (CmisSyncFolder.CmisSyncFolder cmisSyncFolder)
        {
            this.cmisSyncFolder = cmisSyncFolder;
            this.localCrawlWorker = new LocalCrawlWorker (cmisSyncFolder, semiSyncTriplets);
        }

        public void Start() {
            localCrawlWorker.Start ();
        }

        ~SemiSyncTripletManager ()
        {
            Dispose (false);
        }

        public void Dispose ()
        {
            Dispose (true);
            GC.SuppressFinalize (this);
        }

        private Object disposeLock = new object ();
        private bool disposed = false;
        protected virtual void Dispose (bool disposing)
        {
            lock (disposeLock) {
                if (!this.disposed) {
                    if (disposing)
                        this.semiSyncTriplets.Dispose ();
                    this.disposed = true;
                }
            }
        }
    }
}