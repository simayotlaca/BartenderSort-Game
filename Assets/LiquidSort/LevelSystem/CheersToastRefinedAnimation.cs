using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Native uGUI rendition of the approved 720px cheers preview. The Animator supplies
    /// a seconds clock; this component owns only its generated visuals, never audio or cards.
    /// Sampling is deterministic, including backward seeks and repeated result-screen plays.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    [AddComponentMenu("Liquid Sort/Cheers Toast Refined Animation")]
    public sealed class CheersToastRefinedAnimation : MonoBehaviour
    {
        [Header("Animator clock in seconds")]
        [SerializeField, Min(0f)] public float timelineSeconds;
        [SerializeField] private float sampledSeconds;

        [Header("Original artwork; full texture canvases are preserved")]
        public Texture2D bartenderTexture;
        public Texture2D sortTexture;
        public Texture2D cocktailFrontTexture;
        public Texture2D mugFrontTexture;
        public Texture2D orangeLiquidTexture;
        public Texture2D blueLiquidTexture;
        public Texture2D orangeBubbleTexture;
        public Texture2D blueBubbleTexture;
        public Texture2D cocktailMaskTexture;
        public Texture2D mugMaskTexture;
        public Shader liquidShader;

        public bool IsInitialized => initialized && visualRoot != null;
        public float SequenceTimeSeconds => sampledSeconds;
        public RectTransform VisualRoot => visualRoot;
        public float RimGap => Vector2.Distance(OrangeRim, BlueRim);
        public Vector2 OrangeRim => GetRim(orange, sampledSeconds);
        public Vector2 BlueRim => GetRim(blue, sampledSeconds);

        private const float ContactTime = BartenderCheersSequence.ContactTime;
        private const float GlassYOffset = 22f;
        private const float TitleArrivalTime = BartenderCheersSequence.TitleTime;
        private static readonly Vector2 ImpactPoint = new Vector2(360f, 435f);
        private static readonly int WaveId = Shader.PropertyToID("_Wave");
        private static readonly int SloshId = Shader.PropertyToID("_Slosh");
        private static readonly int TitleWaveId = Shader.PropertyToID("_TitleWave");
        private readonly List<UnityEngine.Object> ownedResources = new List<UnityEngine.Object>();
        private readonly List<Drop> drops = new List<Drop>(30);
        private bool initialized;
        private RectTransform visualRoot;
        private CanvasGroup visualGroup;
        private Glass orange, blue;
        private RectTransform titleRoot;
        private CanvasGroup titleGroup;
        private RawImage bartenderImage, sortImage;
        private CanvasGroup glintGroup;
        private CheersToastRefinedGlintGraphic glint;

        private sealed class Glass
        {
            public float width, height, aspect, scale, angle, sourceU, surfaceV, crestHeight;
            public int side;
            public Vector2 maskMin, maskMax, rim, start, contact, arcA, arcB, hold, finish;
            public float startAngle, holdAngle, finishAngle;
            public RectTransform root, liquid;
            public CanvasGroup group;
            public RawImage liquidImage;
            public Rect maskRect;
            public Color32[] maskPixels;
            public int maskWidth, maskHeight;
        }

        private struct Pose
        {
            public Vector2 center;
            public float angle, scale, alpha;
        }

        private struct LiquidPose
        {
            public float y, angle, surfaceY, crest, back;
        }

        private sealed class Drop
        {
            public float radius, vx, vy, release, life, gravity;
            public Vector2 origin;
            public RectTransform rect;
            public RawImage image;
        }

        private static readonly float[] BubbleRadii =
        {
            6f, 8.8f, 6.8f, 12f, 8.5f, 6.2f, 11f, 7.5f,
            5.5f, 8f, 6f, 9.5f, 6.5f, 7.5f, 5.8f
        };

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
            if (Application.isPlaying) Sample(timelineSeconds);
        }

        /// <summary>Explicit editor or runtime seek; no wall-clock state or random values are used.</summary>
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
            if (bartenderTexture == null || sortTexture == null || cocktailFrontTexture == null
                || mugFrontTexture == null || orangeLiquidTexture == null || blueLiquidTexture == null
                || orangeBubbleTexture == null || blueBubbleTexture == null
                || cocktailMaskTexture == null || mugMaskTexture == null) return false;
            if (liquidShader == null) liquidShader = Shader.Find("LiquidSort/UI/CheersToastRefinedLiquid");
            if (liquidShader == null) return false;

            // Generated children are not prefab data. Recover cleanly after a domain reload,
            // while ordinary replays retain this subtree and its texture/material caches.
            Transform stale = transform.Find("RefinedVisuals");
            if (stale != null)
            {
                stale.gameObject.SetActive(false);
                DestroyOwned(stale.gameObject);
            }
            ReleaseResources();
            drops.Clear();
            visualRoot = CreateRect("RefinedVisuals", transform, new Vector2(720, 720));
            visualGroup = AddGroup(visualRoot);

            RawImage halo = CreateImage("Atmosphere", visualRoot,
                CreateRadialTexture("Cheers atmosphere", false), new Vector2(550, 550));
            halo.rectTransform.anchoredPosition = ToUnity(new Vector2(360, 470));

            blue = CreateGlass(false);
            orange = CreateGlass(true);
            RectTransform backDrops = CreateRect("BackBubbles", visualRoot, new Vector2(720, 720));
            CreateTitle();
            RectTransform frontDrops = CreateRect("FrontBubbles", visualRoot, new Vector2(720, 720));
            CreateDrops(orange, orangeBubbleTexture, backDrops, frontDrops);
            CreateDrops(blue, blueBubbleTexture, backDrops, frontDrops);

            RectTransform glintRoot = CreateRect("ContactGlint", visualRoot, new Vector2(36, 36));
            glintRoot.anchoredPosition = ToUnity(ImpactPoint + new Vector2(0, GlassYOffset));
            glintGroup = AddGroup(glintRoot);
            glint = glintRoot.gameObject.AddComponent<CheersToastRefinedGlintGraphic>();
            glint.raycastTarget = false;
            glint.color = HtmlColor("FFF8DB");
            initialized = true;
            return true;
        }

        private Glass CreateGlass(bool isOrange)
        {
            Glass s = new Glass();
            s.width = isOrange ? 310 : 285;
            s.height = isOrange ? 387 : 395;
            s.aspect = isOrange ? 4488f / 5608f : 4260f / 5908f;
            s.scale = isOrange ? .76f : .72f;
            s.side = isOrange ? -1 : 1;
            s.rim = isOrange ? new Vector2(127.67f, -133f) : new Vector2(-130.70f, -171f);
            s.angle = (isOrange ? 23f : -18f) * Mathf.Deg2Rad;
            s.startAngle = (isOrange ? 16f : -12f) * Mathf.Deg2Rad;
            s.holdAngle = (isOrange ? 26f : -19.5f) * Mathf.Deg2Rad;
            s.finishAngle = (isOrange ? 10f : -7f) * Mathf.Deg2Rad;
            s.sourceU = isOrange ? .718f : .255f;
            s.surfaceV = isOrange ? .724f : .73f;
            s.crestHeight = isOrange ? 35f : 62f;
            s.maskMin = isOrange ? new Vector2(.09761291f, .360705f) : new Vector2(-.01130712f, -.022911f);
            s.maskMax = isOrange ? new Vector2(.90449594f, .874127f) : new Vector2(.79378159f, 1.009592f);
            s.hold = isOrange ? new Vector2(357.8f, 425) : new Vector2(361, 432);
            s.finish = isOrange ? new Vector2(346, 428) : new Vector2(376, 433);
            s.contact = ImpactPoint - Rotate(s.rim, s.angle) * s.scale;
            s.start = new Vector2(isOrange ? -88 : 812, s.contact.y + (isOrange ? 18 : 10));
            s.arcA = s.start + (isOrange ? new Vector2(84, -9) : new Vector2(-96, -5));
            s.arcB = s.contact + (isOrange ? new Vector2(-64, 2.5f) : new Vector2(72, 2));
            s.root = CreateRect(isOrange ? "Cocktail" : "Mug", visualRoot, new Vector2(s.width, s.height));
            s.group = AddGroup(s.root);

            Vector2 maskSize = Vector2.Scale(s.maskMax - s.maskMin, new Vector2(s.width, s.height));
            Vector2 maskCenter = Vector2.Scale((s.maskMin + s.maskMax - Vector2.one) * .5f,
                new Vector2(s.width, s.height));
            Texture2D maskTexture = isOrange ? cocktailMaskTexture : mugMaskTexture;
            RawImage mask = CreateImage("InteriorMask", s.root, maskTexture, maskSize);
            mask.rectTransform.anchoredPosition = maskCenter;
            mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
            float padding = s.crestHeight + 16f;
            s.liquidImage = CreateImage("Liquid", mask.transform,
                isOrange ? orangeLiquidTexture : blueLiquidTexture,
                new Vector2(s.height, s.height + padding * 2f));
            s.liquid = s.liquidImage.rectTransform;
            s.liquidImage.uvRect = new Rect(0, -padding / s.height, 1, 1 + padding * 2f / s.height);
            s.liquid.anchoredPosition = -maskCenter;
            Material material = Own(new Material(liquidShader) { name = "Cheers " + s.root.name + " liquid" });
            material.SetTexture("_MaskTex", maskTexture);
            material.SetVector("_Geometry", new Vector4(s.height, s.crestHeight, s.width, s.height));
            material.SetVector("_MaskRect", new Vector4(maskCenter.x, maskCenter.y, maskSize.x, maskSize.y));
            s.liquidImage.material = material;
            float frontWidth = Mathf.Min(s.width, s.height * s.aspect);
            CreateImage("Front", s.root, isOrange ? cocktailFrontTexture : mugFrontTexture,
                new Vector2(frontWidth, frontWidth / s.aspect));

            s.maskRect = new Rect((s.maskMin.x - .5f) * s.width, (.5f - s.maskMax.y) * s.height,
                maskSize.x, maskSize.y);
            CacheMask(s, maskTexture);
            return s;
        }

        private void CreateTitle()
        {
            titleRoot = CreateRect("Title", visualRoot, new Vector2(720, 720));
            titleGroup = AddGroup(titleRoot);
            bartenderImage = CreateWord("BARTENDER", bartenderTexture, 120, 68, 480, 143.55f,
                new Rect(22f / 2172f, 1f - (64f + 634f) / 724f, 2120f / 2172f, 634f / 724f), true);
            sortImage = CreateWord("SORT", sortTexture, 202, 216, 316, 108.87f,
                new Rect(50f / 1983f, 1f - (97f + 656f) / 793f, 1904f / 1983f, 656f / 793f), false);
        }

        private RawImage CreateWord(string label, Texture2D texture, float x, float y,
            float width, float height, Rect crop, bool isOrange)
        {
            RawImage word = CreateImage(label, titleRoot, texture, new Vector2(width, height));
            word.rectTransform.anchoredPosition = new Vector2(x + width * .5f - 360, 325 - y - height * .5f);
            word.uvRect = crop;
            Material material = Own(new Material(liquidShader) { name = "Cheers " + label + " faces" });
            material.SetFloat("_Mode", 1);
            material.SetVector("_TitleCrop", new Vector4(crop.x, crop.y, crop.width, crop.height));
            material.SetVector(TitleWaveId, new Vector4(0, isOrange ? 1 : -.86f, height, isOrange ? 1 : 0));
            word.material = material;
            return word;
        }

        private void CreateDrops(Glass glass, Texture2D texture,
            RectTransform back, RectTransform front)
        {
            bool isBlue = glass.side > 0;
            for (int k = 0; k < BubbleRadii.Length; k++)
            {
                float fan = (k % 8) / 7f;
                bool outer = k < 8;
                Drop d = new Drop
                {
                    radius = BubbleRadii[k],
                    vx = (fan * 2 - 1) * (outer ? 590 : 410) + (isBlue ? 34 : -34),
                    vy = -(outer ? 745 : 545) - (1 - Mathf.Abs(fan * 2 - 1)) * 240,
                    release = ContactTime + (isBlue ? .086f : .064f) + (k % 3) * .012f + (k / 8) * .018f,
                    life = outer ? .62f : .49f, gravity = 720
                };
                d.origin = GetOrigin(glass, d.release) + new Vector2(0, GlassYOffset);
                d.image = CreateImage((glass.side < 0 ? "Orange" : "Blue") + "Bubble" + (k + 1).ToString("00"),
                    k % 3 == 0 ? back : front, texture, Vector2.one * d.radius * 2.16f);
                // Padded alpha squares from the original 256px artwork. Cropping the
                // drawing keeps both colors round and matches the preview's diameter.
                d.image.uvRect = isBlue
                    ? new Rect(27f / 256f, 26f / 256f, 201f / 256f, 201f / 256f)
                    : new Rect(40f / 256f, 42f / 256f, 176f / 256f, 176f / 256f);
                d.rect = d.image.rectTransform;
                drops.Add(d);
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
                angle = -lagged.angle * .78f, surfaceY = (.5f - s.surfaceV) * s.height, crest = crest,
                back = Smooth((t - ContactTime - .24f) / .08f) * (1 - Smooth((t - ContactTime - .36f) / .18f))
            };
        }

        private static float WaveOffset(Glass s, LiquidPose l, float x)
        {
            float u = x / s.height + .5f;
            float hump = Mathf.Exp(-Mathf.Pow((u - s.sourceU) / .145f, 2));
            float opposite = Mathf.Exp(-Mathf.Pow((u - (s.side < 0 ? .34f : .59f)) / .19f, 2));
            return -s.crestHeight * l.crest * hump + s.crestHeight * l.back * (.17f * hump - .10f * opposite);
        }

        private static Vector2 GetOrigin(Glass s, float t)
        {
            Pose p = GetPose(s, t);
            LiquidPose l = GetLiquidPose(s, t);
            float x = (s.sourceU - .5f) * s.height;
            Vector2 local = Rotate(new Vector2(x, l.surfaceY + WaveOffset(s, l, x)), l.angle) + new Vector2(0, l.y);
            if (s.maskPixels != null)
            {
                Rect m = s.maskRect;
                int px = Mathf.RoundToInt((local.x - m.x) / m.width * (s.maskWidth - 1));
                int py = Mathf.Max(0, Mathf.RoundToInt((local.y - m.y) / m.height * (s.maskHeight - 1)));
                if (px >= 0 && px < s.maskWidth)
                {
                    while (py < s.maskHeight && s.maskPixels[((s.maskHeight - 1 - py) * s.maskWidth) + px].a < 96) py++;
                    if (py < s.maskHeight)
                        local.y = Mathf.Max(local.y, m.y + (float)py / (s.maskHeight - 1) * m.height);
                }
            }
            return p.center + Rotate(local, p.angle) * p.scale;
        }

        private static Vector2 GetRim(Glass s, float t)
        {
            if (s == null) return Vector2.zero;
            Pose p = GetPose(s, t);
            return p.center + Rotate(s.rim, p.angle) * p.scale + new Vector2(0, GlassYOffset);
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
            // uGUI caches a separate stencil material for a graphic below Mask. Keep that
            // instance current too; mutating only the source material freezes the crest.
            Material rendering = image.materialForRendering;
            if (rendering != image.material) rendering.SetVector(property, value);
        }

        private void CacheMask(Glass glass, Texture2D source)
        {
            if (source.isReadable)
            {
                glass.maskWidth = source.width;
                glass.maskHeight = source.height;
                glass.maskPixels = source.GetPixels32();
                return;
            }
            // Read back a small alpha-only initialization cache without changing importers.
            // 512px bounds keep rim-origin precision below one authored design pixel.
            int width = Mathf.Min(512, source.width);
            int height = Mathf.Max(2, Mathf.RoundToInt((float)source.height / source.width * width));
            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply(false, false);
                glass.maskWidth = width;
                glass.maskHeight = height;
                glass.maskPixels = readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (readable != null) DestroyOwned(readable);
            }
        }

        private Texture2D CreateRadialTexture(string label, bool isContact)
        {
            const int size = 128;
            Color[] pixels = new Color[size * size];
            Color core = HtmlColor("FFF4C0"), edge = HtmlColor("FFD77A"), atmosphere = HtmlColor("3F6885");
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float r = new Vector2((x + .5f) / size * 2 - 1, (y + .5f) / size * 2 - 1).magnitude;
                Color color;
                if (isContact)
                {
                    color = Color.Lerp(core, edge, Mathf.Clamp01(r / .35f));
                    color.a = r < .35f ? Mathf.Lerp(107f / 255f, 36f / 255f, r / .35f)
                        : Mathf.Lerp(36f / 255f, 0, Mathf.Clamp01((r - .35f) / .65f));
                }
                else
                {
                    color = atmosphere;
                    color.a = 28f / 255f * (1 - Mathf.Clamp01((r - 20f / 275f) / (1 - 20f / 275f)));
                }
                pixels[y * size + x] = color;
            }
            return CreateTexture(label, size, pixels);
        }

        private Texture2D CreateTexture(string label, int size, Color[] pixels)
        {
            Texture2D texture = Own(new Texture2D(size, size, TextureFormat.RGBA32, false)
                { name = label, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp });
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static RectTransform CreateRect(string label, Transform parent, Vector2 size)
        {
            GameObject go = new GameObject(label, typeof(RectTransform))
                { layer = parent.gameObject.layer, hideFlags = HideFlags.DontSave };
            RectTransform rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = size;
            return rect;
        }

        private static CanvasGroup AddGroup(RectTransform rect)
        {
            CanvasGroup group = rect.gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
            return group;
        }

        private static RawImage CreateImage(string label, Transform parent, Texture texture, Vector2 size)
        {
            RectTransform rect = CreateRect(label, parent, size);
            RawImage image = rect.gameObject.AddComponent<RawImage>();
            image.texture = texture;
            image.raycastTarget = false;
            return image;
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
            if (visualRoot != null) DestroyOwned(visualRoot.gameObject);
            visualRoot = null;
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
        private static Color HtmlColor(string rgb)
        {
            ColorUtility.TryParseHtmlString("#" + rgb, out Color color);
            return color;
        }
    }

    /// <summary>Four short, round-ended diagonal rays matching the preview's contact glint.</summary>
    [AddComponentMenu("")]
    internal sealed class CheersToastRefinedGlintGraphic : MaskableGraphic
    {
        private float rayLength = 4f;
        private float rayWidth = 1.8f;

        internal void SetShape(float length, float width)
        {
            if (Mathf.Approximately(rayLength, length) && Mathf.Approximately(rayWidth, width)) return;
            rayLength = length;
            rayWidth = width;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            UIVertex vertex = UIVertex.simpleVert;
            vertex.color = color;
            const int capSegments = 8;
            const int perimeterCount = (capSegments + 1) * 2;
            float halfWidth = rayWidth * .5f;
            for (int ray = 0; ray < 4; ray++)
            {
                float angle = Mathf.PI * .25f + ray * Mathf.PI * .5f;
                Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Vector2 normal = new Vector2(-direction.y, direction.x);
                int center = vh.currentVertCount;
                vertex.position = direction * ((2 + rayLength) * .5f);
                vh.AddVert(vertex);
                for (int cap = 0; cap < 2; cap++)
                for (int i = 0; i <= capSegments; i++)
                {
                    float theta = (cap == 0 ? -.5f : .5f) * Mathf.PI + i * Mathf.PI / capSegments;
                    Vector2 capCenter = direction * (cap == 0 ? rayLength : 2f);
                    vertex.position = capCenter + halfWidth
                        * (direction * Mathf.Cos(theta) + normal * Mathf.Sin(theta));
                    vh.AddVert(vertex);
                }
                for (int i = 0; i < perimeterCount; i++)
                    vh.AddTriangle(center, center + i + 1, center + (i + 1) % perimeterCount + 1);
            }
        }
    }
}
