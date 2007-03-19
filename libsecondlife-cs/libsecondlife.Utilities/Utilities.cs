using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using libsecondlife;
using libsecondlife.Packets;

namespace libsecondlife.Utilities
{
    public static class Realism
    {
        public readonly static LLUUID TypingAnimation = new LLUUID("c541c47f-e0c0-058b-ad1a-d6ae3a4584d9");

        /// <summary>
        ///  A psuedo-realistic chat function that uses the typing sound and
        /// animation, types at three characters per second, and randomly 
        /// pauses. This function will block until the message has been sent
        /// </summary>
        /// <param name="client">A reference to the client that will chat</param>
        /// <param name="message">The chat message to send</param>
        public static void Chat(SecondLife client, string message)
        {
            Chat(client, message, MainAvatar.ChatType.Normal, 3);
        }

        /// <summary>
        /// A psuedo-realistic chat function that uses the typing sound and
        /// animation, types at a given rate, and randomly pauses. This 
        /// function will block until the message has been sent
        /// </summary>
        /// <param name="client">A reference to the client that will chat</param>
        /// <param name="message">The chat message to send</param>
        /// <param name="type">The chat type (usually Normal, Whisper or Shout)</param>
        /// <param name="cps">Characters per second rate for chatting</param>
        public static void Chat(SecondLife client, string message, MainAvatar.ChatType type, int cps)
        {
            Random rand = new Random();
            int characters = 0;
            bool typing = true;

            // Start typing
            client.Self.Chat("", 0, MainAvatar.ChatType.StartTyping);
            client.Self.AnimationStart(TypingAnimation);

            while (characters < message.Length)
            {
                if (!typing)
                {
                    // Start typing again
                    client.Self.Chat("", 0, MainAvatar.ChatType.StartTyping);
                    client.Self.AnimationStart(TypingAnimation);
                    typing = true;
                }
                else
                {
                    // Randomly pause typing
                    if (rand.Next(10) >= 9)
                    {
                        client.Self.Chat("", 0, MainAvatar.ChatType.StopTyping);
                        client.Self.AnimationStop(TypingAnimation);
                        typing = false;
                    }
                }

                // Sleep for a second and increase the amount of characters we've typed
                System.Threading.Thread.Sleep(1000);
                characters += cps;
            }

            // Send the message
            client.Self.Chat(message, 0, type);

            // Stop typing
            client.Self.Chat("", 0, MainAvatar.ChatType.StopTyping);
            client.Self.AnimationStop(TypingAnimation);
        }
    }

    public class Connection
    {
        private SecondLife Client;
        private ulong SimHandle = 0;
        private LLVector3 Position = LLVector3.Zero;
//        private LLUUID Seat = LLUUID.Zero;
        private System.Timers.Timer CheckTimer;

        public Connection(SecondLife client, int timerFrequency)
        {
            Client = client;

            CheckTimer = new System.Timers.Timer(timerFrequency);
            CheckTimer.Elapsed += new System.Timers.ElapsedEventHandler(CheckTimer_Elapsed);
        }

        void CheckTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (SimHandle != 0)
            {
                if (Client.Network.CurrentSim.Handle != 0 &&
                    Client.Network.CurrentSim.Handle != SimHandle)
                {
                    // Attempt to move to our target sim
                    Client.Self.Teleport(SimHandle, Position);
                }
            }
        }

