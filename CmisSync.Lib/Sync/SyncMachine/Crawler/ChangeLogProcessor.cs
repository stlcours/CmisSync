﻿#pragma warning disable 0414, 0219
using System;
using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;

using CmisSync.Lib.Config;
using CmisSync.Lib.Cmis;
using CmisSync.Lib.Sync.SyncTriplet;
using CmisSync.Lib.Sync.SyncMachine.Crawler;
using CmisSync.Lib.Sync.SyncMachine.Exceptions;
using CmisSync.Lib.Sync.SyncMachine.Internal;
using CmisSync.Lib.Utilities.FileUtilities;

using log4net;
using DotCMIS;
using DotCMIS.Client;
using DotCMIS.Enums;
using DotCMIS.Exceptions;

namespace CmisSync.Lib.Sync.SyncMachine.Crawler
{

    /// <summary>
    /// The changelog processor gets all changes from CMIS server after last synchroning. Then
    /// it creates synctriplets as well as triplets' dependencies and pushes them to the full
    /// synctriplet queue.
    /// </summary>
    public class ChangeLogProcessor
    {
        private static readonly ILog Logger = LogManager.GetLogger (typeof (ChangeLogProcessor));

        private BlockingCollection<SyncTriplet.SyncTriplet> fullSyncTriplets = null;

        private Dictionary<string, List<IChangeEvent>> changeBuffer = null;

        private CmisSyncFolder.CmisSyncFolder cmisSyncFolder;

        private ItemsDependencies idps = null;

        private HashSet<string> possibleProcessedParentBuffer = new HashSet<string> (); 

        private ISession session;

        public ChangeLogProcessor (CmisSyncFolder.CmisSyncFolder cmisSyncFolder, ISession session, 
            BlockingCollection<SyncTriplet.SyncTriplet> fullSyncTriplets, ItemsDependencies idps)
        {
            this.cmisSyncFolder = cmisSyncFolder;
            this.session = session;
            this.fullSyncTriplets = fullSyncTriplets;
            this.idps = idps;
        }

