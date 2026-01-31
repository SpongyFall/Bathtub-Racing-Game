/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved
 * Unauthorized copying of this file, via any medium is strictly prohibited
 * Proprietary and confidential, email: contactus@unitygo.net
 *
 *
 * Authorized use is explicitly limited to the following:
 * -The ability to view and reference source code without changing it
 * -The ability to enhance debugging with source code access
 * -The ability to distribute products based on original Unity Asset Store package and the unmodified GONet source
 *
 * All other use cases are explicitly forbidden, including but not limited to:
 * -The ability to modify source code for redistribution
 * -The ability to modify source code for use in products outside the original Unity Asset Store package
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using GONet.Generation;

namespace GONet.Editor.Generation
{
    /// <summary>
    /// GONet v2: Generates the SoA descriptor file that defines exact stream capacities and intervals.
    ///
    /// This generator:
    /// 1. Scans all prefabs in project (Assets + Addressables)
    /// 2. Discovers unique (ValueType, SyncInterval) combinations
    /// 3. Generates GONet_SoA_Descriptor.cs with exact capacities
    /// 4. Generates per-CodeGenId stream participation mapping
    ///
    /// Output: Assets/GONet/Code/GONet/Generation/Generated/GONet_SoA_Descriptor.cs
    ///
    /// Called from: GONetParticipant_AutoMagicalSyncCompanion_Generated_Generator.GenerateFiles()
    /// </summary>
    internal static class GONet_SoA_Descriptor_Generator
    {
        private const string OUTPUT_FILE_PATH = "Assets/GONet/Code/GONet/Generation/Generated/GONet_SoA_Descriptor.cs";
        private const float CAPACITY_SAFETY_MARGIN = 50f; // TEMP: Large headroom for scene instances + runtime spawns

        /// <summary>
        /// Main entry point: Generate the SoA descriptor file.
        /// Called during code generation pipeline.
        /// </summary>
        public static void GenerateSoADescriptor(List<GONetParticipant_ComponentsWithAutoSyncMembers> allUniqueSnaps)
        {
            GONetLog.Info("GONet v2: Analyzing project for SoA sizing...");

            // Discover all unique streams needed
            var streamInfo = DiscoverStreamsFromSnaps(allUniqueSnaps);

            // Generate the descriptor file
            string code = GenerateDescriptorCode(streamInfo);

            // Write to file
            File.WriteAllText(OUTPUT_FILE_PATH, code);

            GONetLog.Info($"GONet v2: Generated SoA descriptor with {streamInfo.Count} streams at {OUTPUT_FILE_PATH}");
        }

