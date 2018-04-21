﻿// #define DO_NOT_LOAD_CACHE

using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;

using ModIO.API;

using UnityEngine;

namespace ModIO
{
    public delegate void ModEventHandler(ModProfile modProfile);
    public delegate void ModIDEventHandler(int modId);
    public delegate void ModfileEventHandler(int modId, Modfile newModfile);
    public delegate void ModLogoUpdatedEventHandler(int modId, ModLogoVersion version, Texture2D texture);
    public delegate void ModGalleryImageUpdatedEventHandler(int modId, string imageFileName, ModGalleryImageVersion version, Texture2D texture);

    public enum ModBinaryStatus
    {
        Missing,
        RequiresUpdate,
        UpToDate
    }

    [System.Serializable]
    public class AuthenticatedUser
    {
        public string oAuthToken = "";
        public UserProfile profile = null;
        public List<int> subscribedModIDs = new List<int>();
    }

    public class ModManager
    {
        // ---------[ INNER CLASSES ]---------
        [System.Serializable]
        private class ManifestData
        {
            public TimeStamp lastUpdateTimeStamp;
            public List<ModEvent> unresolvedEvents;
            public GameProfile gameProfile;
            public List<string> serializedImageCache;
        }

        // ---------[ VARIABLES ]---------
        private static ManifestData manifest = null;
        private static AuthenticatedUser authUser = null;

        public static string cacheDirectory { get; private set; }
        
        private static string manifestPath { get { return cacheDirectory + "manifest.data"; } }
        private static string userdataPath { get { return cacheDirectory + "user.data"; } }

        // --------- [ INITIALIZATION ]---------
        public static void Initialize()
        {
            if(manifest != null)
            {
                return;
            }
            manifest = new ManifestData();

            #pragma warning disable CS0162
            #if DEBUG
            if(GlobalSettings.USE_TEST_SERVER)
            {
                cacheDirectory = Application.persistentDataPath + "/modio_testServer/";
            }
            else
            #endif
            {
                cacheDirectory = Application.persistentDataPath + "/modio/";
            }
            #pragma warning restore CS0162

            Debug.Log("[mod.io] Initializing ModManager using cache directory: " + cacheDirectory);

            #if UNITY_EDITOR
            if(Application.isPlaying)
            #endif
            {
                var go = new UnityEngine.GameObject("ModIO.UpdateRunner");
                go.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInBuild;
                go.AddComponent<UpdateRunner>();
            }

            LoadCacheFromDisk();
            FetchAndCacheGameProfile();

            FetchAndRebuildModCache();
        }

        private static void LoadCacheFromDisk()
        {
            if (!Directory.Exists(cacheDirectory))
            {
                Directory.CreateDirectory(cacheDirectory);
            }

            #if DO_NOT_LOAD_CACHE
            {
                manifest = new ManifestData();
                manifest.lastUpdateTimeStamp = new TimeStamp();
                manifest.unresolvedEvents = new List<ModEvent>();
                manifest.gameProfile = new GameProfile();

                WriteManifestToDisk();
            }
            #else
            {
                // Attempt to load manifest
                if(!Utility.TryParseJsonFile(manifestPath, out manifest))
                {
                    manifest = new ManifestData();
                    manifest.lastUpdateTimeStamp = new TimeStamp();
                    manifest.unresolvedEvents = new List<ModEvent>();
                    manifest.serializedImageCache = new List<string>();
                    manifest.gameProfile = new GameProfile();

                    WriteManifestToDisk();
                }

                // Attempt to load imageCache
                serverToLocalImageURLMap = new Dictionary<string, string>();
                int i = 0;
                while(i < manifest.serializedImageCache.Count)
                {
                    string[] imageLocation = manifest.serializedImageCache[i].Split('*');
                    if(imageLocation.Length != 2
                       || String.IsNullOrEmpty(imageLocation[0])
                       || String.IsNullOrEmpty(imageLocation[1])
                       || !File.Exists(imageLocation[1]))
                    {
                        manifest.serializedImageCache.RemoveAt(i);
                    }
                    else
                    {
                        serverToLocalImageURLMap.Add(imageLocation[0], imageLocation[1]);
                        ++i;
                    }
                }

                // Attempt to load user
                if(Utility.TryParseJsonFile(userdataPath, out authUser))
                {
                    Action<WebRequestError> onAuthenticationFail = (error) =>
                    {
                        if(error.responseCode == 401
                            || error.responseCode == 403) // Failed authentication
                        {
                            LogUserOut();
                        }
                    };

                    Client.GetAuthenticatedUser(authUser.oAuthToken,
                                                null,
                                                onAuthenticationFail);
                }

                // Attempt to load mod data
                if(!Directory.Exists(cacheDirectory + "mods/"))
                {
                    Directory.CreateDirectory(cacheDirectory + "mods/");
                }

                string[] modDirectories = Directory.GetDirectories(cacheDirectory + "mods/");
                foreach(string modDir in modDirectories)
                {
                    // Load ModProfile from Disk
                    ModProfile profile;
                    if(Utility.TryParseJsonFile(modDir + "/mod_profile.data", out profile))
                    {
                        modCache.Add(profile.id, profile);
                    }
                    else
                    {
                        // TODO(@jackson): better
                        Debug.LogWarning("[mod.io] Unable to parse mod profile at: " + modDir + "/mod_profile.data");
                    }
                }
            }
            #endif
        }

        private static void FetchAndCacheGameProfile()
        {
            Action<API.GameObject> cacheGameProfile = (gameObject) =>
            {
                manifest.gameProfile.ApplyGameObjectValues(gameObject);
                WriteManifestToDisk();
            };

            Client.GetGame(cacheGameProfile, null);
        }

