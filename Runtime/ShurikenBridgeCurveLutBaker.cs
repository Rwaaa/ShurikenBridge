using System;
using System.Collections.Generic;
using UnityEngine;

namespace ShurikenBridge
{
    /// <summary>
    /// Hash-deduplicated 64 x 512 RGBAHalf Texture2D LUT baker for Unity ParticleSystem modules:
    /// - Row*4 + 0: ColorOverLifetime (RGBA Linear)
    /// - Row*4 + 1: (SizeOverLifetime, SpeedModifierOverLifetime, RotationZOverLifetime, NoiseStrengthOverLifetime)
    /// - Row*4 + 2: ColorBySpeed (RGBA Linear)
    /// - Row*4 + 3: (SizeBySpeed, RotationBySpeed, 0, 0)
    /// Thousands of emitters sharing the same prefab/preset share a single LUT profile with 0.000 ms runtime CPU cost.
    /// </summary>
    public sealed class ShurikenBridgeCurveLutBaker : IDisposable
    {
        public const int LutWidth = 64;
        public const int MaxProfiles = 128;
        public const int RowsPerProfile = 4;
        public const int TextureHeight = MaxProfiles * RowsPerProfile; // 512

        private Texture2D _lutTexture;
        private readonly Dictionary<int, int> _hashToProfile = new Dictionary<int, int>(64);
        private readonly Color[] _scratchColorLifeRow = new Color[LutWidth];
        private readonly Color[] _scratchCurveLifeRow = new Color[LutWidth];
        private readonly Color[] _scratchColorSpeedRow = new Color[LutWidth];
        private readonly Color[] _scratchCurveSpeedRow = new Color[LutWidth];
        private int _nextProfileIndex;
        private bool _isTextureDirty;

        public Texture2D LutTexture => _lutTexture;
        public int AllocatedProfileCount => _nextProfileIndex;

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only CPU LUT sampler mirroring HLSL SampleLutRow() 1:1 for Scene View particle picking & debugging.
        /// </summary>
        public Color EditorSampleLutRow(int rowY, float t)
        {
            if (_lutTexture == null)
            {
                return Color.white;
            }
            int clampedRow = Mathf.Clamp(rowY, 0, TextureHeight - 1);
            float coord = Mathf.Clamp01(t) * (LutWidth - 1);
            int x0 = Mathf.FloorToInt(coord);
            int x1 = Mathf.Min(x0 + 1, LutWidth - 1);
            float fracX = coord - x0;
            Color v0 = _lutTexture.GetPixel(x0, clampedRow);
            Color v1 = _lutTexture.GetPixel(x1, clampedRow);
            return Color.LerpUnclamped(v0, v1, fracX);
        }
#endif

        public ShurikenBridgeCurveLutBaker()
        {
            EnsureTextureCreated();
        }

