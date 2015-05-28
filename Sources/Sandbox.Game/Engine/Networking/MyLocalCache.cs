﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game.World;

using VRage.Utils;
using VRage.Serialization;
using VRage.Trace;
using VRageMath;
using Sandbox.Engine.Utils;
using System.IO.Compression;
using Sandbox.Common;
using VRage.Library.Utils;
using MyFileSystem = VRage.FileSystem.MyFileSystem;

namespace Sandbox.Engine.Networking
{
    public class MyLocalCache
    {
        private const string CHECKPOINT_FILE = "Sandbox.sbc";
        private const string LAST_LOADED_TIMES_FILE = "LastLoaded.sbl";
        private const string LAST_SESSION_FILE = "LastSession.sbl";

        public static string LastLoadedTimesPath { get { return Path.Combine(MyFileSystem.SavesPath, LAST_LOADED_TIMES_FILE); } }
        public static string LastSessionPath { get { return Path.Combine(MyFileSystem.SavesPath, LAST_SESSION_FILE); } }
        public static string ContentSessionsPath { get { return "Worlds"; } }
        public static string MissionSessionsPath { get { return "Missions"; } }
        public static string BattlesSessionsPath { get { return "Battles"; } }

        private static string GetSectorPath(string sessionPath, Vector3I sectorPosition)
        {
            return Path.Combine(sessionPath, GetSectorName(sectorPosition) + ".sbs");
        }

        private static string GetSectorName(Vector3I sectorPosition)
        {
            return String.Format("{0}_{1}_{2}_{3}_", "SANDBOX", sectorPosition.X, sectorPosition.Y, sectorPosition.Z);
        }

        public static string GetSessionSavesPath(string sessionUniqueName, bool contentFolder, bool createIfNotExists = true)
        {
            string path;

            if (contentFolder)
            {
                path = Path.Combine(MyFileSystem.ContentPath, ContentSessionsPath, sessionUniqueName);
            }
            else
            {
                path = Path.Combine(MyFileSystem.SavesPath, sessionUniqueName);
            }

            if (createIfNotExists)
                Directory.CreateDirectory(path);

            return path;
        }

        private static MyWorldInfo LoadWorldInfo(string sessionPath)
        {
            MyWorldInfo worldInfo = null;

            try
            {
                System.Xml.Linq.XDocument doc = null;
                string checkpointFile = Path.Combine(sessionPath, CHECKPOINT_FILE);
                if (!File.Exists(checkpointFile))
                    return null;

                using (var stream = MyFileSystem.OpenRead(checkpointFile).UnwrapGZip())
                {
                    doc = XDocument.Load(stream);
                }
                Debug.Assert(doc != null);
                var root = doc.Root;
                Debug.Assert(root != null);
                
                var session      = root.Element("SessionName");
                var description  = root.Element("Description");
                var lastSaveTime = root.Element("LastSaveTime");
                var lastLoadTime = root.Element("LastLoadTime");
                var worldId      = root.Element("WorldID");
                var workshopId   = root.Element("WorkshopId");
                var briefing = root.Element("Briefing");

                worldInfo = new MyWorldInfo();

                if (session      != null) worldInfo.SessionName = session.Value;
                if (description  != null) worldInfo.Description = description.Value;
                if (lastSaveTime != null) DateTime.TryParse(lastSaveTime.Value, out worldInfo.LastSaveTime);
                if (lastLoadTime != null) DateTime.TryParse(lastLoadTime.Value, out worldInfo.LastLoadTime);

                if (workshopId != null)
                {
                    ulong tmp;
                    if (ulong.TryParse(workshopId.Value, out tmp))
                        worldInfo.WorkshopId = tmp;
                }
                if (briefing != null)
                    worldInfo.Briefing = briefing.Value;
            }
            catch (Exception ex)
            {
                MySandboxGame.Log.WriteLine(ex);
            }
            return worldInfo;
        }

        public static MyObjectBuilder_Checkpoint LoadCheckpoint(string sessionPath, out ulong sizeInBytes)
        {
            sizeInBytes = 0;
            var checkpointFile = Path.Combine(sessionPath, CHECKPOINT_FILE);

            if (!File.Exists(checkpointFile))
                return null;

            MyObjectBuilder_Checkpoint result = null;
            Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.DeserializeXML(checkpointFile, out result, out sizeInBytes);
            if (result != null && string.IsNullOrEmpty(result.SessionName))
            {
                result.SessionName = Path.GetFileNameWithoutExtension(checkpointFile);
            }

            return result;
        }