        private static void FetchAndRebuildModCache()
        {
            Action<List<ModObject>> onModObjectsReceived = (modObjects) =>
            {
                ApplyModObjectsToCache(modObjects);

                foreach(var modObject in modObjects)
                {
                    int modId = modObject.id;
                    FetchAllResultsForQuery<MetadataKVPObject>((p,s,e) => Client.GetAllModKVPMetadata(modId,
                                                                                                      p, s, e),
                                                               (r) => ApplyKVPsToCache(modId, r),
                                                               Client.LogError);
                }
            };

            FetchAllResultsForQuery<ModObject>((p,s,e) => Client.GetAllMods(GetAllModsFilter.All, p, s, e),
                                               onModObjectsReceived,
                                               Client.LogError);
        }

        private static void ApplyModObjectsToCache(List<ModObject> modObjects)
        {
            // TODO(@jackson): Implement mod is unavailable
            // TODO(@jackson): Check for modfile change

            manifest.lastUpdateTimeStamp = TimeStamp.Now();
            WriteManifestToDisk();

            var addedMods = new List<ModProfile>();
            var updatedMods = new List<ModProfile>();
            foreach(ModObject modObject in modObjects)
            {
                ModProfile profile;
                if(modCache.TryGetValue(modObject.id, out profile))
                {
                    updatedMods.Add(profile);
                }
                else
                {
                    profile = new ModProfile();
                    addedMods.Add(profile);
                }

                profile.ApplyModObjectValues(modObject);
                StoreModData(profile);
            }

            if(OnModAdded != null)
            {
                foreach(ModProfile profile in addedMods)
                {
                    OnModAdded(profile);
                }
            }

            if(OnModUpdated != null)
            {
                foreach(ModProfile profile in updatedMods)
                {
                    OnModUpdated(profile.id);
                }
            }
        }

        // TODO(@jackson): Defend
        private static void ApplyKVPsToCache(int modId, List<MetadataKVPObject> kvps)
        {
            ModProfile profile = ModManager.GetModProfile(modId);

            profile.ApplyMetadataKVPObjectValues(kvps.ToArray());

            // TODO(@jackson): Notify
            
            StoreModData(profile);
        }



        // ---------[ AUTOMATED UPDATING ]---------
        private const int SECONDS_BETWEEN_POLLING = 15;
        private static bool isUpdatePollingEnabled = false;
        private static bool isUpdatePollingRunning = false;

        public static void EnableUpdatePolling()
        {
            if(!isUpdatePollingEnabled)
            {
                #if UNITY_EDITOR
                if(!Application.isPlaying)
                {
                    UnityEditor.EditorApplication.update += PollForUpdates;
                }
                else
                #endif
                {
                    UpdateRunner.onUpdate += PollForUpdates;
                }
                isUpdatePollingEnabled = true;
            }
        }
        public static void DisableUpdatePolling()
        {
            if(isUpdatePollingEnabled)
            {
                isUpdatePollingEnabled = false;
                #if UNITY_EDITOR
                if(!Application.isPlaying)
                {
                    UnityEditor.EditorApplication.update -= PollForUpdates;
                }
                else
                #endif
                {
                    UpdateRunner.onUpdate -= PollForUpdates;
                }
            }
        }

        private static void PollForUpdates()
        {
            int secondsSinceUpdate = (TimeStamp.Now().AsServerTimeStamp()
                                      - manifest.lastUpdateTimeStamp.AsServerTimeStamp());

            if(secondsSinceUpdate >= SECONDS_BETWEEN_POLLING)
            {
                TimeStamp fromTimeStamp = manifest.lastUpdateTimeStamp;
                TimeStamp untilTimeStamp = TimeStamp.Now();

                // - Get Game Updates -
                FetchAndCacheGameProfile();

                // - Get ModProfile Events -
                GetAllModEventsFilter eventFilter = new GetAllModEventsFilter();
                eventFilter.ApplyIntRange(GetAllModEventsFilter.Field.DateAdded,
                                          fromTimeStamp.AsServerTimeStamp(), true,
                                          untilTimeStamp.AsServerTimeStamp(), false);
                eventFilter.ApplyBooleanIs(GetAllModEventsFilter.Field.Latest,
                                           true);

                FetchAllResultsForQuery<EventObject>((p, s, e) => Client.GetAllModEvents(eventFilter, p, s, e),
                                                     (r) =>
                                                     {
                                                        ProcessModEvents(r);
                                                        manifest.lastUpdateTimeStamp = untilTimeStamp;
                                                     },
                                                     Client.LogError);

                // TODO(@jackson): Replace with Event Polling
                // - Get Subscription Updates -
                if(authUser != null)
                {
                    GetUserSubscriptionsFilter subscriptionFilter = new GetUserSubscriptionsFilter();
                    subscriptionFilter.ApplyIntEquality(GetUserSubscriptionsFilter.Field.GameId, GlobalSettings.GAME_ID);

                    FetchAllResultsForQuery<ModObject>((p,s,e)=>Client.GetUserSubscriptions(authUser.oAuthToken,
                                                                                            subscriptionFilter,
                                                                                            p, s, e),
                                                        UpdateUserSubscriptions,
                                                        Client.LogError);
                }
            }
        }