        /// <summary>
        /// Discover unique (ValueType, SyncInterval) combinations from all prefabs.
        /// Returns dictionary: (valueType, syncInterval) -> StreamInfo
        /// </summary>
        private static Dictionary<(Type valueType, float syncInterval), StreamInfo> DiscoverStreamsFromSnaps(
            List<GONetParticipant_ComponentsWithAutoSyncMembers> allUniqueSnaps)
        {
            var streams = new Dictionary<(Type, float), StreamInfo>();

            foreach (var snap in allUniqueSnaps)
            {
                // Track if we've added Transform sync for this snap (avoid duplicates)
                bool hasAddedPositionSync = false;
                bool hasAddedRotationSync = false;

                int componentCount = snap.ComponentMemberNames_By_ComponentTypeFullName?.Length ?? 0;
                GONetLog.Info($"GONet v2: Analyzing CodeGenId {snap.codeGenerationId} ({componentCount} components)");

                if (snap.ComponentMemberNames_By_ComponentTypeFullName == null)
                {
                    GONetLog.Warning($"  ComponentMemberNames_By_ComponentTypeFullName is NULL for CodeGenId {snap.codeGenerationId}!");
                    continue;
                }

                // Iterate through all components
                foreach (var component in snap.ComponentMemberNames_By_ComponentTypeFullName)
                {
                    int memberCount = component.autoSyncMembers?.Length ?? 0;
                    GONetLog.Info($"  Component: {component.componentTypeName}, isTransformIntrinsics={component.isTransformIntrinsics}, members={memberCount}");

                    // DEBUG: Check each part of Transform condition
                    if (component.isTransformIntrinsics)
                    {
                        GONetLog.Info($"    [DEBUG] Transform intrinsics TRUE. hasAddedPositionSync={hasAddedPositionSync}, hasAddedRotationSync={hasAddedRotationSync}");
                    }

                    // AUTO-DETECT TRANSFORM SYNC (v2 optimization by default)
                    // Check if this component represents Transform intrinsics (position/rotation)
                    if (component.isTransformIntrinsics && !hasAddedPositionSync && !hasAddedRotationSync)
                    {
                        GONetLog.Info($"    TRANSFORM INTRINSICS DETECTED! Processing {memberCount} members");

                        if (component.autoSyncMembers == null || component.autoSyncMembers.Length == 0)
                        {
                            GONetLog.Warning($"      Transform intrinsics detected but autoSyncMembers is null/empty!");
                            continue;
                        }

                        // Transform sync uses hardcoded profiles (_GONet_Transform_Position, _GONet_Transform_Rotation)
                        // Both default to 24 Hz sync rate
                        foreach (var member in component.autoSyncMembers)
                        {
                            GONetLog.Info($"      Member: {member.memberName}, type: {member.memberTypeFullName}");

                            // Type.GetType requires assembly-qualified name for types in other assemblies
                            // UnityEngine types need ", UnityEngine.CoreModule" appended
                            string typeString = member.memberTypeFullName;
                            if (!typeString.Contains(",") && typeString.StartsWith("UnityEngine."))
                            {
                                typeString = typeString + ", UnityEngine.CoreModule";
                            }

                            Type valueType = Type.GetType(typeString);
                            if (valueType == null)
                            {
                                GONetLog.Warning($"      Type.GetType returned NULL for {member.memberTypeFullName} (tried: {typeString})");
                                continue;
                            }

                            GONetLog.Info($"      Successfully resolved type: {valueType.FullName}");

                            // Transform position (Vector3)
                            if (valueType == typeof(Vector3) && !hasAddedPositionSync)
                            {
                                var key = (typeof(Vector3), 1f / 24f); // 24 Hz from _GONet_Transform_Position
                                if (!streams.ContainsKey(key))
                                {
                                    streams[key] = new StreamInfo
                                    {
                                        ValueType = typeof(Vector3),
                                        SyncInterval = 1f / 24f,
                                        Capacity = 0,
                                        CodeGenIdsUsingStream = new HashSet<byte>()
                                    };
                                }
                                streams[key].Capacity++;
                                streams[key].CodeGenIdsUsingStream.Add(snap.codeGenerationId);
                                hasAddedPositionSync = true;
                            }

                            // Transform rotation (Quaternion)
                            if (valueType == typeof(Quaternion) && !hasAddedRotationSync)
                            {
                                var key = (typeof(Quaternion), 1f / 24f); // 24 Hz from _GONet_Transform_Rotation
                                if (!streams.ContainsKey(key))
                                {
                                    streams[key] = new StreamInfo
                                    {
                                        ValueType = typeof(Quaternion),
                                        SyncInterval = 1f / 24f,
                                        Capacity = 0,
                                        CodeGenIdsUsingStream = new HashSet<byte>()
                                    };
                                }
                                streams[key].Capacity++;
                                streams[key].CodeGenIdsUsingStream.Add(snap.codeGenerationId);
                                hasAddedRotationSync = true;
                            }
                        }
                    }

                    // USER-DEFINED [GONetAutoMagicalSync] FIELDS
                    // Iterate through all members in this component
                    foreach (var member in component.autoSyncMembers)
                    {
                        // Skip if this is Transform intrinsics (already handled above)
                        if (component.isTransformIntrinsics)
                            continue;

                        // Get value type from member type full name
                        // Type.GetType requires assembly-qualified name for types in other assemblies
                        string typeString = member.memberTypeFullName;
                        if (!typeString.Contains(",") && typeString.StartsWith("UnityEngine."))
                        {
                            typeString = typeString + ", UnityEngine.CoreModule";
                        }

                        Type valueType = Type.GetType(typeString);
                        if (valueType == null)
                            continue;

                        // Normalize value type to SoA stream type
                        Type streamType = NormalizeToStreamType(valueType);
                        if (streamType == null)
                            continue; // Unsupported type (custom serializers handled separately)

                        // Get sync interval from profile (from member attribute)
                        float syncInterval = GetSyncInterval(member);

                        // Create or update stream info
                        var key = (streamType, syncInterval);
                        if (!streams.ContainsKey(key))
                        {
                            streams[key] = new StreamInfo
                            {
                                ValueType = streamType,
                                SyncInterval = syncInterval,
                                Capacity = 0,
                                CodeGenIdsUsingStream = new HashSet<byte>()
                            };
                        }

                        streams[key].Capacity++;
                        streams[key].CodeGenIdsUsingStream.Add(snap.codeGenerationId);
                    }
                }
            }

            // Apply safety margin to capacities AND read telemetry hints
            foreach (var stream in streams.Values)
            {
                int baselineCapacity = (int)(stream.Capacity * CAPACITY_SAFETY_MARGIN);

                // Read telemetry peak from previous sessions (Editor only)
                string streamTypeName = GetStreamTypeName(stream.ValueType);
                int hz = Mathf.RoundToInt(1f / stream.SyncInterval);
                string telemetryKey = $"GONet_SoA_Peak_{streamTypeName}_{hz}Hz";
                int telemetryPeak = UnityEditor.EditorPrefs.GetInt(telemetryKey, 0);

                if (telemetryPeak > 0)
                {
                    // Use max(baseline, telemetry × 1.3 headroom)
                    int telemetryCapacity = Mathf.CeilToInt(telemetryPeak * 1.3f);
                    stream.Capacity = Mathf.Max(baselineCapacity, telemetryCapacity);
                    GONetLog.Info($"  [Telemetry] {streamTypeName} @ {hz}Hz: baseline={baselineCapacity}, peak={telemetryPeak}, final={stream.Capacity}");
                }
                else
                {
                    stream.Capacity = baselineCapacity;
                }
            }

            GONetLog.Info($"GONet v2: Discovered {streams.Count} unique streams:");
            foreach (var kvp in streams.OrderBy(x => x.Key.Item2))
            {
                var streamType = GetStreamTypeName(kvp.Key.Item1);
                var hz = 1f / kvp.Key.Item2;
                GONetLog.Info($"  - {streamType} @ {hz:F1} Hz: {kvp.Value.Capacity} objects");
            }

            return streams;
        }

