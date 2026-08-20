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
    /// Captures the real DynamicBone.Update -> LateUpdate state transition.
    /// The sequence is intentionally lower-body focused so it can be replayed
    /// by the Blender skirt solver without scanning unrelated scene objects.
    /// </summary>
    internal static class DBDERuntimeSequenceExporter
    {
        private const string FormatVersion = "DBDE.RuntimeSequence.v3";
        private const int MaxFrames = 600;

        private static readonly FieldInfo ParticlesField = typeof(DynamicBone).GetField(
            "m_Particles", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TimeField = typeof(DynamicBone).GetField(
            "m_Time", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo LocalGravityField = typeof(DynamicBone).GetField(
            "m_LocalGravity", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WeightField = typeof(DynamicBone).GetField(
            "m_Weight", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ObjectScaleField = typeof(DynamicBone).GetField(
            "m_ObjectScale", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ObjectMoveField = typeof(DynamicBone).GetField(
            "m_ObjectMove", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ObjectPrevPositionField = typeof(DynamicBone).GetField(
            "m_ObjectPrevPosition", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Dictionary<Type, ParticleFields> ParticleFieldCache =
            new Dictionary<Type, ParticleFields>();

        private static bool active;
        private static int lastFrame = -1;
        private static List<DynamicBone> dynamicBones = new List<DynamicBone>();
        private static List<DynamicBoneCollider> colliders = new List<DynamicBoneCollider>();
        private static HashSet<DynamicBone> seenLateUpdates = new HashSet<DynamicBone>();
        private static Dictionary<DynamicBone, Dictionary<int, ReferenceState>> references =
            new Dictionary<DynamicBone, Dictionary<int, ReferenceState>>();
        private static List<string> frames = new List<string>();
        private static int startFrame;
        private static float startTime;
        private static string startTitle;

        internal static bool IsActive
        {
            get { return active; }
        }

        internal static string Toggle(string title)
        {
            return active ? Stop() : Start(title);
        }

        internal static string Start(string title)
        {
            if (active)
                return "Continuous runtime capture is already active.";

            dynamicBones = FindLowerBodySkirtDynamicBones();
            if (dynamicBones.Count == 0)
                throw new InvalidOperationException("No lower-body skirt DynamicBone was found.");
            DBDEVer02RuntimeCapture.Start(dynamicBones[0]);

            colliders.Clear();
            for (int i = 0; i < dynamicBones.Count; i++)
                AddColliders(colliders, dynamicBones[i].m_Colliders);

            references.Clear();
            frames.Clear();
            seenLateUpdates.Clear();
            lastFrame = -1;
            startFrame = Time.frameCount;
            startTime = Time.time;
            startTitle = title ?? string.Empty;
            active = true;
            return "Continuous runtime capture started for " + dynamicBones.Count +
                   " DynamicBone and " + colliders.Count + " colliders. Press F8 again to stop.";
        }

        internal static string Stop()
        {
            if (!active)
                return "Continuous runtime capture is not active.";

            active = false;
            try
            {
                if (frames.Count == 0)
                    return "Continuous runtime capture stopped: no complete frame was observed.";

                string directory = Path.Combine(Paths.ConfigPath, "DBDE_RuntimeSnapshots");
                Directory.CreateDirectory(directory);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(directory, "DBDE_LowerBodySkirtSequence_" + stamp + ".json");
                var writer = new JsonWriter();
                writer.BeginObject();
                writer.PropertyString("formatVersion", FormatVersion);
                writer.PropertyString("pluginVersion", DBDE.Version);
                writer.PropertyString("exportScope", "gm_top.lowerBodySkirt");
                writer.PropertyString("capturePhase", "post-DynamicBone.LateUpdate");
                writer.PropertyString("inputPhase", "post-DynamicBone.Update InitTransforms");
                writer.PropertyString("title", startTitle);
                writer.PropertyString("capturedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                writer.PropertyString("gameVersion", Application.version);
                writer.PropertyString("unityVersion", Application.unityVersion);
                writer.PropertyString("activeScene", Application.loadedLevelName);
                writer.PropertyInt("startFrame", startFrame);
                writer.PropertyFloat("startTime", startTime);
                writer.PropertyInt("endFrame", frames.Count == 0 ? startFrame : lastFrame);
                writer.PropertyInt("frameTotal", frames.Count);
                writer.PropertyInt("dynamicBoneTotal", dynamicBones.Count);
                writer.PropertyInt("dynamicBoneVer02Total", DBDEVer02RuntimeCapture.Count);
                writer.PropertyInt("colliderTotal", colliders.Count);
                writer.BeginArrayProperty("frames");
                for (int i = 0; i < frames.Count; i++)
                    writer.RawElement(frames[i]);
                writer.EndArray();
                writer.EndObject();
                File.WriteAllText(path, writer.ToString(), new UTF8Encoding(false));
                return "Continuous runtime capture exported: " + path + " (" + frames.Count + " frames)";
            }
            finally
            {
                dynamicBones.Clear();
                colliders.Clear();
                references.Clear();
                seenLateUpdates.Clear();
                frames.Clear();
                lastFrame = -1;
                DBDEVer02RuntimeCapture.Reset();
            }
        }

        internal static void NotifyAnimatorInput(DynamicBone dynamicBone)
        {
            if (!active || !dynamicBones.Contains(dynamicBone))
                return;
            IList particles = GetParticles(dynamicBone);
            var frameReferences = new Dictionary<int, ReferenceState>();
            if (particles != null)
            {
                for (int i = 0; i < particles.Count; i++)
                {
                    Transform transform = GetParticleTransform(particles[i]);
                    if (transform == null)
                        continue;
                    frameReferences[i] = new ReferenceState(transform);
                }
            }
            references[dynamicBone] = frameReferences;
        }

        internal static void NotifyLateUpdate(DynamicBone dynamicBone)
        {
            if (!active || !dynamicBones.Contains(dynamicBone))
                return;

            if (Time.frameCount != lastFrame)
            {
                lastFrame = Time.frameCount;
                seenLateUpdates.Clear();
            }
            seenLateUpdates.Add(dynamicBone);
            if (seenLateUpdates.Count >= dynamicBones.Count)
                CaptureFrame();
        }

        private static void CaptureFrame()
        {
            if (!active || frames.Count >= MaxFrames)
            {
                if (active && frames.Count >= MaxFrames)
                    Stop();
                return;
            }

            var writer = new JsonWriter();
            writer.BeginObject();
            writer.PropertyInt("frameIndex", frames.Count);
            writer.PropertyInt("frameCount", Time.frameCount);
            writer.PropertyFloat("time", Time.time);
            writer.PropertyFloat("deltaTime", Time.deltaTime);
            writer.PropertyFloat("fixedDeltaTime", Time.fixedDeltaTime);
            writer.PropertyFloat("timeScale", Time.timeScale);
            WriteAnimationContext(writer, dynamicBones.Count == 0 ? null : dynamicBones[0]);
            writer.BeginArrayProperty("dynamicBones");
            for (int i = 0; i < dynamicBones.Count; i++)
                WriteRuntimeDynamicBone(writer, dynamicBones[i], i);
            writer.EndArray();
            DBDEVer02RuntimeCapture.WriteRuntimeChains(writer);
            writer.BeginArrayProperty("colliders");
            for (int i = 0; i < colliders.Count; i++)
                WriteRuntimeCollider(writer, colliders[i], i);
            writer.EndArray();
            writer.EndObject();
            frames.Add(writer.ToString());
            if (frames.Count >= MaxFrames)
            {
                string message = Stop();
                if (DBDE.Logger != null)
                    DBDE.Logger.LogInfo(message);
                DBDE.WriteLowerSkirtExportStatus(message);
            }
        }

        private static void WriteAnimationContext(JsonWriter writer, DynamicBone dynamicBone)
        {
            writer.Property("animationContext");
            writer.BeginObject();
            Animator animator = FindAnimator(dynamicBone);
            writer.PropertyBool("found", animator != null);
            if (animator == null)
            {
                writer.EndObject();
                return;
            }

            writer.PropertyString("animatorPath", GetPath(animator.transform));
            writer.PropertyString(
                "controllerName",
                animator.runtimeAnimatorController == null ? string.Empty : animator.runtimeAnimatorController.name);
            writer.PropertyFloat("speed", animator.speed);
            writer.PropertyInt("layerCount", animator.layerCount);
            writer.BeginArrayProperty("layers");
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                AnimatorStateInfo state;
                AnimatorClipInfo[] clips;
                float layerWeight;
                string layerName;
                try
                {
                    state = animator.GetCurrentAnimatorStateInfo(layer);
                    clips = animator.GetCurrentAnimatorClipInfo(layer);
                    layerWeight = animator.GetLayerWeight(layer);
                    layerName = animator.GetLayerName(layer);
                }
                catch
                {
                    // Transitional Animator layers can be temporarily unreadable.
                    // Skip only that layer so the physics frame remains valid JSON.
                    continue;
                }

                writer.BeginObjectElement();
                writer.PropertyInt("index", layer);
                writer.PropertyString("name", layerName);
                writer.PropertyFloat("weight", layerWeight);
                writer.PropertyInt("shortNameHash", state.shortNameHash);
                writer.PropertyInt("fullPathHash", state.fullPathHash);
                writer.PropertyFloat("normalizedTime", state.normalizedTime);
                writer.PropertyFloat("normalizedTimeLoop", Mathf.Repeat(state.normalizedTime, 1f));
                writer.PropertyFloat("stateLength", state.length);
                writer.PropertyFloat("stateSpeed", state.speed);
                writer.PropertyFloat("stateSpeedMultiplier", state.speedMultiplier);
                writer.PropertyBool("loop", state.loop);
                writer.BeginArrayProperty("clips");
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
            writer.EndObject();
        }

        private static Animator FindAnimator(DynamicBone dynamicBone)
        {
            Transform current = dynamicBone == null ? null : dynamicBone.transform;
            while (current != null)
            {
                Animator animator = current.GetComponent<Animator>();
                if (animator != null)
                    return animator;
                current = current.parent;
            }

            current = dynamicBone == null ? null : dynamicBone.m_Root;
            while (current != null)
            {
                Animator animator = current.GetComponent<Animator>();
                if (animator != null)
                    return animator;
                current = current.parent;
            }
            return null;
        }

        private static void WriteRuntimeDynamicBone(JsonWriter writer, DynamicBone dynamicBone, int index)
        {
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("componentPath", GetPath(dynamicBone == null ? null : dynamicBone.transform));
            writer.PropertyString("rootPath", GetPath(dynamicBone == null ? null : dynamicBone.m_Root));
            writer.PropertyString("componentName", dynamicBone == null ? string.Empty : dynamicBone.name);
            writer.PropertyString("rootName", dynamicBone == null || dynamicBone.m_Root == null ? string.Empty : dynamicBone.m_Root.name);
            writer.PropertyBool("enabled", dynamicBone != null && dynamicBone.enabled);
            if (dynamicBone != null)
            {
                writer.PropertyTransform("componentTransform", dynamicBone.transform);
                writer.PropertyTransform("rootTransform", dynamicBone.m_Root);
                writer.PropertyFloat("updateRate", dynamicBone.m_UpdateRate);
                writer.PropertyFloat("damping", dynamicBone.m_Damping);
                writer.PropertyFloat("elasticity", dynamicBone.m_Elasticity);
                writer.PropertyFloat("inertia", dynamicBone.m_Inert);
                writer.PropertyFloat("radius", dynamicBone.m_Radius);
                writer.PropertyFloat("stiffness", dynamicBone.m_Stiffness);
                writer.PropertyFloat("weight", GetFieldValue(dynamicBone, WeightField, 1f));
                writer.PropertyInt("freezeAxis", (int)dynamicBone.m_FreezeAxis);
                writer.PropertyString("freezeAxisName", dynamicBone.m_FreezeAxis.ToString());
                writer.PropertyVector3("gravity", dynamicBone.m_Gravity);
                writer.PropertyVector3("force", dynamicBone.m_Force);
                writer.PropertyVector3("localGravity", GetFieldValue(dynamicBone, LocalGravityField, Vector3.zero));
                writer.PropertyFloat("internalTimeAccumulator", GetFieldValue(dynamicBone, TimeField, 0f));
                writer.PropertyFloat("objectScale", GetFieldValue(dynamicBone, ObjectScaleField, 1f));
                writer.PropertyVector3("objectMove", GetFieldValue(dynamicBone, ObjectMoveField, Vector3.zero));
                writer.PropertyVector3("objectPreviousPosition", GetFieldValue(dynamicBone, ObjectPrevPositionField, Vector3.zero));
            }

            writer.BeginArrayProperty("particles");
            IList particles = GetParticles(dynamicBone);
            Dictionary<int, ReferenceState> frameReferences;
            references.TryGetValue(dynamicBone, out frameReferences);
            if (particles != null)
            {
                for (int i = 0; i < particles.Count; i++)
                {
                    object particle = particles[i];
                    ParticleFields fields = GetParticleFields(particle);
                    if (fields == null) continue;
                    Transform transform = GetParticleTransform(particle);
                    writer.BeginObjectElement();
                    writer.PropertyInt("index", i);
                    writer.PropertyString("transformPath", GetPath(transform));
                    writer.PropertyInt("parentIndex", GetValue(particle, fields.ParentIndex, -1));
                    writer.PropertyFloat("boneLength", GetValue(particle, fields.BoneLength, 0f));
                    writer.PropertyFloat("damping", GetValue(particle, fields.Damping, 0f));
                    writer.PropertyFloat("elasticity", GetValue(particle, fields.Elasticity, 0f));
                    writer.PropertyFloat("stiffness", GetValue(particle, fields.Stiffness, 0f));
                    writer.PropertyFloat("inertia", GetValue(particle, fields.Inertia, 0f));
                    writer.PropertyFloat("radius", GetValue(particle, fields.Radius, 0f));
                    writer.PropertyVector3("position", GetValue(particle, fields.Position, Vector3.zero));
                    writer.PropertyVector3("previousPosition", GetValue(particle, fields.PreviousPosition, Vector3.zero));
                    writer.PropertyVector3("velocity", GetValue(particle, fields.Position, Vector3.zero) - GetValue(particle, fields.PreviousPosition, Vector3.zero));
                    writer.PropertyVector3("endOffset", GetValue(particle, fields.EndOffset, Vector3.zero));
                    writer.PropertyVector3("initialLocalPosition", GetValue(particle, fields.InitialLocalPosition, Vector3.zero));
                    writer.PropertyQuaternion("initialLocalRotation", GetValue(particle, fields.InitialLocalRotation, Quaternion.identity));
                    if (frameReferences != null && frameReferences.ContainsKey(i))
                    {
                        ReferenceState reference = frameReferences[i];
                        writer.PropertyVector3("animatorLocalPosition", reference.LocalPosition);
                        writer.PropertyQuaternion("animatorLocalRotation", reference.LocalRotation);
                        writer.PropertyVector3("animatorWorldPosition", reference.Position);
                        writer.PropertyQuaternion("animatorWorldRotation", reference.Rotation);
                        writer.PropertyVector3("animatorLossyScale", reference.LossyScale);
                    }
                    if (transform != null)
                    {
                        writer.PropertyVector3("transformLocalPosition", transform.localPosition);
                        writer.PropertyQuaternion("transformLocalRotation", transform.localRotation);
                        writer.PropertyVector3("transformWorldPosition", transform.position);
                        writer.PropertyQuaternion("transformWorldRotation", transform.rotation);
                        writer.PropertyVector3("transformLossyScale", transform.lossyScale);
                    }
                    writer.EndObject();
                }
            }
            writer.EndArray();
            writer.EndObject();
        }

        private static void WriteRuntimeCollider(JsonWriter writer, DynamicBoneCollider collider, int index)
        {
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("transformPath", GetPath(collider == null ? null : collider.transform));
            writer.PropertyBool("enabled", collider != null && collider.enabled);
            if (collider != null)
            {
                writer.PropertyTransform("transform", collider.transform);
                writer.PropertyVector3("center", collider.m_Center);
                writer.PropertyFloat("radius", collider.m_Radius);
                writer.PropertyFloat("height", collider.m_Height);
                writer.PropertyInt("direction", (int)collider.m_Direction);
                writer.PropertyInt("bound", (int)collider.m_Bound);
                float worldRadius = collider.m_Radius * Mathf.Abs(collider.transform.lossyScale.z);
                float halfLength = (collider.m_Height - collider.m_Radius) * 0.5f;
                Vector3 endpoint0 = collider.m_Center;
                Vector3 endpoint1 = collider.m_Center;
                if (halfLength > 0f)
                {
                    if (collider.m_Direction == DynamicBoneCollider.Direction.X)
                    {
                        endpoint0.x -= halfLength;
                        endpoint1.x += halfLength;
                    }
                    else if (collider.m_Direction == DynamicBoneCollider.Direction.Y)
                    {
                        endpoint0.y -= halfLength;
                        endpoint1.y += halfLength;
                    }
                    else
                    {
                        endpoint0.z -= halfLength;
                        endpoint1.z += halfLength;
                    }
                }
                writer.PropertyFloat("worldRadius", worldRadius);
                writer.PropertyVector3("worldCenter", collider.transform.TransformPoint(collider.m_Center));
                writer.PropertyVector3("worldEndpoint0", collider.transform.TransformPoint(endpoint0));
                writer.PropertyVector3("worldEndpoint1", collider.transform.TransformPoint(endpoint1));
            }
            writer.EndObject();
        }

        private static List<DynamicBone> FindLowerBodySkirtDynamicBones()
        {
            var result = new List<DynamicBone>();
            DynamicBone[] components = Resources.FindObjectsOfTypeAll<DynamicBone>();
            for (int i = 0; i < components.Length; i++)
            {
                DynamicBone dynamicBone = components[i];
                if (dynamicBone == null || dynamicBone.gameObject == null || !dynamicBone.gameObject.scene.IsValid() ||
                    !dynamicBone.enabled || !dynamicBone.gameObject.activeInHierarchy)
                    continue;
                if (!IsLowerBodySkirt(dynamicBone))
                    continue;
                if (!result.Contains(dynamicBone))
                    result.Add(dynamicBone);
            }
            return result;
        }

        private static bool IsLowerBodySkirt(DynamicBone dynamicBone)
        {
            if (dynamicBone == null || dynamicBone.m_Root == null)
                return false;
            string componentPath = GetPath(dynamicBone.transform);
            string rootPath = GetPath(dynamicBone.m_Root);
            IList particles = GetParticles(dynamicBone);
            bool hasDress = false;
            if (particles != null)
            {
                for (int i = 0; i < particles.Count; i++)
                {
                    Transform transform = GetParticleTransform(particles[i]);
                    if (transform != null && StartsWith(transform.name, "Dress_"))
                    {
                        hasDress = true;
                        break;
                    }
                }
            }
            return hasDress && (Contains(componentPath, "gm_top") || Contains(rootPath, "gm_top") ||
                string.Equals(dynamicBone.m_Root.name, "\u4e0b\u534a\u8eab", StringComparison.OrdinalIgnoreCase));
        }

        private static bool Contains(string value, string token)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool StartsWith(string value, string token)
        {
            return !string.IsNullOrEmpty(value) && value.StartsWith(token, StringComparison.OrdinalIgnoreCase);
        }

        private static void AddColliders(List<DynamicBoneCollider> target, IList<DynamicBoneCollider> source)
        {
            if (source == null) return;
            for (int i = 0; i < source.Count; i++)
            {
                DynamicBoneCollider collider = source[i];
                if (collider != null && !target.Contains(collider))
                    target.Add(collider);
            }
        }

        private static IList GetParticles(DynamicBone dynamicBone)
        {
            if (dynamicBone == null || ParticlesField == null) return null;
            try { return ParticlesField.GetValue(dynamicBone) as IList; }
            catch { return null; }
        }

        private static Transform GetParticleTransform(object particle)
        {
            ParticleFields fields = GetParticleFields(particle);
            return fields == null ? null : GetValue(particle, fields.Transform, (Transform)null);
        }

        private static ParticleFields GetParticleFields(object particle)
        {
            if (particle == null) return null;
            Type type = particle.GetType();
            ParticleFields fields;
            if (!ParticleFieldCache.TryGetValue(type, out fields))
            {
                fields = new ParticleFields(type);
                ParticleFieldCache[type] = fields;
            }
            return fields;
        }

        private static T GetValue<T>(object instance, FieldInfo field, T fallback)
        {
            if (instance == null || field == null) return fallback;
            try
            {
                object value = field.GetValue(instance);
                return value is T ? (T)value : fallback;
            }
            catch { return fallback; }
        }

        private static T GetFieldValue<T>(object instance, FieldInfo field, T fallback)
        {
            return GetValue(instance, field, fallback);
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

        private sealed class ParticleFields
        {
            private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
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

            internal ParticleFields(Type type)
            {
                Transform = type.GetField("m_Transform", Flags);
                ParentIndex = type.GetField("m_ParentIndex", Flags);
                BoneLength = type.GetField("m_BoneLength", Flags);
                Damping = type.GetField("m_Damping", Flags);
                Elasticity = type.GetField("m_Elasticity", Flags);
                Stiffness = type.GetField("m_Stiffness", Flags);
                Inertia = type.GetField("m_Inert", Flags);
                Radius = type.GetField("m_Radius", Flags);
                Position = type.GetField("m_Position", Flags);
                PreviousPosition = type.GetField("m_PrevPosition", Flags);
                EndOffset = type.GetField("m_EndOffset", Flags);
                InitialLocalPosition = type.GetField("m_InitLocalPosition", Flags);
                InitialLocalRotation = type.GetField("m_InitLocalRotation", Flags);
            }
        }

        private sealed class ReferenceState
        {
            internal readonly Vector3 LocalPosition;
            internal readonly Quaternion LocalRotation;
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal readonly Vector3 LossyScale;

            internal ReferenceState(Transform transform)
            {
                LocalPosition = transform.localPosition;
                LocalRotation = transform.localRotation;
                Position = transform.position;
                Rotation = transform.rotation;
                LossyScale = transform.lossyScale;
            }
        }

        internal sealed class JsonWriter
        {
            private readonly StringBuilder builder = new StringBuilder(256 * 1024);
            private readonly Stack<bool> firstValues = new Stack<bool>();

            public override string ToString() { return builder.ToString(); }
            internal void BeginObject() { builder.Append('{'); firstValues.Push(true); }
            internal void EndObject() { builder.Append('}'); firstValues.Pop(); }
            internal void BeginArray() { builder.Append('['); firstValues.Push(true); }
            internal void EndArray() { builder.Append(']'); firstValues.Pop(); }
            internal void NextValue()
            {
                if (firstValues.Count == 0) return;
                bool first = firstValues.Pop();
                if (!first) builder.Append(',');
                firstValues.Push(false);
            }
            internal void Property(string name) { NextValue(); WriteString(name); builder.Append(':'); }
            internal void BeginObjectElement() { NextValue(); BeginObject(); }
            internal void RawElement(string raw) { NextValue(); builder.Append(raw); }
            internal void BeginArrayProperty(string name) { Property(name); BeginArray(); }
            internal void PropertyString(string name, string value) { Property(name); WriteString(value ?? string.Empty); }
            internal void PropertyInt(string name, int value) { Property(name); builder.Append(value.ToString(CultureInfo.InvariantCulture)); }
            internal void PropertyFloat(string name, float value) { Property(name); WriteFloat(value); }
            internal void PropertyBool(string name, bool value) { Property(name); builder.Append(value ? "true" : "false"); }
            internal void PropertyVector3(string name, Vector3 value) { Property(name); WriteVector3(value); }
            internal void PropertyQuaternion(string name, Quaternion value) { Property(name); WriteQuaternion(value); }
            internal void PropertyTransform(string name, Transform transform)
            {
                Property(name);
                BeginObject();
                if (transform != null)
                {
                    PropertyString("path", GetPath(transform));
                    PropertyVector3("localPosition", transform.localPosition);
                    PropertyQuaternion("localRotation", transform.localRotation);
                    PropertyVector3("localScale", transform.localScale);
                    PropertyVector3("position", transform.position);
                    PropertyQuaternion("rotation", transform.rotation);
                    PropertyVector3("lossyScale", transform.lossyScale);
                }
                EndObject();
            }
            private void WriteVector3(Vector3 value)
            {
                builder.Append('['); WriteFloat(value.x); builder.Append(','); WriteFloat(value.y); builder.Append(','); WriteFloat(value.z); builder.Append(']');
            }
            private void WriteQuaternion(Quaternion value)
            {
                builder.Append('['); WriteFloat(value.x); builder.Append(','); WriteFloat(value.y); builder.Append(','); WriteFloat(value.z); builder.Append(','); WriteFloat(value.w); builder.Append(']');
            }
            private void WriteFloat(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
                builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
            private void WriteString(string value)
            {
                builder.Append('"');
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '\\': builder.Append("\\\\"); break;
                        case '"': builder.Append("\\\""); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < 32) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else builder.Append(c);
                            break;
                    }
                }
                builder.Append('"');
            }
        }
    }
}