        private static void ProcessModEvents(List<EventObject> modEventObjects)
        {
            // - ModProfile Processing Options -
            Action<ModEvent> processModAvailable = (modEvent) =>
            {
                Action<ModObject> onGetMod = (modObject) =>
                {
                    var profile = ModProfile.CreateFromModObject(modObject);

                    StoreModData(profile);
                    manifest.unresolvedEvents.Remove(modEvent);

                    if(OnModAdded != null)
                    {
                        OnModAdded(profile);
                    }
                };

                Client.GetMod(modEvent.modId, onGetMod, Client.LogError);
            };
            Action<ModEvent> processModUnavailable = (modEvent) =>
            {
                // TODO(@jackson): Facilitate marking Mods as installed
                bool isModInstalled = (authUser != null
                                       && authUser.subscribedModIDs.Contains(modEvent.modId));

                if(!isModInstalled
                   && modCache.ContainsKey(modEvent.modId))
                {
                    UncacheMod(modEvent.modId);

                    if(OnModRemoved != null)
                    {
                        OnModRemoved(modEvent.modId);
                    }
                }
                manifest.unresolvedEvents.Remove(modEvent);
            };

            Action<ModEvent> processModEdited = (modEvent) =>
            {
                Action<ModObject> onGetMod = (modObject) =>
                {
                    var profile = ModProfile.CreateFromModObject(modObject);

                    StoreModData(profile);
                    manifest.unresolvedEvents.Remove(modEvent);

                    if(OnModUpdated != null)
                    {
                        OnModUpdated(profile.id);
                    }
                };

                Client.GetMod(modEvent.modId, onGetMod, Client.LogError);
            };

            Action<ModEvent> processModfileChange = (modEvent) =>
            {
                ModProfile profile = GetModProfile(modEvent.modId);

                if(profile == null)
                {
                    Debug.Log("Received Modfile change for uncached mod. Ignoring.");
                    manifest.unresolvedEvents.Remove(modEvent);
                }
                else
                {
                    Action<ModObject> onGetMod = (modObject) =>
                    {
                        profile.ApplyModObjectValues(modObject);

                        StoreModData(profile);

                        if(OnModfileChanged != null)
                        {
                            throw new System.NotImplementedException();
                            // OnModfileChanged(profile.id, profile.modfile);
                        }
                    };

                    Client.GetMod(profile.id, onGetMod, Client.LogError);

                    manifest.unresolvedEvents.Remove(modEvent);
                }
            };

            // - Handle ModProfile Event -
            foreach(EventObject eventObject in modEventObjects)
            {
                var modEvent = ModEvent.CreateFromEventObject(eventObject);

                string eventSummary = "TimeStamp (Local)=" + modEvent.dateAdded.AsLocalDateTime();
                eventSummary += "\nMod=" + modEvent.modId;
                eventSummary += "\nEventType=" + modEvent.eventType.ToString();
                
                Debug.Log("[PROCESSING MOD EVENT]\n" + eventSummary);


                manifest.unresolvedEvents.Add(modEvent);

                switch(modEvent.eventType)
                {
                    case ModEventType.ModfileChanged:
                    {
                        processModfileChange(modEvent);
                    }
                    break;
                    case ModEventType.ModAvailable:
                    {
                        processModAvailable(modEvent);
                    }
                    break;
                    case ModEventType.ModUnavailable:
                    {
                        processModUnavailable(modEvent);
                    }
                    break;
                    case ModEventType.ModEdited:
                    {
                        processModEdited(modEvent);
                    }
                    break;
                    default:
                    {
                        Debug.LogError("Unhandled Event Type: " + modEvent.eventType.ToString());
                    }
                    break;
                }
            }
        }

        private static void UpdateUserSubscriptions(List<ModObject> userSubscriptions)
        {
            if(authUser == null) { return; }

            List<int> addedMods = new List<int>();
            List<int> removedMods = authUser.subscribedModIDs;
            authUser.subscribedModIDs = new List<int>(userSubscriptions.Count);

            foreach(ModObject modObject in userSubscriptions)
            {
                authUser.subscribedModIDs.Add(modObject.id);

                if(removedMods.Contains(modObject.id))
                {
                    removedMods.Remove(modObject.id);
                }
                else
                {
                    addedMods.Add(modObject.id);
                }
            }

            WriteUserDataToDisk();

            // - Notify -
            if(OnModSubscriptionAdded != null)
            {
                foreach(int modId in addedMods)
                {
                    OnModSubscriptionAdded(modId);
                }
            }
            if(OnModSubscriptionRemoved != null)
            {
                foreach(int modId in removedMods)
                {
                    OnModSubscriptionRemoved(modId);
                }
            }
        }

        // ---------[ USER MANAGEMENT ]---------
        public static event Action OnUserLoggedOut;
        public static event ModIDEventHandler OnModSubscriptionAdded;
        public static event ModIDEventHandler OnModSubscriptionRemoved;

        public static AuthenticatedUser GetAuthenticatedUser()
        {
            return authUser;
        }

        public static void RequestSecurityCode(string emailAddress,
                                               Action<APIMessage> onSuccess,
                                               Action<WebRequestError> onError)
        {
            Client.RequestSecurityCode(emailAddress,
                                       result => onSuccess(APIMessage.CreateFromMessageObject(result)),
                                       onError);
        }

        public static void RequestOAuthToken(string securityCode,
                                             Action<string> onSuccess,
                                             Action<WebRequestError> onError)
        {
            Client.RequestOAuthToken(securityCode,
                                        onSuccess,
                                        onError);
        }

        public static void TryLogUserIn(string userOAuthToken,
                                        Action<UserProfile> onSuccess,
                                        Action<WebRequestError> onError)
        {
            Action<API.UserObject> onGetUser = (userObject) =>
            {
                authUser = new AuthenticatedUser();
                authUser.oAuthToken = userOAuthToken;
                authUser.profile = UserProfile.CreateFromUserObject(userObject);
                WriteUserDataToDisk();

                onSuccess(authUser.profile);

                GetUserSubscriptionsFilter subscriptionFilter = new GetUserSubscriptionsFilter();
                subscriptionFilter.ApplyIntEquality(GetUserSubscriptionsFilter.Field.GameId, GlobalSettings.GAME_ID);

                FetchAllResultsForQuery<ModObject>((p,s,e)=>Client.GetUserSubscriptions(authUser.oAuthToken,
                                                                                        subscriptionFilter,
                                                                                        p, s, e),
                                                    UpdateUserSubscriptions,
                                                    Client.LogError);
            };

            Client.GetAuthenticatedUser(userOAuthToken,
                                        onGetUser,
                                        onError);
        }

