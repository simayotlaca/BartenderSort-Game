using DG.Tweening;
using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Owns taps on the world-space booster sprites. It shares <see cref="BartenderUiPointerGuard"/> with
    /// pour input so popups block both without colliders or UI Buttons.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BoosterTrayInput : MonoBehaviour
    {
        private const float ThreeButtonSpacing = 1.34f;
        private const float FourButtonInnerX = 0.67f;
        private const float FourButtonOuterX = 2.01f;
        private const float ThreeButtonTrayWidth = 4.24f;
        private const float FourButtonTrayWidth = 5.58f;
        private const float TrayInnerHorizontalInset = 0.18f;
        private const float LayoutSeconds = 0.24f;

        // Shake rotation and scale only. Layout already tweens position when the tray changes between three
        // and four buttons.
        private const float RefusalSeconds = 0.30f;
        private const float RefusalTiltDegrees = 7f;
        private const float RefusalScale = 0.95f;
        private const float RefusalVolume = 0.42f;
        private const float RefusalPitch = 1.14f;

        private const float PressScale = 0.90f;
        private const float PressDownSeconds = 0.07f;
        private const float PressUpSeconds = 0.16f;

        /// <summary>Adds a little finger room around the sprite.</summary>
        private const float TouchPadding = 0.06f;

        /// <summary>Shared tray access lets the time-boost effect find its origin across separate hierarchies.</summary>
        public static BoosterTrayInput Current { get; private set; }

        /// <summary>Clear the old instance when entering Play with domain reload off.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Current = null;
        }

        /// <summary>World position of the time button, or null if missing.</summary>
        public Transform AddTimeAnchor
        {
            get
            {
                EnsureResolved();
                return addTime.root;
            }
        }

        /// <summary>
        /// Use this button face for tutorial highlights. <see cref="AddTimeAnchor"/> includes the price
        /// label and remains the flight origin.
        /// </summary>
        public Transform AddTimeHighlightAnchor
        {
            get
            {
                EnsureResolved();
                return addTime.face != null
                    ? addTime.face.transform
                    : AddTimeAnchor;
            }
        }

        private BoosterBarPresenter presenter;

        [Header("Authored tray hierarchy")]
        [SerializeField] private Transform trayRoot;
        [SerializeField] private SpriteRenderer trayArtwork;
        [SerializeField] private SpriteRenderer trayPurpleRim;
        [SerializeField] private SpriteRenderer trayInnerFace;
        [SerializeField] private SpriteRenderer trayGoldFrame;

        [Header("Authored booster controls")]
        [SerializeField] private Transform undoRoot;
        [SerializeField] private SpriteRenderer undoFace;
        [SerializeField] private Transform extraGlassRoot;
        [SerializeField] private SpriteRenderer extraGlassFace;
        [SerializeField] private Transform shuffleRoot;
        [SerializeField] private SpriteRenderer shuffleFace;
        [SerializeField] private Transform addTimeRoot;
        [SerializeField] private SpriteRenderer addTimeFace;
        private Tween trayResize;
        private Slot undo, extraGlass, shuffle, addTime;
        private bool resolved;
        private bool addTimeVisibilityKnown;
        private bool addTimeVisible;

        private struct Slot
        {
            public Transform root;
            public SpriteRenderer face;
            public Vector3 restScale;
            public Quaternion restRotation;
            public Tween press;
            public Tween layout;
            public bool valid;
        }

        /// <summary>Registers the presenter; the tray resolves on the first tap.</summary>
        public void Bind(BoosterBarPresenter owner)
        {
            presenter = owner;
            if (owner != null) EnsureResolved();
        }

        internal bool ValidateBindings(out string reason)
        {
            EnsureResolved();
            if (!undo.valid)
            {
                reason = "Görünen Undo düğmesi veya yüz sprite'ı bulunamadı.";
                return false;
            }
            if (!extraGlass.valid)
            {
                reason = "Görünen Add Glass düğmesi veya yüz sprite'ı bulunamadı.";
                return false;
            }
            if (!shuffle.valid)
            {
                reason = "Görünen Shuffle düğmesi veya yüz sprite'ı bulunamadı.";
                return false;
            }
            if (!addTime.valid)
            {
                reason = "Görünen Add Time düğmesi veya yüz sprite'ı bulunamadı.";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// Booster faces consume taps even when disabled, so Update order cannot send the same tap to a
        /// glass.
        /// </summary>
        public bool OwnsScreenPoint(Camera camera, Vector2 screenPoint)
        {
            if (!isActiveAndEnabled || camera == null) return false;
            EnsureResolved();

            float depth = TrayDepth();
            Vector3 world = camera.ScreenToWorldPoint(new Vector3(
                screenPoint.x, screenPoint.y,
                Mathf.Abs(camera.transform.position.z - depth)));
            world.z = depth;
            return Contains(undo, world) || Contains(extraGlass, world)
                   || Contains(shuffle, world) || Contains(addTime, world);
        }

        /// <summary>
        /// Hide time boost until timed orders begin. Play the reveal once; repeated Refresh calls leave it
        /// alone.
        /// </summary>
        public void SetAddTimeVisible(bool visible)
        {
            EnsureResolved();
            if (addTime.root == null) return;

            bool wasKnown = addTimeVisibilityKnown;
            bool changed = !addTimeVisibilityKnown || addTimeVisible != visible;
            addTimeVisibilityKnown = true;
            addTimeVisible = visible;
            if (!changed && addTime.root.gameObject.activeSelf == visible) return;

            KillPress(ref addTime);
            if (!visible)
            {
                addTime.root.gameObject.SetActive(false);
                ApplyLayout(false, wasKnown && Application.isPlaying);
                return;
            }

            addTime.root.gameObject.SetActive(true);
            ApplyLayout(true, wasKnown && Application.isPlaying);
            if (!Application.isPlaying || !addTime.valid) return;
            addTime.root.localScale = addTime.restScale * 0.72f;
            addTime.press = addTime.root.DOScale(addTime.restScale, 0.34f)
                .SetEase(Ease.OutBack).SetUpdate(true).SetRecyclable(true)
                .SetTarget(addTime.root);
        }

        private void OnEnable() => Current = this;

        private void OnDisable()
        {
            if (ReferenceEquals(Current, this)) Current = null;
            KillSlotTweens(ref undo);
            KillSlotTweens(ref extraGlass);
            KillSlotTweens(ref shuffle);
            KillSlotTweens(ref addTime);
            if (trayResize != null && trayResize.IsActive()) trayResize.Kill();
            trayResize = null;
            // A disable may interrupt reveal motion. Make the next Refresh restore every slot, even if
            // activeSelf already matches.
            addTimeVisibilityKnown = false;
        }

        private void Update()
        {
            if (presenter == null) return;
            if (!TryReadPointerDown(out Vector2 screenPoint, out int pointerId)) return;

            // An overlaid UI panel owns this tap. Pour input uses the same guard.
            if (BartenderUiPointerGuard.IsPointerOverUi(screenPoint, pointerId)) return;

            Camera camera = Camera.main;
            if (camera == null) return;
            EnsureResolved();

            Vector3 world = camera.ScreenToWorldPoint(new Vector3(
                screenPoint.x, screenPoint.y,
                Mathf.Abs(camera.transform.position.z - TrayDepth())));
            world.z = TrayDepth();

            if (Contains(undo, world))
            {
                if (presenter.CanRequestUndo)
                {
                    PlayPress(ref undo);
                    presenter.RequestUndo();
                }
                else Refuse(ref undo, BoosterBarPresenter.BoosterKind.Undo);
                return;
            }
            if (Contains(extraGlass, world))
            {
                if (presenter.CanRequestExtraGlass)
                {
                    PlayPress(ref extraGlass);
                    presenter.RequestExtraGlass();
                }
                else Refuse(ref extraGlass, BoosterBarPresenter.BoosterKind.ExtraGlass);
                return;
            }
            if (Contains(shuffle, world))
            {
                if (presenter.CanRequestShuffle)
                {
                    PlayPress(ref shuffle);
                    presenter.RequestShuffle();
                }
                else Refuse(ref shuffle, BoosterBarPresenter.BoosterKind.Shuffle);
                return;
            }
            if (!Contains(addTime, world)) return;
            if (!presenter.CanRequestAddTime)
            {
                Refuse(ref addTime, BoosterBarPresenter.BoosterKind.AddTime);
                return;
            }
            PlayPress(ref addTime);
            presenter.RequestAddTime();
        }

        private float TrayDepth() =>
            undo.valid ? undo.root.position.z
            : extraGlass.valid ? extraGlass.root.position.z
            : shuffle.valid ? shuffle.root.position.z
            : addTime.valid ? addTime.root.position.z : 0f;

        // Resolve references.

        private void EnsureResolved()
        {
            if (resolved) return;
            resolved = true;

            undo = MakeSlot(undoRoot, undoFace);
            extraGlass = MakeSlot(extraGlassRoot, extraGlassFace);
            shuffle = MakeSlot(shuffleRoot, shuffleFace);
            addTime = MakeSlot(addTimeRoot, addTimeFace);

            if (!undo.valid && !extraGlass.valid && !shuffle.valid && !addTime.valid)
                Debug.LogError(
                    "Booster şeridinin authored root/face Inspector bağlantıları eksik; "
                    + "şerit tıklanamaz kalacak.",
                    this);
        }

        private void ApplyLayout(bool fourButtons, bool animate)
        {
            bool tween = animate && Application.isPlaying && isActiveAndEnabled;
            SetSlotX(ref undo,
                fourButtons ? -FourButtonOuterX : -ThreeButtonSpacing, tween);
            SetSlotX(ref extraGlass,
                fourButtons ? -FourButtonInnerX : 0f, tween);
            SetSlotX(ref shuffle,
                fourButtons ? FourButtonInnerX : ThreeButtonSpacing, tween);
            SetSlotX(ref addTime, FourButtonOuterX, tween);
            SetTrayWidth(fourButtons ? FourButtonTrayWidth : ThreeButtonTrayWidth, tween);
        }

        private static void SetSlotX(ref Slot slot, float x, bool animate)
        {
            if (slot.root == null) return;
            if (slot.layout != null && slot.layout.IsActive()) slot.layout.Kill();
            slot.layout = null;

            Vector3 position = slot.root.localPosition;
            if (Mathf.Approximately(position.x, x)) return;
            if (!animate)
            {
                position.x = x;
                slot.root.localPosition = position;
                return;
            }

            slot.layout = slot.root.DOLocalMoveX(x, LayoutSeconds)
                .SetEase(Ease.InOutQuad).SetUpdate(true).SetRecyclable(true)
                .SetTarget(slot.root);
        }

        private void SetTrayWidth(float targetWidth, bool animate)
        {
            float currentWidth = trayArtwork != null
                ? trayArtwork.size.x
                : trayGoldFrame != null ? trayGoldFrame.size.x : targetWidth;
            if (Mathf.Approximately(currentWidth, targetWidth))
            {
                WriteTrayWidth(targetWidth);
                return;
            }

            if (trayResize != null && trayResize.IsActive()) trayResize.Kill();
            trayResize = null;
            if (!animate)
            {
                WriteTrayWidth(targetWidth);
                return;
            }

            float width = currentWidth;
            trayResize = DOTween.To(() => width, value =>
                {
                    width = value;
                    WriteTrayWidth(value);
                }, targetWidth, LayoutSeconds)
                .SetEase(Ease.InOutQuad).SetUpdate(true).SetRecyclable(true)
                .SetTarget(trayRoot);
        }

        private void WriteTrayWidth(float width)
        {
            WriteSlicedWidth(trayArtwork, width);
            WriteSlicedWidth(trayPurpleRim, width);
            WriteSlicedWidth(trayInnerFace,
                Mathf.Max(0.01f, width - TrayInnerHorizontalInset));
            WriteSlicedWidth(trayGoldFrame, width);
        }

        private static void WriteSlicedWidth(SpriteRenderer renderer, float width)
        {
            if (renderer == null) return;
            renderer.drawMode = SpriteDrawMode.Sliced;
            renderer.size = new Vector2(width, renderer.size.y);
        }

        private static Slot MakeSlot(Transform root, SpriteRenderer face)
        {
            var slot = new Slot
            {
                root = root,
                face = face,
                restScale = root != null ? root.localScale : Vector3.one,
                restRotation = root != null ? root.localRotation : Quaternion.identity
            };
            slot.valid = root != null && face != null;
            return slot;
        }

        // Handle taps.

        private static bool Contains(Slot slot, Vector3 world)
        {
            if (!slot.valid || slot.root == null || slot.face == null
                || !slot.root.gameObject.activeInHierarchy)
                return false;

            // Read current bounds for every tap because safe-area fitting can move and scale the tray.
            Bounds bounds = slot.face.bounds;
            bounds.Expand(TouchPadding);
            Vector3 flat = new Vector3(world.x, world.y, bounds.center.z);
            return bounds.Contains(flat);
        }

        private void PlayPress(ref Slot slot)
        {
            if (slot.press != null && slot.press.IsActive()) slot.press.Kill();
            slot.root.localScale = slot.restScale;
            Transform root = slot.root;
            Vector3 rest = slot.restScale;
            slot.press = DOTween.Sequence()
                .SetTarget(root).SetUpdate(true).SetRecyclable(true)
                .Append(root.DOScale(rest * PressScale, PressDownSeconds)
                    .SetEase(Ease.OutQuad).SetRecyclable(true))
                .Append(root.DOScale(rest, PressUpSeconds)
                    .SetEase(Ease.OutBack).SetRecyclable(true));
        }

        /// <summary>
        /// Too few coins always opens the shop, even when another rule also blocks the booster. With enough
        /// coins, rejection only shakes the button.
        /// </summary>
        private void Refuse(ref Slot slot, BoosterBarPresenter.BoosterKind kind)
        {
            // While choosing a shuffle target, other boosters cannot open a shop or send another board
            // command.
            if (presenter.IsShuffleSelectionActive)
            {
                PlayRefusal(ref slot);
                return;
            }

            int cost = presenter.CoinCostOf(kind);

            if (!BartenderProgressService.CanAfford(cost)
                && BartenderShopPresenter.TryOpenCurrentScene())
            {
                // The shop handles feedback now. Play the rejection sound but skip the extra shake.
                BsAudio.Instance?.Play(BsSfx.Invalid, RefusalVolume, RefusalPitch);
                return;
            }

            PlayRefusal(ref slot);
        }

        /// <summary>Shows clear feedback for any rejected booster tap.</summary>
        private void PlayRefusal(ref Slot slot)
        {
            if (!slot.valid || slot.root == null) return;
            if (slot.press != null && slot.press.IsActive()) slot.press.Kill();

            Transform root = slot.root;
            Vector3 rest = slot.restScale;
            Quaternion restRotation = slot.restRotation;
            root.localScale = rest;
            root.localRotation = restRotation;

            float quarter = RefusalSeconds * 0.25f;
            Vector3 tilt = new Vector3(0f, 0f, RefusalTiltDegrees);
            Sequence sequence = DOTween.Sequence()
                .SetTarget(root).SetUpdate(true).SetRecyclable(true);
            sequence.Append(root.DOLocalRotate(tilt, quarter)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Append(root.DOLocalRotate(-tilt, quarter * 2f)
                .SetEase(Ease.InOutQuad).SetRecyclable(true));
            sequence.Append(root.DOLocalRotate(Vector3.zero, quarter)
                .SetEase(Ease.InQuad).SetRecyclable(true));
            sequence.Insert(0f, root.DOScale(rest * RefusalScale, quarter)
                .SetEase(Ease.OutQuad).SetRecyclable(true));
            sequence.Insert(quarter, root.DOScale(rest, RefusalSeconds - quarter)
                .SetEase(Ease.OutBack).SetRecyclable(true));

            // Restore the authored rotation after the absolute-angle tween.
            sequence.OnKill(() =>
            {
                if (root == null) return;
                root.localRotation = restRotation;
                root.localScale = rest;
            });
            slot.press = sequence;

            // Reuse pour rejection audio, quieter and slightly higher for a soft refusal.
            BsAudio.Instance?.Play(BsSfx.Invalid, RefusalVolume, RefusalPitch);
            BartenderHaptics.Light();
        }

        private static void KillPress(ref Slot slot)
        {
            if (slot.press != null && slot.press.IsActive()) slot.press.Kill();
            slot.press = null;
            if (slot.root == null || !slot.valid) return;
            slot.root.localScale = slot.restScale;
            slot.root.localRotation = slot.restRotation;
        }

        private static void KillSlotTweens(ref Slot slot)
        {
            KillPress(ref slot);
            if (slot.layout != null && slot.layout.IsActive()) slot.layout.Kill();
            slot.layout = null;
        }

        // Read input.

        private static bool TryReadPointerDown(out Vector2 screenPoint, out int pointerId)
        {
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.phase != TouchPhase.Began) continue;
                screenPoint = touch.position;
                pointerId = touch.fingerId;
                return true;
            }

            // Ignore mouse input while touch is active so one tap cannot send two commands.
            if (Input.touchCount == 0 && Input.GetMouseButtonDown(0))
            {
                screenPoint = Input.mousePosition;
                pointerId = -1;
                return true;
            }

            screenPoint = default;
            pointerId = 0;
            return false;
        }
    }
}
