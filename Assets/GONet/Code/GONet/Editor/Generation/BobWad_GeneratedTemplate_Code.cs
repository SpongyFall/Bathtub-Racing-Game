/* GONet (TM, serial number 88592370), Copyright (c) 2019-2023 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@galoreinteractive.com
 * 
 *
 * Authorized use is explicitly limited to the following:	
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on original source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 * -The ability to modify source code for local use only
 * -The ability to distribute products based on modified sources for non-commercial purposes, whereas this license must be included if source code provided in said products
 * -The ability to commercialize products built on modified source code, whereas this license must be included if source code provided in said products and whereas the products are interactive multi-player video games and cannot be viewed as a product competitive to GONet
 */

using GONet;
using GONet.Generation;
using GONet.Utils;
using MemoryPack;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Assets.GONet.Code.GONet.Editor.Generation
{
    public partial class BobWad_GeneratedTemplate
    {
        private byte[] usedCodegenIds;
        private List<GONetParticipant_ComponentsWithAutoSyncMembers> allUniqueSnapsForPersistence;

        /// <summary>
        /// memberOwnerTypeName => [memberName => memberTypeName]
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, ValueTuple<string, AnimatorControllerParameterGenerationInfo>>> uniqueCombos = new Dictionary<string, Dictionary<string, ValueTuple<string, AnimatorControllerParameterGenerationInfo>>>();

        private readonly Dictionary<byte, GONetParticipant_ComponentsWithAutoSyncMembers_Single[]> allUniqueSnapsByCodeGenerationId = new Dictionary<byte, GONetParticipant_ComponentsWithAutoSyncMembers_Single[]>();

        private readonly List<Type> allUniqueITransientEventStructTypes = new List<Type>();
        private readonly List<Type> allUniqueIPersistentEventStructTypes = new List<Type>();

        private readonly Dictionary<int, string> syncEventGeneratedTypesMap;

        internal BobWad_GeneratedTemplate(byte[] usedCodegenIds, List<GONetParticipant_ComponentsWithAutoSyncMembers> allUniqueSnapsForPersistence, List<Type> allUniqueIGONetEventStructTypes)
        {
            this.usedCodegenIds = usedCodegenIds;
            this.allUniqueSnapsForPersistence = allUniqueSnapsForPersistence;

            List<string> stringList = new List<string>();

            { // populate uniqueCombos
                uniqueCombos.Clear();
                allUniqueSnapsByCodeGenerationId.Clear();
                allUniqueITransientEventStructTypes.Clear();
                allUniqueIPersistentEventStructTypes.Clear();
                foreach (var l in allUniqueSnapsForPersistence)
                {
                    if (!allUniqueSnapsByCodeGenerationId.ContainsKey(l.codeGenerationId))
                    {
                        allUniqueSnapsByCodeGenerationId[l.codeGenerationId] = l.ComponentMemberNames_By_ComponentTypeFullName;
                    }

                    foreach (var ll in l.ComponentMemberNames_By_ComponentTypeFullName)
                    {
                        Dictionary<string, ValueTuple<string, AnimatorControllerParameterGenerationInfo>> componentTypeNamesMap;
                        string componentTypeName = ll.componentTypeFullName;

                        //If it is an animator, we add this string in order to make the difference
                        if (ll.componentTypeFullName == typeof(Animator).FullName)
                        {
                            componentTypeName += ll.autoSyncMembers[0].animatorControllerName; // NOTE: just "randomly" grabbling the first one and ASSuming they are all the same, which is a good assumption at the time of writing!
                        }
                        if (!uniqueCombos.TryGetValue(componentTypeName, out componentTypeNamesMap))
                        {
                            uniqueCombos[componentTypeName] = componentTypeNamesMap = new Dictionary<string, ValueTuple<string, AnimatorControllerParameterGenerationInfo>>();
                        }

                        foreach (var lll in ll.autoSyncMembers)
                        {
                            string memberTypeFullName = lll.animatorControllerParameterId == 0 ? lll.memberTypeFullName : lll.animatorControllerParameterTypeFullName;

                            const string UNDIE = "_";
                            const string PER = ".";

                            ValueTuple<string, AnimatorControllerParameterGenerationInfo> memberTuple =
                                ValueTuple.Create(
                                    memberTypeFullName,
                                    lll.animatorControllerParameterId == 0 ? null : new AnimatorControllerParameterGenerationInfo(lll.animatorControllerName, lll.animatorControllerParameterName));

                            string memberName = lll.memberName;
                            if (memberTuple.Item2 != null)
                            {
                                memberName += string.Concat(UNDIE, memberTuple.Item2.ControllerParameterName);
                            }
                            componentTypeNamesMap[memberName] = memberTuple;

                            if (ll.componentTypeFullName.Contains(PER))
                            {
                                stringList.Add(ll.componentTypeFullName + UNDIE + memberName);
                            }
                            else
                            {
                                stringList.Add(ll.componentTypeName + UNDIE + memberName);
                            }
                        }
                    }
                }

                // IMPORTANT: Sort stringList for deterministic ordering!
                // Dictionary iteration order is not guaranteed, so without sorting,
                // the order of entries can differ between build and editor sessions.
                // This would cause different enum values in SyncEvent_GeneratedTypes.cs,
                // breaking synchronization between server (build) and client (editor).
                //
                // Priority-based sort ensures GONet core intrinsics (GONetParticipant, Transform)
                // always come first (indices 0-6), followed by everything else alphabetically.
                stringList.Sort((a, b) =>
                {
                    int priorityA = GetSortPriority(a);
                    int priorityB = GetSortPriority(b);

                    if (priorityA != priorityB)
                        return priorityA.CompareTo(priorityB);

                    // Same priority: sort alphabetically for determinism
                    return string.Compare(a, b, StringComparison.Ordinal);
                });

                {//populate IGONetEvent structs
                    allUniqueIGONetEventStructTypes.ForEach(iGONetEventStructType =>
                    {
                        if (TypeUtils.IsTypeAInstanceOfTypeB(iGONetEventStructType, typeof(ITransientEvent)))
                        {
                            allUniqueITransientEventStructTypes.Add(iGONetEventStructType);
                        }
                        else if (TypeUtils.IsTypeAInstanceOfTypeB(iGONetEventStructType, typeof(IPersistentEvent)))
                        {
                            allUniqueIPersistentEventStructTypes.Add(iGONetEventStructType);
                        }
                        else
                        {
                            GONetLog.Error($"GONet Error: The struct called {iGONetEventStructType.FullName} is not implementing {typeof(ITransientEvent).FullName} nor {typeof(IPersistentEvent).FullName}. Skipping this struct from the code generation...");
                        }
                    });
                }

                {
                    const string FILE_PATH = "Assets/GONet/Code/GONet/Generation/syncEventGeneratedTypes" + GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.BINARY_FILE_SUFFIX;
                    //Load syncEvents generated types from bin file
                    SyncEvent_GeneratedTypesFromBin syncEvent_GeneratedTypesFromBin = LoadAllGeneratedTypesFromPersistenceFile(FILE_PATH);
                    syncEventGeneratedTypesMap = syncEvent_GeneratedTypesFromBin.syncEventGeneratedTypesMap;

                    //Remove those types that no longer exist
                    HashSet<int> indexesToRemove = new HashSet<int>();
                    foreach (var keyValuePair in syncEventGeneratedTypesMap)
                    {
                        if (!stringList.Contains(keyValuePair.Value))
                        {
                            indexesToRemove.Add(keyValuePair.Key);
                        }
                    }

                    foreach (int indexToRemove in indexesToRemove)
                    {
                        syncEventGeneratedTypesMap.Remove(indexToRemove);
                    }

                    //Check for empty gaps
                    bool areEmptyGaps = false;
                    Queue<int> gapIndexes = new Queue<int>();

                    int maxIndex = syncEventGeneratedTypesMap.Keys.Count == 0 ? -1 : syncEventGeneratedTypesMap.Keys.Max();
                    for (int i = 0; i < maxIndex; ++i)
                    {
                        if (!syncEventGeneratedTypesMap.ContainsKey(i))
                        {
                            areEmptyGaps = true;
                            gapIndexes.Enqueue(i);
                        }
                    }

                    //Add new types
                    for (int i = 0; i < stringList.Count; ++i)
                    {
                        if (!syncEventGeneratedTypesMap.ContainsValue(stringList[i]))
                        {
                            if (areEmptyGaps)
                            {
                                syncEventGeneratedTypesMap[gapIndexes.Dequeue()] = stringList[i];
                                areEmptyGaps = gapIndexes.Count > 0;
                            }
                            else
                            {
                                ++maxIndex;
                                syncEventGeneratedTypesMap.Add(maxIndex, stringList[i]);
                            }
                        }
                    }

                    //Save into persistance
                    SaveGeneratedTypesToPersistenceFile(syncEvent_GeneratedTypesFromBin, FILE_PATH);
                }
            }
        }

        private static SyncEvent_GeneratedTypesFromBin LoadAllGeneratedTypesFromPersistenceFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                byte[] snapsFileBytes = File.ReadAllBytes(filePath);
                var l = SerializationUtils.DeserializeFromBytes<SyncEvent_GeneratedTypesFromBin>(snapsFileBytes);

                return l;
            }
            else
            {
                return new SyncEvent_GeneratedTypesFromBin();
            }
        }

        private static void SaveGeneratedTypesToPersistenceFile(SyncEvent_GeneratedTypesFromBin allSnaps, string filePath)
        {
            if (!Directory.Exists("Assets/GONet/Code/GONet/Generation/"))
            {
                Directory.CreateDirectory("Assets/GONet/Code/GONet/Generation/");
            }

            int returnBytesUsedCount;
            byte[] snapsFileBytes = SerializationUtils.SerializeToBytes(allSnaps, out returnBytesUsedCount, out bool doesNeedToReturn);
            FileUtils.WriteBytesToFile(filePath, snapsFileBytes, returnBytesUsedCount, FileMode.Truncate);
            if (doesNeedToReturn)
            {
                SerializationUtils.ReturnByteArray(snapsFileBytes);
            }
        }

        /// <summary>
        /// Returns sort priority for sync event types to ensure GONet core intrinsics
        /// (GONetParticipant members at 0-4, Transform members at 5-6) always come first.
        /// Lower priority value = sorted earlier.
        /// </summary>
        private static int GetSortPriority(string syncEventTypeName)
        {
            // Priority 0: GONetParticipant members (indices 0-4)
            if (syncEventTypeName.Contains("GONetParticipant"))
                return 0;

            // Priority 1: Transform members (indices 5-6)
            if (syncEventTypeName.Contains("Transform_rotation") || syncEventTypeName.Contains("Transform_position"))
                return 1;

            // Priority 2: Other Transform members
            if (syncEventTypeName.Contains("UnityEngine.Transform"))
                return 2;

            // Priority 3: Everything else (user-defined sync properties)
            return 3;
        }
    }

    [MemoryPackable]
    public partial class SyncEvent_GeneratedTypesFromBin
    {
        public Dictionary<int, string> syncEventGeneratedTypesMap;

        public SyncEvent_GeneratedTypesFromBin()
        {
            syncEventGeneratedTypesMap = new Dictionary<int, string>();
        }

        [MemoryPack.MemoryPackConstructor]
        public SyncEvent_GeneratedTypesFromBin(Dictionary<int, string> syncEventGeneratedTypesMap)
        {
            this.syncEventGeneratedTypesMap = syncEventGeneratedTypesMap;
        }
    }

    public class AnimatorControllerParameterGenerationInfo
    {
        public string ControllerName;
        public string ControllerParameterName;

        public AnimatorControllerParameterGenerationInfo(string controllerName, string controllerParameterName)
        {
            ControllerName = controllerName;
            ControllerParameterName = controllerParameterName;
        }
    }

    public class SyncEvent_GeneratedTypes_GeneratedTemplate : BobWad_GeneratedTemplateBase
    {
        /// <summary>
        /// memberOwnerTypeName => [memberName => memberTypeName]
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, ValueTuple<string, AnimatorControllerParameterGenerationInfo>>> uniqueCombos = new Dictionary<string, Dictionary<string, ValueTuple<string, AnimatorControllerParameterGenerationInfo>>>();

        private readonly Dictionary<int, string> syncEventGeneratedTypesMap;

        internal SyncEvent_GeneratedTypes_GeneratedTemplate(List<GONetParticipant_ComponentsWithAutoSyncMembers> allUniqueSnapsForPersistence)
        {
            List<string> stringList = new List<string>();
            uniqueCombos.Clear();

            { // populate uniqueCombos
                foreach (var l in allUniqueSnapsForPersistence)
                {
                    foreach (var ll in l.ComponentMemberNames_By_ComponentTypeFullName)
                    {
                        Dictionary<string, ValueTuple<string, AnimatorControllerParameterGenerationInfo>> componentTypeNamesMap;
                        string componentTypeName = ll.componentTypeFullName;
                        bool isAnimationType = false;

                        //If it is an animator, we add this string in order to make the difference
                        if (ll.componentTypeFullName == typeof(Animator).FullName)
                        {
                            isAnimationType = true;
                            componentTypeName += ll.autoSyncMembers[0].animatorControllerName; // NOTE: just "randomly" grabbling the first one and ASSuming they are all the same, which is a good assumption at the time of writing!
                        }
                        if (!uniqueCombos.TryGetValue(componentTypeName, out componentTypeNamesMap))
                        {
                            uniqueCombos[componentTypeName] = componentTypeNamesMap = new Dictionary<string, ValueTuple<string, AnimatorControllerParameterGenerationInfo>>();
                        }

                        foreach (var lll in ll.autoSyncMembers)
                        {
                            string memberTypeFullName = lll.animatorControllerParameterId == 0 ? lll.memberTypeFullName : lll.animatorControllerParameterTypeFullName;

                            const string UNDIE = "_";
                            const string PER = ".";

                            ValueTuple<string, AnimatorControllerParameterGenerationInfo> memberTuple =
                                ValueTuple.Create(
                                    memberTypeFullName,
                                    lll.animatorControllerParameterId == 0 ? null : new AnimatorControllerParameterGenerationInfo(lll.animatorControllerName, lll.animatorControllerParameterName));

                            string memberName = lll.memberName;
                            if (memberTuple.Item2 != null)
                            {
                                memberName += string.Concat(UNDIE, memberTuple.Item2.ControllerParameterName);
                            }
                            componentTypeNamesMap[memberName] = memberTuple;

                            if (ll.componentTypeFullName.Contains(PER))
                            {
                                stringList.Add(componentTypeName + UNDIE + memberName);
                            }
                            else
                            {
                                stringList.Add(ll.componentTypeName + UNDIE + memberName);
                            }
                        }
                    }
                }

                // IMPORTANT: Sort stringList for deterministic ordering!
                // Dictionary iteration order is not guaranteed, so without sorting,
                // the order of entries can differ between build and editor sessions.
                // This would cause different enum values in SyncEvent_GeneratedTypes.cs,
                // breaking synchronization between server (build) and client (editor).
                //
                // Priority-based sort ensures GONet core intrinsics (GONetParticipant, Transform)
                // always come first (indices 0-6), followed by everything else alphabetically.
                stringList.Sort((a, b) =>
                {
                    int priorityA = GetSortPriority(a);
                    int priorityB = GetSortPriority(b);

                    if (priorityA != priorityB)
                        return priorityA.CompareTo(priorityB);

                    // Same priority: sort alphabetically for determinism
                    return string.Compare(a, b, StringComparison.Ordinal);
                });

                // NOTE: The following foreach block was removed during performance optimization (log cleanup).
                // It contained only debug logging that was slowing down Fast Iteration Mode by 138+ log calls.

                {
                    const string FILE_PATH = "Assets/GONet/Code/GONet/Generation/syncEventGeneratedTypes" + GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.BINARY_FILE_SUFFIX;

                    //Load syncEvents generated types from bin file
                    SyncEvent_GeneratedTypesFromBin syncEvent_GeneratedTypesFromBin = LoadAllGeneratedTypesFromPersistenceFile(FILE_PATH);
                    syncEventGeneratedTypesMap = syncEvent_GeneratedTypesFromBin.syncEventGeneratedTypesMap;

                    //Remove those types that no longer exist
                    HashSet<int> indexesToRemove = new HashSet<int>();
                    foreach (var keyValuePair in syncEventGeneratedTypesMap)
                    {
                        if (!stringList.Contains(keyValuePair.Value))
                        {
                            indexesToRemove.Add(keyValuePair.Key);
                        }
                    }

                    foreach (int indexToRemove in indexesToRemove)
                    {
                        syncEventGeneratedTypesMap.Remove(indexToRemove);
                    }

                    //Check for empty gaps
                    bool areEmptyGaps = false;
                    Queue<int> gapIndexes = new Queue<int>();

                    int maxIndex = syncEventGeneratedTypesMap.Keys.Count == 0 ? -1 : syncEventGeneratedTypesMap.Keys.Max();
                    for (int i = 0; i < maxIndex; ++i)
                    {
                        if (!syncEventGeneratedTypesMap.ContainsKey(i))
                        {
                            areEmptyGaps = true;
                            gapIndexes.Enqueue(i);
                        }
                    }

                    //Add new types
                    for (int i = 0; i < stringList.Count; ++i)
                    {
                        if (!syncEventGeneratedTypesMap.ContainsValue(stringList[i]))
                        {
                            if (areEmptyGaps)
                            {
                                syncEventGeneratedTypesMap[gapIndexes.Dequeue()] = stringList[i];
                                areEmptyGaps = gapIndexes.Count > 0;
                            }
                            else
                            {
                                ++maxIndex;
                                syncEventGeneratedTypesMap.Add(maxIndex, stringList[i]);
                            }
                        }
                    }

                    //Save into persistance
                    SaveGeneratedTypesToPersistenceFile(syncEvent_GeneratedTypesFromBin, FILE_PATH);
                }
            }
        }

        private static SyncEvent_GeneratedTypesFromBin LoadAllGeneratedTypesFromPersistenceFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                byte[] snapsFileBytes = File.ReadAllBytes(filePath);
                if (snapsFileBytes != null && snapsFileBytes.Length > 0)
                {
                    var l = SerializationUtils.DeserializeFromBytes<SyncEvent_GeneratedTypesFromBin>(snapsFileBytes);
                    return l;
                }
            }

            return new SyncEvent_GeneratedTypesFromBin();
        }

        private static void SaveGeneratedTypesToPersistenceFile(SyncEvent_GeneratedTypesFromBin allSnaps, string filePath)
        {
            if (!Directory.Exists("Assets/GONet/Code/GONet/Generation/"))
            {
                Directory.CreateDirectory("Assets/GONet/Code/GONet/Generation/");
            }

            int returnBytesUsedCount;
            byte[] snapsFileBytes = SerializationUtils.SerializeToBytes(allSnaps, out returnBytesUsedCount, out bool doesNeedToReturn);
            FileUtils.WriteBytesToFile(filePath, snapsFileBytes, returnBytesUsedCount, FileMode.Truncate);
            if (doesNeedToReturn)
            {
                SerializationUtils.ReturnByteArray(snapsFileBytes);
            }
        }

        /// <summary>
        /// Returns sort priority for sync event types to ensure GONet core intrinsics
        /// (GONetParticipant members at 0-4, Transform members at 5-6) always come first.
        /// Lower priority value = sorted earlier.
        /// </summary>
        private static int GetSortPriority(string syncEventTypeName)
        {
            // Priority 0: GONetParticipant members (indices 0-4)
            if (syncEventTypeName.Contains("GONetParticipant"))
                return 0;

            // Priority 1: Transform members (indices 5-6)
            if (syncEventTypeName.Contains("Transform_rotation") || syncEventTypeName.Contains("Transform_position"))
                return 1;

            // Priority 2: Other Transform members
            if (syncEventTypeName.Contains("UnityEngine.Transform"))
                return 2;

            // Priority 3: Everything else (user-defined sync properties)
            return 3;
        }

        public virtual string TransformText()
        {
#line default
#line hidden
            this.Write("namespace GONet\n{\n");

            HashSet<string> syncEventNames = new HashSet<string>();
#line default
#line hidden
            this.Write("\tpublic enum SyncEvent_GeneratedTypes\n\t{\n");//TODO
            // IMPORTANT: Iterate in sorted order by key (index) for deterministic output
            foreach (var keyValuePair in syncEventGeneratedTypesMap.OrderBy(kvp => kvp.Key))
            {
                const string SYNC_EV = "SyncEvent_";
                const string PER = ".";
                const string UNDIE = "_";
                string tmp = keyValuePair.Value.Contains(PER) ? keyValuePair.Value.Substring(keyValuePair.Value.LastIndexOf(PER) + 1) : keyValuePair.Value;
                if (syncEventNames.Contains(tmp))
                {
                    tmp = keyValuePair.Value.Replace(PER, UNDIE);
                }
                else
                {
                    syncEventNames.Add(tmp);
                }

                tmp = string.Concat(SYNC_EV, tmp);

#line default
#line hidden
                this.Write($"\t\t{tmp} = {keyValuePair.Key},\n");
            }

#line default
#line hidden
            this.Write("\n\t}\n}");
            return this.GenerationEnvironment.ToString();
        }
    }
}