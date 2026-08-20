using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KKAPI.Studio;
using UnityEngine;

namespace DynamicBoneDistributionEditor
{
    /// <summary>
    /// Captures the Studio animation state that is only available after the game
    /// has loaded and started the selected pose/motion.
    /// </summary>
    internal static class DBDEMotionRuntimeExporter
    {
        private const string FormatVersion = "DBDE.MotionRuntimeSnapshot.v1";

        internal static string Export()
        {
            if (!Studio.Studio.Instance)
                throw new InvalidOperationException("Studio is not loaded.");

            var selected = StudioAPI.GetSelectedObjects().ToList();
            var snapshot = new MotionRuntimeSnapshot
            {
                formatVersion = FormatVersion,
                pluginVersion = DBDE.Version,
                capturedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                gameVersion = Application.version,
                unityVersion = Application.unityVersion,
                activeScene = Application.loadedLevelName,
                frameCount = Time.frameCount,
                time = Time.time,
                selectedObjectCount = selected.Count,
            };

            foreach (KeyValuePair<int, Studio.ObjectCtrlInfo> entry in Studio.Studio.Instance.dicObjectCtrl)
            {
                Studio.OCIChar character = entry.Value as Studio.OCIChar;
                if (character == null) continue;

                var row = CaptureCharacter(character, selected.Contains(character));
                snapshot.characters.Add(row);
            }

            if (snapshot.characters.Count == 0)
                throw new InvalidOperationException("No Studio characters were found.");

            string directory = Path.Combine(BepInEx.Paths.ConfigPath, "DBDE_RuntimeSnapshots");
            Directory.CreateDirectory(directory);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(directory, "DBDE_MotionRuntimeSnapshot_" + stamp + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(snapshot, true), new System.Text.UTF8Encoding(false));
            DBDE.Logger.LogInfo("Motion runtime JSON exported: " + path + " (" + snapshot.characters.Count + " Studio character(s))");
            return path;
        }

        private static MotionCharacterSnapshot CaptureCharacter(Studio.OCIChar character, bool selected)
        {
            var row = new MotionCharacterSnapshot
            {
                objectKey = character.objectInfo == null ? -1 : character.objectInfo.dicKey,
                gameObjectName = character.charInfo == null ? string.Empty : character.charInfo.gameObject.name,
                transformPath = character.charInfo == null ? string.Empty : GetPath(character.charInfo.transform),
                selected = selected,
                isAnimeMotion = character.isAnimeMotion,
                isHAnime = character.isHAnime,
            };

            if (character.oiCharInfo != null)
            {
                row.sex = character.sex;
                row.anime = CaptureAnimeInfo(character);
            }

            if (character.charAnimeCtrl != null)
            {
                row.charAnimeStateHash = character.charAnimeCtrl.nameHadh;
                row.animator = CaptureAnimator(character.charAnimeCtrl.animator);
            }

            row.yure = CaptureYure(character.yureCtrl);
            return row;
        }

