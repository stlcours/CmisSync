﻿using log4net;
using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;

using CmisSync.Lib.Cmis;
using CmisSync.Lib.Sync;
using CmisSync.Lib.Sync.SyncTriplet;
using CmisSync.Lib.Sync.SyncMachine.Crawler;
using CmisSync.Lib.Sync.SyncMachine.Exceptions;
using CmisSync.Lib.Sync.SyncMachine.Internal;
using CmisSync.Lib.Utilities.FileUtilities;

using DotCMIS.Client;
using DotCMIS.Exceptions;

namespace CmisSync.Lib.Sync.SyncMachine
{
    public class SyncTripletAssembler : IDisposable
    {

        private static readonly ILog Logger = LogManager.GetLogger (typeof (SyncTripletAssembler));

        private ISession session;

        private BlockingCollection<SyncTriplet.SyncTriplet> fullSyncTriplets = null;

        private BlockingCollection<SyncTriplet.SyncTriplet> semiSyncTriplets = null;

        private CmisSyncFolder.CmisSyncFolder cmisSyncFolder = null;

        private ChangeLogProcessor changeLogProcessor = null;

        private ConcurrentDictionary<string, SyncTriplet.SyncTriplet> remoteBuffer = new ConcurrentDictionary<String, SyncTriplet.SyncTriplet> ();

        private OrderedDictionary orderedRemoteBuffer = new OrderedDictionary ();

        // orderedRemoteBuffer Lock
        private object orbLock = new object ();

        public SyncTripletAssembler (CmisSyncFolder.CmisSyncFolder cmisSyncFolder,
                                     ISession session
                                    )
        {
            this.cmisSyncFolder = cmisSyncFolder;
            this.session = session;
        }

        public void StartForChangeLog(
            BlockingCollection<SyncTriplet.SyncTriplet> full,
            ItemsDependencies idps
        ) {
            this.fullSyncTriplets = full;
            this.changeLogProcessor = new ChangeLogProcessor (cmisSyncFolder, session, fullSyncTriplets, idps);

            changeLogProcessor.Start ();

        }

        public void StartForLocalWatcherAndLocalChange (
            BlockingCollection<SyncTriplet.SyncTriplet> semi,
            BlockingCollection<SyncTriplet.SyncTriplet> full
        )
        {
            this.semiSyncTriplets = semi;
            this.fullSyncTriplets = full;

            // Assemble semiTriplets generated from local crawler
            foreach (SyncTriplet.SyncTriplet semiTriplet in semiSyncTriplets.GetConsumingEnumerable ()) {

                // If ignore samelowername, use lowerinvariant to lookup in already-crawled-remote-triplet dictionary.
                // One note: IgnoreIfSameLowercaseName is applied only on remote server. it seems that if local fs is 
                // case sensitive while remote is not, remote will regard two distinct files in local as duplicated files
                // and rename one of them while upload.
                string _key = cmisSyncFolder.CmisProfile.CmisProperties.IgnoreIfSameLowercaseNames ? semiTriplet.Name.ToLowerInvariant () : semiTriplet.Name;

                String remotePath = "";
                if (semiTriplet.DBExist) {
                    remotePath = CmisFileUtil.PathCombine (cmisSyncFolder.RemotePath, semiTriplet.DBStorage.DBRemotePath);
                } else {
                    remotePath = CmisFileUtil.PathCombine (cmisSyncFolder.RemotePath, semiTriplet.LocalStorage.RelativePath);
                }

                try {
                    ICmisObject remoteObject = session.GetObjectByPath (remotePath, false);

                    if (semiTriplet.IsFolder) {
                        IFolder remoteFolder = (IFolder)remoteObject;
                        SyncTripletFactory.AssembleRemoteIntoLocal (remoteFolder, cmisSyncFolder, semiTriplet);
                    } else {
                        IDocument remoteDocument = (IDocument)remoteObject;

                        SyncTripletFactory.AssembleRemoteIntoLocal (remoteDocument, remotePath, cmisSyncFolder, semiTriplet);
                    }
                } catch (Exception) {
                    Console.WriteLine (" - remote path: {0} Not found", remotePath);
                }

                if (!fullSyncTriplets.TryAdd (semiTriplet)) {
                    Console.WriteLine (" - assembled triplet: {0} is not appended to full sync triplet list.", semiTriplet.Name);
                }
            }
        }