        public static void LogUserOut()
        {
            authUser = null;
            DeleteUserDataFromDisk();
            if(OnUserLoggedOut != null)
            {
                OnUserLoggedOut();
            }
        }

        public static void SubscribeToMod(int modId,
                                          Action<ModProfile> onSuccess,
                                          Action<WebRequestError> onError)
        {
            Client.SubscribeToMod(authUser.oAuthToken,
                                     modId,
                                     (message) =>
                                     {
                                        authUser.subscribedModIDs.Add(modId);
                                        onSuccess(GetModProfile(modId));
                                     },
                                     onError);
        }

        public static void UnsubscribeFromMod(int modId,
                                              Action<ModProfile> onSuccess,
                                              Action<WebRequestError> onError)
        {
            Client.UnsubscribeFromMod(authUser.oAuthToken,
                                         modId,
                                         (message) =>
                                         {
                                            authUser.subscribedModIDs.Remove(modId);
                                            onSuccess(GetModProfile(modId));
                                         },
                                         onError);
        }

        public static bool IsSubscribedToMod(int modId)
        {
            foreach(int subscribedModID in authUser.subscribedModIDs)
            {
                if(subscribedModID == modId) { return true; }
            }
            return false;
        }

        // ---------[ MOD MANAGEMENT ]---------
        public static event ModEventHandler OnModAdded;
        public static event ModIDEventHandler OnModRemoved;
        public static event ModIDEventHandler OnModUpdated;
        public static event ModfileEventHandler OnModfileChanged;

        private static Dictionary<int, ModProfile> modCache = new Dictionary<int, ModProfile>();

        public static string GetModDirectory(int modId)
        {
            return cacheDirectory + "mods/" + modId + "/";
        }

        public static ModProfile GetModProfile(int modId)
        {
            ModProfile profile;
            modCache.TryGetValue(modId, out profile);
            return profile;
        }

        // TODO(@jackson): Pass other components
        private static void StoreModData(ModProfile modProfile)
        {
            // - Cache -
            modCache[modProfile.id] = modProfile;

            // - Write to disk -
            string modDir = GetModDirectory(modProfile.id);
            Directory.CreateDirectory(modDir);
            File.WriteAllText(modDir + "mod_profile.data", JsonUtility.ToJson(modProfile));
            // File.WriteAllText(modDir + "mod_logo.data", JsonUtility.ToJson(modProfile.logo.AsImageSet()));
        }

        private static void StoreModDatas(ModProfile[] modArray)
        {
            foreach(ModProfile mod in modArray)
            {
                StoreModData(mod);
            }
        }
        private static void UncacheMod(int modId)
        {
            string modDir = GetModDirectory(modId);
            Directory.Delete(modDir, true);
        }

        public static ModProfile[] GetModProfiles(GetAllModsFilter filter)
        {
            return Utility.CollectionToArray(modCache.Values);
        }

        public static void DeleteAllDownloadedBinaries(int modId)
        {
            string[] binaryFilePaths = Directory.GetFiles(GetModDirectory(modId), "modfile_*.zip");
            foreach(string binaryFilePath in binaryFilePaths)
            {
                File.Delete(binaryFilePath);
            }
        }

        public static ModBinaryStatus GetBinaryStatus(ModProfile profile)
        {
            if(File.Exists(GetModDirectory(profile.id) + "modfile_" + profile.primaryModfileId + ".zip"))
            {
                return ModBinaryStatus.UpToDate;
            }
            else
            {
                string[] modfileURLs = Directory.GetFiles(GetModDirectory(profile.id), "modfile_*.zip");
                if(modfileURLs.Length > 0)
                {
                    return ModBinaryStatus.RequiresUpdate;
                }
                else
                {
                    return ModBinaryStatus.Missing;
                }
            }
        }

        public static string GetBinaryPath(ModProfile profile)
        {
            if(File.Exists(GetModDirectory(profile.id) + "modfile_" + profile.primaryModfileId + ".zip"))
            {
                return GetModDirectory(profile.id) + "modfile_" + profile.primaryModfileId + ".zip";
            }
            else
            {
                string[] modfileURLs = Directory.GetFiles(GetModDirectory(profile.id), "modfile_*.zip");
                if(modfileURLs.Length > 0)
                {
                    return modfileURLs[0];
                }
            }
            return null;
        }

        // ---------[ MODFILE & BINARY MANAGEMENT ]---------
        public static void LoadOrDownloadModfile(int modId, int modfileId,
                                                 Action<Modfile> onSuccess,
                                                 Action<WebRequestError> onError)
        {
            string modfileFilePath = (GetModDirectory(modId) + "modfile_"
                                      + modfileId.ToString() + ".data");
            if(File.Exists(modfileFilePath))
            {
                // Load ModProfile from Disk
                Modfile modfile = JsonUtility.FromJson<Modfile>(File.ReadAllText(modfileFilePath));
                onSuccess(modfile);
            }
            else
            {
                Action<ModfileObject> writeModfileToDisk = (m) =>
                {
                    Modfile newModfile = Modfile.CreateFromModfileObject(m);
                    File.WriteAllText(modfileFilePath,
                                      JsonUtility.ToJson(newModfile));
                    onSuccess(newModfile);
                };

                Client.GetModfile(modId, modfileId,
                                  writeModfileToDisk,
                                  onError);
            }
        }

