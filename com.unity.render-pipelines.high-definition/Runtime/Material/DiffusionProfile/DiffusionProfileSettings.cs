using System;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    [GenerateHLSL]
    public class DiffusionProfileConstants
    {
        public const int DIFFUSION_PROFILE_COUNT      = 17; // Max. number of profiles, including the slot taken by the neutral profile
        public const int DIFFUSION_PROFILE_NEUTRAL_ID = 0;  // Does not result in blurring
        public const int SSS_N_SAMPLES_NEAR_FIELD     = 55; // Used for extreme close ups; must be a Fibonacci number
        public const int SSS_N_SAMPLES_FAR_FIELD      = 21; // Used at a regular distance; must be a Fibonacci number
        public const int SSS_LOD_THRESHOLD            = 4;  // The LoD threshold of the near-field kernel (in pixels)
    }

    [Serializable]
    public sealed class DiffusionProfile : IEquatable<DiffusionProfile>
    {
        public enum TexturingMode : uint
        {
            PreAndPostScatter = 0,
            PostScatter = 1
        }

        public enum TransmissionMode : uint
        {
            Regular = 0,
            ThinObject = 1
        }

        public string name;

        [ColorUsage(false, true)]
        public Color            scatteringDistance;         // Per color channel (no meaningful units)
        [ColorUsage(false, true)]
        public Color            transmissionTint;           // HDR color
        public TexturingMode    texturingMode;
        public TransmissionMode transmissionMode;
        public Vector2          thicknessRemap;             // X = min, Y = max (in millimeters)
        public float            worldScale;                 // Size of the world unit in meters
        public float            ior;                        // 1.4 for skin (mean ~0.028)

        public Vector3          shapeParam { get; private set; }               // RGB = shape parameter: S = 1 / D
        public float            maxRadius { get; private set; }                // In millimeters
        public Vector2[]        filterKernelNearField { get; private set; }    // X = radius, Y = reciprocal of the PDF
        public Vector2[]        filterKernelFarField { get; private set; }     // X = radius, Y = reciprocal of the PDF
        public Vector4          halfRcpWeightedVariances { get; private set; }
        public Vector4[]        filterKernelBasic { get; private set; }

        // Unique hash used in shaders to identify the index in the diffusion profile array
        public uint             hash = 0;

        public DiffusionProfile(string name)
        {
            this.name          = name;

            scatteringDistance = Color.grey;
            transmissionTint   = Color.white;
            texturingMode      = TexturingMode.PreAndPostScatter;
            transmissionMode   = TransmissionMode.ThinObject;
            thicknessRemap     = new Vector2(0f, 5f);
            worldScale         = 1f;
            ior                = 1.4f; // Typical value for skin specular reflectance
        }

        public void Validate()
        {
            thicknessRemap.y = Mathf.Max(thicknessRemap.y, 0f);
            thicknessRemap.x = Mathf.Clamp(thicknessRemap.x, 0f, thicknessRemap.y);
            worldScale       = Mathf.Max(worldScale, 0.001f);
            ior              = Mathf.Clamp(ior, 1.0f, 2.0f);

            UpdateKernel();
        }

        // Ref: Approximate Reflectance Profiles for Efficient Subsurface Scattering by Pixar.
        public void UpdateKernel()
        {
            if (filterKernelNearField == null || filterKernelNearField.Length != DiffusionProfileConstants.SSS_N_SAMPLES_NEAR_FIELD)
                filterKernelNearField = new Vector2[DiffusionProfileConstants.SSS_N_SAMPLES_NEAR_FIELD];

            if (filterKernelFarField == null || filterKernelFarField.Length != DiffusionProfileConstants.SSS_N_SAMPLES_FAR_FIELD)
                filterKernelFarField = new Vector2[DiffusionProfileConstants.SSS_N_SAMPLES_FAR_FIELD];

            // Note: if the scattering distance is 0, exp2(-inf) will produce 0, as desired.
            shapeParam = new Vector3(1.0f / scatteringDistance.r,
                                     1.0f / scatteringDistance.g,
                                     1.0f / scatteringDistance.b);

            // We importance sample the color channel with the widest scattering distance.
            float s = Mathf.Min(shapeParam.x, shapeParam.y, shapeParam.z);

            // Importance sample the normalized diffuse reflectance profile for the computed value of 's'.
            // ------------------------------------------------------------------------------------
            // R[r, phi, s]   = s * (Exp[-r * s] + Exp[-r * s / 3]) / (8 * Pi * r)
            // PDF[r, phi, s] = r * R[r, phi, s]
            // CDF[r, s]      = 1 - 1/4 * Exp[-r * s] - 3/4 * Exp[-r * s / 3]
            // ------------------------------------------------------------------------------------

            // Importance sample the near field kernel.
            for (int i = 0, n = DiffusionProfileConstants.SSS_N_SAMPLES_NEAR_FIELD; i < n; i++)
            {
                float p = (i + 0.5f) * (1.0f / n);
                float r = DisneyProfileCdfInverse(p, s);

                // N.b.: computation of normalized weights, and multiplication by the surface albedo
                // of the actual geometry is performed at runtime (in the shader).
                filterKernelNearField[i].x = r;
                filterKernelNearField[i].y = 1f / DisneyProfilePdf(r, s);
            }

            // Importance sample the far field kernel.
            for (int i = 0, n = DiffusionProfileConstants.SSS_N_SAMPLES_FAR_FIELD; i < n; i++)
            {
                float p = (i + 0.5f) * (1.0f / n);
                float r = DisneyProfileCdfInverse(p, s);

                // N.b.: computation of normalized weights, and multiplication by the surface albedo
                // of the actual geometry is performed at runtime (in the shader).
                filterKernelFarField[i].x = r;
                filterKernelFarField[i].y = 1f / DisneyProfilePdf(r, s);
            }

            maxRadius = filterKernelFarField[DiffusionProfileConstants.SSS_N_SAMPLES_FAR_FIELD - 1].x;
        }

        static float DisneyProfile(float r, float s)
        {
            return s * (Mathf.Exp(-r * s) + Mathf.Exp(-r * s * (1.0f / 3.0f))) / (8.0f * Mathf.PI * r);
        }

        static float DisneyProfilePdf(float r, float s)
        {
            return r * DisneyProfile(r, s);
        }

        static float DisneyProfileCdf(float r, float s)
        {
            return 1.0f - 0.25f * Mathf.Exp(-r * s) - 0.75f * Mathf.Exp(-r * s * (1.0f / 3.0f));
        }

        static float DisneyProfileCdfDerivative1(float r, float s)
        {
            return 0.25f * s * Mathf.Exp(-r * s) * (1.0f + Mathf.Exp(r * s * (2.0f / 3.0f)));
        }

        static float DisneyProfileCdfDerivative2(float r, float s)
        {
            return (-1.0f / 12.0f) * s * s * Mathf.Exp(-r * s) * (3.0f + Mathf.Exp(r * s * (2.0f / 3.0f)));
        }

        // The CDF is not analytically invertible, so we use Halley's Method of root finding.
        // { f(r, s, p) = CDF(r, s) - p = 0 } with the initial guess { r = (10^p - 1) / s }.
        static float DisneyProfileCdfInverse(float p, float s)
        {
            // Supply the initial guess.
            float r = (Mathf.Pow(10f, p) - 1f) / s;
            float t = float.MaxValue;

            while (true)
            {
                float f0 = DisneyProfileCdf(r, s) - p;
                float f1 = DisneyProfileCdfDerivative1(r, s);
                float f2 = DisneyProfileCdfDerivative2(r, s);
                float dr = f0 / (f1 * (1f - f0 * f2 / (2f * f1 * f1)));

                if (Mathf.Abs(dr) < t)
                {
                    r = r - dr;
                    t = Mathf.Abs(dr);
                }
                else
                {
                    // Converged to the best result.
                    break;
                }
            }

            return r;
        }

        public bool Equals(DiffusionProfile other)
        {
            if (other == null)
                return false;

            return  scatteringDistance == other.scatteringDistance &&
                    transmissionTint == other.transmissionTint &&
                    texturingMode == other.texturingMode &&
                    transmissionMode == other.transmissionMode &&
                    thicknessRemap == other.thicknessRemap &&
                    worldScale == other.worldScale &&
                    ior == other.ior &&
                    shapeParam == other.shapeParam &&
                    maxRadius == other.maxRadius &&
                    filterKernelNearField == other.filterKernelNearField &&
                    filterKernelFarField == other.filterKernelFarField &&
                    halfRcpWeightedVariances == other.halfRcpWeightedVariances &&
                    filterKernelBasic == other.filterKernelBasic;
        }
    }

    public sealed partial class DiffusionProfileSettings : ScriptableObject, ISerializationCallbackReceiver
    {
        public DiffusionProfile profile;

        [NonSerialized] public Vector4 thicknessRemaps;           // Remap: 0 = start, 1 = end - start
        [NonSerialized] public Vector4 worldScales;               // X = meters per world unit; Y = world units per meter
        [NonSerialized] public Vector4 shapeParams;               // RGB = S = 1 / D, A = filter radius
        [NonSerialized] public Vector4 transmissionTintsAndFresnel0; // RGB = color, A = fresnel0
        [NonSerialized] public Vector4 disabledTransmissionTintsAndFresnel0; // RGB = black, A = fresnel0 - For debug to remove the transmission
        [NonSerialized] public Vector4[] filterKernels;             // XY = near field, ZW = far field; 0 = radius, 1 = reciprocal of the PDF
        
        void OnEnable()
        {
            k_Migration.Migrate(this);

            if (profile == null)
                profile = new DiffusionProfile("Diffusion Profile ");

            profile.Validate();

            UpdateCache();
        }

        public void UpdateCache()
        {
            if (filterKernels == null)
                filterKernels = new Vector4[DiffusionProfileConstants.SSS_N_SAMPLES_NEAR_FIELD];

            thicknessRemaps  = new Vector4(profile.thicknessRemap.x, profile.thicknessRemap.y - profile.thicknessRemap.x, 0f, 0f);
            worldScales      = new Vector4(profile.worldScale, 1.0f / profile.worldScale, 0f, 0f);

            // Premultiply S by ((-1.0 / 3.0) * LOG2_E) on the CPU.
            const float log2e = 1.44269504088896340736f;
            const float k     = (-1.0f / 3.0f) * log2e;

            shapeParams   = profile.shapeParam * k;
            shapeParams.w = profile.maxRadius;
            // Convert ior to fresnel0
            float fresnel0 = (profile.ior - 1.0f) / (profile.ior + 1.0f);
            fresnel0 *= fresnel0; // square
            transmissionTintsAndFresnel0 = new Vector4(profile.transmissionTint.r * 0.25f, profile.transmissionTint.g * 0.25f, profile.transmissionTint.b * 0.25f, fresnel0); // Premultiplied
            disabledTransmissionTintsAndFresnel0 = new Vector4(0.0f, 0.0f, 0.0f, fresnel0);

            for (int n = 0; n < DiffusionProfileConstants.SSS_N_SAMPLES_NEAR_FIELD; n++)
            {
                filterKernels[n].x = profile.filterKernelNearField[n].x;
                filterKernels[n].y = profile.filterKernelNearField[n].y;

                if (n < DiffusionProfileConstants.SSS_N_SAMPLES_FAR_FIELD)
                {
                    filterKernels[n].z = profile.filterKernelFarField[n].x;
                    filterKernels[n].w = profile.filterKernelFarField[n].y;
                }
            }
        }

        // Initialize the settings for the default diffusion  profile
        public void SetDefaultParams()
        {
            worldScales = Vector4.one;
            shapeParams = Vector4.zero;
            transmissionTintsAndFresnel0.w = 0.04f; // Match DEFAULT_SPECULAR_VALUE defined in Lit.hlsl

            for (int n = 0; n < DiffusionProfileConstants.SSS_N_SAMPLES_NEAR_FIELD; n++)
            {
                filterKernels[n].x = 0f;
                filterKernels[n].y = 1f;
                filterKernels[n].z = 0f;
                filterKernels[n].w = 1f;
            }
        }

        public void OnBeforeSerialize() {}

        public void OnAfterDeserialize()
        {
#if UNITY_EDITOR
            // watch for asset duplication, a workaround because the AssetModificationProcessor doesn't handle all the cases
            // i.e: https://issuetracker.unity3d.com/issues/assetmodificationprocessor-is-not-notified-when-an-asset-is-duplicated
            UnityEditor.Experimental.Rendering.HDPipeline.DiffusionProfileHashTable.UpdateUniqueHash(this);
#endif
        }
    }
}