        public static MyObjectBuilder_Sector LoadSector(string sessionPath, Vector3I sectorPosition, out ulong sizeInBytes)
        {
            return LoadSector(GetSectorPath(sessionPath, sectorPosition), out sizeInBytes);
        }

        private static MyObjectBuilder_Sector LoadSector(string path, out ulong sizeInBytes)
        {
            MyObjectBuilder_Sector newSector = Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_Sector>();

            MyObjectBuilder_Sector result;
            Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.DeserializeXML<MyObjectBuilder_Sector>(path, out result, out sizeInBytes);

            if (result == null)
            {
                MySandboxGame.Log.WriteLine("Incorrect save data");
                return null;
            }
            return result;
        }

        public static bool SaveSector(MyObjectBuilder_Sector sector, string sessionPath, Vector3I sectorPosition, out ulong sizeInBytes)
        {
            var relativePath = GetSectorPath(sessionPath, sectorPosition);
            return Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.SerializeXML(relativePath, MySandboxGame.Config.CompressSaveGames, sector, out sizeInBytes);
        }

        public static bool SaveCheckpoint(MyObjectBuilder_Checkpoint checkpoint, string sessionPath)
        {
            ulong sizeInBytes;
            return SaveCheckpoint(checkpoint, sessionPath, out sizeInBytes);
        }

        public static bool SaveCheckpoint(MyObjectBuilder_Checkpoint checkpoint, string sessionPath, out ulong sizeInBytes)
        {
            var checkpointFile = Path.Combine(sessionPath, CHECKPOINT_FILE);
            return Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.SerializeXML(checkpointFile, MySandboxGame.Config.CompressSaveGames, checkpoint, out sizeInBytes);
        }

        public static List<Tuple<string, MyWorldInfo>> GetAvailableWorldInfos()
        {
            MySandboxGame.Log.WriteLine("Loading available saves - START");
            var result = new List<Tuple<string, MyWorldInfo>>();
            using (MySandboxGame.Log.IndentUsing(LoggingOptions.ALL))
            {
                if (MyFakes.ENABLE_LOADING_CONTENT_WORLDS)
                {
                    // Search in Content/Sessions as well as App Data folder
                    GetWorldInfoFromDirectory(Path.Combine(MyFileSystem.ContentPath, ContentSessionsPath), result);
                }

                GetWorldInfoFromDirectory(MyFileSystem.SavesPath, result);

                LoadLastLoadedTimes(result);
            }
            MySandboxGame.Log.WriteLine("Loading available saves - END");
            return result;
        }

        public static List<Tuple<string, MyWorldInfo>> GetAvailableMissionInfos()
        {
            MySandboxGame.Log.WriteLine("Loading available mission - START");
            var result = new List<Tuple<string, MyWorldInfo>>();
            using (MySandboxGame.Log.IndentUsing(LoggingOptions.ALL))
            {
                GetWorldInfoFromDirectory(Path.Combine(MyFileSystem.ContentPath, MissionSessionsPath), result);
            }
            MySandboxGame.Log.WriteLine("Loading available missions - END");
            return result;
        }

        public static List<Tuple<string, MyWorldInfo>> GetAvailableBattlesInfos()
        {
            MySandboxGame.Log.WriteLine("Loading available battles - START");
            var result = new List<Tuple<string, MyWorldInfo>>();
            using (MySandboxGame.Log.IndentUsing(LoggingOptions.ALL))
            {
                GetWorldInfoFromDirectory(Path.Combine(MyFileSystem.ContentPath, BattlesSessionsPath), result);
            }
            MySandboxGame.Log.WriteLine("Loading available battles - END");
            return result;
        }

        private static void GetWorldInfoFromDirectory(string path, List<Tuple<string, MyWorldInfo>> result)
        {
            bool dirExists = Directory.Exists(path);
            MySandboxGame.Log.WriteLine(string.Format("GetWorldInfoFromDirectory (Exists: {0}) '{1}'", dirExists, path));

            if (!dirExists)
                return;

            foreach (var saveDir in Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly))
            {
                MyWorldInfo worldInfo = MyLocalCache.LoadWorldInfo(saveDir);
                if (worldInfo != null)
                {
                    if (string.IsNullOrEmpty(worldInfo.SessionName))
                        worldInfo.SessionName = Path.GetFileName(saveDir);
                }
                result.Add(Tuple.Create(saveDir, worldInfo));
            }
        }