        public static FileDownload StartBinaryDownload(int modId, int modfileId)
        {
            string binaryFilePath = (GetModDirectory(modId) + "binary_"
                                     + modfileId.ToString() + ".zip");

            FileDownload download = new FileDownload();
            Action<ModfileObject> queueBinaryDownload = (m) =>
            {
                download.sourceURL = m.download.binary_url;
                download.fileURL = binaryFilePath;
                download.EnableFilehashVerification(m.filehash.md5);

                DownloadManager.AddQueuedDownload(download);
            };

            Client.GetModfile(modId, modfileId,
                              queueBinaryDownload,
                              download.MarkAsFailed);

            return download;
        }

        // ---------[ IMAGE MANAGEMENT ]---------
        public static event ModLogoUpdatedEventHandler OnModLogoUpdated;
        public static event ModGalleryImageUpdatedEventHandler OnModGalleryImageUpdated;
        
        private static Dictionary<string, string> serverToLocalImageURLMap;

        public static string GenerateModLogoFilePath(int modId, ModLogoVersion version)
        {
            return GetModDirectory(modId) + @"/logo/" + version.ToString() + ".png";
        }

        public static Texture2D FindSavedImageMatchingServerURL(string serverURL)
        {
            string filePath;
            Texture2D imageTexture;
            if(serverToLocalImageURLMap.TryGetValue(serverURL, out filePath)
               && Utility.TryLoadTextureFromFile(filePath, out imageTexture))
            {
                return imageTexture;
            }
            return null;
        }

        // TODO(@jackson): Defend
        // TODO(@jackson): Record whether completed (lest placeholder be accepted)
        public static TextureDownload DownloadAndSaveImageAsPNG(string serverURL,
                                                                string downloadFilePath,
                                                                Texture2D placeholderTexture)
        {
            Debug.Assert(Path.GetExtension(downloadFilePath).Equals(".png"),
                         String.Format("[mod.io] Images can only be saved in PNG format."
                                       + "\n\'{0}\' appears to be in a different format.",
                                       downloadFilePath));

            var download = new TextureDownload();

            Directory.CreateDirectory(Path.GetDirectoryName(downloadFilePath));
            File.WriteAllBytes(downloadFilePath, placeholderTexture.EncodeToPNG());

            serverToLocalImageURLMap[serverURL] = downloadFilePath;

            download.sourceURL = serverURL;
            download.OnCompleted += (d) =>
            {
                File.WriteAllBytes(downloadFilePath, download.texture.EncodeToPNG());
                manifest.serializedImageCache.Add(serverURL + "*" + downloadFilePath);
                WriteManifestToDisk();
            };
            DownloadManager.AddConcurrentDownload(download);

            return download;
        }

        public static Texture2D LoadOrDownloadModLogo(int modId, ModLogoVersion version)
        {
            // TODO(@jackson): Defend
            ModProfile profile = GetModProfile(modId);

            Texture2D texture = null;
            string filePath = string.Empty;
            string serverURL = profile.logoLocator.GetVersionSource(version);
            if(serverToLocalImageURLMap.TryGetValue(serverURL, out filePath))
            {
                Utility.TryLoadTextureFromFile(filePath, out texture);
            }

            if(texture == null)
            {
                texture = UISettings.Instance.DownloadingPlaceholderImages.modLogo;
                filePath = GenerateModLogoFilePath(profile.id, version);

                var download = DownloadAndSaveImageAsPNG(serverURL,
                                                         filePath,
                                                         texture);
                download.OnCompleted += (d) =>
                {
                    if(OnModLogoUpdated != null)
                    {
                        OnModLogoUpdated(modId, version, download.texture);
                    }
                };
            }

            return texture;
        }

        // TODO(@jackson): Defend
        public static Texture2D LoadOrDownloadModGalleryImage(int modId, string imageFileName,
                                                              ModGalleryImageVersion version)
        {
            // TODO(@jackson): Defend
            var profile = GetModProfile(modId);

            var imageLocator = profile.GetGalleryImageWithFileName(imageFileName);
            if(imageLocator == null)
            {
                Debug.LogWarning("[mod.io] Unable to find Gallery Image with FileName \'"
                                 + imageFileName + "\' in mod profile ["
                                 + modId + "]:"
                                 + profile.name);
                return null;
            }

            string serverURL = imageLocator.GetVersionSource(version);
            string filePath = null;
            Texture2D texture = null;
            if(serverToLocalImageURLMap.TryGetValue(serverURL, out filePath))
            {
                Utility.TryLoadTextureFromFile(filePath, out texture);
            }

            if(texture == null)
            {
                // TODO(@jackson): Replace with correct placeholder
                texture = UISettings.Instance.DownloadingPlaceholderImages.modLogo;

                filePath = String.Format(@"{0}/modImages/{1}/{2}/{3}.png",
                                         Application.temporaryCachePath,
                                         modId,
                                         version.ToString(),
                                         Path.GetFileNameWithoutExtension(imageFileName));

                // TODO(@jackson): Fix the filePath
                var download = DownloadAndSaveImageAsPNG(serverURL,
                                                         filePath,
                                                         texture);
                download.OnCompleted += (d) =>
                {
                    if(OnModGalleryImageUpdated != null)
                    {
                        OnModGalleryImageUpdated(modId, imageFileName,
                                                 version, download.texture);
                    }
                };
            }

            return texture;
        }

