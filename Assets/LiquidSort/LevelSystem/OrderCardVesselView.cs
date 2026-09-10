using System.Collections.Generic;
using BartenderSort.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace LiquidSort.Levels
{
    /// <summary>Keeps the original card sketches and fits the shared gameplay liquid into their authored interiors.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(1000)]
    [RequireComponent(typeof(RectTransform), typeof(SortingGroup))]
    public sealed class OrderCardVesselView : MonoBehaviour
    {
        [SerializeField] private RectTransform frame;
        [SerializeField] private CanvasGroup cardGroup;
        [Tooltip("Authored liquid renderers in capacity order. Gameplay shells stay hidden on cards.")]
        [SerializeField] private LiquidBottle[] glasses = new LiquidBottle[5];
        [SerializeField] private SpriteRenderer sketchFront;
        [SerializeField] private Sprite[] sketchSprites = new Sprite[5];
        [Tooltip("Liquid renderer fit, normalized within each original sketch's complete sprite canvas.")]
        [SerializeField] private Rect[] liquidRects = new Rect[5];

        private static readonly int AlphaId = Shader.PropertyToID("_Alpha");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private readonly List<Color> colors = new List<Color>(5);
        private MaterialPropertyBlock properties;
        private readonly Dictionary<LiquidBottle, RenderState[]> states =
            new Dictionary<LiquidBottle, RenderState[]>(5);
        private LiquidBottle selected;
        private VesselRimGarnish selectedRim;
        private VesselFloatingGarnish selectedFloating;
        private RenderState[] selectedStates;
        private CanvasGroup[] parentGroups;
        private SortingGroup sorting;

        private struct RenderState
        {
            public Renderer Renderer;
            public bool IsLiquid;
            public bool HasAlpha;
        }

        public bool IsReady
        {
            get
            {
                if (cardGroup == null || sketchFront == null
                    || glasses == null || glasses.Length != 5
                    || sketchSprites == null || sketchSprites.Length != 5
                    || liquidRects == null || liquidRects.Length != 5)
                    return false;
                for (int i = 0; i < glasses.Length; i++)
                    if (sketchSprites[i] == null || liquidRects[i].width <= 0f
                        || liquidRects[i].height <= 0f || glasses[i] == null || !glasses[i].Profiled
                        || !glasses[i].transform.IsChildOf(transform)
                        || glasses[i].capacity != i + 1
                        || glasses[i].profile.front == null
                        || glasses[i].profile.interiorMask == null)
                        return false;
                return true;
            }
        }

        public void Show(GlassType type, IReadOnlyList<int> contents,
            BsPalette palette, bool fill, float visualScale)
        {
            Hide();
            int index = BsRules.Capacity(type) - 1;
            if (!IsReady || index < 0 || index >= glasses.Length) return;
            frame = frame != null ? frame : (RectTransform)transform;
            sorting = sorting != null ? sorting : GetComponent<SortingGroup>();
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas != null)
            {
                sorting.sortingLayerID = canvas.rootCanvas.sortingLayerID;
                sorting.sortingOrder = canvas.rootCanvas.sortingOrder + 1;
            }
            parentGroups = GetComponentsInParent<CanvasGroup>(true);
            selected = glasses[index];
            sketchFront.sprite = sketchSprites[index];
            sketchFront.sortingOrder = 5;
            sketchFront.enabled = true;
            colors.Clear();
            if (fill && contents != null)
                for (int i = 0; i < contents.Count; i++)
                    colors.Add(palette != null ? palette.ColorAt(contents[i]) : Color.magenta);
            selected.SetUnits(colors);
            Fit(index, visualScale);
            selected.gameObject.SetActive(true);
            selectedRim = selected.GetComponent<VesselRimGarnish>();
            selectedFloating = selected.GetComponent<VesselFloatingGarnish>();
            selected.Refresh();
            if (!states.TryGetValue(selected, out selectedStates))
            {
                Renderer[] renderers = selected.GetComponentsInChildren<Renderer>(true);
                selectedStates = new RenderState[renderers.Length];
                for (int i = 0; i < renderers.Length; i++)
                {
                    Material material = renderers[i].sharedMaterial;
                    selectedStates[i] = new RenderState
                    {
                        Renderer = renderers[i],
                        IsLiquid = renderers[i].transform == selected.transform.Find("Liquid"),
                        HasAlpha = material != null && material.HasProperty(AlphaId)
                    };
                }
                states.Add(selected, selectedStates);
            }
            ApplyAlpha(EffectiveAlpha());
        }

        public void Hide()
        {
            if (selectedStates != null) ApplyAlpha(1f);
            if (sketchFront != null) sketchFront.enabled = false;
            if (glasses != null)
                for (int i = 0; i < glasses.Length; i++)
                    if (glasses[i] != null) glasses[i].gameObject.SetActive(false);
            selected = null;
            selectedStates = null;
        }

        private void LateUpdate()
        {
            if (selected != null) ApplyAlpha(EffectiveAlpha());
        }

        private void Fit(int index, float visualScale)
        {
            Transform parent = selected.transform.parent;
            Vector3 width = parent.InverseTransformVector(frame.TransformVector(Vector3.right));
            Vector3 height = parent.InverseTransformVector(frame.TransformVector(Vector3.up));
            Sprite sprite = sketchSprites[index];
            // Match the original UI Image fit, including transparent padding and the unchanged card scale.
            float pixelScale = Mathf.Min(frame.rect.width * width.magnitude / sprite.rect.width,
                frame.rect.height * height.magnitude / sprite.rect.height) * visualScale;
            Vector2 drawn = sprite.rect.size * pixelScale;
            Vector3 centre = parent.InverseTransformPoint(frame.TransformPoint(frame.rect.center));
            Vector3 bottomLeft = centre - (Vector3)(drawn * 0.5f);
            sketchFront.transform.localRotation = Quaternion.identity;
            sketchFront.transform.localScale = Vector3.one * (pixelScale * sprite.pixelsPerUnit);
            sketchFront.transform.localPosition = bottomLeft + (Vector3)(sprite.pivot * pixelScale);

            Rect inside = liquidRects[index];
            Rect quad = selected.profile.QuadRect;
            Vector3 scale = new Vector3(drawn.x * inside.width / quad.width,
                drawn.y * inside.height / quad.height, 1f);
            selected.transform.localRotation = Quaternion.identity;
            selected.transform.localScale = scale;
            selected.transform.localPosition = bottomLeft
                + new Vector3(drawn.x * inside.xMin - quad.xMin * scale.x,
                    drawn.y * inside.yMin - quad.yMin * scale.y, 0f);
        }

        private float EffectiveAlpha()
        {
            float alpha = 1f;
            if (parentGroups == null) return cardGroup != null ? cardGroup.alpha : 1f;
            for (int i = 0; i < parentGroups.Length; i++)
            {
                CanvasGroup group = parentGroups[i];
                if (group == null || !group.enabled) continue;
                alpha *= group.alpha;
                if (group.ignoreParentGroups) break;
            }
            return Mathf.Clamp01(alpha);
        }

        private void ApplyAlpha(float alpha)
        {
            // LiquidBottle can rebind decorations on enable/start; the card only presents its liquid.
            if (selectedRim != null && selectedRim.enabled) selectedRim.enabled = false;
            if (selectedFloating != null && selectedFloating.enabled) selectedFloating.enabled = false;
            properties ??= new MaterialPropertyBlock();
            sketchFront.color = Color.white;
            sketchFront.GetPropertyBlock(properties);
            properties.SetColor(ColorId, new Color(1f, 1f, 1f, alpha));
            sketchFront.SetPropertyBlock(properties);
            for (int i = 0; i < selectedStates.Length; i++)
            {
                RenderState state = selectedStates[i];
                if (state.Renderer == null) continue;
                state.Renderer.forceRenderingOff = !state.IsLiquid || alpha <= 0.0001f;
                if (!state.IsLiquid || !state.HasAlpha) continue;
                state.Renderer.GetPropertyBlock(properties);
                properties.SetFloat(AlphaId, alpha);
                state.Renderer.SetPropertyBlock(properties);
            }
        }
    }
}
