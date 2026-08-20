using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

namespace DynamicBoneDistributionEditor
{
    /// <summary>
    /// Writes a lossless runtime snapshot for the currently opened DBDE target.
    /// The file is intentionally independent from DBDE's MessagePack save data:
    /// it is meant for Blender/import tooling and runtime solver comparison.
    /// </summary>
    internal static class DBDERuntimeExporter
    {
        private const string FormatVersion = "DBDE.RuntimeSnapshot.v1";
        private static readonly MethodInfo DynamicBoneInitTransformsMethod = typeof(DynamicBone).GetMethod(
            "InitTransforms", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo DynamicBoneApplyParticlesMethod = typeof(DynamicBone).GetMethod(
            "ApplyParticlesToTransforms", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DynamicBoneParticlesField = typeof(DynamicBone).GetField(
            "m_Particles", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DynamicBoneBoneTotalLengthField = typeof(DynamicBone).GetField(
            "m_BoneTotalLength", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DynamicBoneWeightField = typeof(DynamicBone).GetField(
            "m_Weight", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo DynamicBoneLocalGravityField = typeof(DynamicBone).GetField(
            "m_LocalGravity", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Dictionary<Type, DynamicBoneParticleFields> DynamicBoneParticleFieldCache =
            new Dictionary<Type, DynamicBoneParticleFields>();

        internal static string Export(IList<DBDEDynamicBoneEdit> edits, string title)
        {
            var dynamicBones = new List<DynamicBone>();
            var dynamicBonesVer02 = new List<DynamicBone_Ver02>();
            var groups = new List<DBDEGroupSnapshot>();
            var colliders = new List<DynamicBoneCollider>();

            int editCount = edits == null ? 0 : edits.Count;
            for (int groupIndex = 0; groupIndex < editCount; groupIndex++)
            {
                DBDEDynamicBoneEdit edit = edits[groupIndex];
                if (edit == null) continue;

                var group = new DBDEGroupSnapshot
                {
                    Index = groupIndex,
                    Name = SafeGetButtonName(edit),
                    PrimaryRoot = GetRootName(SafeGetPrimary(edit)),
                    DynamicBoneIndices = new List<int>()
                };

                List<DynamicBone> editBones = SafeGetDynamicBones(edit);
                for (int i = 0; i < editBones.Count; i++)
                {
                    DynamicBone dynamicBone = editBones[i];
                    if (dynamicBone == null) continue;

                    int index = dynamicBones.IndexOf(dynamicBone);
                    if (index < 0)
                    {
                        index = dynamicBones.Count;
                        dynamicBones.Add(dynamicBone);
                    }
                    if (!group.DynamicBoneIndices.Contains(index))
                        group.DynamicBoneIndices.Add(index);

                    AddColliders(colliders, dynamicBone.m_Colliders);
                }
                groups.Add(group);
            }

            int registeredDynamicBoneCount = dynamicBones.Count;
            var sceneOnlyGroup = new DBDEGroupSnapshot
            {
                Index = groups.Count,
                Name = "Scene Scan (not registered by DBDE)",
                PrimaryRoot = string.Empty,
                DynamicBoneIndices = new List<int>()
            };

            List<DynamicBone> sceneDynamicBones = FindLoadedSceneComponents<DynamicBone>();
            for (int i = 0; i < sceneDynamicBones.Count; i++)
            {
                DynamicBone dynamicBone = sceneDynamicBones[i];
                int index = dynamicBones.IndexOf(dynamicBone);
                if (index < 0)
                {
                    index = dynamicBones.Count;
                    dynamicBones.Add(dynamicBone);
                    sceneOnlyGroup.DynamicBoneIndices.Add(index);
                }
                AddColliders(colliders, dynamicBone.m_Colliders);
            }

            if (sceneOnlyGroup.DynamicBoneIndices.Count > 0)
                groups.Add(sceneOnlyGroup);

            dynamicBonesVer02.AddRange(FindLoadedSceneComponents<DynamicBone_Ver02>());
            for (int i = 0; i < dynamicBonesVer02.Count; i++)
                AddColliders(colliders, dynamicBonesVer02[i].Colliders);

            List<DynamicBoneCollider> sceneColliders = FindLoadedSceneComponents<DynamicBoneCollider>();
            for (int i = 0; i < sceneColliders.Count; i++)
            {
                DynamicBoneCollider collider = sceneColliders[i];
                if (!colliders.Contains(collider)) colliders.Add(collider);
            }

            if (dynamicBones.Count == 0 && dynamicBonesVer02.Count == 0)
                throw new InvalidOperationException("No live DynamicBone or DynamicBone_Ver02 components were found in the loaded scene.");

            string directory = Path.Combine(Paths.ConfigPath, "DBDE_RuntimeSnapshots");
            Directory.CreateDirectory(directory);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string filePath = Path.Combine(directory, "DBDE_RuntimeSnapshot_" + stamp + ".json");

            var writer = new RuntimeJsonWriter();
            writer.BeginObject();
            writer.PropertyString("formatVersion", FormatVersion);
            writer.PropertyString("pluginVersion", DBDE.Version);
            writer.PropertyString("title", title ?? string.Empty);
            writer.PropertyString("capturedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            writer.PropertyString("gameVersion", Application.version);
            writer.PropertyString("unityVersion", Application.unityVersion);
            writer.PropertyString("activeScene", Application.loadedLevelName);
            writer.PropertyInt("frameCount", Time.frameCount);
            writer.PropertyFloat("time", Time.time);
            writer.PropertyFloat("deltaTime", Time.deltaTime);
            writer.PropertyFloat("fixedDeltaTime", Time.fixedDeltaTime);
            writer.PropertyFloat("timeScale", Time.timeScale);
            writer.PropertyString("dataPath", Application.dataPath);
            writer.PropertyInt("dbdeRegisteredDynamicBoneCount", registeredDynamicBoneCount);
            writer.PropertyInt("loadedSceneDynamicBoneCount", dynamicBones.Count);
            writer.PropertyInt("loadedSceneDynamicBoneVer02Count", dynamicBonesVer02.Count);
            writer.PropertyInt("loadedSceneColliderCount", colliders.Count);

            writer.BeginArrayProperty("groups");
            for (int i = 0; i < groups.Count; i++)
                WriteGroup(writer, groups[i]);
            writer.EndArray();

            writer.BeginArrayProperty("dynamicBones");
            for (int i = 0; i < dynamicBones.Count; i++)
                WriteDynamicBone(writer, dynamicBones[i], i);
            writer.EndArray();

            writer.BeginArrayProperty("dynamicBonesVer02");
            for (int i = 0; i < dynamicBonesVer02.Count; i++)
                WriteDynamicBoneVer02(writer, dynamicBonesVer02[i], i);
            writer.EndArray();

            writer.BeginArrayProperty("colliders");
            for (int i = 0; i < colliders.Count; i++)
                WriteCollider(writer, colliders[i], i);
            writer.EndArray();
            writer.EndObject();

            File.WriteAllText(filePath, writer.ToString(), new UTF8Encoding(false));
            DBDE.Logger.LogInfo("Runtime JSON exported: " + filePath + " (" + dynamicBones.Count + " DynamicBone, " + dynamicBonesVer02.Count + " DynamicBone_Ver02, " + colliders.Count + " Collider)");
            return filePath;
        }

        internal static string ExportLowerBodySkirt(string title)
        {
            List<DynamicBone> sceneDynamicBones = FindLoadedSceneComponents<DynamicBone>();
            var dynamicBones = new List<DynamicBone>();
            var colliders = new List<DynamicBoneCollider>();
            var scannedRoots = new List<string>();

            for (int i = 0; i < sceneDynamicBones.Count; i++)
            {
                DynamicBone dynamicBone = sceneDynamicBones[i];
                if (dynamicBone == null) continue;

                string rootName = GetRootName(dynamicBone);
                if (!string.IsNullOrEmpty(rootName) && scannedRoots.Count < 64 && !scannedRoots.Contains(rootName))
                    scannedRoots.Add(rootName);

                if (!IsLowerBodySkirtDynamicBone(dynamicBone)) continue;
                dynamicBones.Add(dynamicBone);
                AddColliders(colliders, dynamicBone.m_Colliders);
            }

            if (dynamicBones.Count == 0)
            {
                throw new InvalidOperationException(
                    "No gm_top lower-body skirt DynamicBone was found. " +
                    "Open DBDE for the character wearing gm_top, press Rescan ALL, and try again. " +
                    "Scanned roots: " + string.Join(", ", scannedRoots.ToArray()));
            }

            string directory = Path.Combine(Paths.ConfigPath, "DBDE_RuntimeSnapshots");
            Directory.CreateDirectory(directory);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string filePath = Path.Combine(directory, "DBDE_LowerBodySkirtSnapshot_" + stamp + ".json");

            var group = new DBDEGroupSnapshot
            {
                Index = 0,
                Name = "gm_top Lower Body Skirt",
                PrimaryRoot = GetRootName(dynamicBones[0]),
                DynamicBoneIndices = new List<int>()
            };
            for (int i = 0; i < dynamicBones.Count; i++) group.DynamicBoneIndices.Add(i);

            var writer = new RuntimeJsonWriter();
            writer.BeginObject();
            writer.PropertyString("formatVersion", FormatVersion);
            writer.PropertyString("pluginVersion", DBDE.Version);
            writer.PropertyString("exportScope", "gm_top.lowerBodySkirt");
            writer.PropertyString("filterRule", "Dress_* particle plus gm_top/lower-body root hierarchy");
            writer.PropertyString("title", title ?? string.Empty);
            writer.PropertyString("capturedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            writer.PropertyString("gameVersion", Application.version);
            writer.PropertyString("unityVersion", Application.unityVersion);
            writer.PropertyString("activeScene", Application.loadedLevelName);
            writer.PropertyInt("frameCount", Time.frameCount);
            writer.PropertyFloat("time", Time.time);
            writer.PropertyFloat("deltaTime", Time.deltaTime);
            writer.PropertyFloat("fixedDeltaTime", Time.fixedDeltaTime);
            writer.PropertyFloat("timeScale", Time.timeScale);
            writer.PropertyString("dataPath", Application.dataPath);
            writer.PropertyInt("candidateSceneDynamicBoneCount", sceneDynamicBones.Count);
            writer.PropertyInt("dbdeRegisteredDynamicBoneCount", dynamicBones.Count);
            writer.PropertyInt("loadedSceneDynamicBoneCount", dynamicBones.Count);
            writer.PropertyInt("loadedSceneDynamicBoneVer02Count", 0);
            writer.PropertyInt("loadedSceneColliderCount", colliders.Count);
            // A particle snapshot alone cannot identify the exact source pose.
            // Save the Animator state that drives this gm_top chain so Blender
            // can align an imported Action to the same game frame.
            WriteAnimationContext(writer, dynamicBones[0]);

            writer.BeginArrayProperty("groups");
            WriteGroup(writer, group);
            writer.EndArray();

            writer.BeginArrayProperty("dynamicBones");
            for (int i = 0; i < dynamicBones.Count; i++)
                WriteDynamicBone(writer, dynamicBones[i], i);
            writer.EndArray();

            writer.BeginArrayProperty("dynamicBonesVer02");
            writer.EndArray();

            writer.BeginArrayProperty("colliders");
            for (int i = 0; i < colliders.Count; i++)
                WriteCollider(writer, colliders[i], i);
            writer.EndArray();
            writer.EndObject();

            File.WriteAllText(filePath, writer.ToString(), new UTF8Encoding(false));
            DBDE.Logger.LogInfo(
                "Lower-body skirt runtime JSON exported: " + filePath +
                " (" + dynamicBones.Count + " DynamicBone, " + colliders.Count + " referenced Collider)");
            return filePath;
        }

        private static bool IsLowerBodySkirtDynamicBone(DynamicBone dynamicBone)
        {
            if (dynamicBone == null) return false;

            string componentPath = GetPath(dynamicBone.transform);
            string rootName = GetRootName(dynamicBone);
            string rootPath = GetPath(dynamicBone.m_Root);
            bool hasDressTransform = HasDressTransform(dynamicBone);
            if (!hasDressTransform) return false;

            bool belongsToGmTop = ContainsIgnoreCase(componentPath, "gm_top") ||
                                  ContainsIgnoreCase(rootPath, "gm_top");
            bool hasLowerBodyRoot = string.Equals(rootName, "\u4e0b\u534a\u8eab", StringComparison.OrdinalIgnoreCase) ||
                                    StartsWithIgnoreCase(rootName, "Dress_") ||
                                    ContainsIgnoreCase(rootName, "lowerbody") ||
                                    ContainsIgnoreCase(rootName, "lower_body") ||
                                    ContainsIgnoreCase(rootName, "lower body") ||
                                    ContainsIgnoreCase(rootName, "skirt");
            return belongsToGmTop || hasLowerBodyRoot;
        }

        private static bool HasDressTransform(DynamicBone dynamicBone)
        {
            IList particles = GetRuntimeParticles(dynamicBone);
            if (particles != null)
            {
                for (int i = 0; i < particles.Count; i++)
                {
                    Transform transform = GetRuntimeParticleTransform(particles[i]);
                    if (transform != null && StartsWithIgnoreCase(transform.name, "Dress_"))
                        return true;
                }
            }

            if (dynamicBone.m_Root == null) return false;
            Transform[] transforms = dynamicBone.m_Root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && StartsWithIgnoreCase(transforms[i].name, "Dress_"))
                    return true;
            }
            return false;
        }

        private static bool ContainsIgnoreCase(string value, string token)
        {
            return !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(token) &&
                   value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool StartsWithIgnoreCase(string value, string token)
        {
            return !string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(token) &&
                   value.StartsWith(token, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteAnimationContext(RuntimeJsonWriter writer, DynamicBone dynamicBone)
        {
            writer.BeginObjectProperty("animationContext");
            Animator animator = FindAnimator(dynamicBone);
            writer.PropertyBool("found", animator != null);
            if (animator == null)
            {
                writer.EndObjectProperty();
                return;
            }

            try
            {
                writer.PropertyString("animatorPath", GetPath(animator.transform));
                writer.PropertyString(
                    "controllerName",
                    animator.runtimeAnimatorController == null ? string.Empty : animator.runtimeAnimatorController.name);
                writer.PropertyFloat("speed", animator.speed);
                writer.PropertyInt("layerCount", animator.layerCount);
                writer.BeginArrayProperty("layers");
                for (int layer = 0; layer < animator.layerCount; layer++)
                {
                    AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
                    writer.BeginObjectElement();
                    writer.PropertyInt("index", layer);
                    writer.PropertyFloat("weight", animator.GetLayerWeight(layer));
                    writer.PropertyInt("shortNameHash", state.shortNameHash);
                    writer.PropertyInt("fullPathHash", state.fullPathHash);
                    writer.PropertyFloat("normalizedTime", state.normalizedTime);
                    writer.PropertyFloat("normalizedTimeLoop", Mathf.Repeat(state.normalizedTime, 1f));
                    writer.PropertyFloat("stateLength", state.length);
                    writer.PropertyFloat("stateSpeed", state.speed);
                    writer.PropertyFloat("stateSpeedMultiplier", state.speedMultiplier);
                    writer.PropertyBool("loop", state.loop);
                    writer.BeginArrayProperty("clips");
                    AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(layer);
                    for (int i = 0; i < clips.Length; i++)
                    {
                        AnimatorClipInfo clipInfo = clips[i];
                        AnimationClip clip = clipInfo.clip;
                        writer.BeginObjectElement();
                        writer.PropertyString("name", clip == null ? string.Empty : clip.name);
                        writer.PropertyFloat("length", clip == null ? 0f : clip.length);
                        writer.PropertyFloat("frameRate", clip == null ? 0f : clip.frameRate);
                        writer.PropertyFloat("weight", clipInfo.weight);
                        writer.EndObject();
                    }
                    writer.EndArray();
                    writer.EndObject();
                }
                writer.EndArray();
            }
            catch (Exception ex)
            {
                // Keep the DynamicBone export usable even for a transitional
                // Animator state that Unity refuses to inspect this frame.
                writer.PropertyString("readError", ex.Message);
            }
            writer.EndObjectProperty();
        }

        private static Animator FindAnimator(DynamicBone dynamicBone)
        {
            Transform current = dynamicBone == null ? null : dynamicBone.transform;
            while (current != null)
            {
                Animator animator = current.GetComponent<Animator>();
                if (animator != null) return animator;
                current = current.parent;
            }

            current = dynamicBone == null ? null : dynamicBone.m_Root;
            while (current != null)
            {
                Animator animator = current.GetComponent<Animator>();
                if (animator != null) return animator;
                current = current.parent;
            }
            return null;
        }

        private static List<T> FindLoadedSceneComponents<T>() where T : Component
        {
            var result = new List<T>();
            T[] components = Resources.FindObjectsOfTypeAll<T>();
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                if (component == null || component.gameObject == null) continue;
                if (!component.gameObject.scene.IsValid()) continue;
                result.Add(component);
            }
            return result;
        }

        private static void WriteGroup(RuntimeJsonWriter writer, DBDEGroupSnapshot group)
        {
            writer.BeginObjectElement();
            writer.PropertyInt("index", group.Index);
            writer.PropertyString("name", group.Name);
            writer.PropertyString("primaryRoot", group.PrimaryRoot);
            writer.BeginArrayProperty("dynamicBoneIndices");
            for (int i = 0; i < group.DynamicBoneIndices.Count; i++)
                writer.IntElement(group.DynamicBoneIndices[i]);
            writer.EndArray();
            writer.EndObject();
        }

        private static void WriteDynamicBone(RuntimeJsonWriter writer, DynamicBone dynamicBone, int index)
        {
            Dictionary<int, StaticReferenceTransform> staticReferences = CaptureStaticReferenceTransforms(dynamicBone);
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("componentType", dynamicBone.GetType().FullName);
            writer.PropertyString("componentName", dynamicBone.name);
            writer.PropertyString("componentPath", GetPath(dynamicBone.transform));
            writer.PropertyBool("enabled", dynamicBone.enabled);
            writer.PropertyTransform("componentTransform", dynamicBone.transform);
            writer.PropertyString("rootName", GetRootName(dynamicBone));
            writer.PropertyString("rootPath", GetPath(dynamicBone.m_Root));
            writer.PropertyTransform("rootTransform", dynamicBone.m_Root);
            writer.PropertyFloat("updateRate", dynamicBone.m_UpdateRate);
            writer.PropertyFloat("damping", dynamicBone.m_Damping);
            writer.PropertyFloat("elasticity", dynamicBone.m_Elasticity);
            writer.PropertyFloat("inertia", dynamicBone.m_Inert);
            writer.PropertyFloat("radius", dynamicBone.m_Radius);
            writer.PropertyFloat("stiffness", dynamicBone.m_Stiffness);
            writer.PropertyFloat("endLength", dynamicBone.m_EndLength);
            writer.PropertyVector3("endOffset", dynamicBone.m_EndOffset);
            writer.PropertyVector3("gravity", dynamicBone.m_Gravity);
            writer.PropertyVector3("force", dynamicBone.m_Force);
            writer.PropertyInt("freezeAxis", (int)dynamicBone.m_FreezeAxis);
            writer.PropertyString("freezeAxisName", dynamicBone.m_FreezeAxis.ToString());
            writer.PropertyBool("distantDisable", dynamicBone.m_DistantDisable);
            writer.PropertyString("referenceObjectPath", GetPath(dynamicBone.m_ReferenceObject));
            writer.PropertyFloat("distanceToObject", dynamicBone.m_DistanceToObject);
            writer.PropertyStringList("exclusions", dynamicBone.m_Exclusions, dynamicBone.m_Root);
            writer.PropertyStringList("notRolls", dynamicBone.m_notRolls, dynamicBone.m_Root);
            writer.PropertyCurve("dampingDistribution", dynamicBone.m_DampingDistrib);
            writer.PropertyCurve("elasticityDistribution", dynamicBone.m_ElasticityDistrib);
            writer.PropertyCurve("inertiaDistribution", dynamicBone.m_InertDistrib);
            writer.PropertyCurve("radiusDistribution", dynamicBone.m_RadiusDistrib);
            writer.PropertyCurve("stiffnessDistribution", dynamicBone.m_StiffnessDistrib);

            writer.BeginArrayProperty("colliderPaths");
            if (dynamicBone.m_Colliders != null)
            {
                for (int i = 0; i < dynamicBone.m_Colliders.Count; i++)
                    writer.StringElement(GetPath(dynamicBone.m_Colliders[i] == null ? null : dynamicBone.m_Colliders[i].transform));
            }
            writer.EndArray();

            writer.PropertyFloat("boneTotalLength", GetDynamicBoneRuntimeValue(dynamicBone, DynamicBoneBoneTotalLengthField, 0f));
            writer.PropertyFloat("weight", GetDynamicBoneRuntimeValue(dynamicBone, DynamicBoneWeightField, 0f));
            writer.PropertyVector3("localGravity", GetDynamicBoneRuntimeValue(dynamicBone, DynamicBoneLocalGravityField, Vector3.zero));

            writer.BeginArrayProperty("particles");
            IList particles = GetRuntimeParticles(dynamicBone);
            if (particles != null)
            {
                for (int i = 0; i < particles.Count; i++)
                {
                    object particle = particles[i];
                    DynamicBoneParticleFields fields = GetRuntimeParticleFields(particle);
                    if (fields == null) continue;
                    writer.BeginObjectElement();
                    writer.PropertyInt("index", i);
                    writer.PropertyString("transformPath", GetPath(GetRuntimeParticleValue(particle, fields.Transform, (Transform)null)));
                    writer.PropertyInt("parentIndex", GetRuntimeParticleValue(particle, fields.ParentIndex, -1));
                    writer.PropertyFloat("boneLength", GetRuntimeParticleValue(particle, fields.BoneLength, 0f));
                    writer.PropertyFloat("damping", GetRuntimeParticleValue(particle, fields.Damping, 0f));
                    writer.PropertyFloat("elasticity", GetRuntimeParticleValue(particle, fields.Elasticity, 0f));
                    writer.PropertyFloat("stiffness", GetRuntimeParticleValue(particle, fields.Stiffness, 0f));
                    writer.PropertyFloat("inertia", GetRuntimeParticleValue(particle, fields.Inertia, 0f));
                    writer.PropertyFloat("radius", GetRuntimeParticleValue(particle, fields.Radius, 0f));
                    writer.PropertyVector3("position", GetRuntimeParticleValue(particle, fields.Position, Vector3.zero));
                    writer.PropertyVector3("previousPosition", GetRuntimeParticleValue(particle, fields.PreviousPosition, Vector3.zero));
                    writer.PropertyVector3("endOffset", GetRuntimeParticleValue(particle, fields.EndOffset, Vector3.zero));
                    writer.PropertyVector3("initialLocalPosition", GetRuntimeParticleValue(particle, fields.InitialLocalPosition, Vector3.zero));
                    writer.PropertyQuaternion("initialLocalRotation", GetRuntimeParticleValue(particle, fields.InitialLocalRotation, Quaternion.identity));
                    StaticReferenceTransform staticReference;
                    if (staticReferences.TryGetValue(i, out staticReference))
                        writer.PropertyStaticReferenceTransform("staticReferenceTransform", staticReference);
                    writer.EndObject();
                }
            }
            writer.EndArray();
            writer.EndObject();
        }

        private static Dictionary<int, StaticReferenceTransform> CaptureStaticReferenceTransforms(DynamicBone dynamicBone)
        {
            var references = new Dictionary<int, StaticReferenceTransform>();
            IList particles = GetRuntimeParticles(dynamicBone);
            if (dynamicBone == null || particles == null)
                return references;

            try
            {
                // Match DynamicBone.Update(): capture the reset local Transform
                // chain, not the current simulated particle pose.
                if (DynamicBoneInitTransformsMethod != null)
                    DynamicBoneInitTransformsMethod.Invoke(dynamicBone, null);

                for (int i = 0; i < particles.Count; i++)
                {
                    Transform transform = GetRuntimeParticleTransform(particles[i]);
                    if (transform == null)
                        continue;
                    references[i] = new StaticReferenceTransform(transform);
                }
            }
            catch (Exception ex)
            {
                DBDE.Logger.LogWarning("Could not capture DynamicBone static reference transforms: " + ex.Message);
            }
            finally
            {
                try
                {
                    // Restore the active physics pose without resetting the particle
                    // buffers so exporting is visually non-destructive in-game.
                    if (DynamicBoneApplyParticlesMethod != null)
                        DynamicBoneApplyParticlesMethod.Invoke(dynamicBone, null);
                }
                catch (Exception ex)
                {
                    DBDE.Logger.LogWarning("Could not restore DynamicBone pose after reference capture: " + ex.Message);
                }
            }
            return references;
        }

        private static IList GetRuntimeParticles(DynamicBone dynamicBone)
        {
            if (dynamicBone == null || DynamicBoneParticlesField == null) return null;
            try
            {
                return DynamicBoneParticlesField.GetValue(dynamicBone) as IList;
            }
            catch (Exception ex)
            {
                DBDE.Logger.LogWarning("Could not inspect DynamicBone runtime particles: " + ex.Message);
                return null;
            }
        }

        private static T GetDynamicBoneRuntimeValue<T>(DynamicBone dynamicBone, FieldInfo field, T fallback)
        {
            if (dynamicBone == null || field == null) return fallback;
            try
            {
                object value = field.GetValue(dynamicBone);
                return value is T ? (T)value : fallback;
            }
            catch (Exception ex)
            {
                DBDE.Logger.LogWarning("Could not inspect DynamicBone runtime field " + field.Name + ": " + ex.Message);
                return fallback;
            }
        }

        private static Transform GetRuntimeParticleTransform(object particle)
        {
            DynamicBoneParticleFields fields = GetRuntimeParticleFields(particle);
            return fields == null
                ? null
                : GetRuntimeParticleValue(particle, fields.Transform, (Transform)null);
        }

        private static DynamicBoneParticleFields GetRuntimeParticleFields(object particle)
        {
            if (particle == null) return null;

            Type type = particle.GetType();
            DynamicBoneParticleFields fields;
            if (!DynamicBoneParticleFieldCache.TryGetValue(type, out fields))
            {
                fields = new DynamicBoneParticleFields(type);
                DynamicBoneParticleFieldCache[type] = fields;
            }
            return fields;
        }

        private static T GetRuntimeParticleValue<T>(object particle, FieldInfo field, T fallback)
        {
            if (particle == null || field == null) return fallback;
            try
            {
                object value = field.GetValue(particle);
                return value is T ? (T)value : fallback;
            }
            catch (Exception ex)
            {
                DBDE.Logger.LogWarning("Could not inspect DynamicBone particle field " + field.Name + ": " + ex.Message);
                return fallback;
            }
        }

        private sealed class DynamicBoneParticleFields
        {
            private const BindingFlags ParticleFieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            internal readonly FieldInfo Transform;
            internal readonly FieldInfo ParentIndex;
            internal readonly FieldInfo BoneLength;
            internal readonly FieldInfo Damping;
            internal readonly FieldInfo Elasticity;
            internal readonly FieldInfo Stiffness;
            internal readonly FieldInfo Inertia;
            internal readonly FieldInfo Radius;
            internal readonly FieldInfo Position;
            internal readonly FieldInfo PreviousPosition;
            internal readonly FieldInfo EndOffset;
            internal readonly FieldInfo InitialLocalPosition;
            internal readonly FieldInfo InitialLocalRotation;

            internal DynamicBoneParticleFields(Type type)
            {
                Transform = type.GetField("m_Transform", ParticleFieldFlags);
                ParentIndex = type.GetField("m_ParentIndex", ParticleFieldFlags);
                BoneLength = type.GetField("m_BoneLength", ParticleFieldFlags);
                Damping = type.GetField("m_Damping", ParticleFieldFlags);
                Elasticity = type.GetField("m_Elasticity", ParticleFieldFlags);
                Stiffness = type.GetField("m_Stiffness", ParticleFieldFlags);
                Inertia = type.GetField("m_Inert", ParticleFieldFlags);
                Radius = type.GetField("m_Radius", ParticleFieldFlags);
                Position = type.GetField("m_Position", ParticleFieldFlags);
                PreviousPosition = type.GetField("m_PrevPosition", ParticleFieldFlags);
                EndOffset = type.GetField("m_EndOffset", ParticleFieldFlags);
                InitialLocalPosition = type.GetField("m_InitLocalPosition", ParticleFieldFlags);
                InitialLocalRotation = type.GetField("m_InitLocalRotation", ParticleFieldFlags);
            }
        }

        private static void WriteDynamicBoneVer02(RuntimeJsonWriter writer, DynamicBone_Ver02 dynamicBone, int index)
        {
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("componentType", dynamicBone.GetType().FullName);
            writer.PropertyString("componentName", dynamicBone.name);
            writer.PropertyString("componentPath", GetPath(dynamicBone.transform));
            writer.PropertyBool("enabled", dynamicBone.enabled);
            writer.PropertyString("comment", dynamicBone.Comment);
            writer.PropertyTransform("componentTransform", dynamicBone.transform);
            writer.PropertyString("rootName", dynamicBone.Root == null ? string.Empty : dynamicBone.Root.name);
            writer.PropertyString("rootPath", GetPath(dynamicBone.Root));
            writer.PropertyTransform("rootTransform", dynamicBone.Root);
            writer.PropertyFloat("updateRate", dynamicBone.UpdateRate);
            writer.PropertyFloat("reflectSpeed", dynamicBone.ReflectSpeed);
            writer.PropertyInt("heavyLoopMaxCount", dynamicBone.HeavyLoopMaxCount);
            writer.PropertyVector3("gravity", dynamicBone.Gravity);
            writer.PropertyVector3("force", dynamicBone.Force);
            writer.PropertyFloat("weight", dynamicBone.GetWeight());
            writer.PropertyInt("patternIndex", dynamicBone.PtnNo);

            writer.BeginArrayProperty("colliderPaths");
            if (dynamicBone.Colliders != null)
            {
                for (int i = 0; i < dynamicBone.Colliders.Count; i++)
                    writer.StringElement(GetPath(dynamicBone.Colliders[i] == null ? null : dynamicBone.Colliders[i].transform));
            }
            writer.EndArray();

            writer.BeginArrayProperty("bonePaths");
            if (dynamicBone.Bones != null)
            {
                for (int i = 0; i < dynamicBone.Bones.Count; i++)
                    writer.StringElement(GetPath(dynamicBone.Bones[i]));
            }
            writer.EndArray();

            writer.BeginArrayProperty("patterns");
            if (dynamicBone.Patterns != null)
            {
                for (int i = 0; i < dynamicBone.Patterns.Count; i++)
                    WriteDynamicBoneVer02Pattern(writer, dynamicBone.Patterns[i], i);
            }
            writer.EndArray();

            List<DynamicBone_Ver02.Particle> particles = SafeGetRuntimeParticles(dynamicBone);
            writer.BeginArrayProperty("runtimeParticles");
            for (int i = 0; i < particles.Count; i++)
                WriteDynamicBoneVer02RuntimeParticle(writer, particles[i], i);
            writer.EndArray();
            writer.EndObject();
        }

        private static void WriteDynamicBoneVer02Pattern(RuntimeJsonWriter writer, DynamicBone_Ver02.BonePtn pattern, int index)
        {
            if (pattern == null) return;
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("name", pattern.Name);
            writer.PropertyVector3("gravity", pattern.Gravity);
            writer.PropertyVector3("endOffset", pattern.EndOffset);
            writer.PropertyFloat("endOffsetDamping", pattern.EndOffsetDamping);
            writer.PropertyFloat("endOffsetElasticity", pattern.EndOffsetElasticity);
            writer.PropertyFloat("endOffsetStiffness", pattern.EndOffsetStiffness);
            writer.PropertyFloat("endOffsetInertia", pattern.EndOffsetInert);

            writer.BeginArrayProperty("boneParameters");
            if (pattern.Params != null)
            {
                for (int i = 0; i < pattern.Params.Count; i++)
                    WriteDynamicBoneVer02BoneParameter(writer, pattern.Params[i], i);
            }
            writer.EndArray();

            writer.BeginArrayProperty("particleParameters");
            if (pattern.ParticlePtns != null)
            {
                for (int i = 0; i < pattern.ParticlePtns.Count; i++)
                    WriteDynamicBoneVer02ParticlePattern(writer, pattern.ParticlePtns[i], i);
            }
            writer.EndArray();
            writer.EndObject();
        }

        private static void WriteDynamicBoneVer02BoneParameter(RuntimeJsonWriter writer, DynamicBone_Ver02.BoneParameter parameter, int index)
        {
            if (parameter == null) return;
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("name", parameter.Name);
            writer.PropertyString("referenceTransformPath", GetPath(parameter.RefTransform));
            writer.PropertyTransform("referenceTransform", parameter.RefTransform);
            writer.PropertyBool("isRotationCalc", parameter.IsRotationCalc);
            writer.PropertyFloat("damping", parameter.Damping);
            writer.PropertyFloat("elasticity", parameter.Elasticity);
            writer.PropertyFloat("stiffness", parameter.Stiffness);
            writer.PropertyFloat("inertia", parameter.Inert);
            writer.PropertyFloat("nextBoneLength", parameter.NextBoneLength);
            writer.PropertyFloat("radius", parameter.CollisionRadius);
            WriteDynamicBoneVer02Limits(writer, parameter.IsMoveLimit, parameter.MoveLimitMin, parameter.MoveLimitMax,
                parameter.KeepLengthLimitMin, parameter.KeepLengthLimitMax, parameter.IsCrush,
                parameter.CrushMoveAreaMin, parameter.CrushMoveAreaMax, parameter.CrushAddXYMin, parameter.CrushAddXYMax);
            writer.EndObject();
        }

        private static void WriteDynamicBoneVer02ParticlePattern(RuntimeJsonWriter writer, DynamicBone_Ver02.ParticlePtn parameter, int index)
        {
            if (parameter == null) return;
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("transformPath", GetPath(parameter.refTrans));
            writer.PropertyBool("isRotationCalc", parameter.IsRotationCalc);
            writer.PropertyFloat("damping", parameter.Damping);
            writer.PropertyFloat("elasticity", parameter.Elasticity);
            writer.PropertyFloat("stiffness", parameter.Stiffness);
            writer.PropertyFloat("inertia", parameter.Inert);
            writer.PropertyFloat("scaleNextBoneLength", parameter.ScaleNextBoneLength);
            writer.PropertyFloat("radius", parameter.Radius);
            writer.PropertyVector3("endOffset", parameter.EndOffset);
            writer.PropertyVector3("initialLocalPosition", parameter.InitLocalPosition);
            writer.PropertyQuaternion("initialLocalRotation", parameter.InitLocalRotation);
            writer.PropertyVector3("initialLocalScale", parameter.InitLocalScale);
            writer.PropertyVector3("localPosition", parameter.LocalPosition);
            WriteDynamicBoneVer02Limits(writer, parameter.IsMoveLimit, parameter.MoveLimitMin, parameter.MoveLimitMax,
                parameter.KeepLengthLimitMin, parameter.KeepLengthLimitMax, parameter.IsCrush,
                parameter.CrushMoveAreaMin, parameter.CrushMoveAreaMax, parameter.CrushAddXYMin, parameter.CrushAddXYMax);
            writer.EndObject();
        }

        private static void WriteDynamicBoneVer02RuntimeParticle(RuntimeJsonWriter writer, DynamicBone_Ver02.Particle particle, int index)
        {
            if (particle == null) return;
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("transformPath", GetPath(particle.Transform));
            writer.PropertyString("referenceTransformPath", GetPath(particle.refTrans));
            writer.PropertyInt("parentIndex", particle.ParentIndex);
            writer.PropertyBool("isRotationCalc", particle.IsRotationCalc);
            writer.PropertyFloat("damping", particle.Damping);
            writer.PropertyFloat("elasticity", particle.Elasticity);
            writer.PropertyFloat("stiffness", particle.Stiffness);
            writer.PropertyFloat("inertia", particle.Inert);
            writer.PropertyFloat("scaleNextBoneLength", particle.ScaleNextBoneLength);
            writer.PropertyFloat("radius", particle.Radius);
            writer.PropertyVector3("position", particle.Position);
            writer.PropertyVector3("previousPosition", particle.PrevPosition);
            writer.PropertyVector3("endOffset", particle.EndOffset);
            writer.PropertyVector3("initialLocalPosition", particle.InitLocalPosition);
            writer.PropertyQuaternion("initialLocalRotation", particle.InitLocalRotation);
            writer.PropertyVector3("initialLocalScale", particle.InitLocalScale);
            writer.PropertyVector3("localPosition", particle.LocalPosition);
            WriteDynamicBoneVer02Limits(writer, particle.IsMoveLimit, particle.MoveLimitMin, particle.MoveLimitMax,
                particle.KeepLengthLimitMin, particle.KeepLengthLimitMax, particle.IsCrush,
                particle.CrushMoveAreaMin, particle.CrushMoveAreaMax, particle.CrushAddXYMin, particle.CrushAddXYMax);
            writer.EndObject();
        }

        private static void WriteDynamicBoneVer02Limits(RuntimeJsonWriter writer, bool isMoveLimit, Vector3 moveLimitMin,
            Vector3 moveLimitMax, float keepLengthLimitMin, float keepLengthLimitMax, bool isCrush,
            float crushMoveAreaMin, float crushMoveAreaMax, float crushAddXYMin, float crushAddXYMax)
        {
            writer.PropertyBool("isMoveLimit", isMoveLimit);
            writer.PropertyVector3("moveLimitMin", moveLimitMin);
            writer.PropertyVector3("moveLimitMax", moveLimitMax);
            writer.PropertyFloat("keepLengthLimitMin", keepLengthLimitMin);
            writer.PropertyFloat("keepLengthLimitMax", keepLengthLimitMax);
            writer.PropertyBool("isCrush", isCrush);
            writer.PropertyFloat("crushMoveAreaMin", crushMoveAreaMin);
            writer.PropertyFloat("crushMoveAreaMax", crushMoveAreaMax);
            writer.PropertyFloat("crushAddXYMin", crushAddXYMin);
            writer.PropertyFloat("crushAddXYMax", crushAddXYMax);
        }

        private static List<DynamicBone_Ver02.Particle> SafeGetRuntimeParticles(DynamicBone_Ver02 dynamicBone)
        {
            try
            {
                FieldInfo field = typeof(DynamicBone_Ver02).GetField("Particles", BindingFlags.Instance | BindingFlags.NonPublic);
                List<DynamicBone_Ver02.Particle> particles = field == null
                    ? null
                    : field.GetValue(dynamicBone) as List<DynamicBone_Ver02.Particle>;
                return particles ?? new List<DynamicBone_Ver02.Particle>();
            }
            catch (Exception ex)
            {
                DBDE.Logger.LogWarning("Could not inspect DynamicBone_Ver02 runtime particles: " + ex.Message);
                return new List<DynamicBone_Ver02.Particle>();
            }
        }

        private static void WriteCollider(RuntimeJsonWriter writer, DynamicBoneCollider collider, int index)
        {
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("componentType", collider.GetType().FullName);
            writer.PropertyString("componentName", collider.name);
            writer.PropertyString("gameObjectName", collider.gameObject == null ? string.Empty : collider.gameObject.name);
            writer.PropertyString("transformPath", GetPath(collider.transform));
            writer.PropertyTransform("transform", collider.transform);
            writer.PropertyVector3("center", collider.m_Center);
            writer.PropertyFloat("radius", collider.m_Radius);
            writer.PropertyFloat("height", collider.m_Height);
            writer.PropertyInt("direction", (int)collider.m_Direction);
            writer.PropertyString("directionName", collider.m_Direction.ToString());
            writer.PropertyInt("bound", (int)collider.m_Bound);
            writer.PropertyString("boundName", collider.m_Bound.ToString());
            writer.PropertyBool("enabled", collider.enabled);
            writer.EndObject();
        }

        private static void AddColliders(List<DynamicBoneCollider> target, IList<DynamicBoneCollider> source)
        {
            if (source == null) return;
            for (int i = 0; i < source.Count; i++)
            {
                DynamicBoneCollider collider = source[i];
                if (collider != null && !target.Contains(collider)) target.Add(collider);
            }
        }

        private static DynamicBone SafeGetPrimary(DBDEDynamicBoneEdit edit)
        {
            try { return edit.PrimaryDynamicBone; }
            catch (Exception ex)
            {
                DBDE.Logger.LogWarning("Could not resolve DBDE primary DynamicBone: " + ex.Message);
                return null;
            }
        }

        private static List<DynamicBone> SafeGetDynamicBones(DBDEDynamicBoneEdit edit)
        {
            try { return edit.DynamicBones ?? new List<DynamicBone>(); }
            catch (Exception ex)
            {
                DBDE.Logger.LogWarning("Could not enumerate DBDE DynamicBones: " + ex.Message);
                return new List<DynamicBone>();
            }
        }

        private static string SafeGetButtonName(DBDEDynamicBoneEdit edit)
        {
            try { return edit.GetButtonName(); }
            catch { return string.Empty; }
        }

        private static string GetRootName(DynamicBone dynamicBone)
        {
            return dynamicBone == null || dynamicBone.m_Root == null ? string.Empty : dynamicBone.m_Root.name;
        }

        private static string GetPath(Transform transform)
        {
            if (transform == null) return string.Empty;
            var parts = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                parts.Insert(0, current.name);
                current = current.parent;
            }
            return string.Join("/", parts.ToArray());
        }

        private sealed class StaticReferenceTransform
        {
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal readonly Vector3 LossyScale;

            internal StaticReferenceTransform(Transform transform)
            {
                Position = transform.position;
                Rotation = transform.rotation;
                LossyScale = transform.lossyScale;
            }
        }

        private sealed class DBDEGroupSnapshot
        {
            internal int Index;
            internal string Name;
            internal string PrimaryRoot;
            internal List<int> DynamicBoneIndices;
        }

        private sealed class RuntimeJsonWriter
        {
            private readonly StringBuilder builder = new StringBuilder(64 * 1024);
            private readonly Stack<bool> firstValues = new Stack<bool>();

            public override string ToString() { return builder.ToString(); }

            internal void BeginObject() { builder.Append('{'); firstValues.Push(true); }
            internal void EndObject() { builder.Append('}'); firstValues.Pop(); }
            internal void BeginArray() { builder.Append('['); firstValues.Push(true); }
            internal void EndArray() { builder.Append(']'); firstValues.Pop(); }

            internal void Property(string name)
            {
                NextValue();
                WriteString(name);
                builder.Append(':');
            }

            internal void BeginObjectElement() { NextValue(); BeginObject(); }
            internal void BeginArrayElement() { NextValue(); BeginArray(); }

            internal void BeginObjectProperty(string name) { Property(name); BeginObject(); }
            internal void BeginArrayProperty(string name) { Property(name); BeginArray(); }

            internal void EndObjectProperty() { EndObject(); }

            internal void NextValue()
            {
                if (firstValues.Count == 0) return;
                bool first = firstValues.Pop();
                if (!first) builder.Append(',');
                firstValues.Push(false);
            }

            internal void StringElement(string value) { NextValue(); WriteString(value); }
            internal void IntElement(int value) { NextValue(); builder.Append(value.ToString(CultureInfo.InvariantCulture)); }

            internal void PropertyString(string name, string value) { Property(name); WriteString(value); }
            internal void PropertyInt(string name, int value) { Property(name); builder.Append(value.ToString(CultureInfo.InvariantCulture)); }
            internal void PropertyFloat(string name, float value) { Property(name); WriteFloat(value); }
            internal void PropertyBool(string name, bool value) { Property(name); builder.Append(value ? "true" : "false"); }

            internal void PropertyVector3(string name, Vector3 value)
            {
                Property(name);
                builder.Append('[');
                WriteFloat(value.x); builder.Append(',');
                WriteFloat(value.y); builder.Append(',');
                WriteFloat(value.z);
                builder.Append(']');
            }

            internal void PropertyQuaternion(string name, Quaternion value)
            {
                Property(name);
                builder.Append('[');
                WriteFloat(value.x); builder.Append(',');
                WriteFloat(value.y); builder.Append(',');
                WriteFloat(value.z); builder.Append(',');
                WriteFloat(value.w);
                builder.Append(']');
            }

            internal void PropertyStaticReferenceTransform(string name, StaticReferenceTransform value)
            {
                Property(name);
                BeginObject();
                PropertyVector3("position", value.Position);
                PropertyQuaternion("rotation", value.Rotation);
                PropertyVector3("lossyScale", value.LossyScale);
                EndObject();
            }

            internal void PropertyTransform(string name, Transform transform)
            {
                Property(name);
                if (transform == null)
                {
                    builder.Append("null");
                    return;
                }
                BeginObject();
                PropertyString("path", GetPath(transform));
                PropertyVector3("localPosition", transform.localPosition);
                PropertyQuaternion("localRotation", transform.localRotation);
                PropertyVector3("localScale", transform.localScale);
                PropertyVector3("position", transform.position);
                PropertyQuaternion("rotation", transform.rotation);
                PropertyVector3("lossyScale", transform.lossyScale);
                EndObject();
            }

            internal void PropertyStringList(string name, IList<Transform> transforms, Transform root)
            {
                Property(name);
                BeginArray();
                if (transforms != null)
                {
                    for (int i = 0; i < transforms.Count; i++)
                    {
                        Transform transform = transforms[i];
                        StringElement(root == null ? GetPath(transform) : root.GetPathToChild(transform));
                    }
                }
                EndArray();
            }

            internal void PropertyCurve(string name, AnimationCurve curve)
            {
                Property(name);
                if (curve == null)
                {
                    builder.Append("null");
                    return;
                }
                BeginArray();
                Keyframe[] keys = curve.keys;
                for (int i = 0; i < keys.Length; i++)
                {
                    BeginObjectElement();
                    PropertyFloat("time", keys[i].time);
                    PropertyFloat("value", keys[i].value);
                    PropertyFloat("inTangent", keys[i].inTangent);
                    PropertyFloat("outTangent", keys[i].outTangent);
                    EndObject();
                }
                EndArray();
            }

            private void WriteFloat(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    builder.Append("null");
                    return;
                }
                builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }

            private void WriteString(string value)
            {
                if (value == null) value = string.Empty;
                builder.Append('"');
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < 32)
                                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else
                                builder.Append(c);
                            break;
                    }
                }
                builder.Append('"');
            }
        }
    }
}
