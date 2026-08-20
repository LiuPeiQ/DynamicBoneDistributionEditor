using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DynamicBoneDistributionEditor
{
    /// <summary>
    /// Adds the four body DynamicBone_Ver02 components to RuntimeSequence v3.
    /// The Blender replay path consumes the captured Animator input and the
    /// post-LateUpdate transform output directly before replacing it with an
    /// independent solver.
    /// </summary>
    internal static class DBDEVer02RuntimeCapture
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo ParticlesField = typeof(DynamicBone_Ver02).GetField("Particles", Flags);
        private static readonly FieldInfo WeightField = typeof(DynamicBone_Ver02).GetField("Weight", Flags);
        private static readonly FieldInfo ObjectScaleField = typeof(DynamicBone_Ver02).GetField("ObjectScale", Flags);
        private static readonly FieldInfo ObjectMoveField = typeof(DynamicBone_Ver02).GetField("ObjectMove", Flags);
        private static readonly FieldInfo ObjectPrevPositionField = typeof(DynamicBone_Ver02).GetField("ObjectPrevPosition", Flags);
        private static readonly FieldInfo UpdateTimeField = typeof(DynamicBone_Ver02).GetField("UpdateTime", Flags);

        private static readonly List<DynamicBone_Ver02> Chains = new List<DynamicBone_Ver02>();
        private static readonly Dictionary<DynamicBone_Ver02, Dictionary<int, ReferenceState>> References =
            new Dictionary<DynamicBone_Ver02, Dictionary<int, ReferenceState>>();
        private static readonly Dictionary<DynamicBone_Ver02, int> PostFrames =
            new Dictionary<DynamicBone_Ver02, int>();

        internal static int Count { get { return Chains.Count; } }

        internal static void Start(DynamicBone skirt)
        {
            Reset();
            string avatar = FirstPathSegment(skirt == null ? null : skirt.m_Root);
            DynamicBone_Ver02[] all = Resources.FindObjectsOfTypeAll<DynamicBone_Ver02>();
            for (int i = 0; i < all.Length; i++)
            {
                DynamicBone_Ver02 chain = all[i];
                if (chain == null || chain.Root == null || !chain.enabled || !chain.gameObject.activeInHierarchy)
                    continue;
                if (!IsBodyRoot(chain.Root.name) || (avatar.Length > 0 && FirstPathSegment(chain.Root) != avatar))
                    continue;
                Chains.Add(chain);
            }
            Chains.Sort(delegate(DynamicBone_Ver02 left, DynamicBone_Ver02 right)
            {
                return string.Compare(GetPath(left == null ? null : left.Root), GetPath(right == null ? null : right.Root), StringComparison.Ordinal);
            });
        }

        internal static void Reset()
        {
            Chains.Clear();
            References.Clear();
            PostFrames.Clear();
        }

        internal static void NotifyAnimatorInput(DynamicBone_Ver02 chain)
        {
            if (!DBDERuntimeSequenceExporter.IsActive || chain == null || !Chains.Contains(chain))
                return;
            IList particles = GetParticles(chain);
            var states = new Dictionary<int, ReferenceState>();
            if (particles != null)
            {
                for (int i = 0; i < particles.Count; i++)
                {
                    DynamicBone_Ver02.Particle particle = particles[i] as DynamicBone_Ver02.Particle;
                    if (particle != null && particle.Transform != null)
                        states[i] = new ReferenceState(particle.Transform);
                }
            }
            References[chain] = states;
        }

        internal static void NotifyLateUpdate(DynamicBone_Ver02 chain)
        {
            if (!DBDERuntimeSequenceExporter.IsActive || chain == null || !Chains.Contains(chain))
                return;
            PostFrames[chain] = Time.frameCount;
        }

        internal static void WriteRuntimeChains(DBDERuntimeSequenceExporter.JsonWriter writer)
        {
            writer.BeginArrayProperty("dynamicBonesVer02");
            for (int i = 0; i < Chains.Count; i++)
                WriteRuntimeChain(writer, Chains[i], i);
            writer.EndArray();
        }

        private static void WriteRuntimeChain(DBDERuntimeSequenceExporter.JsonWriter writer, DynamicBone_Ver02 chain, int index)
        {
            writer.BeginObjectElement();
            writer.PropertyInt("index", index);
            writer.PropertyString("componentType", "DynamicBone_Ver02");
            writer.PropertyString("componentPath", GetPath(chain == null ? null : chain.transform));
            writer.PropertyString("rootPath", GetPath(chain == null ? null : chain.Root));
            writer.PropertyString("rootName", chain == null || chain.Root == null ? string.Empty : chain.Root.name);
            writer.PropertyString("comment", chain == null ? string.Empty : chain.Comment);
            writer.PropertyBool("enabled", chain != null && chain.enabled);
            writer.PropertyInt("capturedPostLateUpdateFrame", GetValue(PostFrames, chain, -1));
            if (chain != null)
            {
                writer.PropertyTransform("componentTransform", chain.transform);
                writer.PropertyTransform("rootTransform", chain.Root);
                writer.PropertyFloat("updateRate", chain.UpdateRate);
                writer.PropertyFloat("reflectSpeed", chain.ReflectSpeed);
                writer.PropertyInt("heavyLoopMaxCount", chain.HeavyLoopMaxCount);
                writer.PropertyVector3("gravity", chain.Gravity);
                writer.PropertyVector3("force", chain.Force);
                writer.PropertyFloat("weight", GetField(chain, WeightField, 1f));
                writer.PropertyFloat("objectScale", GetField(chain, ObjectScaleField, 1f));
                writer.PropertyVector3("objectMove", GetField(chain, ObjectMoveField, Vector3.zero));
                writer.PropertyVector3("objectPreviousPosition", GetField(chain, ObjectPrevPositionField, Vector3.zero));
                writer.PropertyFloat("internalTimeAccumulator", GetField(chain, UpdateTimeField, 0f));
                writer.PropertyInt("patternIndex", chain.PtnNo);
            }

            writer.BeginArrayProperty("runtimeParticles");
            IList particles = GetParticles(chain);
            Dictionary<int, ReferenceState> references;
            References.TryGetValue(chain, out references);
            if (particles != null)
            {
                for (int i = 0; i < particles.Count; i++)
                {
                    DynamicBone_Ver02.Particle particle = particles[i] as DynamicBone_Ver02.Particle;
                    if (particle == null) continue;
                    Transform transform = particle.Transform;
                    writer.BeginObjectElement();
                    writer.PropertyInt("index", i);
                    writer.PropertyString("transformPath", GetPath(transform));
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
                    writer.PropertyVector3("velocity", particle.Position - particle.PrevPosition);
                    writer.PropertyVector3("endOffset", particle.EndOffset);
                    writer.PropertyVector3("initialLocalPosition", particle.InitLocalPosition);
                    writer.PropertyQuaternion("initialLocalRotation", particle.InitLocalRotation);
                    writer.PropertyVector3("initialLocalScale", particle.InitLocalScale);
                    writer.PropertyVector3("localPosition", particle.LocalPosition);
                    writer.PropertyBool("isMoveLimit", particle.IsMoveLimit);
                    writer.PropertyVector3("moveLimitMin", particle.MoveLimitMin);
                    writer.PropertyVector3("moveLimitMax", particle.MoveLimitMax);
                    writer.PropertyFloat("keepLengthLimitMin", particle.KeepLengthLimitMin);
                    writer.PropertyFloat("keepLengthLimitMax", particle.KeepLengthLimitMax);
                    writer.PropertyBool("isCrush", particle.IsCrush);
                    writer.PropertyFloat("crushMoveAreaMin", particle.CrushMoveAreaMin);
                    writer.PropertyFloat("crushMoveAreaMax", particle.CrushMoveAreaMax);
                    writer.PropertyFloat("crushAddXYMin", particle.CrushAddXYMin);
                    writer.PropertyFloat("crushAddXYMax", particle.CrushAddXYMax);
                    if (references != null && references.ContainsKey(i))
                    {
                        ReferenceState reference = references[i];
                        writer.PropertyVector3("animatorLocalPosition", reference.LocalPosition);
                        writer.PropertyQuaternion("animatorLocalRotation", reference.LocalRotation);
                        writer.PropertyVector3("animatorLocalScale", reference.LocalScale);
                        writer.PropertyVector3("animatorWorldPosition", reference.Position);
                        writer.PropertyQuaternion("animatorWorldRotation", reference.Rotation);
                        writer.PropertyVector3("animatorLossyScale", reference.LossyScale);
                    }
                    if (transform != null)
                    {
                        writer.PropertyVector3("transformLocalPosition", transform.localPosition);
                        writer.PropertyQuaternion("transformLocalRotation", transform.localRotation);
                        writer.PropertyVector3("transformLocalScale", transform.localScale);
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

        private static IList GetParticles(DynamicBone_Ver02 chain)
        {
            if (chain == null || ParticlesField == null) return null;
            try { return ParticlesField.GetValue(chain) as IList; }
            catch { return null; }
        }

        private static T GetField<T>(object instance, FieldInfo field, T fallback)
        {
            if (instance == null || field == null) return fallback;
            try
            {
                object value = field.GetValue(instance);
                return value is T ? (T)value : fallback;
            }
            catch { return fallback; }
        }

        private static int GetValue(Dictionary<DynamicBone_Ver02, int> values, DynamicBone_Ver02 key, int fallback)
        {
            int value;
            return key != null && values.TryGetValue(key, out value) ? value : fallback;
        }

        private static bool IsBodyRoot(string name)
        {
            return name != null && (
                name.StartsWith("cf_d_bust01_", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("cf_d_siri01_", StringComparison.OrdinalIgnoreCase));
        }

        private static string FirstPathSegment(Transform transform)
        {
            string path = GetPath(transform);
            int slash = path.IndexOf('/');
            return slash < 0 ? path : path.Substring(0, slash);
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

        private sealed class ReferenceState
        {
            internal readonly Vector3 LocalPosition;
            internal readonly Quaternion LocalRotation;
            internal readonly Vector3 LocalScale;
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal readonly Vector3 LossyScale;

            internal ReferenceState(Transform transform)
            {
                LocalPosition = transform.localPosition;
                LocalRotation = transform.localRotation;
                LocalScale = transform.localScale;
                Position = transform.position;
                Rotation = transform.rotation;
                LossyScale = transform.lossyScale;
            }
        }
    }
}
