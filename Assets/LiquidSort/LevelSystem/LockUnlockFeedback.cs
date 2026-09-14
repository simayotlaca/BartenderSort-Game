using UnityEngine;

namespace LiquidSort.Levels
{
    internal sealed class LockUnlockFeedback
    {
        internal const float Duration = 0.50f;
        internal const float GrowDuration = 0.10f;
        internal const float CountExitEnd = Duration - 0.015f;
        private const float OpenEnd = 0.255f;
        private const float FadeStart = 0.32f;
        private const float GlassSheenEnd = 0.45f;
        private const float VisibleHeightShare = 0.81f;
        private static Sprite sparkleSprite;
        private static Shader glassSheenShader;
        private static readonly int SheenProgressId = Shader.PropertyToID("_Progress");
        private static readonly int SheenStrengthId = Shader.PropertyToID("_Strength");
        private static readonly int SheenLocalRectId = Shader.PropertyToID("_LocalRect");
        private static readonly int SheenUvRectId = Shader.PropertyToID("_SpriteUvRect");

        // Boundary between the shackle and housing in the 512px closed-lock artwork, measured from
        // the top left. Following both rounded feet keeps the housing's shoulders and centre rim
        // attached to the body; a horizontal crop would carry those parts away with the shackle.
        private static readonly Vector2[] ShackleBoundary =
        {
            new Vector2(0f, 194f),
            new Vector2(140f, 194f),
            new Vector2(142f, 201f),
            new Vector2(149f, 206f),
            new Vector2(173f, 210f),
            new Vector2(195f, 207f),
            new Vector2(204f, 202f),
            new Vector2(204f, 190f),
            new Vector2(308f, 190f),
            new Vector2(309f, 202f),
            new Vector2(317f, 207f),
            new Vector2(341f, 210f),
            new Vector2(362f, 207f),
            new Vector2(370f, 201f),
            new Vector2(372f, 194f),
            new Vector2(512f, 194f)
        };

        private GameObject root;
        private Sprite bodySprite;
        private Sprite shackleSprite;
        private Transform shacklePivot;
        private Vector3 pivotRestPosition;
        private Vector3 restWorldPosition;
        private Quaternion restWorldRotation;
        private Vector3 restLocalScale;
        private float spriteHeight;
        private float visibleWorldHeight;
        private float enlargedScale;
        private float glintScale;
        private float elapsed;
        private SpriteRenderer glassFront;
        private SpriteRenderer glassSheen;
        private Material glassSheenMaterial;

        internal Transform Root => root != null ? root.transform : null;
        internal SpriteRenderer BodyRenderer { get; private set; }
        internal SpriteRenderer ShackleRenderer { get; private set; }
        internal SpriteRenderer GlintRenderer { get; private set; }
        internal bool OpeningStarted => elapsed >= GrowDuration;
        internal float Opacity => 1f - Smooth(Mathf.InverseLerp(FadeStart, Duration, elapsed));
        internal float DimOpacity => 1f - Smooth(Mathf.InverseLerp(
            GrowDuration, OpenEnd, elapsed));

        private LockUnlockFeedback() { }

        internal static LockUnlockFeedback Create(
            SpriteRenderer source, Transform owner, Sprite sprite,
            Vector2 localCenter, float localScale, Material material,
            float minimumVisibleWorldHeight = 0f, bool wholeGlass = false)
        {
            if (source == null || owner == null || sprite == null || sprite.texture == null
                || source.transform.parent == null || localScale <= 0f
                || float.IsNaN(localScale) || float.IsInfinity(localScale))
                return null;

            var effect = new LockUnlockFeedback();
            try
            {
                effect.Build(source, owner, sprite, localCenter, localScale, material,
                             minimumVisibleWorldHeight, wholeGlass);
                return effect;
            }
            catch (System.Exception exception)
            {
                effect.Dispose();
                Debug.LogWarning("Could not create cosmetic lock feedback: " + exception.Message);
                return null;
            }
        }

