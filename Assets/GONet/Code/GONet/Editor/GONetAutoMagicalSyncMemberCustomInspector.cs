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

using GONet.Generation;
using GONet.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GONet.Editor
{
    [CustomEditor(typeof(GONetParticipant))]
    public class GONetAutoMagicalSyncMemberCustomInspector : UnityEditor.Editor
    {
        GONetParticipant targetGONetParticipant;

        const string SCR = " (Script)";

        private const string PREFAB_FILE_EXTENSION = ".prefab";

        // SessionState key prefixes for persisting foldout states across inspector refreshes
        private const string SESSION_KEY_PREFIX = "GONet_Inspector_Foldout_";
        private const string SESSION_KEY_SYNC_ATTR = SESSION_KEY_PREFIX + "SyncAttrSection";
        private const string SESSION_KEY_TRANSFORM = SESSION_KEY_PREFIX + "TransformIntrinsics";

        private void OnEnable()
        {
            targetGONetParticipant = (GONetParticipant)target;
        }

        public override void OnInspectorGUI()
        {
            DrawGONetParticipantSpecifics(targetGONetParticipant);
        }

        /// <summary>
        /// Gets a unique SessionState key for a foldout based on the target object and foldout identifier.
        /// Using instance ID ensures different GONetParticipants can have independent foldout states.
        /// </summary>
        private string GetFoldoutSessionKey(string foldoutId)
        {
            return $"{SESSION_KEY_PREFIX}{targetGONetParticipant.GetInstanceID()}_{foldoutId}";
        }

        /// <summary>
        /// Gets the foldout state from SessionState, defaulting to the specified value if not set.
        /// </summary>
        private bool GetFoldoutState(string foldoutId, bool defaultValue = true)
        {
            return SessionState.GetBool(GetFoldoutSessionKey(foldoutId), defaultValue);
        }

        /// <summary>
        /// Sets the foldout state in SessionState for persistence across inspector refreshes.
        /// </summary>
        private void SetFoldoutState(string foldoutId, bool value)
        {
            SessionState.SetBool(GetFoldoutSessionKey(foldoutId), value);
        }

        private void DrawGONetParticipantSpecifics(GONetParticipant targetGONetParticipant)
        {
            bool guiEnabledPrevious = GUI.enabled;
            GUI.enabled = false;

            const string NOT_SET = "<not set>";

            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            serializedObject.UpdateIfRequiredOrScript();

            /* this stuff is no longer going to be accurate necessarilly at this moment, so don't show it in inspector
            {
                EditorGUILayout.BeginHorizontal();
                const string DESIGN = "Design Time Location";
                EditorGUILayout.LabelField(DESIGN);
                EditorGUILayout.TextField(targetGONetParticipant.DesignTimeLocation);
                EditorGUILayout.EndHorizontal();
            }

            { // codeGenerationId
                EditorGUILayout.BeginHorizontal();
                const string CODE_GEN_ID = "Code Generation Id";
                EditorGUILayout.LabelField(CODE_GEN_ID);
                string value = targetGONetParticipant.CodeGenerationId == GONetParticipant.CodeGenerationId_Unset ? NOT_SET : targetGONetParticipant.CodeGenerationId.ToString();
                EditorGUILayout.TextField(value);
                EditorGUILayout.EndHorizontal();
            }
            */

            { // AutoDontDestroyOnLoad - IMPORTANT: This should be at the top!
                var pre = GUI.enabled;
                GUI.enabled = !Application.isPlaying;
                EditorGUILayout.BeginHorizontal();
                SerializedProperty serializedProperty = serializedObject.FindProperty(nameof(GONetParticipant.AutoDontDestroyOnLoad));
                const string TT = @"When enabled, this GONetParticipant (and its GameObject) will be automatically moved to the DontDestroyOnLoad scene when instantiated at runtime.

This is essential for objects that must persist across scene changes, such as:
- GONet_GlobalContext (networking session context)
- GONet_LocalContext (per-client connection state)
- Player objects that should survive scene transitions
- Any networked object that needs to remain active during scene loading

WHY THIS IS IMPORTANT:
Without this flag, these objects would be destroyed during scene changes, breaking network synchronization and causing late-joining clients to fail initialization.

When a scene unloads, any objects in that scene are normally destroyed. By moving to DontDestroyOnLoad, the object persists and remains synchronized across all clients.

WHEN TO USE:
- Enable for networking infrastructure objects (GONet contexts)
- Enable for player objects that should persist across scenes
- Enable for any object spawned at runtime that needs to survive scene changes
- Leave disabled for scene-specific objects that should be destroyed with their scene

NOTE: This only affects runtime instantiation. Objects placed directly in scenes during design time follow Unity's normal scene lifecycle.";
                GUIContent tooltip = new GUIContent(StringUtils.AddSpacesBeforeUppercase(nameof(GONetParticipant.AutoDontDestroyOnLoad), 1), TT);
                if (EditorGUILayout.PropertyField(serializedProperty, tooltip))
                {
                    // Property field changed
                }
                EditorGUILayout.EndHorizontal();
                GUI.enabled = pre;
            }

            { // IsRigidBodyOwnerOnlyControlled
                var pre = GUI.enabled;
                GUI.enabled = !Application.isPlaying;
                EditorGUILayout.BeginHorizontal();
                SerializedProperty serializedProperty = serializedObject.FindProperty(nameof(GONetParticipant.IsRigidBodyOwnerOnlyControlled));
                const string TT = @"The expectation on setting this to true is the values for <see cref=""IsPositionSyncd""/> and <see cref=""IsRotationSyncd""/> are true
and the associated <see cref=""GameObject""/> has a <see cref=""Rigidbody""/> installed on it as well 
and <see cref=""Rigidbody.isKinematic""/> is false and if using gravity, <see cref=""Rigidbody.useGravity""/> is true.

If all that applies, then non-owners (i.e., <see cref=""IsMine""/> is false) will have <see cref=""Rigidbody.isKinematic""/> set to true and <see cref=""Rigidbody.useGravity""/> set to false
so the auto magically sync'd values for position and rotation come from owner controlled actions only.

IMPORTANT: This is not going have an effect if/when changed during a running game.  This needs to be set during design time.  Maybe a future release will decorate it with <see cref=""GONetAutoMagicalSyncAttribute""/>, if people need it.";
                GUIContent tooltip = new GUIContent(StringUtils.AddSpacesBeforeUppercase(nameof(GONetParticipant.IsRigidBodyOwnerOnlyControlled), 1), TT);
                if (EditorGUILayout.PropertyField(serializedProperty, tooltip))
                {
                    // TODO why does this method return bool?  do we need to do something!
                }
                EditorGUILayout.EndHorizontal();
                GUI.enabled = pre;
            }

            { // ShouldHideDuringRemoteInstantiate
                var pre = GUI.enabled;
                GUI.enabled = !Application.isPlaying;
                EditorGUILayout.BeginHorizontal();
                SerializedProperty serializedProperty = serializedObject.FindProperty(nameof(GONetParticipant.ShouldHideDuringRemoteInstantiate));
                const string TT = @"This is an option (good for projectiles) to deal with there being an inherent delay of <see cref=""GONetMain.valueBlendingBufferLeadSeconds""/> from the time a
remote instantiation of this <see cref=""GONetParticipant""/> (and <see cref=""IsMine""/> is false) occurs and the time auto-magical sync data starts processing for value blending
(i.e., <see cref=""GONetAutoMagicalSyncSettings_ProfileTemplate.ShouldBlendBetweenValuesReceived""/> and <see cref=""GONetAutoMagicalSyncAttribute.ShouldBlendBetweenValuesReceived""/>).

When this option is set to true, all <see cref=""Renderer""/> components on this (including children) are turned off during the buffer lead time delay and then turned back on.

If this option does not exactly suit your needs and you want something similar, then just subscribe using <see cref=""GONetMain.EventBus""/> to the <see cref=""GONetParticipantStartedEvent""/>
and check if that event's envelope has <see cref=""GONetEventEnvelope.IsSourceRemote""/> set to true and you can implement your own option to deal with this situation.";
                GUIContent tooltip = new GUIContent(StringUtils.AddSpacesBeforeUppercase(nameof(GONetParticipant.ShouldHideDuringRemoteInstantiate), 1), TT);
                if (EditorGUILayout.PropertyField(serializedProperty, tooltip))
                {
                    // TODO why does this method return bool?  do we need to do something!
                }
                EditorGUILayout.EndHorizontal();
                GUI.enabled = pre;
            }

            // DestroyWhenSpawnerLeaves - now a serialized field directly on GONetParticipant
            // Only show for prefabs being edited (Project window or Prefab Edit Mode), NOT for scene instances.
            // Scene objects are immune to spawner death (SpawnerPersistentId=0), so this setting doesn't apply.
            bool isPrefabAsset = UnityEditor.PrefabUtility.IsPartOfPrefabAsset(targetGONetParticipant.gameObject);
            bool isInPrefabEditMode = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null;
            bool shouldShowDestroyWhenSpawnerLeaves = isPrefabAsset || isInPrefabEditMode;

            if (shouldShowDestroyWhenSpawnerLeaves)
            {
                var pre = GUI.enabled;
                GUI.enabled = !Application.isPlaying;
                EditorGUILayout.BeginHorizontal();

                SerializedProperty destroyWhenSpawnerLeavesProperty = serializedObject.FindProperty("destroyWhenSpawnerLeaves");

                const string DESTROY_TT = @"DISTRIBUTED HOST FAILOVER:
When the machine that spawned this object leaves or crashes, what should happen?

TRUE (default): Destroy the object when spawner leaves.
Use for: Player characters, player-owned weapons, player-spawned projectiles.
These objects are 'bound' to their player/client and should die with them.

FALSE: Transfer to new host on failover (object survives).
Use for: World objects, NPCs, doors, tradeable items.
These objects should persist even when their original spawner leaves.

NOTE: Scene-defined objects are IMMUNE regardless of this setting.

GUIDELINE: If it can be traded or picked up by another player, set to FALSE.";
                GUIContent tooltip = new GUIContent("Destroy When Spawner Leaves", DESTROY_TT);

                EditorGUILayout.PropertyField(destroyWhenSpawnerLeavesProperty, tooltip);

                EditorGUILayout.EndHorizontal();
                GUI.enabled = pre;
            }

            if (Application.isPlaying) // this value is only really relevant during play (not to mention, the way we determine this is faulty otherwise...false positives everywhere)
            { // design time?
                {
                    EditorGUILayout.BeginHorizontal();
                    const string INSTANTI = "Was Instantiated?";
                    EditorGUILayout.LabelField(INSTANTI);
                    EditorGUILayout.Toggle(targetGONetParticipant.WasInstantiated);
                    EditorGUILayout.EndHorizontal();
                }

                { // GoNetId
                    EditorGUILayout.BeginHorizontal();
                    const string GONET_ID = "GO Net Id";
                    const string TT = "This is a combination of GO Net Id (RAW) and Owner Authority Id";
                    GUIContent tooltip = new GUIContent(GONET_ID, TT);
                    EditorGUILayout.LabelField(tooltip);
                    string value = targetGONetParticipant.GONetId == GONetParticipant.GONetId_Unset ? NOT_SET : targetGONetParticipant.GONetId.ToString();
                    EditorGUILayout.TextField(value);
                    EditorGUILayout.EndHorizontal();
                }

                if (targetGONetParticipant.GONetIdAtInstantiation != targetGONetParticipant.GONetId)
                { // GoNetIdAtInstantiation
                    EditorGUILayout.BeginHorizontal();
                    const string GONET_ID = "GO Net Id (At Instantiation)";
                    const string TT = "This is the original/first GONetId assigned, but it has changed due to someone else assuming authority over it (e.g., the server via GONetMain.Server_AssumeAuthorityOver()).";
                    GUIContent tooltip = new GUIContent(GONET_ID, TT);
                    EditorGUILayout.LabelField(tooltip);
                    EditorGUILayout.TextField(targetGONetParticipant.GONetIdAtInstantiation.ToString());
                    EditorGUILayout.EndHorizontal();
                }

                { // GoNetId RAW
                    EditorGUILayout.BeginHorizontal();
                    const string GONET_ID_RAW = "GO Net Id (RAW)";
                    EditorGUILayout.LabelField(GONET_ID_RAW);
                    string value = targetGONetParticipant.gonetId_raw == GONetParticipant.GONetId_Unset ? NOT_SET : targetGONetParticipant.gonetId_raw.ToString();
                    EditorGUILayout.TextField(value);
                    EditorGUILayout.EndHorizontal();
                }

                { // OwnerAuthorityId
                    EditorGUILayout.BeginHorizontal();
                    const string OWNER_AUTHORITY_ID = "Owner Authority Id";
                    EditorGUILayout.LabelField(OWNER_AUTHORITY_ID);
                    const string GONET_SERVER = "<GONet server>";
                    string value = targetGONetParticipant.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Server
                        ? GONET_SERVER
                        : (targetGONetParticipant.OwnerAuthorityId == GONetMain.OwnerAuthorityId_Unset ? NOT_SET : targetGONetParticipant.OwnerAuthorityId.ToString());
                    EditorGUILayout.TextField(value);
                    EditorGUILayout.EndHorizontal();
                }

                { // IsMine
                    EditorGUILayout.BeginHorizontal();
                    const string IS_MINE = "Is Mine?";
                    EditorGUILayout.LabelField(IS_MINE);
                    EditorGUILayout.Toggle(GONetMain.IsMine(targetGONetParticipant));
                    EditorGUILayout.EndHorizontal();
                }

                { // Client_IsInLimbo
                    EditorGUILayout.BeginHorizontal();
                    const string IS_IN_LIMBO = "Client Is In Limbo?";
                    const string TT = @"CLIENT ONLY: Indicates this object is in 'limbo' state - spawned locally but waiting for GONetId batch from server.

IMPORTANT: Limbo is RARE - only occurs during extreme rapid spawning (100+ spawns/sec). Most games will NEVER see this.

While in limbo:
- Object exists locally but has NO GONetId assigned
- Object is NOT networked and CANNOT sync values
- Object CANNOT receive RPCs
- OnGONetReady() is BLOCKED (will fire after graduation)

When a new batch arrives from server, limbo objects automatically 'graduate':
- GONetId assigned from batch
- Disabled components re-enabled (if limbo mode disabled them)
- OnGONetReady() fired
- Object becomes fully networked

See Client_GONetIdBatchLimboMode enum for different limbo mode behaviors.";
                    GUIContent tooltip = new GUIContent(IS_IN_LIMBO, TT);
                    EditorGUILayout.LabelField(tooltip);

                    // Show with colored background if in limbo (yellow/warning color)
                    Color previousColor = GUI.backgroundColor;
                    if (targetGONetParticipant.Client_IsInLimbo)
                    {
                        GUI.backgroundColor = new Color(1f, 1f, 0f, 0.5f); // Yellow tint
                    }

                    EditorGUILayout.Toggle(targetGONetParticipant.Client_IsInLimbo);
                    GUI.backgroundColor = previousColor;

                    EditorGUILayout.EndHorizontal();
                }

                if (targetGONetParticipant.RemotelyControlledByAuthorityId != GONetMain.OwnerAuthorityId_Unset)
                { // RemotelyControlledByAuthorityId && IsMine_ToRemotelyControl
                    EditorGUILayout.BeginHorizontal();
                    const string REMOTELY_CONTROLLED_BY_AUTHORITY_ID = "Remotely Controlled by Authority Id";
                    EditorGUILayout.LabelField(REMOTELY_CONTROLLED_BY_AUTHORITY_ID);
                    string value = targetGONetParticipant.RemotelyControlledByAuthorityId.ToString();
                    EditorGUILayout.TextField(value);
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    const string IS_REMOTELY_CONTROLLED_BY_ME = "Is Mine (for Remote Control)?";
                    EditorGUILayout.LabelField(IS_REMOTELY_CONTROLLED_BY_ME);
                    EditorGUILayout.Toggle(targetGONetParticipant.IsMine_ToRemotelyControl);
                    EditorGUILayout.EndHorizontal();
                }
            }

            GUI.enabled = guiEnabledPrevious;

            const string ATTR = "[GONetAutoMagicalSync] Items to Sync";
            bool isSyncAttrSectionFolded = GetFoldoutState(SESSION_KEY_SYNC_ATTR, true);
            bool newSyncAttrSectionFolded = EditorGUILayout.Foldout(isSyncAttrSectionFolded, ATTR);
            if (newSyncAttrSectionFolded != isSyncAttrSectionFolded)
            {
                SetFoldoutState(SESSION_KEY_SYNC_ATTR, newSyncAttrSectionFolded);
            }
            if (newSyncAttrSectionFolded)
            { // Handle/draw all [GONetAutoMagicalSync] members
                EditorGUI.indentLevel++;

                foreach (var siblingMonoBehaviour in targetGONetParticipant.GetComponents<MonoBehaviour>())
                {
                    if (!(siblingMonoBehaviour is GONetParticipant))
                    {
                        var autoSyncMembersInSibling =
                            siblingMonoBehaviour
                                .GetType()
                                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                                .Where(member => (member.MemberType == MemberTypes.Property || member.MemberType == MemberTypes.Field)
                                                && member.GetCustomAttribute(typeof(GONetAutoMagicalSyncAttribute), true) != null);

                        if (autoSyncMembersInSibling.Count() > 0)
                        {
                            bool guiEnabledPrevious_inner = GUI.enabled;
                            GUI.enabled = false;

                            string ScriptName = siblingMonoBehaviour.GetType().Name;
                            string scriptFoldoutKey = $"Script_{ScriptName}";
                            bool isfoldie = GetFoldoutState(scriptFoldoutKey, false);
                            bool newIsfoldie = EditorGUILayout.Foldout(isfoldie, string.Concat(ScriptName, SCR));
                            if (newIsfoldie != isfoldie)
                            {
                                SetFoldoutState(scriptFoldoutKey, newIsfoldie);
                            }
                            if (newIsfoldie)
                            {
                                EditorGUI.indentLevel++;

                                EditorGUILayout.BeginHorizontal();
                                const string ScriptLabel = "Script";
                                EditorGUILayout.LabelField(ScriptLabel);

                                GUI.enabled = true;
                                if (GUILayout.Button(ScriptName, GetClickableDisabledLabelStyle()))
                                {
                                    var script = MonoScript.FromMonoBehaviour(siblingMonoBehaviour);
                                    if ((EditorApplication.timeSinceStartup - lastClickableLabelClickedTime) < CONSIDER_DOUBLE_CLICK_IF_WITHIN_TIME)
                                    {
                                        AssetDatabase.OpenAsset(script);
                                    }
                                    else
                                    {
                                        //Selection.SetActiveObjectWithContext(script, null); // this would be cool, but prevents the ability to double click since focus goes to this script and the thing to double click is no longer visible in inspector!!!
                                        EditorGUIUtility.PingObject(script);
                                    }

                                    lastClickableLabelClickedTime = EditorApplication.timeSinceStartup;
                                }
                                GUI.enabled = false;

                                EditorGUILayout.EndHorizontal();

                                foreach (var autoSyncMember in autoSyncMembersInSibling)
                                {
                                    EditorGUILayout.BeginHorizontal();
                                    EditorGUILayout.LabelField(autoSyncMember.Name, GUILayout.MaxWidth(150));

                                    EditorGUILayout.TextField(autoSyncMember.MemberType == MemberTypes.Field ? ((FieldInfo)autoSyncMember).GetValue(siblingMonoBehaviour).ToString() : ((PropertyInfo)autoSyncMember).GetValue(siblingMonoBehaviour).ToString(),
                                        GUILayout.MinWidth(70), GUILayout.ExpandWidth(true));

                                    GONetAutoMagicalSyncAttribute autoSyncMember_SyncAttribute = (GONetAutoMagicalSyncAttribute)autoSyncMember.GetCustomAttribute(typeof(GONetAutoMagicalSyncAttribute), true);

                                    { // is at rest?
                                        GONetParticipant_AutoMagicalSyncCompanion_Generated syncCompanion = GONetMain.GetSyncCompanionByGNP(targetGONetParticipant);

                                        byte index = 0;
                                        if (syncCompanion != null && syncCompanion.TryGetIndexByMemberName(autoSyncMember.Name, out index))
                                        {
                                            bool isAtRest = syncCompanion != null ? syncCompanion.IsValueAtRest(index) : false;
                                            EditorGUILayout.LabelField("At_Rest?");
                                            EditorGUILayout.Toggle(isAtRest);
                                        }
                                    }

                                    DrawGONetSyncProfileTemplateButton(autoSyncMember_SyncAttribute.SettingsProfileTemplateName, siblingMonoBehaviour);

                                    EditorGUILayout.EndHorizontal();
                                }
                                EditorGUI.indentLevel--;
                            }

                            GUI.enabled = guiEnabledPrevious_inner;
                        }
                    }
                }


                Animator animator = targetGONetParticipant.GetComponent<Animator>();
                if (AnimationEditorUtils.TryGetAnimatorControllerParameters(animator, out var parameters))
                {
                    if (parameters != null && parameters.Length > 0)
                    {
                        if (targetGONetParticipant.animatorSyncSupport == null)
                        {
                            targetGONetParticipant.animatorSyncSupport = new GONetParticipant.AnimatorControllerParameterMap();
                        }

                        // Detect curve-controlled parameters (e.g., IK params driven by animation curves)
                        // These parameters should typically NOT be synced because Unity will produce warnings
                        // when non-authority tries to set them, and they're computed locally by each client's IK system
                        var curveControlledParams = AnimationEditorUtils.GetCurveControlledParameterNames(animator);

                        string animatorControllerName = animator.runtimeAnimatorController.name;
                        string animatorFoldoutKey = $"Animator_{animatorControllerName}";
                        bool isAnimatorFolded = GetFoldoutState(animatorFoldoutKey, false);

                        EditorGUILayout.BeginHorizontal();

                        EditorGUILayout.BeginHorizontal(GUILayout.MinWidth(140));
                        const string ANIMATOR_INTRINSICS = "Animator (Intrinsics)";
                        bool newIsAnimatorFolded = EditorGUILayout.Foldout(isAnimatorFolded, ANIMATOR_INTRINSICS);
                        if (newIsAnimatorFolded != isAnimatorFolded)
                        {
                            SetFoldoutState(animatorFoldoutKey, newIsAnimatorFolded);
                        }
                        EditorGUILayout.EndHorizontal();

                        DrawGONetSyncProfileTemplateButton(GONetAutoMagicalSyncAttribute.PROFILE_TEMPLATE_NAME___ANIMATOR_CONTROLLER_PARAMETERS);

                        EditorGUILayout.EndHorizontal();

                        if (newIsAnimatorFolded)
                        {
                            EditorGUI.indentLevel++;

                            bool guiEnabledPrevious_inner = GUI.enabled;
                            GUI.enabled = false;

                            EditorGUILayout.BeginHorizontal();
                            const string ControllerLabel = "Controller";
                            EditorGUILayout.LabelField(ControllerLabel);
                            EditorGUILayout.TextField(animatorControllerName);
                            EditorGUILayout.EndHorizontal();

                            GUI.enabled = guiEnabledPrevious_inner;

                            if (Application.isPlaying)
                            {
                                guiEnabledPrevious_inner = GUI.enabled;
                                GUI.enabled = false;
                            }

                            for (int i = 0; i < parameters.Length; ++i)
                            {
                                if (!StringUtils.IsStringValidForCSharpNamingConventions(parameters[i].name))
                                {
                                    GONetLog.Error($"The animation parameter name '{parameters[i].name}' is not valid. Skipping this parameter. Please, check the rules that a string must follow in order to be valid. You can find them within the class StringUtils.IsStringValidForCSharpNamingConventions");
                                    Debug.LogError($"The animation parameter name '{parameters[i].name}' is not valid. Skipping this parameter. Please, check the rules that a string must follow in order to be valid. You can find them within the class StringUtils.IsStringValidForCSharpNamingConventions");
                                    continue;
                                }

                                AnimatorControllerParameter animatorControllerParameter = parameters[i];
                                // Triggers use event-based sync (SetAnimatorTrigger), not value-based monitoring
                                // Float/Int/Bool use value-based sync where isSyncd checkbox applies
                                bool usesValueBasedSync = animatorControllerParameter.type != AnimatorControllerParameterType.Trigger;
                                string parameterSyncMap_key = animatorControllerParameter.name;
                                bool isCurveControlled = curveControlledParams.Contains(parameterSyncMap_key);

                                if (!targetGONetParticipant.animatorSyncSupport.ContainsKey(parameterSyncMap_key))
                                {
                                    targetGONetParticipant.animatorSyncSupport[parameterSyncMap_key] = new GONetParticipant.AnimatorControllerParameter()
                                    {
                                        valueType = animatorControllerParameter.type,
                                        isSyncd = false,
                                        isCurveControlled = isCurveControlled
                                    };
                                }
                                else
                                {
                                    // Update curve-controlled status in case animator changed
                                    var existingParam = targetGONetParticipant.animatorSyncSupport[parameterSyncMap_key];
                                    if (existingParam.isCurveControlled != isCurveControlled)
                                    {
                                        existingParam.isCurveControlled = isCurveControlled;
                                        targetGONetParticipant.animatorSyncSupport[parameterSyncMap_key] = existingParam;
                                        EditorUtility.SetDirty(targetGONetParticipant);
                                    }
                                }

                                // Force isSyncd = false for trigger parameters - they use event-based sync via SetAnimatorTrigger()
                                // and cannot use value-based monitoring (Unity has no Animator.GetTrigger() method)
                                if (!usesValueBasedSync)
                                {
                                    var param = targetGONetParticipant.animatorSyncSupport[parameterSyncMap_key];
                                    if (param.isSyncd)
                                    {
                                        param.isSyncd = false;
                                        targetGONetParticipant.animatorSyncSupport[parameterSyncMap_key] = param;
                                        EditorUtility.SetDirty(targetGONetParticipant);
                                    }
                                }

                                int parameterSyncMap_keyIndex = targetGONetParticipant.animatorSyncSupport.GetCustomKeyIndex(parameterSyncMap_key);
                                var currentParam = targetGONetParticipant.animatorSyncSupport[parameterSyncMap_key];

                                bool guiItemPrior = GUI.enabled;
                                if (!usesValueBasedSync)
                                {
                                    // Trigger parameters use event-based sync via SetAnimatorTrigger() instead of value monitoring.
                                    // The isSyncd checkbox doesn't apply to triggers, so disable it but show tooltip explaining the event-based approach.
                                    GUI.enabled = false;
                                }
                                EditorGUILayout.BeginHorizontal();

                                // Build label with curve-controlled indicator if applicable
                                string labelString;
                                if (isCurveControlled)
                                {
                                    // Visual indicator for curve-controlled params
                                    labelString = string.Concat("Is Syncd: ", parameterSyncMap_key, " [CURVE]");
                                }
                                else
                                {
                                    labelString = string.Concat("Is Syncd: ", parameterSyncMap_key);
                                }

                                GUIContent labelContent = new GUIContent(labelString, string.Empty);
                                if (!usesValueBasedSync)
                                {
                                    labelContent.tooltip = "Trigger parameters use event-based sync instead of value monitoring (Unity has no Animator.GetTrigger() method).\n\nTo sync triggers across the network, use:\n  gonetParticipant.SetAnimatorTrigger(\"TriggerName\");\n\nOr from a GONetParticipantCompanionBehaviour:\n  SetAnimatorTrigger(\"TriggerName\");\n\nFor best performance, use pre-computed hash constants:\n  SetAnimatorTrigger(AnimatorTriggerHashes.TriggerName);\n\nRun GONet code generation to populate AnimatorTriggerHashes with your trigger names.\n\nThe trigger event is automatically reset at end of frame to prevent late-joiners from receiving stale triggers.";
                                }
                                else if (isCurveControlled)
                                {
                                    // Show warning for curve-controlled parameters
                                    labelContent.tooltip = "[CURVE-CONTROLLED PARAMETER]\n\nThis parameter is driven by animation curves (e.g., IK parameters like IKLeftFoot, IKRightFoot).\n\nSyncing curve-controlled parameters is NOT recommended because:\n" +
                                        "1. Unity produces warnings when non-authority tries to set these values\n" +
                                        "2. These values are computed locally by each client's animation/IK system\n" +
                                        "3. Syncing them causes conflicts between network values and local IK, resulting in visual jitter\n\n" +
                                        "Each client should compute these values locally based on their animation state and environment.\n\n" +
                                        "If you enable sync anyway, expect console warnings and potential visual artifacts.";
                                }

                                // Show warning color for curve-controlled params that are synced
                                Color originalColor = GUI.color;
                                if (isCurveControlled && currentParam.isSyncd && usesValueBasedSync)
                                {
                                    GUI.color = new Color(1f, 0.7f, 0.3f); // Orange warning color
                                }

                                EditorGUILayout.LabelField(labelContent);
                                GUI.color = originalColor;

                                SerializedProperty specificInnerMapValue_serializedProperty = serializedObject.FindProperty($"{nameof(GONetParticipant.animatorSyncSupport)}.values.Array.data[{parameterSyncMap_keyIndex}].{nameof(GONetParticipant.AnimatorControllerParameter.isSyncd)}");
                                EditorGUILayout.PropertyField(specificInnerMapValue_serializedProperty, GUIContent.none, false); // IMPORTANT: without this, editing prefabs would never save/persist changes!
                                EditorGUILayout.EndHorizontal();
                                GUI.enabled = guiItemPrior;
                            }

                            GUI.enabled = guiEnabledPrevious_inner;

                            EditorGUI.indentLevel--;
                        }
                    }
                }

                { // what used to be DrawDefaultInspector():
                    const string GOIntrinsics = "Transform (Intrinsics)";
                    bool isTransformFolded = GetFoldoutState(SESSION_KEY_TRANSFORM, true);
                    bool newIsTransformFolded = EditorGUILayout.Foldout(isTransformFolded, GOIntrinsics);// string.Concat(typeof(GONetParticipant).Name, SCR));
                    if (newIsTransformFolded != isTransformFolded)
                    {
                        SetFoldoutState(SESSION_KEY_TRANSFORM, newIsTransformFolded);
                    }
                    if (newIsTransformFolded)
                    {
                        EditorGUI.indentLevel++;

                        bool guiEnabledPrevious_inner = GUI.enabled;
                        GUI.enabled = false;

                        EditorGUILayout.BeginHorizontal();
                        const string ScriptLabel = "Script";
                        EditorGUILayout.LabelField(ScriptLabel);
                        EditorGUILayout.TextField(nameof(Transform));
                        EditorGUILayout.EndHorizontal();

                        GUI.enabled = guiEnabledPrevious_inner;

                        // Check if Rigidbody physics interpolation will be used
                        bool hasRigidbody = targetGONetParticipant.GetComponent<Rigidbody>() != null || targetGONetParticipant.GetComponent<Rigidbody2D>() != null;
                        bool isRigidBodyOwnerControlled = targetGONetParticipant.IsRigidBodyOwnerOnlyControlled;

                        { // IsPositionSyncd:
                            const string POSITION_TT = @"Enable to synchronize position across the network.

IMPORTANT - Rigidbody Physics Interpolation:
When 'Is Rigid Body Owner Only Controlled' is checked AND a Rigidbody component exists, Unity's physics interpolation handles smooth rendering on non-authority clients instead of GONet's value blending system.

How it works:
- Authority (IsMine=true): Physics simulation runs normally
- Non-authority (IsMine=false): Rigidbody set to kinematic, Unity interpolation enabled
- Network updates applied via Rigidbody.MovePosition() for physics-aware rendering
- Unity's Rigidbody.interpolation smooths motion between network updates

Profile settings below still control sync frequency and reliability, but value blending is handled by Unity's physics system when using Rigidbody.";

                            string positionTooltip = (hasRigidbody && isRigidBodyOwnerControlled)
                                ? POSITION_TT
                                : "Enable to synchronize position across the network.";

                            EditorGUILayout.BeginHorizontal();
                            GUIContent positionLabel = new GUIContent(string.Concat("Is Position Syncd"), positionTooltip);
                            EditorGUILayout.LabelField(positionLabel, GUILayout.MaxWidth(150));
                            SerializedProperty positionProperty = serializedObject.FindProperty($"{nameof(GONetParticipant.IsPositionSyncd)}");
                            EditorGUILayout.PropertyField(positionProperty, GUIContent.none, false, GUILayout.MaxWidth(50)); // IMPORTANT: without this, editing would never save/persist changes!
                            DrawGONetSyncProfileTemplateButton(GONetAutoMagicalSyncAttribute.PROFILE_TEMPLATE_NAME___TRANSFORM_POSITION);
                            EditorGUILayout.EndHorizontal();
                        }
                        { // IsRotationSyncd:
                            const string ROTATION_TT = @"Enable to synchronize rotation across the network.

IMPORTANT - Rigidbody Physics Interpolation:
When 'Is Rigid Body Owner Only Controlled' is checked AND a Rigidbody component exists, Unity's physics interpolation handles smooth rendering on non-authority clients instead of GONet's value blending system.

How it works:
- Authority (IsMine=true): Physics simulation runs normally
- Non-authority (IsMine=false): Rigidbody set to kinematic, Unity interpolation enabled
- Network updates applied via Rigidbody.MoveRotation() for physics-aware rendering
- Unity's Rigidbody.interpolation smooths motion between network updates

Profile settings below still control sync frequency and reliability, but value blending is handled by Unity's physics system when using Rigidbody.";

                            string rotationTooltip = (hasRigidbody && isRigidBodyOwnerControlled)
                                ? ROTATION_TT
                                : "Enable to synchronize rotation across the network.";

                            EditorGUILayout.BeginHorizontal();
                            GUIContent rotationLabel = new GUIContent(string.Concat("Is Rotation Syncd"), rotationTooltip);
                            EditorGUILayout.LabelField(rotationLabel, GUILayout.MaxWidth(150));
                            SerializedProperty rotationProperty = serializedObject.FindProperty($"{nameof(GONetParticipant.IsRotationSyncd)}");
                            EditorGUILayout.PropertyField(rotationProperty, GUIContent.none, false, GUILayout.MaxWidth(50)); // IMPORTANT: without this, editing would never save/persist changes!
                            DrawGONetSyncProfileTemplateButton(GONetAutoMagicalSyncAttribute.PROFILE_TEMPLATE_NAME___TRANSFORM_ROTATION);
                            EditorGUILayout.EndHorizontal();
                        }

                        EditorGUI.indentLevel--;
                    }
                }

                if (serializedObject.hasModifiedProperties)
                {
                    if (!Application.isPlaying)
                    {
                        GONetSpawnSupport_DesignTime.AddGONetDesignTimeDirtyReason("Important member data of a GONetParticipant has changed values in editor. Path:" + DesignTimeMetadata.GetFullPath(targetGONetParticipant));
                    }
                }
                serializedObject.ApplyModifiedProperties();

                if (EditorGUI.EndChangeCheck())
                {
                    if (!Application.isPlaying)
                    {
                        EditorUtility.SetDirty(targetGONetParticipant);
                        EditorUtility.SetDirty(targetGONetParticipant.gameObject);

                        bool isPrefab = targetGONetParticipant.DesignTimeLocation.EndsWith(PREFAB_FILE_EXTENSION); // TODO ensure we can count on this....or just use a sure fire way for unity to tell us the answer
                        if (!isPrefab)
                        {
                            EditorUtility.SetDirty(targetGONetParticipant);
                            EditorUtility.SetDirty(targetGONetParticipant.gameObject);
                            EditorSceneManager.MarkAllScenesDirty();
                        }
                    }
                }


                EditorGUI.indentLevel--;
            }
        }

        static double lastClickableLabelClickedTime;
        const double CONSIDER_DOUBLE_CLICK_IF_WITHIN_TIME = 0.3;

        static GUIStyle clickableDisabledLabelStyle;
        static GUIStyle GetClickableDisabledLabelStyle()
        {
            if (clickableDisabledLabelStyle == null)
            {
                clickableDisabledLabelStyle = new GUIStyle(GUI.skin.textField);
                clickableDisabledLabelStyle.normal.textColor = Color.grey; // make it look disabled
            }
            return clickableDisabledLabelStyle;
        }

        private static void DrawGONetSyncProfileTemplateButton(string settingsProfileTemplateName, MonoBehaviour siblingMonoBehaviour = null)
        {
            const string PROFILE = "profile: ";
            const string TOOLTIP_PROFILE = "Click to select the corresponding GONet SyncSettingsProfile asset in Project view.\nOnce selected, you can view/edit the sync settings for all values using this profile.";
            const string TOOLTIP_ATTR = "No profile identified in [GONetAutoMagicalSync(SettingsProfileTemplateName=\"<profile name here>\")].\nClick to open the C# class with the [GONetAutoMagicalSync] attribute for this field/property.\nOnce open, you can view/edit the sync settings for this value directly in the C# Attribute -OR- set the name of the profile you want to use.";

            string tooltip = TOOLTIP_PROFILE;
            string profileName = settingsProfileTemplateName;

            if (string.IsNullOrWhiteSpace(settingsProfileTemplateName))
            {
                if (siblingMonoBehaviour == null)
                {
                    throw new System.Exception("not supported");
                }

                tooltip = TOOLTIP_ATTR;
                profileName = "N/A (uses C# Attribute settings)";
            }

            bool superInnerPrev = GUI.enabled;
            GUI.enabled = true;
            GUIContent buttonTextWithTooltip = new GUIContent(string.Concat(PROFILE, profileName), tooltip);
            if (GUILayout.Button(buttonTextWithTooltip))
            {
                if (tooltip == TOOLTIP_PROFILE)
                {
                    UnityEngine.Object mainAsset = AssetDatabase.LoadMainAssetAtPath(string.Concat(
                        GONetEditorWindow.ASSETS_SYNC_SETTINGS_PROFILES_FOLDER_PATH,
                        settingsProfileTemplateName,
                        GONetEditorWindow.ASSET_FILE_EXTENSION));
                    if (mainAsset != null)
                    {
                        Selection.activeObject = mainAsset;
                    }
                    else
                    {
                        const string OOPS = "Oops.  The profile/template name used here (i.e., \"";
                        const string NAME = "\") does NOT match with any of the available entries in the folder: ";
                        const string INSTEAD = ".\nAt runtime, the following profile/template will be used instead: ";
                        const string NEW = "\nTo create a new sync settings profile/template, open the GONet Editor Support window (see File menu named GONet), enter the name of the new profile/temple, click Create and edit the settings to your liking.";
                        Debug.LogWarning(string.Concat(OOPS, settingsProfileTemplateName ?? string.Empty, NAME, GONetEditorWindow.ASSETS_SYNC_SETTINGS_PROFILES_FOLDER_PATH, INSTEAD, GONetAutoMagicalSyncAttribute.PROFILE_TEMPLATE_NAME___DEFAULT, NEW));
                    }
                }
                else if (tooltip == TOOLTIP_ATTR)
                {
                    var script = MonoScript.FromMonoBehaviour(siblingMonoBehaviour);
                    AssetDatabase.OpenAsset(script);
                }
            }
            GUI.enabled = superInnerPrev;
        }
    }
}