        public void Start ()
        {

            Console.WriteLine (" Start Changelog process :");

            changeBuffer =  new Dictionary<string, List<IChangeEvent>> ();

            Config.CmisSyncConfig.Feature features = null;
            if (ConfigManager.CurrentConfig.GetFolder (cmisSyncFolder.Name) != null)
                features = ConfigManager.CurrentConfig.GetFolder (cmisSyncFolder.Name).SupportedFeatures;

            int maxNumItems = (features != null && features.MaxNumberOfContentChanges != null) ?  // TODO if there are more items, either loop or force CrawlSync
                (int)features.MaxNumberOfContentChanges : 50;


            string lastTokenOnClient = cmisSyncFolder.Database.GetChangeLogToken ();
            string lastTokenOnServer = CmisUtils.GetChangeLogToken (session);

            if (lastTokenOnClient == lastTokenOnServer) {
                Console.WriteLine ("  Synchronized ");
                return;
            }
            if (lastTokenOnClient == null) {
                Console.WriteLine ("  Should do full sync! Local token is null");
                return;
            }

            // ChangeLog tokens are different, so checking changes is needed.
            var currentChangeToken = lastTokenOnClient;
            IChangeEvents changes;
            do {
                Console.WriteLine (" Get changes for current token: {0}", currentChangeToken);

                // Check which documents/folders have changed.
                changes = session.GetContentChanges (currentChangeToken, cmisSyncFolder.CmisProfile.CmisProperties.IsPropertyChangesSupported, maxNumItems);

                /*
                 * Due to latest report in the master branch: single rename will not duplicate.
                 */
                var changeEvents = changes.ChangeEventList.Where (p => p != changes.ChangeEventList.FirstOrDefault ()).ToList ();

                /*
                 *  To avoid sequential update on a single object.
                 *  TODO: Actually it is not necessary in current version because the changelog processor will exist
                 *  when UPDATE is detected and call the full crawler
                 */
                foreach (IChangeEvent changeEvent in changeEvents) {
                    if (changeBuffer.ContainsKey (changeEvent.ObjectId)) {
                        try {
                            long deltaTime = ((DateTime)changeEvent.ChangeTime).ToFileTime () - ((DateTime)changeBuffer [changeEvent.ObjectId].Last ().ChangeTime).ToFileTime ();
                            // FileTime's unit is 100nano second.
                            // 5000000 = 0.5s = 500ms 
                            // If an Update is following a Create in 500ms, ignore it. Create the object;
                            if (deltaTime > 5000000) {
                                changeBuffer [changeEvent.ObjectId].Add (changeEvent);
                            }
                        } catch {
                            // ChangeTime is null
                            changeBuffer [changeEvent.ObjectId].Add (changeEvent);
                        }
                    } else {
                        changeBuffer [changeEvent.ObjectId] = new List<IChangeEvent> { changeEvent };
                    }
                }

                currentChangeToken = changes.LatestChangeLogToken;

                if (changes.HasMoreItems == true && (currentChangeToken == null || currentChangeToken.Length == 0)) {
                    // then the repository is too old to support changelog, do normal full sync
                    break;
                }
            }
            // Repeat if there were too many changes to fit in a single response.
            while (changes.HasMoreItems ?? false);

            // processs change logs
            foreach (string objId in changeBuffer.Keys) {
                
                //ChangeType? action = changeBuffer [objId].Last ();
                ChangeType? action = changeBuffer [objId].Last ().ChangeType;

                /*for some old version of alfresco, ObjectId of changeEvent would be /RemotePath/ + Id, remove it*/
                string remoteId = objId.Split (CmisUtils.CMIS_FILE_SEPARATOR).Last ();
                try {
                    Console.WriteLine ("  Getting remote object, last type: {0}, id = {1}", action, objId);

                    ICmisObject obj = session.GetObject ( remoteId, false);

                    /*
                     * for some cmis eg: old alfresco, get changelog token for one repository will
                     * return all changelog tokens. Check if they are worth syncing here.
                     *   - SyncFileUtil: their path starts with CmisSyncFolder.RemotePath
                     *   - CmisFileUtil: their name contain no slashes
                     */
                    if (!SyncFileUtil.IsRemoteObjectInCmisSyncPath (obj, cmisSyncFolder) ||
                        !CmisFileUtil.RemoteObjectWorthSyncing (obj)) continue;

                    /*
                     * if this line is called, there must be a remote object, therefore the changetype must be created, updated (or security)
                     * thus it should be buffered for parallel process
                     */                    
                    if (action == ChangeType.Updated) {
                        // do full sync
                        throw new ChangeLogProcessorBrokenException (String.Format(" UPDATE detected, id = {0}, name = {1}", obj.Id, obj.Name));
                    }

                    /*
                     * Remote Create changes do not require the dependencies. Because creating folder locally is thread safe.
                     */
                    SyncTriplet.SyncTriplet triplet = null;
                    if (obj is IFolder) {

                        triplet = SyncTripletFactory.CreateFromRemoteFolder ((IFolder)obj, cmisSyncFolder);
                        Console.WriteLine ("  -- {0} is Folder, id = {1}, action = {2}", ((IFolder)obj).Path, ((IFolder)obj).Id, action);

                    } else if (obj is IDocument) {
                        triplet = SyncTripletFactory.CreateFromRemoteDocument ((IDocument)obj, cmisSyncFolder);
                        Console.WriteLine ("  -- {0} is Document, id = {1}", ((IDocument)obj).Name, ((IDocument)obj).Id);
                    }

                    if (!fullSyncTriplets.TryAdd (triplet)) {
                        Console.WriteLine ("Add folder triplet to full synctriplet queue failed: " + obj.Name);
                        Logger.Error ("Add folder triplet to full synctriplet queue failed: " + obj.Name);
                    }

                } catch (CmisObjectNotFoundException ex) { /* should be CmisObjectNotFoundExcepiton, not Exception, otherwise previous ChangeLogProcessorBroken will be caught */

                    /*
                     * The change type is Deletion, prepare synctriplets and their dependencies for sync.
                     *                     
                     * The idps method delete the idps[o] only when o is processed. But in the changelog
                     * processing, a folder might not be processed due to no change on it while its containing
                     * files might appear in the changelog.                     
                     *
                     * Therefore we use a set possibleProcessedParentBuffer to record all parents and check if 
                     * they've appeared in the changelog. If not, remove them from idps after all changelogs are
                     * processed.                    
                     */                    
                    if (action == ChangeType.Deleted) {

                        var dbpath = cmisSyncFolder.Database.GetPathById (remoteId);
                        string localpath = (dbpath == null ? null : dbpath [0]);

                        if (localpath != null) {

                            /*
                             * Local path of folder does not contains '/' at the end of its name.
                             * So if the length of parent folder is 0, it should be the root folder
                             * of remote repository. One should not add the root folder to idps hash table.                            
                             */
                            String parent = CmisFileUtil.GetUpperFolderOfCmisPath (localpath);
                            if (parent.Length > 0) {
                                parent = parent + CmisUtils.CMIS_FILE_SEPARATOR;

                                // parent folder's operation depends on current object
                                idps.AddItemDependence (parent, dbpath[2].Equals("Folder") ? localpath + CmisUtils.CMIS_FILE_SEPARATOR : localpath);
                                possibleProcessedParentBuffer.Add (parent);
                            }

                            string localFullPath = Path.Combine (cmisSyncFolder.LocalPath, localpath);
                            Console.WriteLine ("  --  {1} event: {0}", action, localFullPath);


                            if (dbpath[2].Equals("Folder")) {
                                Console.WriteLine ("  -- Delete folder work: {0}", localFullPath);

                                /*
                                 * If the folder appears in the changelog, its idps should be removed by full synctriplet processor. 
                                 * So delete it in the possibleDeletionFolderBuffer.                                
                                 */
                                SyncTriplet.SyncTriplet triplet = SyncTripletFactory.CreateSFPFromLocalFolder (localFullPath, cmisSyncFolder);
                                possibleProcessedParentBuffer.Remove (triplet.Name);

                                if (!fullSyncTriplets.TryAdd (triplet)) {
                                    Console.WriteLine ("Add folder deletion triplet to full synctriplet queue failed! {0}", localFullPath);
                                }
                            } else {
                                SyncTriplet.SyncTriplet triplet = SyncTripletFactory.CreateSFPFromLocalDocument (localFullPath, cmisSyncFolder);

                                Console.WriteLine ("  -- Delete file work: {0}, parent: {1}", localFullPath, parent);

                                if (!fullSyncTriplets.TryAdd(triplet)) {
                                    Console.WriteLine ("Add file deletion triplet to full synctriplet queue failed! {0}", localFullPath);
                                }
                            }

                        } else {
                            Console.WriteLine ("  -- {0} not found in DB, ignore", objId);
                        }
                    } 
                }
            }

            Console.WriteLine ("  -- All changelog processed.");

            /* 
             * After all changes are processed, delete all folders in the possibleDeletionFolderBuffer from idps
             * to make the full-triplet-processor consistent with crawler sync that stop blockingcollection when 
             * the idps is empty.            
             */            
            foreach (String pd in possibleProcessedParentBuffer) {
                Console.WriteLine ("  -- Remove possible deletion folder's dependecies {0}", pd);
                idps.RemoveItemDependence (pd, ProcessWorker.SyncResult.SUCCEED);
            }
        }
    }
}
#pragma warning restore 0414, 0219