        public bool Tick(float unscaledDeltaTime)
        {
            if (root == null || BodyRenderer == null || ShackleRenderer == null
                || shacklePivot == null)
            {
                Dispose();
                return false;
            }

            if (float.IsNaN(unscaledDeltaTime) || float.IsInfinity(unscaledDeltaTime))
            {
                Dispose();
                return false;
            }

            elapsed += Mathf.Max(0f, unscaledDeltaTime);
            if (elapsed >= Duration)
            {
                Dispose();
                return false;
            }

            float grow = Mathf.Clamp01(elapsed / GrowDuration);
            float scale = grow < 0.70f
                ? Mathf.Lerp(1f, enlargedScale * 1.025f, EaseOut(grow / 0.70f))
                : Mathf.Lerp(enlargedScale * 1.025f, enlargedScale,
                             Smooth((grow - 0.70f) / 0.30f));
            float fade = 1f - Opacity;
            root.transform.localScale = restLocalScale * scale * Mathf.Lerp(1f, 0.98f, fade);
            root.transform.position = restWorldPosition + restWorldRotation * Vector3.up
                * (visibleWorldHeight * 0.5f * (scale - 1f)
                   + visibleWorldHeight * enlargedScale * 0.035f * fade);

            float lift = EaseOut(Mathf.Clamp01((elapsed - GrowDuration) / 0.022f));
            float opening = Mathf.InverseLerp(GrowDuration + 0.035f, OpenEnd, elapsed);
            float swing = Smooth(opening);
            float liftHeight = Mathf.Lerp(0.022f, 0.006f, swing) * lift;
            shacklePivot.localPosition = pivotRestPosition
                + Vector3.up * (spriteHeight * VisibleHeightShare * liftHeight);
            shacklePivot.localRotation = Quaternion.Euler(0f, 0f, -22f * swing);
            float recoil = 1.6f * Mathf.Sin(Mathf.PI * opening) * Mathf.Exp(-1.2f * opening);
            root.transform.rotation = restWorldRotation * Quaternion.Euler(0f, 0f, recoil);

            float alpha = Opacity;
            Color tint = new Color(1f, 1f, 1f, alpha);
            BodyRenderer.color = tint;
            ShackleRenderer.color = tint;
            if (GlintRenderer != null)
            {
                float glint = Mathf.InverseLerp(GrowDuration, OpenEnd, elapsed);
                float pulse = Mathf.Sin(Mathf.PI * glint);
                GlintRenderer.color = new Color(1f, 0.96f, 0.82f, pulse * 0.9f);
                GlintRenderer.transform.localScale = Vector3.one
                    * (glintScale * Mathf.Lerp(0.55f, 1f, pulse));
                GlintRenderer.enabled = glint > 0f && glint < 1f;
            }
            TickGlassSheen();
            return true;
        }

        internal void AttachGlassSheen(SpriteRenderer front)
        {
            if (front == null || front.sprite == null || root == null || glassSheen != null)
                return;
            if (glassSheenShader == null)
                glassSheenShader = Resources.Load<Shader>("Ui/Locks/GlassUnlockSheen");
            if (glassSheenShader == null || !glassSheenShader.isSupported) return;

            glassFront = front;
            glassSheenMaterial = new Material(glassSheenShader)
            {
                name = "Glass Unlock Reflection", hideFlags = HideFlags.HideAndDontSave
            };
            GameObject overlay = NewObject("Glass Unlock Reflection", front.gameObject.layer);
            overlay.transform.SetParent(root.transform.parent, false);
            glassSheen = overlay.AddComponent<SpriteRenderer>();
            glassSheen.sprite = front.sprite;
            glassSheen.sharedMaterial = glassSheenMaterial;
            glassSheen.sortingLayerID = front.sortingLayerID;
            glassSheen.sortingOrder = front.sortingOrder + 1;
            glassSheen.maskInteraction = front.maskInteraction;
            glassSheen.flipX = front.flipX;
            glassSheen.flipY = front.flipY;
            glassSheen.spriteSortPoint = front.spriteSortPoint;
            glassSheen.drawMode = front.drawMode;
            if (front.drawMode != SpriteDrawMode.Simple) glassSheen.size = front.size;
            glassSheen.color = new Color(1f, 0.91f, 0.66f, 1f);
            Bounds bounds = front.sprite.bounds;
            glassSheenMaterial.SetVector(SheenLocalRectId,
                new Vector4(bounds.min.x, bounds.min.y,
                            Mathf.Max(0.0001f, bounds.size.x),
                            Mathf.Max(0.0001f, bounds.size.y)));
            Vector2[] uvs = front.sprite.uv;
            Vector2 uvMin = Vector2.one;
            Vector2 uvMax = Vector2.zero;
            for (int i = 0; i < uvs.Length; i++)
            {
                uvMin = Vector2.Min(uvMin, uvs[i]);
                uvMax = Vector2.Max(uvMax, uvs[i]);
            }
            glassSheenMaterial.SetVector(SheenUvRectId,
                new Vector4(uvMin.x, uvMin.y, uvMax.x, uvMax.y));
            TickGlassSheen();
        }