        // TODO(@jackson): param -> ids?
        // TODO(@jackson): defend
        // TODO(@jackson): Add preload function?
        public static void DownloadMissingModLogos(ModProfile[] modProfiles,
                                                   ModLogoVersion version)
        {
            var missingLogoProfiles = new List<ModProfile>(modProfiles);

            // Check which logos are missing
            foreach(ModProfile profile in modProfiles)
            {
                string serverURL = profile.logoLocator.GetVersionSource(version);
                string filePath;
                if(serverToLocalImageURLMap.TryGetValue(serverURL,
                                                        out filePath))
                {
                    if(File.Exists(filePath))
                    {
                        missingLogoProfiles.Remove(profile);
                    }
                    else
                    {
                        serverToLocalImageURLMap.Remove(serverURL);
                    }
                }
            }

            // Download
            foreach(ModProfile profile in missingLogoProfiles)
            {
                string logoURL = profile.logoLocator.GetVersionSource(version);
                string filePath = GenerateModLogoFilePath(profile.id, version);
                var download = DownloadAndSaveImageAsPNG(logoURL,
                                                         filePath,
                                                         UISettings.Instance.DownloadingPlaceholderImages.modLogo);

                download.OnCompleted += (d) =>
                {
                    if(OnModLogoUpdated != null)
                    {
                        OnModLogoUpdated(profile.id, version, download.texture);
                    }
                };
            }
        }

        // ---------[ MISC ]------------
        public static GameProfile GetGameProfile()
        {
            return manifest.gameProfile;
        }