        private static StudioAnimeSnapshot CaptureAnimeInfo(Studio.OCIChar character)
        {
            Studio.OICharInfo info = character.oiCharInfo;
            Studio.OICharInfo.AnimeInfo animeInfo = info.animeInfo;
            var result = new StudioAnimeSnapshot
            {
                group = animeInfo == null ? -1 : animeInfo.group,
                category = animeInfo == null ? -1 : animeInfo.category,
                no = animeInfo == null ? -1 : animeInfo.no,
                speed = info.animeSpeed,
                pattern = info.animePattern,
                optionParam1 = info.animeOptionParam == null || info.animeOptionParam.Length < 1 ? 0f : info.animeOptionParam[0],
                optionParam2 = info.animeOptionParam == null || info.animeOptionParam.Length < 2 ? 0f : info.animeOptionParam[1],
                normalizedTime = info.animeNormalizedTime,
                optionVisible = info.animeOptionVisible,
                forceLoop = info.isAnimeForceLoop,
            };

            try
            {
                if (animeInfo != null && Singleton<Studio.Info>.IsInstance())
                {
                    Dictionary<int, Dictionary<int, Studio.Info.AnimeLoadInfo>> categories = null;
                    if (Singleton<Studio.Info>.Instance.dicAnimeLoadInfo.TryGetValue(animeInfo.group, out categories))
                    {
                        Dictionary<int, Studio.Info.AnimeLoadInfo> entries = null;
                        if (categories.TryGetValue(animeInfo.category, out entries))
                        {
                            Studio.Info.AnimeLoadInfo loaded = null;
                            if (entries.TryGetValue(animeInfo.no, out loaded) && loaded != null)
                            {
                                result.name = loaded.name;
                                result.bundlePath = loaded.bundlePath;
                                result.fileName = loaded.fileName;
                                result.clip = loaded.clip;

                                Studio.Info.HAnimeLoadInfo hLoaded = loaded as Studio.Info.HAnimeLoadInfo;
                                if (hLoaded != null)
                                {
                                    result.isMotion = hLoaded.isMotion;
                                    result.breastLayer = hLoaded.breastLayer;
                                    result.dynamicLeft = hLoaded.dynamic != null && hLoaded.dynamic.Length > 0 && hLoaded.dynamic[0];
                                    result.dynamicRight = hLoaded.dynamic != null && hLoaded.dynamic.Length > 1 && hLoaded.dynamic[1];
                                    result.overrideBundlePath = hLoaded.overrideFile == null ? string.Empty : hLoaded.overrideFile.bundlePath;
                                    result.overrideFileName = hLoaded.overrideFile == null ? string.Empty : hLoaded.overrideFile.fileName;
                                    result.yureBundlePath = hLoaded.yureFile == null ? string.Empty : hLoaded.yureFile.bundlePath;
                                    result.yureFileName = hLoaded.yureFile == null ? string.Empty : hLoaded.yureFile.fileName;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.resolveError = ex.Message;
            }

            return result;
        }

        private static AnimatorSnapshot CaptureAnimator(Animator animator)
        {
            if (animator == null) return null;

            var result = new AnimatorSnapshot
            {
                enabled = animator.enabled,
                layerCount = animator.layerCount,
                controllerName = animator.runtimeAnimatorController == null ? string.Empty : animator.runtimeAnimatorController.name,
                controllerType = animator.runtimeAnimatorController == null ? string.Empty : animator.runtimeAnimatorController.GetType().FullName,
                parameters = new List<AnimatorParameterSnapshot>(),
                layers = new List<AnimatorLayerSnapshot>(),
                controllerClips = new List<string>(),
            };

            if (animator.runtimeAnimatorController != null && animator.runtimeAnimatorController.animationClips != null)
            {
                foreach (AnimationClip clip in animator.runtimeAnimatorController.animationClips)
                {
                    if (clip != null && !result.controllerClips.Contains(clip.name))
                        result.controllerClips.Add(clip.name);
                }
            }

            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                var item = new AnimatorParameterSnapshot
                {
                    name = parameter.name,
                    nameHash = parameter.nameHash,
                    type = (int)parameter.type,
                    typeName = parameter.type.ToString(),
                    defaultFloat = parameter.defaultFloat,
                    defaultInt = parameter.defaultInt,
                    defaultBool = parameter.defaultBool,
                };
                try
                {
                    switch (parameter.type)
                    {
                        case AnimatorControllerParameterType.Float:
                            item.currentFloat = animator.GetFloat(parameter.nameHash);
                            break;
                        case AnimatorControllerParameterType.Int:
                            item.currentInt = animator.GetInteger(parameter.nameHash);
                            break;
                        case AnimatorControllerParameterType.Bool:
                        case AnimatorControllerParameterType.Trigger:
                            item.currentBool = animator.GetBool(parameter.nameHash);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    item.readError = ex.Message;
                }
                result.parameters.Add(item);
            }

            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
                AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layer);
                var item = new AnimatorLayerSnapshot
                {
                    index = layer,
                    name = SafeLayerName(animator, layer),
                    weight = animator.GetLayerWeight(layer),
                    current = CaptureState(current),
                    next = CaptureState(next),
                    currentClips = CaptureClips(animator.GetCurrentAnimatorClipInfo(layer)),
                    nextClips = CaptureClips(animator.GetNextAnimatorClipInfo(layer)),
                };
                result.layers.Add(item);
            }

            return result;
        }

        private static AnimatorStateSnapshot CaptureState(AnimatorStateInfo state)
        {
            return new AnimatorStateSnapshot
            {
                shortNameHash = state.shortNameHash,
                fullPathHash = state.fullPathHash,
                normalizedTime = state.normalizedTime,
                length = state.length,
                speed = state.speed,
                speedMultiplier = state.speedMultiplier,
                loop = state.loop,
                tagHash = state.tagHash,
            };
        }

        private static List<AnimatorClipSnapshot> CaptureClips(AnimatorClipInfo[] clips)
        {
            var result = new List<AnimatorClipSnapshot>();
            if (clips == null) return result;
            foreach (AnimatorClipInfo item in clips)
            {
                result.Add(new AnimatorClipSnapshot
                {
                    clipName = item.clip == null ? string.Empty : item.clip.name,
                    weight = item.weight,
                });
            }
            return result;
        }

        private static YureSnapshot CaptureYure(YureCtrl yure)
        {
            if (yure == null) return null;
            var result = new YureSnapshot
            {
                initialized = yure.isInit,
                active = yure.isActives == null ? new bool[0] : yure.isActives,
                breastShapes = new List<YureBreastShapeSnapshot>(),
                animationEntries = new List<string>(),
            };
            if (yure.breastShapes != null)
            {
                for (int i = 0; i < yure.breastShapes.Length; i++)
                {
                    YureCtrl.BreastShapeInfo shape = yure.breastShapes[i];
                    result.breastShapes.Add(new YureBreastShapeSnapshot
                    {
                        index = i,
                        left = shape != null && shape.left,
                        right = shape != null && shape.right,
                    });
                }
            }
            if (yure.dicInfo != null)
                result.animationEntries.AddRange(yure.dicInfo.Keys.ToArray());
            return result;
        }

        private static string SafeLayerName(Animator animator, int layer)
        {
            try { return animator.GetLayerName(layer); }
            catch { return string.Empty; }
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

        [Serializable]
        private sealed class MotionRuntimeSnapshot
        {
            public string formatVersion;
            public string pluginVersion;
            public string capturedAtUtc;
            public string gameVersion;
            public string unityVersion;
            public string activeScene;
            public int frameCount;
            public float time;
            public int selectedObjectCount;
            public List<MotionCharacterSnapshot> characters = new List<MotionCharacterSnapshot>();
        }

        [Serializable]
        private sealed class MotionCharacterSnapshot
        {
            public int objectKey;
            public string gameObjectName;
            public string transformPath;
            public bool selected;
            public int sex;
            public bool isAnimeMotion;
            public bool isHAnime;
            public int charAnimeStateHash;
            public StudioAnimeSnapshot anime;
            public AnimatorSnapshot animator;
            public YureSnapshot yure;
        }

        [Serializable]
        private sealed class StudioAnimeSnapshot
        {
            public int group = -1;
            public int category = -1;
            public int no = -1;
            public string name = string.Empty;
            public string bundlePath = string.Empty;
            public string fileName = string.Empty;
            public string clip = string.Empty;
            public float speed;
            public float pattern;
            public float optionParam1;
            public float optionParam2;
            public float normalizedTime;
            public bool optionVisible;
            public bool forceLoop;
            public bool isMotion;
            public int breastLayer = -1;
            public bool dynamicLeft;
            public bool dynamicRight;
            public string overrideBundlePath = string.Empty;
            public string overrideFileName = string.Empty;
            public string yureBundlePath = string.Empty;
            public string yureFileName = string.Empty;
            public string resolveError = string.Empty;
        }

        [Serializable]
        private sealed class AnimatorSnapshot
        {
            public bool enabled;
            public int layerCount;
            public string controllerName;
            public string controllerType;
            public List<string> controllerClips;
            public List<AnimatorParameterSnapshot> parameters;
            public List<AnimatorLayerSnapshot> layers;
        }

        [Serializable]
        private sealed class AnimatorParameterSnapshot
        {
            public string name;
            public int nameHash;
            public int type;
            public string typeName;
            public float defaultFloat;
            public int defaultInt;
            public bool defaultBool;
            public float currentFloat;
            public int currentInt;
            public bool currentBool;
            public string readError = string.Empty;
        }

        [Serializable]
        private sealed class AnimatorLayerSnapshot
        {
            public int index;
            public string name;
            public float weight;
            public AnimatorStateSnapshot current;
            public AnimatorStateSnapshot next;
            public List<AnimatorClipSnapshot> currentClips;
            public List<AnimatorClipSnapshot> nextClips;
        }

        [Serializable]
        private sealed class AnimatorStateSnapshot
        {
            public int shortNameHash;
            public int fullPathHash;
            public int tagHash;
            public float normalizedTime;
            public float length;
            public float speed;
            public float speedMultiplier;
            public bool loop;
        }

        [Serializable]
        private sealed class AnimatorClipSnapshot
        {
            public string clipName;
            public float weight;
        }

        [Serializable]
        private sealed class YureSnapshot
        {
            public bool initialized;
            public bool[] active;
            public List<YureBreastShapeSnapshot> breastShapes;
            public List<string> animationEntries;
        }

        [Serializable]
        private sealed class YureBreastShapeSnapshot
        {
            public int index;
            public bool left;
            public bool right;
        }
    }
}