        private void TickGlassSheen()
        {
            if (glassSheen == null || glassSheenMaterial == null) return;
            if (glassFront == null || !glassFront.enabled || !glassFront.gameObject.activeInHierarchy)
            {
                glassSheen.enabled = false;
                return;
            }
            Transform source = glassFront.transform;
            glassSheen.transform.SetPositionAndRotation(source.position, source.rotation);
            Transform parent = glassSheen.transform.parent;
            Vector3 parentScale = parent != null ? parent.lossyScale : Vector3.one;
            Vector3 scale = source.lossyScale;
            glassSheen.transform.localScale = new Vector3(
                scale.x / Mathf.Max(0.0001f, Mathf.Abs(parentScale.x)),
                scale.y / Mathf.Max(0.0001f, Mathf.Abs(parentScale.y)), 1f);
            float progress = Mathf.InverseLerp(GrowDuration, GlassSheenEnd, elapsed);
            float strength = Smooth(Mathf.Clamp01(progress / 0.18f))
                * (1f - Smooth(Mathf.InverseLerp(0.28f, 1f, progress))) * 0.62f;
            glassSheenMaterial.SetFloat(SheenProgressId, progress);
            glassSheenMaterial.SetFloat(SheenStrengthId, strength);
            glassSheen.enabled = progress > 0f && progress < 1f;
        }

        public void Dispose()
        {
            if (root != null) root.SetActive(false);
            if (glassSheen != null) glassSheen.gameObject.SetActive(false);
            DestroyOwned(glassSheen != null ? glassSheen.gameObject : null);
            DestroyOwned(glassSheenMaterial);
            DestroyOwned(root);
            DestroyOwned(bodySprite);
            DestroyOwned(shackleSprite);
            root = null;
            bodySprite = null;
            shackleSprite = null;
            shacklePivot = null;
            BodyRenderer = null;
            ShackleRenderer = null;
            GlintRenderer = null;
            glassFront = null;
            glassSheen = null;
            glassSheenMaterial = null;
        }

        private void Build(SpriteRenderer source, Transform owner, Sprite sprite,
                           Vector2 localCenter, float localScale, Material material,
                           float minimumVisibleWorldHeight, bool wholeGlass)
        {
            Transform basis = source.transform.parent;
            root = NewObject("LockUnlockFeedback", source.gameObject.layer);
            Transform rootTransform = root.transform;
            rootTransform.SetPositionAndRotation(
                basis.TransformPoint(new Vector3(localCenter.x, localCenter.y, 0f)),
                basis.rotation);
            rootTransform.localScale = Vector3.Scale(
                basis.lossyScale, new Vector3(localScale, localScale, 1f));
            rootTransform.SetParent(owner, true);
            restWorldPosition = rootTransform.position;
            restWorldRotation = rootTransform.rotation;
            restLocalScale = rootTransform.localScale;
            spriteHeight = sprite.rect.height / sprite.pixelsPerUnit;
            visibleWorldHeight = spriteHeight * VisibleHeightShare
                * Mathf.Abs(rootTransform.lossyScale.y);
            enlargedScale = Mathf.Clamp(minimumVisibleWorldHeight
                / Mathf.Max(0.0001f, visibleWorldHeight), wholeGlass ? 1.06f : 1.08f,
                wholeGlass ? 1.16f : 1.30f);

            bodySprite = CreatePiece(sprite, false);
            shackleSprite = CreatePiece(sprite, true);
            BodyRenderer = NewRenderer("Lock Body", rootTransform, source, bodySprite, material);

            shacklePivot = NewObject("Shackle Hinge", source.gameObject.layer).transform;
            shacklePivot.SetParent(rootTransform, false);
            pivotRestPosition = ArtPoint(sprite, new Vector2(341f, 207f));
            shacklePivot.localPosition = pivotRestPosition;
            ShackleRenderer = NewRenderer(
                "Opening Shackle", shacklePivot, source, shackleSprite, material);
            ShackleRenderer.transform.localPosition = -pivotRestPosition;

            if (sparkleSprite == null)
                sparkleSprite = Resources.Load<Sprite>("Ui/Locks/Ui_LockState_Sparkle_v1");
            if (sparkleSprite != null && sparkleSprite.bounds.size.y > 0.0001f)
            {
                GlintRenderer = NewRenderer("Lock Glint", rootTransform, source, sparkleSprite, null);
                GlintRenderer.sortingOrder = BodyRenderer.sortingOrder + 1;
                GlintRenderer.transform.localPosition = ArtPoint(sprite, new Vector2(177f, 195f));
                glintScale = spriteHeight * VisibleHeightShare * 0.27f
                    / sparkleSprite.bounds.size.y;
                GlintRenderer.enabled = false;
            }
        }