        /// <summary>
        /// Normalize value type to SoA stream type.
        /// All value-blendable types get optimized by default:
        /// - Vector2 -> Vector2 (2-component stream)
        /// - Vector3 -> Vector3 (3-component stream, SIMD-friendly)
        /// - Vector4 -> Vector4 (4-component stream, SIMD-optimal)
        /// - Quaternion -> Quaternion (4-component stream, SIMD-optimal)
        /// - float/int/bool -> float (scalar stream, grouped for SIMD)
        /// </summary>
        private static Type NormalizeToStreamType(Type valueType)
        {
            // Vector types (native SIMD support)
            if (valueType == typeof(Vector2))
                return typeof(Vector2);
            if (valueType == typeof(Vector3))
                return typeof(Vector3);
            if (valueType == typeof(Vector4))
                return typeof(Vector4);
            if (valueType == typeof(Quaternion))
                return typeof(Quaternion);

            // Scalar types (will be grouped for SIMD processing)
            // bool/byte/short/int all convert to float for blending
            if (valueType == typeof(float) ||
                valueType == typeof(int) ||
                valueType == typeof(bool) ||
                valueType == typeof(byte) ||
                valueType == typeof(short) ||
                valueType == typeof(ushort) ||
                valueType == typeof(uint) ||
                valueType == typeof(double))
                return typeof(float); // Scalar stream (Burst will SIMD-vectorize these)

            // Unsupported types (custom serializers, strings, complex types)
            // These will continue using v1 system
            return null;
        }