        public void StayInSim(ulong handle, LLVector3 desiredPosition)
        {
            SimHandle = handle;
            Position = desiredPosition;
            CheckTimer.Start();
        }
    }

    ///// <summary>
    ///// Keeps an up to date inventory of the currently seen objects in each
    ///// simulator
    ///// </summary>
    //public class ObjectTracker
    //{
    //    private SecondLife Client;
    //    private Dictionary<ulong, Dictionary<uint, PrimObject>> SimPrims = new Dictionary<ulong, Dictionary<uint, PrimObject>>();

    //    /// <summary>
    //    /// Default constructor
    //    /// </summary>
    //    /// <param name="client">A reference to the SecondLife client to track
    //    /// objects for</param>
    //    public ObjectTracker(SecondLife client)
    //    {
    //        Client = client;
    //    }
    //}

    /// <summary>
    /// Maintains a cache of avatars and does blocking lookups for avatar data
    /// </summary>
    public class AvatarTracker
    {
        protected SecondLife Client;
        protected Dictionary<LLUUID, Avatar> avatars = new Dictionary<LLUUID, Avatar>();
        protected Dictionary<LLUUID, ManualResetEvent> NameLookupEvents = new Dictionary<LLUUID, ManualResetEvent>();
        protected Dictionary<LLUUID, ManualResetEvent> StatisticsLookupEvents = new Dictionary<LLUUID, ManualResetEvent>();
        protected Dictionary<LLUUID, ManualResetEvent> PropertiesLookupEvents = new Dictionary<LLUUID, ManualResetEvent>();
        protected Dictionary<LLUUID, ManualResetEvent> InterestsLookupEvents = new Dictionary<LLUUID, ManualResetEvent>();
        protected Dictionary<LLUUID, ManualResetEvent> GroupsLookupEvents = new Dictionary<LLUUID, ManualResetEvent>();

        public AvatarTracker(SecondLife client)
        {
            Client = client;

            Client.Avatars.OnAvatarNames += new AvatarManager.AvatarNamesCallback(Avatars_OnAvatarNames);
            Client.Avatars.OnAvatarInterests += new AvatarManager.AvatarInterestsCallback(Avatars_OnAvatarInterests);
            Client.Avatars.OnAvatarProperties += new AvatarManager.AvatarPropertiesCallback(Avatars_OnAvatarProperties);
            Client.Avatars.OnAvatarStatistics += new AvatarManager.AvatarStatisticsCallback(Avatars_OnAvatarStatistics);
            Client.Avatars.OnAvatarGroups += new AvatarManager.AvatarGroupsCallback(Avatars_OnAvatarGroups);

            Client.Objects.OnNewAvatar += new ObjectManager.NewAvatarCallback(Objects_OnNewAvatar);
            Client.Objects.OnObjectUpdated += new ObjectManager.ObjectUpdatedCallback(Objects_OnObjectUpdated);
        }

        /// <summary>
        /// Check if a particular avatar is in the local cache
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public bool Contains(LLUUID id)
        {
            return avatars.ContainsKey(id);
        }

        public Dictionary<LLUUID, Avatar> SimLocalAvatars()
        {
            Dictionary<LLUUID, Avatar> local = new Dictionary<LLUUID, Avatar>();

            lock (avatars)
            {
                foreach (Avatar avatar in avatars.Values)
                {
                    if (avatar.CurrentSim == Client.Network.CurrentSim)
                        local[avatar.ID] = avatar;
                }
            }

            return local;
        }

        /// <summary>
        /// Get an avatar's name, either from the cache or request it.
        /// This function is blocking
        /// </summary>
        /// <param name="id">Avatar key to look up</param>
        /// <returns>The avatar name, or String.Empty if the lookup failed</returns>
        public string GetAvatarName(LLUUID id)
        {
            // Short circuit the cache lookup in GetAvatarNames
            if (Contains(id))
                return LocalAvatarNameLookup(id);

            // Add to the dictionary
            lock (NameLookupEvents) NameLookupEvents.Add(id, new ManualResetEvent(false));

            // Call function
            Client.Avatars.RequestAvatarName(id);

            // Start blocking while we wait for this name to be fetched
            NameLookupEvents[id].WaitOne(5000, false);

            // Clean up
            lock (NameLookupEvents) NameLookupEvents.Remove(id);

            // Return
            return LocalAvatarNameLookup(id);
        }

        //public void BeginGetAvatarName(LLUUID id)
        //{
        //    // TODO: BeginGetAvatarNames is pretty bulky, rewrite a simple version here

        //    List<LLUUID> ids = new List<LLUUID>();
        //    ids.Add(id);
        //    BeginGetAvatarNames(ids);
        //}

        //public void BeginGetAvatarNames(List<LLUUID> ids)
        //{
        //    Dictionary<LLUUID, string> havenames = new Dictionary<LLUUID, string>();
        //    List<LLUUID> neednames = new List<LLUUID>();

        //    // Fire callbacks for the ones we already have cached
        //    foreach (LLUUID id in ids)
        //    {
        //        if (Avatars.ContainsKey(id))
        //        {
        //            havenames[id] = Avatars[id].Name;
        //            //Short circuit the lookup process
        //            if (ManualResetEvents.ContainsKey(id))
        //            {
        //                ManualResetEvents[id].Set();
        //                return;
        //            }
        //        }
        //        else
        //        {
        //            neednames.Add(id);
        //        }
        //    }

        //    if (havenames.Count > 0 && OnAgentNames != null)
        //    {
        //        OnAgentNames(havenames);
        //    }

        //    if (neednames.Count > 0)
        //    {
        //        UUIDNameRequestPacket request = new UUIDNameRequestPacket();

        //        request.UUIDNameBlock = new UUIDNameRequestPacket.UUIDNameBlockBlock[neednames.Count];

        //        for (int i = 0; i < neednames.Count; i++)
        //        {
        //            request.UUIDNameBlock[i] = new UUIDNameRequestPacket.UUIDNameBlockBlock();
        //            request.UUIDNameBlock[i].ID = neednames[i];
        //        }

        //        Client.Network.SendPacket(request);
        //    }
        //}

        public bool GetAvatarProfile(LLUUID id, out Avatar.Interests interests, out Avatar.AvatarProperties properties,
            out Avatar.Statistics statistics, out List<LLUUID> groups)
        {
            // Do a local lookup first
            if (avatars.ContainsKey(id) && avatars[id].ProfileProperties.BornOn != null &&
                avatars[id].ProfileProperties.BornOn != String.Empty)
            {
                interests = avatars[id].ProfileInterests;
                properties = avatars[id].ProfileProperties;
                statistics = avatars[id].ProfileStatistics;
                groups = avatars[id].Groups;

                return true;
            }

            // Create the ManualResetEvents
            lock (PropertiesLookupEvents)
                if (!PropertiesLookupEvents.ContainsKey(id))
                    PropertiesLookupEvents[id] = new ManualResetEvent(false);
            lock (InterestsLookupEvents)
                if (!InterestsLookupEvents.ContainsKey(id))
                    InterestsLookupEvents[id] = new ManualResetEvent(false);
            lock (StatisticsLookupEvents)
                if (!StatisticsLookupEvents.ContainsKey(id))
                    StatisticsLookupEvents[id] = new ManualResetEvent(false);
            lock (GroupsLookupEvents)
                if (!GroupsLookupEvents.ContainsKey(id))
                    GroupsLookupEvents[id] = new ManualResetEvent(false);

            // Request the avatar profile
            Client.Avatars.RequestAvatarProperties(id);

            // Wait for all of the events to complete
            PropertiesLookupEvents[id].WaitOne(5000, false);
            InterestsLookupEvents[id].WaitOne(5000, false);
            StatisticsLookupEvents[id].WaitOne(5000, false);
            GroupsLookupEvents[id].WaitOne(5000, false);

            // Destroy the ManualResetEvents
            lock (PropertiesLookupEvents)
                PropertiesLookupEvents.Remove(id);
            lock (InterestsLookupEvents)
                InterestsLookupEvents.Remove(id);
            lock (StatisticsLookupEvents)
                StatisticsLookupEvents.Remove(id);
            lock (GroupsLookupEvents)
                GroupsLookupEvents.Remove(id);

            // If we got a filled in profile return everything
            if (avatars.ContainsKey(id) && avatars[id].ProfileProperties.BornOn != null &&
                avatars[id].ProfileProperties.BornOn != String.Empty)
            {
                interests = avatars[id].ProfileInterests;
                properties = avatars[id].ProfileProperties;
                statistics = avatars[id].ProfileStatistics;
                groups = avatars[id].Groups;

                return true;
            }
            else
            {
                interests = new Avatar.Interests();
                properties = new Avatar.AvatarProperties();
                statistics = new Avatar.Statistics();
                groups = null;

                return false;
            }
        }

        /// <summary>
        /// This function will only check if the avatar name exists locally,
        /// it will not do any networking calls to fetch the name
        /// </summary>
        /// <returns>The avatar name, or an empty string if it's not found</returns>
        protected string LocalAvatarNameLookup(LLUUID id)
        {
            lock (avatars)
            {
                if (avatars.ContainsKey(id))
                    return avatars[id].Name;
                else
                    return String.Empty;
            }
        }

        void Objects_OnNewAvatar(Simulator simulator, Avatar avatar, ulong regionHandle, ushort timeDilation)
        {
            // TODO:
        }

        void Objects_OnObjectUpdated(Simulator simulator, ObjectUpdate update, ulong regionHandle, ushort timeDilation)
        {
            // TODO:
        }

        private void Avatars_OnAvatarNames(Dictionary<LLUUID, string> names)
        {
            lock (avatars)
            {
                foreach (KeyValuePair<LLUUID, string> kvp in names)
                {
                    if (!avatars.ContainsKey(kvp.Key) || avatars[kvp.Key] == null)
                        avatars[kvp.Key] = new Avatar();

                    // FIXME: Change this to .name when we move inside libsecondlife
                    avatars[kvp.Key].Name = kvp.Value;

                    if (NameLookupEvents.ContainsKey(kvp.Key))
                        NameLookupEvents[kvp.Key].Set();
                }
            }
        }

        void Avatars_OnAvatarStatistics(LLUUID avatarID, Avatar.Statistics statistics)
        {
            lock (avatars)
            {
                if (!avatars.ContainsKey(avatarID))
                    avatars[avatarID] = new Avatar();

                avatars[avatarID].ProfileStatistics = statistics;
            }

            if (StatisticsLookupEvents.ContainsKey(avatarID))
                StatisticsLookupEvents[avatarID].Set();
        }

        void Avatars_OnAvatarProperties(LLUUID avatarID, Avatar.AvatarProperties properties)
        {
            lock (avatars)
            {
                if (!avatars.ContainsKey(avatarID))
                    avatars[avatarID] = new Avatar();

                avatars[avatarID].ProfileProperties = properties;
            }

            if (PropertiesLookupEvents.ContainsKey(avatarID))
                PropertiesLookupEvents[avatarID].Set();
        }

        void Avatars_OnAvatarInterests(LLUUID avatarID, Avatar.Interests interests)
        {
            lock (avatars)
            {
                if (!avatars.ContainsKey(avatarID))
                    avatars[avatarID] = new Avatar();

                avatars[avatarID].ProfileInterests = interests;
            }

            if (InterestsLookupEvents.ContainsKey(avatarID))
                InterestsLookupEvents[avatarID].Set();
        }

        void Avatars_OnAvatarGroups(LLUUID avatarID, AvatarGroupsReplyPacket.GroupDataBlock[] groups)
        {
            List<LLUUID> groupList = new List<LLUUID>();

            foreach (AvatarGroupsReplyPacket.GroupDataBlock block in groups)
            {
                // TODO: We just toss away all the other information here, seems like a waste...
                groupList.Add(block.GroupID);
            }

            lock (avatars)
            {
                if (!avatars.ContainsKey(avatarID))
                    avatars[avatarID] = new Avatar();

                avatars[avatarID].Groups = groupList;
            }

            if (GroupsLookupEvents.ContainsKey(avatarID))
                GroupsLookupEvents[avatarID].Set();
        }
    }

    /// <summary>
    /// 
    /// </summary>
    public class ParcelDownloader
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="simulator">Simulator where the parcels are located</param>
        /// <param name="Parcels">Mapping of parcel LocalIDs to Parcel objects</param>
        public delegate void ParcelsDownloadedCallback(Simulator simulator, Dictionary<int, Parcel> Parcels, int[,] map);


        /// <summary>
        /// 
        /// </summary>
        public event ParcelsDownloadedCallback OnParcelsDownloaded;

        private SecondLife Client;
        /// <summary>Dictionary of 64x64 arrays of parcels which have been successfully downloaded 
        /// for each simulator (and their LocalID's, 0 = Null)</summary>
        private Dictionary<Simulator, int[,]> ParcelMarked = new Dictionary<Simulator, int[,]>();
        /// <summary></summary>
        private Dictionary<Simulator, Dictionary<int, Parcel>> Parcels = new Dictionary<Simulator, Dictionary<int, Parcel>>();

        private ParcelManager.ParcelPropertiesCallback packet_callback = null;
        private ArrayList active_sims;

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="client">A reference to the SecondLife client</param>
        public ParcelDownloader(SecondLife client)
        {
            Client = client;
            active_sims = new ArrayList();
        }

        public void DownloadSimParcels(Simulator simulator)
        {
            lock (active_sims)
            {
                if (active_sims.Count == 0 && packet_callback != null)
                    Client.Log("DownloadSimParcels: no active sims, but I have a callback anyway?", Helpers.LogLevel.Error);

                if (active_sims.Count != 0 && packet_callback == null)
                    Client.Log("DownloadSimParcels: active sims, but no callback?", Helpers.LogLevel.Error);

                if (active_sims.Contains(simulator))
                {
                    Client.Log("DownloadSimParcels(" + simulator + ") called more than once?", Helpers.LogLevel.Error);
                    return;
                }

                active_sims.Add(simulator);
                packet_callback = new ParcelManager.ParcelPropertiesCallback(Parcels_OnParcelProperties);
                Client.Parcels.OnParcelProperties += packet_callback;
            }

            lock (ParcelMarked)
            {
                if (!ParcelMarked.ContainsKey(simulator))
                {
                    ParcelMarked[simulator] = new int[64, 64];
                    Parcels[simulator] = new Dictionary<int, Parcel>();
                }
            }

            Client.Parcels.PropertiesRequest(simulator, 0.0f, 0.0f, 0.0f, 0.0f, -10000, false);
        }

        private void Parcels_OnParcelProperties(Parcel parcel, ParcelManager.ParcelResult result, int sequenceID, 
            bool snapSelection)
        {
            if (result == ParcelManager.ParcelResult.NoData)
            {
                Client.Log("ParcelDownloader received a NoData response, sequenceID " + sequenceID, 
                    Helpers.LogLevel.Warning);
                return;
            }

            if (!ParcelMarked.ContainsKey(parcel.Simulator))
            {
                Client.Log("ParcelDownloader received unexpected parcel data for " + parcel.Simulator, 
                    Helpers.LogLevel.Info);
                return;
            }

            int x, y, index, subindex;
            byte val;
            int[,] markers = ParcelMarked[parcel.Simulator];
            Dictionary<int, Parcel> simParcels = Parcels[parcel.Simulator];

            lock (simParcels)
            {
                if (!simParcels.ContainsKey(parcel.LocalID))
                    simParcels[parcel.LocalID] = parcel;
            }

            // Mark this area as downloaded
            for (x = 0; x < 64; x++)
                for (y = 0; y < 64; y++)
                    if (markers[y, x] == 0)
                    {
                        index = ((x * 64) + y);
                        subindex = index % 8;
                        index /= 8;

                        val = parcel.Bitmap[index];

                        markers[y, x] = ((val >> subindex) & 1) == 1 ? parcel.LocalID : 0;
                    }

            // Request parcel information for the next missing area
            for (x = 0; x < 64; x++)
            {
                for (y = 0; y < 64; y++)
                {
                    if (markers[x, y] == 0)
                    {
                        Client.Parcels.PropertiesRequest(parcel.Simulator,
                                           (y * 4.0f) + 4.0f, (x * 4.0f) + 4.0f,
                                           (y * 4.0f), (x * 4.0f), -10000, false);

                        return;
                    }
                }
            }

            // If we get here, there are no more zeroes in the markers map
            lock (active_sims)
            {
                if (active_sims.Contains(parcel.Simulator))
                {
                    active_sims.Remove(parcel.Simulator);
                    if (OnParcelsDownloaded != null)
                    {
                        // This map is complete, fire callback
                        Client.Parcels.OnParcelProperties -= packet_callback;
                        try { OnParcelsDownloaded(parcel.Simulator, simParcels, markers); }
                        catch (Exception e) { Client.Log(e.ToString(), Helpers.LogLevel.Error); }
                    }
                }
                else
                    Client.Log("ParcelDownloader: Got parcel properties from a sim (" + parcel.Simulator + 
                        ") we're not downloading", Helpers.LogLevel.Info);
            }
        }
    }
}