        public static string GetLastSessionPath()
        {
            if (MyFinalBuildConstants.IS_OFFICIAL)
                return null;

            if (!File.Exists(LastSessionPath))
                return null;

            MyObjectBuilder_LastSession lastSession = null;
            Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.DeserializeXML(LastSessionPath, out lastSession);
            if (lastSession == null)
                return null;

            if (!String.IsNullOrEmpty(lastSession.Path))
            {
                string path = Path.Combine(lastSession.IsContentWorlds ? MyFileSystem.ContentPath : MyFileSystem.SavesPath, lastSession.Path);
                if (Directory.Exists(path))
                    return path;
            }

            return null;
        }

        public static bool SaveLastSessionInfo(string sessionPath)
        {
            if (MyFinalBuildConstants.IS_OFFICIAL)
                return true;

            MyObjectBuilder_LastSession lastSession = Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_LastSession>();
            if (sessionPath != null)
            {
                lastSession.Path = sessionPath;
                lastSession.IsContentWorlds = sessionPath.StartsWith(MyFileSystem.ContentPath, StringComparison.InvariantCultureIgnoreCase);
            }

            ulong sizeInBytes;
            return Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.SerializeXML(LastSessionPath, false, lastSession, out sizeInBytes);
        }

        public static void ClearLastSessionInfo()
        {
            string lastSessionPath = Path.Combine(MyFileSystem.SavesPath, "LastSession.sbl");

            if (File.Exists(lastSessionPath))
                File.Delete(lastSessionPath);
        }

        #region Last loaded times

        public static bool SaveLastLoadedTime(string sessionPath, DateTime lastLoadedTime)
        {
            MyObjectBuilder_LastLoadedTimes builder = null;
            Dictionary<string, DateTime> times;

            if (File.Exists(LastLoadedTimesPath) && Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.DeserializeXML(LastLoadedTimesPath, out builder))
                times = builder.LastLoaded.Dictionary;
            else
                times = new Dictionary<string, DateTime>(1);

            times[sessionPath] = lastLoadedTime;

            if (builder == null)
                builder = Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_LastLoadedTimes>();

            if (builder.LastLoaded == null)
                builder.LastLoaded = new SerializableDictionary<string, DateTime>(times);
            else
                builder.LastLoaded.Dictionary = times;

            return Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.SerializeXML(LastLoadedTimesPath, false, builder);
        }

        private static bool SaveLastLoadedTimes(Dictionary<string, DateTime> lastLoadedTimes)
        {
            var builder = Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.CreateNewObject<MyObjectBuilder_LastLoadedTimes>();
            builder.LastLoaded = new SerializableDictionary<string, DateTime>(lastLoadedTimes);
            ulong sizeInBytes;
            return Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.SerializeXML(LastLoadedTimesPath, false, builder, out sizeInBytes);
        }

        /// <summary>
        /// Backward compatibility.
        /// </summary>
        private static DateTime LoadLastLoadedTimeFromCheckpoint(string sessionPath)
        {
            DateTime result = new DateTime();
            try
            {
                string pathXml = Path.Combine(sessionPath, CHECKPOINT_FILE);
                var doc = XDocument.Load(pathXml);
                Debug.Assert(doc != null);
                var root = doc.Root;
                Debug.Assert(root != null);

                var lastLoadTime = root.Element("LastLoadTime");
                if (lastLoadTime != null)
                    DateTime.TryParse(lastLoadTime.Value, out result);
            }
            catch (Exception ex)
            {
                MySandboxGame.Log.WriteLine(ex);
            }
            return result;
        }

        private static void LoadLastLoadedTimes(List<Tuple<string, MyWorldInfo>> outputWorldInfos)
        {
            if (File.Exists(LastLoadedTimesPath))
            {
                MyObjectBuilder_LastLoadedTimes builder;

                if (Sandbox.Common.ObjectBuilders.Serializer.MyObjectBuilderSerializer.DeserializeXML(LastLoadedTimesPath, out builder))
                {
                    foreach (var pair in outputWorldInfos)
                    {
                        if (pair.Item2 != null)
                            builder.LastLoaded.Dictionary.TryGetValue(pair.Item1, out pair.Item2.LastLoadTime);
                    }
                }
            }
            else
            {
                // Backward compatibility. Loading from checkpoint files and saving in new format.
                foreach (var pair in outputWorldInfos)
                {
                    if (pair.Item2 != null)
                        pair.Item2.LastLoadTime = LoadLastLoadedTimeFromCheckpoint(pair.Item1);
                }

                Dictionary<string, DateTime> times = new Dictionary<string, DateTime>();
                foreach (var pair in outputWorldInfos)
                {
                    if (pair.Item2 != null)
                        times[pair.Item1] = pair.Item2.LastLoadTime;
                }
                SaveLastLoadedTimes(times);
            }
        }

        #endregion
    }
}