        private static void WriteManifestToDisk()
        {
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest));
        }

        private static void WriteUserDataToDisk()
        {
            File.WriteAllText(userdataPath, JsonUtility.ToJson(authUser));
        }

        private static void DeleteUserDataFromDisk()
        {
            File.Delete(userdataPath);
        }

        // TODO(@jackson): Add MKVPs, Mod Dependencies
        public static void SubmitNewMod(EditableModProfile modEdits,
                                        Action<ModProfile> modSubmissionSucceeded,
                                        Action<WebRequestError> modSubmissionFailed)
        {
            Debug.Assert(modEdits.name.isDirty && modEdits.summary.isDirty);
            Debug.Assert(File.Exists(modEdits.logoLocator.value.source));

            // - Initial Mod Submission -
            var parameters = new AddModParameters();
            parameters.name = modEdits.name.value;
            parameters.summary = modEdits.summary.value;
            parameters.logo = BinaryUpload.Create(Path.GetFileName(modEdits.logoLocator.value.source),
                                                  File.ReadAllBytes(modEdits.logoLocator.value.source));
            if(modEdits.visibility.isDirty)
            {
                parameters.visible = (int)modEdits.visibility.value;
            }
            if(modEdits.nameId.isDirty)
            {
                parameters.name_id = modEdits.nameId.value;
            }
            if(modEdits.description.isDirty)
            {
                parameters.description = modEdits.description.value;
            }
            if(modEdits.homepageURL.isDirty)
            {
                parameters.name_id = modEdits.homepageURL.value;
            }
            if(modEdits.metadataBlob.isDirty)
            {
                parameters.metadata_blob = modEdits.metadataBlob.value;
            }
            if(modEdits.nameId.isDirty)
            {
                parameters.name_id = modEdits.nameId.value;
            }
            if(modEdits.tags.isDirty)
            {
                parameters.tags = modEdits.tags.value;
            }

            // NOTE(@jackson): As add Mod takes more parameters than edit,
            //  we can ignore some of the elements in the EditModParameters
            //  when passing to SubmitModProfileComponents
            var remainingModEdits = new EditableModProfile();
            remainingModEdits.youtubeURLs = modEdits.youtubeURLs;
            remainingModEdits.sketchfabURLs = modEdits.sketchfabURLs;
            remainingModEdits.galleryImageLocators = modEdits.galleryImageLocators;

            Client.AddMod(authUser.oAuthToken,
                          parameters,
                          result => SubmitModProfileComponents(ModProfile.CreateFromModObject(result),
                                                               remainingModEdits,
                                                               modSubmissionSucceeded,
                                                               modSubmissionFailed),
                          modSubmissionFailed);
        }
        // TODO(@jackson): Add MKVPs, Mod Dependencies
        public static void SubmitModChanges(int modId,
                                            EditableModProfile modEdits,
                                            Action<ModProfile> modSubmissionSucceeded,
                                            Action<WebRequestError> modSubmissionFailed)
        {
            Debug.Assert(modId > 0);

            // TODO(@jackson): Defend this code
            ModProfile profile = GetModProfile(modId);

            if(modEdits.status.isDirty
               || modEdits.visibility.isDirty
               || modEdits.name.isDirty
               || modEdits.nameId.isDirty
               || modEdits.summary.isDirty
               || modEdits.description.isDirty
               || modEdits.homepageURL.isDirty
               || modEdits.metadataBlob.isDirty)
            {
                var parameters = new EditModParameters();
                if(modEdits.status.isDirty)
                {
                    parameters.status = (int)modEdits.status.value;
                }
                if(modEdits.visibility.isDirty)
                {
                    parameters.visible = (int)modEdits.visibility.value;
                }
                if(modEdits.name.isDirty)
                {
                    parameters.name = modEdits.name.value;
                }
                if(modEdits.nameId.isDirty)
                {
                    parameters.name_id = modEdits.nameId.value;
                }
                if(modEdits.summary.isDirty)
                {
                    parameters.summary = modEdits.summary.value;
                }
                if(modEdits.description.isDirty)
                {
                    parameters.description = modEdits.description.value;
                }
                if(modEdits.homepageURL.isDirty)
                {
                    parameters.homepage = modEdits.homepageURL.value;
                }
                if(modEdits.metadataBlob.isDirty)
                {
                    parameters.metadata_blob = modEdits.metadataBlob.value;
                }

                Client.EditMod(authUser.oAuthToken,
                               modId, parameters,
                               (p) => SubmitModProfileComponents(profile, modEdits,
                                                                 modSubmissionSucceeded,
                                                                 modSubmissionFailed),
                               modSubmissionFailed);
            }
            // - Get updated ModProfile -
            else
            {
                SubmitModProfileComponents(profile,
                                           modEdits,
                                           modSubmissionSucceeded,
                                           modSubmissionFailed);
            }
        }

        private static void SubmitModProfileComponents(ModProfile profile,
                                                       EditableModProfile modEdits,
                                                       Action<ModProfile> modSubmissionSucceeded,
                                                       Action<WebRequestError> modSubmissionFailed)
        {
            List<Action> submissionActions = new List<Action>();
            int nextActionIndex = 0;
            Action<MessageObject> doNextSubmissionAction = (m) =>
            {
                if(nextActionIndex < submissionActions.Count)
                {
                    submissionActions[nextActionIndex++]();
                }
            };

            // - Media -
            if(modEdits.logoLocator.isDirty
               || modEdits.youtubeURLs.isDirty
               || modEdits.sketchfabURLs.isDirty
               || modEdits.galleryImageLocators.isDirty)
            {
                var addMediaParameters = new AddModMediaParameters();
                var deleteMediaParameters = new DeleteModMediaParameters();
                
                if(modEdits.logoLocator.isDirty
                   && File.Exists(modEdits.logoLocator.value.source))
                {
                    addMediaParameters.logo = BinaryUpload.Create(Path.GetFileName(modEdits.logoLocator.value.source),
                                                                  File.ReadAllBytes(modEdits.logoLocator.value.source));
                }
                
                if(modEdits.youtubeURLs.isDirty)
                {
                    var addedYouTubeLinks = new List<string>(modEdits.youtubeURLs.value);
                    foreach(string youtubeLink in profile.youtubeURLs)
                    {
                        addedYouTubeLinks.Remove(youtubeLink);
                    }
                    addMediaParameters.youtube = addedYouTubeLinks.ToArray();

                    var removedTags = new List<string>(profile.youtubeURLs);
                    foreach(string youtubeLink in modEdits.youtubeURLs.value)
                    {
                        removedTags.Remove(youtubeLink);
                    }
                    deleteMediaParameters.youtube = addedYouTubeLinks.ToArray();
                }
                
                if(modEdits.sketchfabURLs.isDirty)
                {
                    var addedSketchfabLinks = new List<string>(modEdits.sketchfabURLs.value);
                    foreach(string sketchfabLink in profile.sketchfabURLs)
                    {
                        addedSketchfabLinks.Remove(sketchfabLink);
                    }
                    addMediaParameters.sketchfab = addedSketchfabLinks.ToArray();

                    var removedTags = new List<string>(profile.sketchfabURLs);
                    foreach(string sketchfabLink in modEdits.sketchfabURLs.value)
                    {
                        removedTags.Remove(sketchfabLink);
                    }
                    deleteMediaParameters.sketchfab = addedSketchfabLinks.ToArray();
                }

                if(modEdits.galleryImageLocators.isDirty)
                {
                    var addedImageFilePaths = new List<string>();
                    foreach(var locator in modEdits.galleryImageLocators.value)
                    {
                        if(File.Exists(locator.source))
                        {
                            addedImageFilePaths.Add(locator.source);
                        }
                    }
                    // - Create Images.Zip -
                    if(addedImageFilePaths.Count > 0)
                    {
                        string galleryZipLocation = Application.temporaryCachePath + "/modio/imageGallery_" + DateTime.Now.ToFileTime() + ".zip";
                        ZipUtil.Zip(galleryZipLocation, addedImageFilePaths.ToArray());
        
                        var imageGalleryUpload = BinaryUpload.Create("images.zip",
                                                                     File.ReadAllBytes(galleryZipLocation));

                        addMediaParameters.images = imageGalleryUpload;
                    }

                    var removedImageFileNames = new List<string>();
                    foreach(var locator in profile.galleryImageLocators)
                    {
                        removedImageFileNames.Add(locator.fileName);
                    }
                    foreach(var locator in modEdits.galleryImageLocators.value)
                    {
                        removedImageFileNames.Remove(locator.fileName);
                    }

                    if(removedImageFileNames.Count > 0)
                    {
                        deleteMediaParameters.images = removedImageFileNames.ToArray();
                    }
                }

                if(addMediaParameters.stringValues.Count > 0
                   || addMediaParameters.binaryData.Count > 0)
                {
                    submissionActions.Add(() =>
                    {
                        Client.AddModMedia(authUser.oAuthToken,
                                           profile.id,
                                           addMediaParameters,
                                           doNextSubmissionAction, modSubmissionFailed);
                    });
                }
                if(deleteMediaParameters.stringValues.Count > 0)
                {
                    submissionActions.Add(() =>
                    {
                        Client.DeleteModMedia(authUser.oAuthToken,
                                              profile.id,
                                              deleteMediaParameters,
                                              doNextSubmissionAction, modSubmissionFailed);
                    });
                }
            }

            // - Tags -
            if(modEdits.tags.isDirty)
            {
                var addedTags = new List<string>(modEdits.tags.value);
                foreach(string tag in profile.tags)
                {
                    addedTags.Remove(tag);
                }

                var removedTags = new List<string>(profile.tags);
                foreach(string tag in modEdits.tags.value)
                {
                    removedTags.Remove(tag);
                }

                if(addedTags.Count > 0)
                {
                    submissionActions.Add(() =>
                    {
                        var parameters = new AddModTagsParameters();
                        parameters.tags = addedTags.ToArray();
                        Client.AddModTags(authUser.oAuthToken,
                                          profile.id, parameters,
                                          doNextSubmissionAction, modSubmissionFailed);
                    });
                }
                if(removedTags.Count > 0)
                {
                    submissionActions.Add(() =>
                    {
                        var parameters = new DeleteModTagsParameters();
                        parameters.tags = removedTags.ToArray();
                        Client.DeleteModTags(authUser.oAuthToken,
                                             profile.id, parameters,
                                             doNextSubmissionAction, modSubmissionFailed);
                    });
                }
            }

            // - Metadata KVP -

            // - Mod Dependencies -

            // - Team Members -

            // - Get Updated Profile -
            submissionActions.Add(() => Client.GetMod(profile.id,
                                                      (mo) =>
                                                      {
                                                        profile.ApplyModObjectValues(mo);
                                                        modSubmissionSucceeded(profile);
                                                      },
                                                      modSubmissionFailed));

            // - Start submission chain -
            doNextSubmissionAction(new MessageObject());
        }

        // TODO(@jackson): Convert onError to string!
        public static void UploadModBinary_Unzipped(int modId,
                                                    EditableModfile modfileValues,
                                                    string unzippedBinaryLocation,
                                                    bool setPrimary,
                                                    Action<Modfile> onSuccess,
                                                    Action<WebRequestError> onError)
        {
            string binaryZipLocation = Application.temporaryCachePath + "/modio/" + System.IO.Path.GetFileNameWithoutExtension(unzippedBinaryLocation) + DateTime.Now.ToFileTime() + ".zip";

            ZipUtil.Zip(binaryZipLocation, unzippedBinaryLocation);

            UploadModBinary_Zipped(modId, modfileValues, binaryZipLocation, setPrimary, onSuccess, onError);
        }

        public static void UploadModBinary_Zipped(int modId,
                                                  EditableModfile modfileValues,
                                                  string binaryZipLocation,
                                                  bool setPrimary,
                                                  Action<Modfile> onSuccess,
                                                  Action<WebRequestError> onError)
        {
            string buildFilename = Path.GetFileName(binaryZipLocation);
            byte[] buildZipData = File.ReadAllBytes(binaryZipLocation);

            AddModfileParameters parameters = new AddModfileParameters();
            parameters.filedata = BinaryUpload.Create(buildFilename, buildZipData);
            if(modfileValues.version.isDirty)
            {
                parameters.version = modfileValues.version.value;
            }
            if(modfileValues.changelog.isDirty)
            {
                parameters.changelog = modfileValues.changelog.value;
            }
            if(modfileValues.metadataBlob.isDirty)
            {
                parameters.metadata_blob = modfileValues.metadataBlob.value;
            }

            // TODO(@jackson): parameters.filehash

            Client.AddModfile(authUser.oAuthToken,
                              modId,
                              parameters,
                              (m) => onSuccess(Modfile.CreateFromModfileObject(m)),
                              onError);
        }

        // --- TEMPORARY PASS-THROUGH FUNCTIONS ---
        public static void AddPositiveRating(int modId,
                                             Action<APIMessage> onSuccess,
                                             Action<WebRequestError> onError)
        {
            Client.AddModRating(authUser.oAuthToken,
                                modId, new AddModRatingParameters(1),
                                result => onSuccess(APIMessage.CreateFromMessageObject(result)),
                                onError);
        }

        // public static void AddModTeamMember(int modId, UnsubmittedTeamMember teamMember,
        //                                     Action<APIMessage> onSuccess,
        //                                     Action<WebRequestError> onError)
        // {
        //     Client.AddModTeamMember(authUser.oAuthToken,
        //                             modId, teamMember.AsAddModTeamMemberParameters(),
        //                             result => OnSuccessWrapper(result, onSuccess),
        //                             onError);
        // }

        public static void DeleteModFromServer(int modId,
                                               Action<APIMessage> onSuccess,
                                               Action<WebRequestError> onError)
        {
            // TODO(@jackson): Remvoe Mod Locally

            Client.DeleteMod(authUser.oAuthToken,
                             modId,
                             result => onSuccess(APIMessage.CreateFromMessageObject(result)),
                             onError);
        }

        public static void DeleteModComment(int modId, int commentId,
                                            Action<APIMessage> onSuccess,
                                            Action<WebRequestError> onError)
        {
            Client.DeleteModComment(authUser.oAuthToken,
                                    modId, commentId,
                                    result => onSuccess(APIMessage.CreateFromMessageObject(result)),
                                    onError);
        }

        public delegate void GetAllObjectsQuery<T>(PaginationParameters pagination,
                                                    Action<ObjectArray<T>> onSuccess,
                                                    Action<WebRequestError> onError);

        public static void FetchAllResultsForQuery<T>(GetAllObjectsQuery<T> query,
                                                      Action<List<T>> onSuccess,
                                                      Action<WebRequestError> onError)
        {
            var pagination = new PaginationParameters()
            {
                limit = PaginationParameters.LIMIT_MAX,
                offset = 0,
            };

            var results = new List<T>();

            query(pagination,
                  (r) => FetchQueryResultsRecursively(query,
                                                      r,
                                                      pagination,
                                                      results,
                                                      onSuccess,
                                                      onError),
                  onError);
        }

        private static void FetchQueryResultsRecursively<T>(GetAllObjectsQuery<T> query,
                                                            ObjectArray<T> queryResult,
                                                            PaginationParameters pagination,
                                                            List<T> culmativeResults,
                                                            Action<List<T>> onSuccess,
                                                            Action<WebRequestError> onError)
        {
            Debug.Assert(pagination.limit > 0);

            culmativeResults.AddRange(queryResult.data);

            if(queryResult.result_count < queryResult.result_limit)
            {
                onSuccess(culmativeResults);
            }
            else
            {
                pagination.offset += pagination.limit;

                query(pagination,
                      (r) => FetchQueryResultsRecursively(query,
                                                          queryResult,
                                                          pagination,
                                                          culmativeResults,
                                                          onSuccess,
                                                          onError),
                      onError);
            }
        }

        // public static void GetAllModTeamMembers(int modId,
        //                                         Action<TeamMember[]> onSuccess,
        //                                         Action<WebRequestError> onError)
        // {
        //     Client.GetAllModTeamMembers(modId, GetAllModTeamMembersFilter.None,
        //                                    result => OnSuccessWrapper(result, onSuccess),
        //                                    onError);
        // }
    }
}