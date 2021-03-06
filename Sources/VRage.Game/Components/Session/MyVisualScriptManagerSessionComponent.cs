﻿using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using VRage.FileSystem;
using VRage.Game.Components;
using VRage.Game.VisualScripting;
using VRage.Game.VisualScripting.ScriptBuilder;
using VRage.Utils;

namespace VRage.Game.SessionComponents
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation, 1000, typeof(MyObjectBuilder_VisualScriptManagerSessionComponent))]
    public class MyVisualScriptManagerSessionComponent : MySessionComponentBase
    {
        private static bool                                         m_firstUpdate = true;
        private List<IMyLevelScript>                                m_levelScripts;
        private MyObjectBuilder_VisualScriptManagerSessionComponent m_objectBuilder;
        private MyVSStateMachineManager                             m_smManager;
        // there are all of the found paths
        private readonly Dictionary<string, string>                 m_relativePathsToAbsolute = new Dictionary<string, string>();
        // just the State machine paths
        private readonly List<string>                               m_stateMachineDefinitionFilePaths = new List<string>();  

        public MyVSStateMachineManager SMManager
        {
            get { return m_smManager; }
        }

        public override void Init(MyObjectBuilder_SessionComponent sessionComponent)
        {
            base.Init(sessionComponent);

            // Only servers can run this session component
            if(!Session.IsServer) return;
            MyObjectBuilder_VisualScriptManagerSessionComponent ob = (MyObjectBuilder_VisualScriptManagerSessionComponent) sessionComponent;
            m_objectBuilder = ob;
            m_relativePathsToAbsolute.Clear();
            m_stateMachineDefinitionFilePaths.Clear();

            // Runs game started event
            m_firstUpdate = ob.FirstRun;

            // load vanilla level script files
            if (ob.LevelScriptFiles != null)
                foreach (string relativeFilePath in ob.LevelScriptFiles)
                {
                    var absolutePath = Path.Combine(MyFileSystem.ContentPath, relativeFilePath);
                    // Add only existing files
                    if(MyFileSystem.FileExists(absolutePath))
                    {
                        m_relativePathsToAbsolute.Add(relativeFilePath, absolutePath);
                    }
                    else
                    {
                        MyLog.Default.WriteLine(relativeFilePath + " Level Script was not found.");
                        Debug.Fail(relativeFilePath + " Level Script was not found.");
                    }
                }

            // load vanilla mission manchines
            if(ob.StateMachines != null)
                foreach (var relativeFilePath in ob.StateMachines)
                {
                    var absolutePath = Path.Combine(MyFileSystem.ContentPath, relativeFilePath);
                    // Add only existing files
                    if (MyFileSystem.FileExists(absolutePath))
                    {
                        if (!m_relativePathsToAbsolute.ContainsKey(relativeFilePath))
                        {
                            m_stateMachineDefinitionFilePaths.Add(absolutePath);
                        }

                        m_relativePathsToAbsolute.Add(relativeFilePath, absolutePath);
                    }
                    else
                    {
                        MyLog.Default.WriteLine(relativeFilePath + " Mission File was not found.");
                        Debug.Fail(relativeFilePath + " Mission File was not found.");
                    }
                }

            // Load mission machines and level scripts from mods
            // Overrides vanilla files
            if(Session.Mods != null)
            {
                foreach (var modItem in Session.Mods)
                {
                    // First try is a mod archive
                    var directoryPath = Path.Combine(MyFileSystem.ModsPath, modItem.PublishedFileId + ".sbm");
                    if (!MyFileSystem.DirectoryExists(directoryPath))
                    {
                        directoryPath = Path.Combine(MyFileSystem.ModsPath, modItem.Name);
                        if(!MyFileSystem.DirectoryExists(directoryPath))
                            directoryPath = null;
                    }

                    if (!string.IsNullOrEmpty(directoryPath))
                    {
                        foreach (var filePath in MyFileSystem.GetFiles(directoryPath, "*", MySearchOption.AllDirectories))
                        {
                            var extension = Path.GetExtension(filePath);
                            var relativePath = MyFileSystem.MakeRelativePath(Path.Combine(directoryPath, "VisualScripts"), filePath);
                            if (extension == ".vs" || extension == ".vsc")
                            {
                                if(m_relativePathsToAbsolute.ContainsKey(relativePath))
                                    m_relativePathsToAbsolute[relativePath] = filePath;
                                else
                                    m_relativePathsToAbsolute.Add(relativePath, filePath);
                            }
                        }
                    }
                }
            }

            // Provider will compile all required scripts for the session.
            MyVSAssemblyProvider.Init(m_relativePathsToAbsolute.Values);
            // Retrive the Level script instances
            m_levelScripts = MyVSAssemblyProvider.GetLevelScriptInstances();

            // mission manager initialization - state machine definitions
            m_smManager = new MyVSStateMachineManager();
            foreach (var stateMachineFilePath in m_stateMachineDefinitionFilePaths)
            {
                m_smManager.AddMachine(stateMachineFilePath);
            }
        }

        public override void BeforeStart()
        {
            // Restore the statemachines that were running before the save happened.
            if (m_objectBuilder != null && m_objectBuilder.ScriptStateMachineManager != null)
                foreach (var cursorData in m_objectBuilder.ScriptStateMachineManager.ActiveStateMachines)
                    if (!m_smManager.Restore(cursorData.StateMachineName, cursorData.Cursors))
                        Debug.Fail("Failed to load " + cursorData.StateMachineName + " mission state machine cursors.");
        }

        public override void UpdateBeforeSimulation()
        {
            // Only servers can run this session component
            if (!Session.IsServer) return;

            if(m_smManager != null)
                m_smManager.Update();

            if (m_levelScripts == null) return;
            foreach (var levelScript in m_levelScripts)
            {
                if(m_firstUpdate)
                    levelScript.GameStarted();
                else
                    levelScript.Update();
            }

            m_firstUpdate = false;
        }

        protected override void UnloadData()
        {
            base.UnloadData();
            DisposeRunningScripts();
        }

        private void DisposeRunningScripts()
        {
            if(m_levelScripts == null) return;

            foreach (var levelScript in m_levelScripts)
            {
                levelScript.GameFinished();
                levelScript.Dispose();
            }

            m_smManager.Dispose();
            m_smManager = null;

            m_levelScripts.Clear();
        }

        public override MyObjectBuilder_SessionComponent GetObjectBuilder()
        {
            if (!Session.IsServer) return null;

            m_objectBuilder.ScriptStateMachineManager = m_smManager.GetObjectBuilder();
            m_objectBuilder.FirstRun = false;

            return m_objectBuilder;
        }

        public void Reset()
        {
            if (m_smManager != null)
                m_smManager.Dispose();
            m_firstUpdate = true;
        }
    }
}