        private static SpriteRenderer NewRenderer(string name, Transform parent,
            SpriteRenderer source, Sprite sprite, Material material)
        {
            GameObject part = NewObject(name, source.gameObject.layer);
            part.transform.SetParent(parent, false);
            SpriteRenderer renderer = part.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            if (material != null) renderer.sharedMaterial = material;
            renderer.color = Color.white;
            renderer.sortingLayerID = source.sortingLayerID;
            renderer.sortingOrder = source.sortingOrder + 2;
            renderer.maskInteraction = SpriteMaskInteraction.None;
            return renderer;
        }

        private static Sprite CreatePiece(Sprite source, bool shackle)
        {
            Rect rect = source.rect;
            Sprite piece = Sprite.Create(source.texture, rect,
                new Vector2(source.pivot.x / rect.width, source.pivot.y / rect.height),
                source.pixelsPerUnit, 0, SpriteMeshType.FullRect);
            piece.name = shackle ? "Unlock Shackle" : "Unlock Body";

            int stripCount = ShackleBoundary.Length - 1;
            var vertices = new Vector2[stripCount * 4];
            var triangles = new ushort[stripCount * 6];
            for (int i = 0; i < stripCount; i++)
            {
                Vector2 a = ShackleBoundary[i];
                Vector2 b = ShackleBoundary[i + 1];
                float edgeY = shackle ? 0f : 512f;
                int vertex = i * 4;
                vertices[vertex] = ArtPixelPoint(source, a);
                vertices[vertex + 1] = ArtPixelPoint(source, b);
                vertices[vertex + 2] = ArtPixelPoint(source, new Vector2(b.x, edgeY));
                vertices[vertex + 3] = ArtPixelPoint(source, new Vector2(a.x, edgeY));
                int triangle = i * 6;
                triangles[triangle] = (ushort)vertex;
                triangles[triangle + 1] = (ushort)(vertex + 1);
                triangles[triangle + 2] = (ushort)(vertex + 2);
                triangles[triangle + 3] = (ushort)vertex;
                triangles[triangle + 4] = (ushort)(vertex + 2);
                triangles[triangle + 5] = (ushort)(vertex + 3);
            }

            try
            {
                piece.OverrideGeometry(vertices, triangles);
                if (piece.vertices.Length != vertices.Length)
                    throw new System.InvalidOperationException("Lock sprite geometry was rejected.");
                piece.hideFlags = HideFlags.HideAndDontSave;
            }
            catch
            {
                DestroyOwned(piece);
                throw;
            }
            return piece;
        }

        private static Vector2 ArtPoint(Sprite source, Vector2 artPoint)
        {
            return (ArtPixelPoint(source, artPoint) - source.pivot) / source.pixelsPerUnit;
        }

        private static Vector2 ArtPixelPoint(Sprite source, Vector2 artPoint)
        {
            return new Vector2(artPoint.x / 512f * source.rect.width,
                               (1f - artPoint.y / 512f) * source.rect.height);
        }

        private static GameObject NewObject(string name, int layer)
        {
            return new GameObject(name) { layer = layer, hideFlags = HideFlags.HideAndDontSave };
        }

        private static void DestroyOwned(Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) Object.Destroy(item);
            else Object.DestroyImmediate(item);
        }

        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);
        private static float Smooth(float t) => t * t * (3f - 2f * t);
    }
}
