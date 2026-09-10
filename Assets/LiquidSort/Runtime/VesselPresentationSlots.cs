using UnityEngine;

namespace LiquidSort
{
    /// <summary>I reference authored vessel objects from the shared prefab. Runtime code never creates or repairs this hierarchy.</summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class VesselPresentationSlots : MonoBehaviour
    {
        [Header("Interaction")]
        [SerializeField] private SpriteRenderer invalidMoveHighlight;
        [SerializeField] private SpriteRenderer mechanicRevealFeedback;

        [Header("Locks")]
        [SerializeField] private SpriteRenderer wholeInteriorDim;
        [SerializeField] private SpriteRenderer wholeGlassDim;
        [SerializeField] private SpriteRenderer wholeGlassLock;
        [SerializeField] private SpriteRenderer[] lockQuestions =
            System.Array.Empty<SpriteRenderer>();

        [Header("Grounding")]
        [SerializeField] private SpriteRenderer softHalo;
        [SerializeField] private SpriteRenderer contactShadow;

        [Header("Rim garnish")]
        [SerializeField] private SpriteRenderer rimGarnish;
        [SerializeField] private SpriteRenderer rimGarnishOverlay;
        [SerializeField] private SpriteRenderer rimGarnishContactShadow;

        [Header("Floating garnish")]
        [SerializeField] private SpriteRenderer floatingGarnish;
        [SerializeField] private SpriteRenderer floatingGarnishSecondary;
        [SerializeField] private SpriteRenderer condensationGarnish;

        public SpriteRenderer InvalidMoveHighlight => invalidMoveHighlight;
        public SpriteRenderer MechanicRevealFeedback => mechanicRevealFeedback;
        public SpriteRenderer WholeInteriorDim => wholeInteriorDim;
        public SpriteRenderer WholeGlassDim => wholeGlassDim;
        public SpriteRenderer WholeGlassLock => wholeGlassLock;
        public SpriteRenderer[] LockQuestions => lockQuestions;
        public SpriteRenderer SoftHalo => softHalo;
        public SpriteRenderer ContactShadow => contactShadow;
        public SpriteRenderer RimGarnish => rimGarnish;
        public SpriteRenderer RimGarnishOverlay => rimGarnishOverlay;
        public SpriteRenderer RimGarnishContactShadow => rimGarnishContactShadow;
        public SpriteRenderer FloatingGarnish => floatingGarnish;
        public SpriteRenderer FloatingGarnishSecondary => floatingGarnishSecondary;
        public SpriteRenderer CondensationGarnish => condensationGarnish;
    }
}
