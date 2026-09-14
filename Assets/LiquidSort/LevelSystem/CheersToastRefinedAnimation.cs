using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Plays the cheers toast. Every visual (glasses, liquids, bubbles, title, glint) is authored under
    /// RefinedVisuals in the prefab; this component only poses them from the Animator clock.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [AddComponentMenu("Liquid Sort/Cheers Toast Refined Animation")]
    public sealed class CheersToastRefinedAnimation : MonoBehaviour
    {
        private const int BubbleCount = 15;

        [Header("Animator clock in seconds")]
        [SerializeField, Min(0f)] public float timelineSeconds;
        [SerializeField] private float sampledSeconds;

        [Serializable]
        private sealed class GlassParts
        {
            public RectTransform root;
            public CanvasGroup group;
            [Tooltip("Masked liquid; its material is copied at runtime so the wave never edits the asset.")]
            public RawImage liquid;
        }

        [Header("Authored visuals (RefinedVisuals)")]
        [SerializeField] private RectTransform visualRoot;
        [SerializeField] private CanvasGroup visualGroup;
        [SerializeField] private GlassParts cocktail = new GlassParts();
        [SerializeField] private GlassParts mug = new GlassParts();
        [SerializeField] private RectTransform titleRoot;
        [SerializeField] private CanvasGroup titleGroup;
        [SerializeField] private RawImage bartenderImage;
        [SerializeField] private RawImage sortImage;
        [SerializeField] private CanvasGroup glintGroup;
        [SerializeField] private CheersToastRefinedGlintGraphic glint;
        [Tooltip("In splash order; each bubble's authored position is where it leaves the liquid.")]
        [SerializeField] private RawImage[] orangeBubbles = new RawImage[BubbleCount];
        [SerializeField] private RawImage[] blueBubbles = new RawImage[BubbleCount];

        public bool IsInitialized => initialized && visualRoot != null;

        private const float ContactTime = BartenderCheersSequence.ContactTime;
        private const float FinishedTime = BartenderCheersSequence.Duration;
        private const float GlassYOffset = 22f;
        private const float TitleArrivalTime = BartenderCheersSequence.TitleTime;
        private static readonly Vector2 ImpactPoint = new Vector2(360f, 435f);
        private static readonly int WaveId = Shader.PropertyToID("_Wave");
        private static readonly int SloshId = Shader.PropertyToID("_Slosh");
        private static readonly int TitleWaveId = Shader.PropertyToID("_TitleWave");
        private readonly List<UnityEngine.Object> ownedResources = new List<UnityEngine.Object>();
        private readonly List<Drop> drops = new List<Drop>(BubbleCount * 2);
        private bool initialized;
        private bool missingPartsReported;
        private Glass orange, blue;

        private sealed class Glass
        {
            public float scale, angle, sourceU;
            public int side;
            public Vector2 rim, start, contact, arcA, arcB, hold, finish;
            public float startAngle, holdAngle, finishAngle;
            public RectTransform root, liquid;
            public CanvasGroup group;
            public RawImage liquidImage;
        }

        private struct Pose
        {
            public Vector2 center;
            public float angle, scale, alpha;
        }

        private struct LiquidPose
        {
            public float y, angle, crest, back;
        }

        private sealed class Drop
        {
            public float vx, vy, release, life, gravity;
            public Vector2 origin;
            public RectTransform rect;
            public RawImage image;
        }

        private void Awake()
        {
            if (Application.isPlaying) EnsureInitialized();
        }

        private void OnEnable()
        {
            if (Application.isPlaying) Sample(0f);
        }

        private void LateUpdate()
        {
            // After the clip ends its last pose is fully faded out; nothing is left to write each frame.
            if (!Application.isPlaying
                || (timelineSeconds >= FinishedTime && sampledSeconds >= FinishedTime)) return;
            Sample(timelineSeconds);
        }

        public void Sample(float t)
        {
            if (!EnsureInitialized()) return;
            sampledSeconds = float.IsNaN(t) || float.IsInfinity(t) ? 0f : Mathf.Max(0f, t);
            t = sampledSeconds;
            visualGroup.alpha = 1f - Smooth((t - 1.6f) / .4f);
            SampleGlass(blue, t);
            SampleGlass(orange, t);
            SampleTitle(t);
            SampleDrops(t);
            SampleGlint(t);
        }

        private bool EnsureInitialized()
        {
            if (IsInitialized) return true;
            if (!HasAuthoredParts()) return false;
            ReleaseResources();
            drops.Clear();
            blue = CreateGlass(false, mug);
            orange = CreateGlass(true, cocktail);
            OwnMaterial(bartenderImage);
            OwnMaterial(sortImage);
            CreateDrops(orange, orangeBubbles);
            CreateDrops(blue, blueBubbles);
            initialized = true;
            return true;
        }

        private bool HasAuthoredParts()
        {
            bool ready = visualRoot != null && visualGroup != null && IsComplete(cocktail) && IsComplete(mug)
                         && titleRoot != null && titleGroup != null && bartenderImage != null && sortImage != null
                         && glintGroup != null && glint != null
                         && IsComplete(orangeBubbles) && IsComplete(blueBubbles);
            if (!ready && !missingPartsReported)
            {
                missingPartsReported = true;
                Debug.LogError("Cheers toast visuals are not assigned on the prefab.", this);
            }
            return ready;
        }

        private static bool IsComplete(GlassParts parts) =>
            parts != null && parts.root != null && parts.group != null && parts.liquid != null;

        private static bool IsComplete(RawImage[] images) =>
            images != null && images.Length == BubbleCount && Array.TrueForAll(images, image => image != null);

        private Glass CreateGlass(bool isOrange, GlassParts parts)
        {
            Glass s = new Glass();
            s.scale = isOrange ? .76f : .72f;
            s.side = isOrange ? -1 : 1;
            s.rim = isOrange ? new Vector2(127.67f, -133f) : new Vector2(-130.70f, -171f);
            s.angle = (isOrange ? 23f : -18f) * Mathf.Deg2Rad;
            s.startAngle = (isOrange ? 16f : -12f) * Mathf.Deg2Rad;
            s.holdAngle = (isOrange ? 26f : -19.5f) * Mathf.Deg2Rad;
            s.finishAngle = (isOrange ? 10f : -7f) * Mathf.Deg2Rad;
            s.sourceU = isOrange ? .718f : .255f;
            s.hold = isOrange ? new Vector2(357.8f, 425) : new Vector2(361, 432);
            s.finish = isOrange ? new Vector2(346, 428) : new Vector2(376, 433);
            s.contact = ImpactPoint - Rotate(s.rim, s.angle) * s.scale;
            s.start = new Vector2(isOrange ? -88 : 812, s.contact.y + (isOrange ? 18 : 10));
            s.arcA = s.start + (isOrange ? new Vector2(84, -9) : new Vector2(-96, -5));
            s.arcB = s.contact + (isOrange ? new Vector2(-64, 2.5f) : new Vector2(72, 2));
            s.root = parts.root;
            s.group = parts.group;
            s.liquidImage = parts.liquid;
            s.liquid = parts.liquid.rectTransform;
            OwnMaterial(s.liquidImage);
            return s;
        }

        private void CreateDrops(Glass glass, RawImage[] images)
        {
            bool isBlue = glass.side > 0;
            for (int k = 0; k < images.Length; k++)
            {
                float fan = (k % 8) / 7f;
                bool outer = k < 8;
                Vector2 authored = images[k].rectTransform.anchoredPosition;
                drops.Add(new Drop
                {
                    vx = (fan * 2 - 1) * (outer ? 590 : 410) + (isBlue ? 34 : -34),
                    vy = -(outer ? 745 : 545) - (1 - Mathf.Abs(fan * 2 - 1)) * 240,
                    release = ContactTime + (isBlue ? .086f : .064f) + (k % 3) * .012f + (k / 8) * .018f,
                    life = outer ? .62f : .49f,
                    gravity = 720,
                    origin = new Vector2(authored.x + 360, 360 - authored.y),
                    rect = images[k].rectTransform,
                    image = images[k]
                });
            }
        }

        private void SampleGlass(Glass s, float t)
        {
            Pose p = GetPose(s, t);
            LiquidPose l = GetLiquidPose(s, t);
            s.root.anchoredPosition = ToUnity(p.center + new Vector2(0, GlassYOffset));
            s.root.localRotation = Quaternion.Euler(0, 0, -p.angle * Mathf.Rad2Deg);
            s.root.localScale = Vector3.one * p.scale;
            s.group.alpha = p.alpha;
            Vector2 maskCenter = ((RectTransform)s.liquid.parent).anchoredPosition;
            s.liquid.anchoredPosition = -maskCenter + new Vector2(0, -l.y);
            s.liquid.localRotation = Quaternion.Euler(0, 0, -l.angle * Mathf.Rad2Deg);
            SetVector(s.liquidImage, WaveId, new Vector4(l.crest, l.back, s.sourceU, s.side < 0 ? .34f : .59f));
            SetVector(s.liquidImage, SloshId, new Vector4(-l.y, -l.angle, 0, 0));
        }

        private static Pose GetPose(Glass s, float t)
        {
            Pose p = new Pose { scale = s.scale, alpha = Smooth((t - .02f) / .07f) };
            if (t <= ContactTime)
            {
                float v = Mathf.Clamp01((t - .012f) / (ContactTime - .012f));
                p.center = new Vector2(Cubic(s.start.x, s.arcA.x, s.arcB.x, s.contact.x, v),
                    Cubic(s.start.y, s.arcA.y, s.arcB.y, s.contact.y, v));
                p.angle = Mathf.LerpUnclamped(s.startAngle, s.angle, Cubic(0, .14f, 1 - .32f / 3, 1, v));
            }
            else
            {
                float age = t - ContactTime;
                Vector2 rim;
                if (age <= .065f)
                {
                    float v = Mathf.Clamp01(age / .065f);
                    float u = Cubic(0, .70f / 3, 1, 1, v);
                    rim = Vector2.LerpUnclamped(ImpactPoint, s.hold, u);
                    p.angle = Mathf.LerpUnclamped(s.angle, s.holdAngle, u);
                }
                else
                {
                    float u = Smooth((age - .065f) / (.590f - .065f));
                    rim = Vector2.LerpUnclamped(s.hold, s.finish, u);
                    p.angle = Mathf.LerpUnclamped(s.holdAngle, s.finishAngle, u);
                }
                p.center = rim - Rotate(s.rim, p.angle) * s.scale;
            }
            return p;
        }

        private static LiquidPose GetLiquidPose(Glass s, float t)
        {
            Pose lagged = GetPose(s, Mathf.Max(0, t - .034f));
            float age = t - ContactTime - .018f;
            float phase = Mathf.Max(0, age) / (s.side < 0 ? .085f : .11f);
            float crest = age > 0 ? phase * phase * Mathf.Exp(2 - 2 * phase) : 0;
            return new LiquidPose
            {
                y = Smooth((t - ContactTime - .19f) / .38f) * (s.side < 0 ? 18 : 27) - crest * 3.5f,
                angle = -lagged.angle * .78f, crest = crest,
                back = Smooth((t - ContactTime - .24f) / .08f) * (1 - Smooth((t - ContactTime - .36f) / .18f))
            };
        }

        private void SampleTitle(float t)
        {
            float age = t - TitleArrivalTime;
            float arriving = EaseOut(age / .155f);
            float scale = age <= .155f ? Mathf.LerpUnclamped(.96f, 1.016f, arriving)
                : Mathf.LerpUnclamped(1.016f, 1, Smooth((age - .155f) / .20f));
            titleRoot.anchoredPosition = new Vector2(0, 35 - 24 * (1 - arriving));
            titleRoot.localScale = Vector3.one * scale;
            titleGroup.alpha = age < 0 ? 0 : Smooth(age / .075f);
            SetVector(bartenderImage, TitleWaveId, new Vector4(TitleSlosh(t, 0), 1, 143.55f, 1));
            SetVector(sortImage, TitleWaveId, new Vector4(TitleSlosh(t, .018f), -.86f, 108.87f, 0));
        }

        private static float TitleSlosh(float t, float delay)
        {
            float age = t - ContactTime - .045f - delay;
            if (age <= 0 || age >= .64f) return 0;
            float u = age / .64f;
            return 11.8f * Mathf.Sin(u * Mathf.PI * 2) * Mathf.Exp(-2.55f * u)
                * (1 - Smooth((u - .73f) / .27f));
        }

        private void SampleDrops(float t)
        {
            foreach (Drop d in drops)
            {
                float age = t - d.release;
                bool visible = age >= 0 && age < d.life;
                if (d.image.enabled != visible) d.image.enabled = visible;
                if (!visible) continue;
                float alpha = Smooth(age / .025f) * (1 - Smooth((age / d.life - .80f) / .20f));
                d.image.color = new Color(1, 1, 1, alpha);
                d.rect.anchoredPosition = ToUnity(d.origin + new Vector2(d.vx * age, d.vy * age + d.gravity * age * age));
                d.rect.localScale = Vector3.one * (1 - .06f * Smooth(age / d.life));
            }
        }

        private void SampleGlint(float t)
        {
            float age = t - ContactTime;
            bool visible = age >= 0 && age <= .105f;
            float p = Mathf.Clamp01(age / .105f);
            glintGroup.alpha = visible ? (1 - Smooth(p)) * .9f : 0;
            if (visible) glint.SetShape(4 + 11 * Mathf.Sin(Mathf.PI * Mathf.Pow(p, .6f)), 1.8f);
        }

        private static void SetVector(RawImage image, int property, Vector4 value)
        {
            image.material.SetVector(property, value);
            Material rendering = image.materialForRendering;
            if (rendering != image.material) rendering.SetVector(property, value);
        }

        // The authored material asset stays untouched; each toast animates its own copy.
        private void OwnMaterial(RawImage image)
        {
            Material authored = image.material;
            if (authored == null || authored == Graphic.defaultGraphicMaterial) return;
            image.material = Own(new Material(authored) { name = authored.name + " (Runtime)" });
        }

        private T Own<T>(T resource) where T : UnityEngine.Object
        {
            resource.hideFlags = HideFlags.HideAndDontSave;
            ownedResources.Add(resource);
            return resource;
        }

        private void ReleaseResources()
        {
            foreach (UnityEngine.Object resource in ownedResources)
                if (resource != null) DestroyOwned(resource);
            ownedResources.Clear();
        }

        private void OnDestroy()
        {
            initialized = false;
            ReleaseResources();
        }

        private static void DestroyOwned(UnityEngine.Object resource)
        {
            if (Application.isPlaying) Destroy(resource);
            else DestroyImmediate(resource);
        }

        private static Vector2 ToUnity(Vector2 point) => new Vector2(point.x - 360, 360 - point.y);
        private static Vector2 Rotate(Vector2 p, float angle)
        {
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            return new Vector2(c * p.x - s * p.y, s * p.x + c * p.y);
        }
        private static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }
        private static float EaseOut(float x) => 1 - Mathf.Pow(1 - Mathf.Clamp01(x), 3);
        private static float Cubic(float a, float b, float c, float d, float u)
        {
            float v = 1 - u;
            return v * v * v * a + 3 * v * v * u * b + 3 * v * u * u * c + u * u * u * d;
        }
    }
}