        /*
         * There should be two idps(es): 
         *   one main for local crawler
         *   one sub for remote crawler
         * Else for one file /xxxx/yyyy/zzz, it is first added to idps by local crawler
         * and is soon pushed to full processor queue and been processed therefore the 
         * idp is deleted from idps.  then the slow remote crawler will add it to idps again. 
         * 
         * It is not desired because we use empty idps to judge if a folder should be deleted
         * 
         * So use local idps for main, after procesing all local files, add remote idps
         * to local idps before push remote triplet to processor queue
         */
        public void StartForLocalCrawler (
            BlockingCollection<SyncTriplet.SyncTriplet> semi,
            BlockingCollection<SyncTriplet.SyncTriplet> full,
            ItemsDependencies idps
        ) {

            // Foreach operation on BlockingCollectio is sequentially executed
            // so a common HashSet rather than ConcurrentDictionary should be enough.
            HashSet<string> processedTriplets = new HashSet<string> ();

            this.semiSyncTriplets = semi;
            this.fullSyncTriplets = full;

            ItemsDependencies r_idps = new ItemsDependencies ();

            //this.remoteCrawlWorker = new RemoteCrawlWorker (cmisSyncFolder, session, remoteBuffer);
            RemoteCrawlWorker orderedRemoteCrawlWorker = new RemoteCrawlWorker (cmisSyncFolder, session, orderedRemoteBuffer, orbLock, r_idps);

            // Start remote crawler for assemble 
            //Task remoteCrawlTask = Task.Factory.StartNew (() => remoteCrawlWorker.Start () );
            Task remoteCrawlTask = Task.Factory.StartNew (() => orderedRemoteCrawlWorker.Start ());

            // Assemble semiTriplets generated from local crawler
            foreach (SyncTriplet.SyncTriplet semiTriplet in semiSyncTriplets.GetConsumingEnumerable ()) {

                SyncTriplet.SyncTriplet remoteTriplet = null;

                // If ignore samelowername, use lowerinvariant to lookup in already-crawled-remote-triplet dictionary.
                // One note: IgnoreIfSameLowercaseName is applied only on remote server. it seems that if local fs is 
                // case sensitive while remote is not, remote will regard two distinct files in local as duplicated files
                // and rename one of them while upload.
                string _key = cmisSyncFolder.CmisProfile.CmisProperties.IgnoreIfSameLowercaseNames ? semiTriplet.Name.ToLowerInvariant () : semiTriplet.Name;

                // if remote info is already crawled
                bool orbContainsKey = false;
                lock (orbLock) {
                    orbContainsKey = orderedRemoteBuffer.Contains (_key);
                    if (orbContainsKey) {

                        remoteTriplet = (SyncTriplet.SyncTriplet)orderedRemoteBuffer [_key];
                        SyncTripletFactory.AssembleRemoteIntoLocal (remoteTriplet, semiTriplet);
                    }
                }

                // if remote is not crawled yet, lookup db for remote path and query CMIS server
                if (!orbContainsKey) {
                    String remotePath = "";
                    if (semiTriplet.DBExist) {
                        remotePath = CmisFileUtil.PathCombine (cmisSyncFolder.RemotePath, semiTriplet.DBStorage.DBRemotePath);
                    } else {
                        // TODO:
                        // Local exist , DB not exist, Remote ???
                        remotePath = CmisFileUtil.PathCombine (cmisSyncFolder.RemotePath, semiTriplet.LocalStorage.RelativePath);
                    }
                    try {
                        ICmisObject remoteObject = session.GetObjectByPath (remotePath, false);

                        if (semiTriplet.IsFolder) {
                            IFolder remoteFolder = (IFolder)remoteObject;
                            SyncTripletFactory.AssembleRemoteIntoLocal (remoteFolder, cmisSyncFolder, semiTriplet);
                        } else {
                            IDocument remoteDocument = (IDocument)remoteObject;

                            SyncTripletFactory.AssembleRemoteIntoLocal (remoteDocument, remotePath, cmisSyncFolder, semiTriplet);
                        }
                    } catch (Exception) {
                        Console.WriteLine (" - remote path: {0} Not found", remotePath);
                    }

                }

                if (!fullSyncTriplets.TryAdd (semiTriplet)) {
                    Console.WriteLine (" - assembled triplet: {0} is not appended to full sync triplet list.", semiTriplet.Name);
                } 
 
                processedTriplets.Add (_key);
            }

            remoteCrawlTask.Wait ();

            // Assemble semiTriplets generated from remote crawler, except those
            // are already processed in the previous process.
            Console.WriteLine (" - Adding remained remote triplets");
            foreach (string key in orderedRemoteBuffer.Keys) {
                // if the triplet is already processed in local crawler, ignore
                if (processedTriplets.Contains (key)) {
                    //Console.WriteLine (" - key: {0}'s assigned remote-semitriplet is already pushed to processor, ignore. Check whether the server is case insensitive.", key);
                    continue;
                } else {
                    Console.WriteLine (" - key: {0}'s assigned remote-semitriplet is not processed yet, push to full sync trip", key);

                    SyncTriplet.SyncTriplet remoteTriplet = (SyncTriplet.SyncTriplet)orderedRemoteBuffer [key];

                    if (remoteTriplet == null) {
                        Console.WriteLine (" - assembled triplet: remote {0} is null.", key);
                        continue;
                    }

                    // merge remote idps for remote only folder (deletion) to processor's idps
                    if (remoteTriplet.IsFolder) {
                        foreach (string dep in r_idps.GetItemDependences (remoteTriplet.Name)) idps.AddItemDependence (remoteTriplet.Name, dep);
                    }

                    if (!fullSyncTriplets.TryAdd (remoteTriplet)) {
                        Console.WriteLine (" - assembled triplet: {0} is not appended to full sync triplet list.", remoteTriplet.Name);
                    }
                }
            }

            // Clear the remote buffer after all objects are pushed to 
            // full synctriplet queue for the next syncing.
            remoteBuffer.Clear ();
            orderedRemoteBuffer.Clear ();
            Console.WriteLine (" - Sync Triplet Assembler Completed. ");
        }

        ~SyncTripletAssembler ()
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
                    if (disposing) {
                    }
                    this.disposed = true;
                }
            }
        }
    }
}