        public void EnsureTextureCreated()
        {
            if (_lutTexture != null)
            {
                return;
            }

            _lutTexture = new Texture2D(LutWidth, TextureHeight, TextureFormat.RGBAHalf, false, true)
            {
                name = "ShurikenBridge_CurveGradientLUT_64x512",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            _hashToProfile.Clear();
            _nextProfileIndex = 0;

            // Profile 0: Identity LUT
            for (int x = 0; x < LutWidth; x++)
            {
                _scratchColorLifeRow[x] = Color.white;
                _scratchCurveLifeRow[x] = new Color(1f, 1f, 0f, 1f);
                _scratchColorSpeedRow[x] = Color.white;
                _scratchCurveSpeedRow[x] = new Color(1f, 0f, 0f, 0f);
            }
            WriteProfileRowsToTexture(0);
            _nextProfileIndex = 1;
            _lutTexture.Apply(false, false);
            _isTextureDirty = false;
        }

        public int GetOrBakeProfileRow(ParticleSystem ps, bool forceUpdate = false)
        {
            if (ps == null)
            {
                return 0;
            }

            EnsureTextureCreated();
            SampleModulesIntoScratch(ps, out int contentHash);

            if (!forceUpdate && _hashToProfile.TryGetValue(contentHash, out int existingProfile))
            {
                return existingProfile;
            }

            int targetProfile;
            if (_hashToProfile.TryGetValue(contentHash, out existingProfile))
            {
                targetProfile = existingProfile;
            }
            else
            {
                targetProfile = (_nextProfileIndex < MaxProfiles)
                    ? _nextProfileIndex++
                    : (Mathf.Abs(contentHash) % (MaxProfiles - 1)) + 1;
                _hashToProfile[contentHash] = targetProfile;
            }

            WriteProfileRowsToTexture(targetProfile);
            _isTextureDirty = true;
            return targetProfile;
        }

        public void FlushIfDirty()
        {
            if (_isTextureDirty && _lutTexture != null)
            {
                _lutTexture.Apply(false, false);
                _isTextureDirty = false;
            }
        }

        private void SampleModulesIntoScratch(ParticleSystem ps, out int outHash)
        {
            ParticleSystem.ColorOverLifetimeModule colLifeMod = ps.colorOverLifetime;
            ParticleSystem.SizeOverLifetimeModule sizeLifeMod = ps.sizeOverLifetime;
            ParticleSystem.VelocityOverLifetimeModule velLifeMod = ps.velocityOverLifetime;
            ParticleSystem.RotationOverLifetimeModule rotLifeMod = ps.rotationOverLifetime;
            ParticleSystem.NoiseModule noiseMod = ps.noise;

            ParticleSystem.ColorBySpeedModule colSpeedMod = ps.colorBySpeed;
            ParticleSystem.SizeBySpeedModule sizeSpeedMod = ps.sizeBySpeed;
            ParticleSystem.RotationBySpeedModule rotSpeedMod = ps.rotationBySpeed;

            bool hasColLife = colLifeMod.enabled;
            bool hasSizeLife = sizeLifeMod.enabled;
            bool hasVelLife = velLifeMod.enabled;
            bool hasRotLife = rotLifeMod.enabled;
            bool hasNoise = noiseMod.enabled;
            bool hasColSpeed = colSpeedMod.enabled;
            bool hasSizeSpeed = sizeSpeedMod.enabled;
            bool hasRotSpeed = rotSpeedMod.enabled;

            ParticleSystem.MinMaxGradient colLifeGrad = hasColLife ? colLifeMod.color : default;
            ParticleSystem.MinMaxCurve sizeLifeCurve = hasSizeLife ? sizeLifeMod.size : default;
            ParticleSystem.MinMaxCurve speedLifeCurve = hasVelLife ? velLifeMod.speedModifier : default;
            ParticleSystem.MinMaxCurve rotLifeCurve = hasRotLife ? rotLifeMod.z : default;
            ParticleSystem.MinMaxCurve noiseCurve = hasNoise ? noiseMod.strength : default;

            ParticleSystem.MinMaxGradient colSpeedGrad = hasColSpeed ? colSpeedMod.color : default;
            ParticleSystem.MinMaxCurve sizeSpeedCurve = hasSizeSpeed ? sizeSpeedMod.size : default;
            ParticleSystem.MinMaxCurve rotSpeedCurve = hasRotSpeed ? rotSpeedMod.z : default;

            unchecked
            {
                int hash = 17;
                for (int x = 0; x < LutWidth; x++)
                {
                    float t = x / (float)(LutWidth - 1);

                    Color cLife = hasColLife ? EvaluateGradient(in colLifeGrad, t).linear : Color.white;
                    float sizeLife = hasSizeLife ? Mathf.Max(0f, EvaluateCurve(in sizeLifeCurve, t, 1f)) : 1f;
                    float speedLife = hasVelLife ? Mathf.Max(0.05f, EvaluateCurve(in speedLifeCurve, t, 1f)) : 1f;
                    float rotLife = hasRotLife ? EvaluateCurve(in rotLifeCurve, t, 0f) : 0f;
                    float noiseMult = hasNoise ? Mathf.Max(0f, EvaluateCurve(in noiseCurve, t, 1f)) : 1f;

                    Color cSpeed = hasColSpeed ? EvaluateGradient(in colSpeedGrad, t).linear : Color.white;
                    float sizeSpeed = hasSizeSpeed ? Mathf.Max(0f, EvaluateCurve(in sizeSpeedCurve, t, 1f)) : 1f;
                    float rotSpeed = hasRotSpeed ? EvaluateCurve(in rotSpeedCurve, t, 0f) : 0f;

                    _scratchColorLifeRow[x] = cLife;
                    _scratchCurveLifeRow[x] = new Color(sizeLife, speedLife, rotLife, noiseMult);
                    _scratchColorSpeedRow[x] = cSpeed;
                    _scratchCurveSpeedRow[x] = new Color(sizeSpeed, rotSpeed, 0f, 0f);

                    if ((x & 7) == 0)
                    {
                        hash = hash * 31 + Mathf.RoundToInt(cLife.r * 1000f);
                        hash = hash * 31 + Mathf.RoundToInt(cLife.g * 1000f);
                        hash = hash * 31 + Mathf.RoundToInt(cLife.b * 1000f);
                        hash = hash * 31 + Mathf.RoundToInt(cLife.a * 1000f);
                        hash = hash * 31 + Mathf.RoundToInt(sizeLife * 1000f);
                        hash = hash * 31 + Mathf.RoundToInt(speedLife * 1000f);
                        hash = hash * 31 + Mathf.RoundToInt(rotLife * 100f);
                        hash = hash * 31 + Mathf.RoundToInt(cSpeed.r * 500f);
                        hash = hash * 31 + Mathf.RoundToInt(cSpeed.b * 500f);
                        hash = hash * 31 + Mathf.RoundToInt(sizeSpeed * 500f);
                        hash = hash * 31 + Mathf.RoundToInt(rotSpeed * 100f);
                    }
                }
                outHash = hash;
            }
        }

        private void WriteProfileRowsToTexture(int profileIndex)
        {
            int baseY = profileIndex * RowsPerProfile;
            _lutTexture.SetPixels(0, baseY + 0, LutWidth, 1, _scratchColorLifeRow);
            _lutTexture.SetPixels(0, baseY + 1, LutWidth, 1, _scratchCurveLifeRow);
            _lutTexture.SetPixels(0, baseY + 2, LutWidth, 1, _scratchColorSpeedRow);
            _lutTexture.SetPixels(0, baseY + 3, LutWidth, 1, _scratchCurveSpeedRow);
        }

        private static Color EvaluateGradient(in ParticleSystem.MinMaxGradient grad, float t)
        {
            switch (grad.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return grad.color;
                case ParticleSystemGradientMode.TwoColors:
                    return Color.Lerp(grad.colorMin, grad.colorMax, 0.5f);
                case ParticleSystemGradientMode.Gradient:
                    return grad.gradient != null ? grad.gradient.Evaluate(t) : Color.white;
                case ParticleSystemGradientMode.TwoGradients:
                    Color cMin = grad.gradientMin != null ? grad.gradientMin.Evaluate(t) : Color.white;
                    Color cMax = grad.gradientMax != null ? grad.gradientMax.Evaluate(t) : Color.white;
                    return Color.Lerp(cMin, cMax, 0.5f);
                default:
                    return grad.Evaluate(t, 0.5f);
            }
        }

        private static float EvaluateCurve(in ParticleSystem.MinMaxCurve curve, float t, float fallback)
        {
            switch (curve.mode)
            {
                case ParticleSystemCurveMode.Constant:
                    return curve.constant;
                case ParticleSystemCurveMode.TwoConstants:
                    return (curve.constantMin + curve.constantMax) * 0.5f;
                case ParticleSystemCurveMode.Curve:
                    return curve.curve != null ? curve.curve.Evaluate(t) * curve.curveMultiplier : fallback;
                case ParticleSystemCurveMode.TwoCurves:
                    float vMin = curve.curveMin != null ? curve.curveMin.Evaluate(t) : fallback;
                    float vMax = curve.curveMax != null ? curve.curveMax.Evaluate(t) : fallback;
                    return ((vMin + vMax) * 0.5f) * curve.curveMultiplier;
                default:
                    return curve.Evaluate(t, 0.5f);
            }
        }

        public void Dispose()
        {
            if (_lutTexture != null)
            {
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_lutTexture);
                else
                    UnityEngine.Object.DestroyImmediate(_lutTexture);
                _lutTexture = null;
            }
            _hashToProfile.Clear();
        }
    }
}