        /// <summary>
        /// Get sync interval from member attribute (from profile).
        /// </summary>
        private static float GetSyncInterval(GONetParticipant_ComponentsWithAutoSyncMembers_SingleMember member)
        {
            // Get interval from sync attribute
            // Default: 60 Hz = 0.01667s (24 Hz in attribute default)
            var attr = member.attribute;
            if (attr != null && attr.SyncChangesEverySeconds > 0)
            {
                return attr.SyncChangesEverySeconds; // Already in seconds
            }

            // Default to 24 Hz (matching AutoMagicalSyncFrequencies._24_Hz)
            return 1f / 24f;
        }

        /// <summary>
        /// Generate the full descriptor code.
        /// </summary>
        private static string GenerateDescriptorCode(Dictionary<(Type, float), StreamInfo> streams)
        {
            var sb = new StringBuilder(10000);

            // Header
            sb.AppendLine("/* GONet (TM, serial number 88592370), Copyright (c) 2019-2025 Galore Interactive LLC - All Rights Reserved");
            sb.AppendLine(" * AUTO-GENERATED by GONet v2 Code Generator - DO NOT EDIT");
            sb.AppendLine(" * Regenerated whenever prefabs or sync profiles change");
            sb.AppendLine(" */");
            sb.AppendLine();

            // Usings
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine("using Unity.Collections;");
            sb.AppendLine("using GONet.Core;");
            sb.AppendLine();

            // Namespace
            sb.AppendLine("namespace GONet.Generation");
            sb.AppendLine("{");

            // Class declaration with multi-rate explanation
            sb.AppendLine("    // ===================================================================");
            sb.AppendLine("    // GONet v2 SoA Global Descriptor – AUTO-GENERATED – DO NOT EDIT");
            sb.AppendLine("    // ===================================================================");
            sb.AppendLine("    // Streams below are discovered automatically from all sync profiles in the project.");
            sb.AppendLine("    // Current streams reflect actual usage:");
            sb.AppendLine("    //   • All Transform sync currently uses 24 Hz (1/24 ≈ 0.041667s)");
            sb.AppendLine("    //   • Add a prefab with different Hz → regenerate → new streams appear automatically");
            sb.AppendLine("    // This is BY DESIGN and fully supports arbitrary multi-rate blending.");
            sb.AppendLine("    // ===================================================================");
            sb.AppendLine();
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// GONet v2: SoA descriptor with exact stream capacities.");
            sb.AppendLine("    /// This file is auto-generated based on prefab analysis.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class GONet_SoA_Descriptor");
            sb.AppendLine("    {");

            // Generate stream constants
            GenerateStreamConstants(sb, streams);

            sb.AppendLine();

            // Generate per-CodeGenId mapping
            GenerateCodeGenIdMapping(sb, streams);

            sb.AppendLine();

            // Generate CreateSoA() method
            GenerateCreateSoAMethod(sb, streams);

            // Close class and namespace
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generate stream capacity and interval constants.
        /// </summary>
        private static void GenerateStreamConstants(StringBuilder sb, Dictionary<(Type, float), StreamInfo> streams)
        {
            sb.AppendLine("        // ===== DISCOVERED STREAMS =====");
            sb.AppendLine("        // Auto-generated from prefab + profile analysis");
            sb.AppendLine();

            int streamIndex = 0;
            foreach (var kvp in streams.OrderBy(x => x.Key.Item2))
            {
                var streamType = GetStreamTypeName(kvp.Key.Item1);
                var hz = 1f / kvp.Key.Item2;
                var streamName = $"{streamType}_{hz:F0}HZ";

                sb.AppendLine($"        // Stream {streamIndex + 1}: {streamType} @ {hz:F1} Hz");
                sb.AppendLine($"        public const int CAPACITY_{streamName} = {kvp.Value.Capacity};");
                sb.AppendLine($"        public const float INTERVAL_{streamName} = {kvp.Key.Item2:F6}f; // 1/{hz:F1}");
                sb.AppendLine();

                streamIndex++;
            }
        }

        /// <summary>
        /// Generate per-CodeGenId stream participation mapping.
        /// </summary>
        private static void GenerateCodeGenIdMapping(StringBuilder sb, Dictionary<(Type, float), StreamInfo> streams)
        {
            sb.AppendLine("        // ===== PER-CODEGEN-ID STREAM MAPPING =====");
            sb.AppendLine("        // Maps each CodeGenId to which streams it participates in");
            sb.AppendLine();

            // Collect all CodeGenIds
            var allCodeGenIds = new HashSet<byte>();
            foreach (var stream in streams.Values)
            {
                foreach (var id in stream.CodeGenIdsUsingStream)
                {
                    allCodeGenIds.Add(id);
                }
            }

            sb.AppendLine("        public static readonly Dictionary<byte, StreamParticipation> CodeGenIdToStreams = new Dictionary<byte, StreamParticipation>");
            sb.AppendLine("        {");

            foreach (var codeGenId in allCodeGenIds.OrderBy(x => x))
            {
                sb.AppendLine($"            // CodeGenId {codeGenId}");
                sb.Append($"            {{ {codeGenId}, new StreamParticipation {{ ");

                // Check which streams this CodeGenId uses
                var usesStreams = new List<string>();
                foreach (var kvp in streams.OrderBy(x => x.Key.Item2))
                {
                    if (kvp.Value.CodeGenIdsUsingStream.Contains(codeGenId))
                    {
                        var streamType = GetStreamTypeName(kvp.Key.Item1);
                        var hz = 1f / kvp.Key.Item2;
                        usesStreams.Add($"{streamType}_{hz:F0}Hz = true");
                    }
                }

                sb.Append(string.Join(", ", usesStreams));
                sb.AppendLine(" }},");
            }

            sb.AppendLine("        };");
            sb.AppendLine();

            // StreamParticipation struct
            sb.AppendLine("        public struct StreamParticipation");
            sb.AppendLine("        {");

            foreach (var kvp in streams.OrderBy(x => x.Key.Item2))
            {
                var streamType = GetStreamTypeName(kvp.Key.Item1);
                var hz = 1f / kvp.Key.Item2;
                sb.AppendLine($"            public bool {streamType}_{hz:F0}Hz;");
            }

            sb.AppendLine("        }");
        }

        /// <summary>
        /// Generate CreateSoA() initialization method.
        /// Creates dynamic stream arrays instead of hardcoded fields (Hz-agnostic architecture).
        /// </summary>
        private static void GenerateCreateSoAMethod(StringBuilder sb, Dictionary<(Type, float), StreamInfo> streams)
        {
            sb.AppendLine("        // ===== INITIALIZATION =====");
            sb.AppendLine("        public static NonAuthorityBlendingSoA_Final CreateSoA()");
            sb.AppendLine("        {");
            sb.AppendLine("            var soa = new NonAuthorityBlendingSoA_Final();");
            sb.AppendLine();

            // Group streams by type
            var vector3Streams = streams.Where(x => x.Key.Item1 == typeof(Vector3)).OrderBy(x => x.Key.Item2).ToList();
            var quaternionStreams = streams.Where(x => x.Key.Item1 == typeof(Quaternion)).OrderBy(x => x.Key.Item2).ToList();
            var scalarStreams = streams.Where(x => x.Key.Item1 == typeof(float)).OrderBy(x => x.Key.Item2).ToList();

            // Find max capacities for shadow buffers
            int maxPositionCapacity = vector3Streams.Any() ? vector3Streams.Max(x => x.Value.Capacity) : 0;
            int maxRotationCapacity = quaternionStreams.Any() ? quaternionStreams.Max(x => x.Value.Capacity) : 0;

            sb.AppendLine($"            // Initialize shadow buffers");
            sb.AppendLine($"            soa.InitializeShadowBuffers({maxPositionCapacity}, {maxRotationCapacity});");
            sb.AppendLine();

            // Generate Vector3 (position) streams
            if (vector3Streams.Any())
            {
                sb.AppendLine($"            // ===== VECTOR3 STREAMS (Positions) =====");
                sb.AppendLine($"            soa.positionStreams = new ValueStream_Position[{vector3Streams.Count}]; // Managed array for ref access");
                sb.AppendLine($"            soa.positionStreamInfos = new NativeArray<SoA_StreamInfo>({vector3Streams.Count}, Allocator.Persistent);");
                sb.AppendLine();

                for (int i = 0; i < vector3Streams.Count; i++)
                {
                    var stream = vector3Streams[i];
                    float interval = stream.Key.Item2;
                    int hz = Mathf.RoundToInt(1f / interval);
                    string capacityConst = GetCapacityConstName(stream.Key.Item1, interval);
                    string intervalConst = GetIntervalConstName(stream.Key.Item1, interval);

                    sb.AppendLine($"            // Vector3 @ {hz} Hz");
                    sb.AppendLine($"            var posStream{i} = new ValueStream_Position();");
                    sb.AppendLine($"            posStream{i}.Initialize({capacityConst});");
                    sb.AppendLine($"            soa.positionStreams[{i}] = posStream{i};");
                    sb.AppendLine($"            soa.positionStreamInfos[{i}] = new SoA_StreamInfo");
                    sb.AppendLine($"            {{");
                    sb.AppendLine($"                streamType = SoA_StreamType.VECTOR3,");
                    sb.AppendLine($"                updateInterval = {intervalConst},");
                    sb.AppendLine($"                capacity = {capacityConst},");
                    sb.AppendLine($"                nextUpdateTime = 0.0,");
                    sb.AppendLine($"                streamIndex = {i}");
                    sb.AppendLine($"            }};");
                    sb.AppendLine();
                }
            }

            // Generate Quaternion (rotation) streams
            if (quaternionStreams.Any())
            {
                sb.AppendLine($"            // ===== QUATERNION STREAMS (Rotations) =====");
                sb.AppendLine($"            soa.rotationStreams = new ValueStream_Rotation[{quaternionStreams.Count}]; // Managed array for ref access");
                sb.AppendLine($"            soa.rotationStreamInfos = new NativeArray<SoA_StreamInfo>({quaternionStreams.Count}, Allocator.Persistent);");
                sb.AppendLine();

                for (int i = 0; i < quaternionStreams.Count; i++)
                {
                    var stream = quaternionStreams[i];
                    float interval = stream.Key.Item2;
                    int hz = Mathf.RoundToInt(1f / interval);
                    string capacityConst = GetCapacityConstName(stream.Key.Item1, interval);
                    string intervalConst = GetIntervalConstName(stream.Key.Item1, interval);

                    sb.AppendLine($"            // Quaternion @ {hz} Hz");
                    sb.AppendLine($"            var rotStream{i} = new ValueStream_Rotation();");
                    sb.AppendLine($"            rotStream{i}.Initialize({capacityConst});");
                    sb.AppendLine($"            soa.rotationStreams[{i}] = rotStream{i};");
                    sb.AppendLine($"            soa.rotationStreamInfos[{i}] = new SoA_StreamInfo");
                    sb.AppendLine($"            {{");
                    sb.AppendLine($"                streamType = SoA_StreamType.QUATERNION,");
                    sb.AppendLine($"                updateInterval = {intervalConst},");
                    sb.AppendLine($"                capacity = {capacityConst},");
                    sb.AppendLine($"                nextUpdateTime = 0.0,");
                    sb.AppendLine($"                streamIndex = {i}");
                    sb.AppendLine($"            }};");
                    sb.AppendLine();
                }
            }

            // Generate Scalar streams
            if (scalarStreams.Any())
            {
                sb.AppendLine($"            // ===== SCALAR STREAMS (Custom fields) =====");
                sb.AppendLine($"            soa.scalarStreams = new ValueStream_Scalars[{scalarStreams.Count}]; // Managed array for ref access");
                sb.AppendLine($"            soa.scalarStreamInfos = new NativeArray<SoA_StreamInfo>({scalarStreams.Count}, Allocator.Persistent);");
                sb.AppendLine();

                for (int i = 0; i < scalarStreams.Count; i++)
                {
                    var stream = scalarStreams[i];
                    float interval = stream.Key.Item2;
                    int hz = Mathf.RoundToInt(1f / interval);
                    string capacityConst = GetCapacityConstName(stream.Key.Item1, interval);
                    string intervalConst = GetIntervalConstName(stream.Key.Item1, interval);

                    sb.AppendLine($"            // Scalar @ {hz} Hz");
                    sb.AppendLine($"            var scalarStream{i} = new ValueStream_Scalars();");
                    sb.AppendLine($"            scalarStream{i}.Initialize({capacityConst});");
                    sb.AppendLine($"            soa.scalarStreams[{i}] = scalarStream{i};");
                    sb.AppendLine($"            soa.scalarStreamInfos[{i}] = new SoA_StreamInfo");
                    sb.AppendLine($"            {{");
                    sb.AppendLine($"                streamType = SoA_StreamType.SCALAR,");
                    sb.AppendLine($"                updateInterval = {intervalConst},");
                    sb.AppendLine($"                capacity = {capacityConst},");
                    sb.AppendLine($"                nextUpdateTime = 0.0,");
                    sb.AppendLine($"                streamIndex = {i}");
                    sb.AppendLine($"            }};");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("            return soa;");
            sb.AppendLine("        }");
        }

        /// <summary>
        /// Get stream type name for code generation.
        /// Maps types to semantic names for generated constants.
        /// </summary>
        private static string GetStreamTypeName(Type type)
        {
            if (type == typeof(Vector2))
                return "VECTOR2";
            if (type == typeof(Vector3))
                return "VECTOR3";
            if (type == typeof(Vector4))
                return "VECTOR4";
            if (type == typeof(Quaternion))
                return "QUATERNION";
            if (type == typeof(float))
                return "SCALAR";

            return "UNKNOWN";
        }

        /// <summary>
        /// Get stream field name for runtime usage (e.g., "vector3_24hz").
        /// </summary>
        private static string GetStreamFieldName(Type type, float interval)
        {
            string typeName = GetStreamTypeName(type).ToLower();
            int hz = Mathf.RoundToInt(1f / interval);
            return $"{typeName}_{hz}hz";
        }

        /// <summary>
        /// Get capacity constant name for code generation (e.g., "CAPACITY_VECTOR3_24HZ").
        /// </summary>
        private static string GetCapacityConstName(Type type, float interval)
        {
            string typeName = GetStreamTypeName(type);
            int hz = Mathf.RoundToInt(1f / interval);
            return $"CAPACITY_{typeName}_{hz}HZ";
        }

        /// <summary>
        /// Get interval constant name for code generation (e.g., "INTERVAL_VECTOR3_24HZ").
        /// </summary>
        private static string GetIntervalConstName(Type type, float interval)
        {
            string typeName = GetStreamTypeName(type);
            int hz = Mathf.RoundToInt(1f / interval);
            return $"INTERVAL_{typeName}_{hz}HZ";
        }

        /// <summary>
        /// Stream metadata collected during discovery.
        /// </summary>
        private class StreamInfo
        {
            public Type ValueType;
            public float SyncInterval;
            public int Capacity;
            public HashSet<byte> CodeGenIdsUsingStream;
        }
    }
}
