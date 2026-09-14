using System.Collections;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
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

        private const float RefusalSeconds = 0.30f;
        private const float RefusalTiltDegrees = 7f;
        private const float RefusalScale = 0.95f;
        private const float RefusalVolume = 0.42f;
        private const float RefusalPitch = 1.14f;

        private const float PressScale = 0.90f;
        private const float PressDownSeconds = 0.07f;
        private const float PressUpSeconds = 0.16f;
        private const float CommandFailureSeconds = 2.5f;
        private const string CommandFailureMessage = "Try again.";

        private const float TouchPadding = 0.06f;

        public static BoosterTrayInput Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Current = null;
        }

        public Transform AddTimeAnchor
        {
            get
            {
                EnsureResolved();
                return addTime.root;
            }
        }

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

        [Header("Command failure feedback")]
        [SerializeField] private GameObject commandFailureRoot;
        [SerializeField] private Text commandFailureLabel;
        private Coroutine commandFailureHide;
        private Tween trayResize;
        private Slot undo = new Slot(), extraGlass = new Slot(),
            shuffle = new Slot(), addTime = new Slot();
        private bool resolved;
        private bool addTimeVisibilityKnown;
        private bool addTimeVisible;

        private sealed class Slot
        {
            public Transform root;
            public SpriteRenderer face;
            public Vector3 restScale;
            public Quaternion restRotation;
            public Tween press;
            public Tween layout;
            public bool valid;
        }

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
                reason = "Undo button missing.";
                return false;
            }
            if (!extraGlass.valid)
            {
                reason = "Add Glass button missing.";
                return false;
            }
            if (!shuffle.valid)
            {
                reason = "Shuffle button missing.";
                return false;
            }
            if (!addTime.valid)
            {
                reason = "Add Time button missing.";
                return false;
            }
            if (commandFailureRoot == null || commandFailureLabel == null
                || commandFailureRoot == gameObject
                || !commandFailureRoot.transform.IsChildOf(transform)
                || !commandFailureLabel.transform.IsChildOf(commandFailureRoot.transform)
                || commandFailureLabel.raycastTarget)
            {
                reason = "Booster message binding invalid.";
                return false;
            }
            reason = null;
            return true;
        }

        private bool TryPickSlot(Camera camera, Vector2 screenPoint,
            out Slot slot, out BoosterBarPresenter.BoosterKind kind)
        {
            slot = null;
            kind = default;
            if (!isActiveAndEnabled || camera == null) return false;
            EnsureResolved();
            float depth = TrayDepth();
            Vector3 world = camera.ScreenToWorldPoint(new Vector3(
                screenPoint.x, screenPoint.y,
                Mathf.Abs(camera.transform.position.z - depth)));
            world.z = depth;
            if (Contains(undo, world))
            {
                slot = undo;
                kind = BoosterBarPresenter.BoosterKind.Undo;
            }
            else if (Contains(extraGlass, world))
            {
                slot = extraGlass;
                kind = BoosterBarPresenter.BoosterKind.ExtraGlass;
            }
            else if (Contains(shuffle, world))
            {
                slot = shuffle;
                kind = BoosterBarPresenter.BoosterKind.Shuffle;
            }
            else if (Contains(addTime, world))
            {
                slot = addTime;
                kind = BoosterBarPresenter.BoosterKind.AddTime;
            }
            return slot != null;
        }

        public void SetAddTimeVisible(bool visible)
        {
            EnsureResolved();
            if (addTime.root == null) return;

            bool wasKnown = addTimeVisibilityKnown;
            bool changed = !addTimeVisibilityKnown || addTimeVisible != visible;
            addTimeVisibilityKnown = true;
            addTimeVisible = visible;
            if (!changed && addTime.root.gameObject.activeSelf == visible) return;

            KillPress(addTime);
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
            TrackPress(addTime, addTime.root.DOScale(addTime.restScale, 0.34f)
                .SetEase(Ease.OutBack).SetUpdate(true).SetRecyclable(true)
                .SetTarget(addTime.root));
        }

        private void OnEnable()
        {
            Current = this;
            ClearCommandFailure();
        }

        private void OnDisable()
        {
            if (ReferenceEquals(Current, this)) Current = null;
            ClearCommandFailure();
            KillSlotTweens(undo);
            KillSlotTweens(extraGlass);
            KillSlotTweens(shuffle);
            KillSlotTweens(addTime);
            KillTrayResize();
            addTimeVisibilityKnown = false;
        }

        internal bool TryHandlePointerDown(Camera camera, Vector2 screenPoint)
        {
            if (!TryPickSlot(camera, screenPoint, out Slot slot,
                    out BoosterBarPresenter.BoosterKind kind)) return false;
            // A missing command binding must not send this button's tap to the board underneath.
            if (presenter == null || !presenter.isActiveAndEnabled) return true;

            bool available = kind switch
            {
                BoosterBarPresenter.BoosterKind.Undo => presenter.CanRequestUndo,
                BoosterBarPresenter.BoosterKind.ExtraGlass => presenter.CanRequestExtraGlass,
                BoosterBarPresenter.BoosterKind.Shuffle => presenter.CanRequestShuffle,
                _ => presenter.CanRequestAddTime,
            };
            if (!available)
            {
                Refuse(slot, kind);
                return true;
            }

            PlayPress(slot);
            switch (kind)
            {
                case BoosterBarPresenter.BoosterKind.Undo: presenter.RequestUndo(); break;
                case BoosterBarPresenter.BoosterKind.ExtraGlass: presenter.RequestExtraGlass(); break;
                case BoosterBarPresenter.BoosterKind.Shuffle: presenter.RequestShuffle(); break;
                case BoosterBarPresenter.BoosterKind.AddTime: presenter.RequestAddTime(); break;
            }
            return true;
        }

        private float TrayDepth() =>
            undo.valid ? undo.root.position.z
            : extraGlass.valid ? extraGlass.root.position.z
            : shuffle.valid ? shuffle.root.position.z
            : addTime.valid ? addTime.root.position.z : 0f;

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
                    "Booster tray bindings missing.",
                    this);
        }

        private void ApplyLayout(bool fourButtons, bool animate)
        {
            bool tween = animate && Application.isPlaying && isActiveAndEnabled;
            SetSlotX(undo,
                fourButtons ? -FourButtonOuterX : -ThreeButtonSpacing, tween);
            SetSlotX(extraGlass,
                fourButtons ? -FourButtonInnerX : 0f, tween);
            SetSlotX(shuffle,
                fourButtons ? FourButtonInnerX : ThreeButtonSpacing, tween);
            SetSlotX(addTime, FourButtonOuterX, tween);
            SetTrayWidth(fourButtons ? FourButtonTrayWidth : ThreeButtonTrayWidth, tween);
        }

        private static void SetSlotX(Slot slot, float x, bool animate)
        {
            if (slot.root == null) return;
            KillLayout(slot);

            Vector3 position = slot.root.localPosition;
            if (Mathf.Approximately(position.x, x)) return;
            if (!animate)
            {
                position.x = x;
                slot.root.localPosition = position;
                return;
            }

            Tween layout = slot.root.DOLocalMoveX(x, LayoutSeconds)
                .SetEase(Ease.InOutQuad).SetUpdate(true).SetRecyclable(true)
                .SetTarget(slot.root);
            slot.layout = layout;
            layout.OnKill(() =>
            {
                if (ReferenceEquals(slot.layout, layout)) slot.layout = null;
            });
        }

        private void SetTrayWidth(float targetWidth, bool animate)
        {
            // A new target at the current width must still cancel motion toward the old target.
            KillTrayResize();
            float currentWidth = trayArtwork != null
                ? trayArtwork.size.x
                : trayGoldFrame != null ? trayGoldFrame.size.x : targetWidth;
            if (Mathf.Approximately(currentWidth, targetWidth))
            {
                WriteTrayWidth(targetWidth);
                return;
            }

            if (!animate)
            {
                WriteTrayWidth(targetWidth);
                return;
            }

            float width = currentWidth;
            Tween resize = DOTween.To(() => width, value =>
                {
                    width = value;
                    WriteTrayWidth(value);
                }, targetWidth, LayoutSeconds)
                .SetEase(Ease.InOutQuad).SetUpdate(true).SetRecyclable(true)
                .SetTarget(trayRoot);
            trayResize = resize;
            resize.OnKill(() =>
            {
                if (ReferenceEquals(trayResize, resize)) trayResize = null;
            });
        }

        private void KillTrayResize()
        {
            Tween resize = trayResize;
            trayResize = null;
            if (resize != null && resize.IsActive()) resize.Kill();
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

        internal void ShowCommandFailure(BoosterBarPresenter.BoosterKind kind)
        {
            if (!isActiveAndEnabled) return;
            EnsureResolved();
            ClearCommandFailure();
            Slot slot = kind switch
            {
                BoosterBarPresenter.BoosterKind.Undo => undo,
                BoosterBarPresenter.BoosterKind.ExtraGlass => extraGlass,
                BoosterBarPresenter.BoosterKind.Shuffle => shuffle,
                _ => addTime,
            };
            PlayRefusal(slot);
            if (commandFailureRoot == null || commandFailureLabel == null) return;
            commandFailureLabel.text = CommandFailureMessage;
            commandFailureRoot.SetActive(true);
            commandFailureHide = StartCoroutine(HideCommandFailureAfterDelay());
        }

        internal void ClearCommandFailure()
        {
            if (commandFailureHide != null)
            {
                StopCoroutine(commandFailureHide);
                commandFailureHide = null;
            }
            if (commandFailureRoot != null) commandFailureRoot.SetActive(false);
            if (commandFailureLabel != null) commandFailureLabel.text = string.Empty;
        }

        private IEnumerator HideCommandFailureAfterDelay()
        {
            yield return new WaitForSecondsRealtime(CommandFailureSeconds);
            commandFailureHide = null;
            ClearCommandFailure();
        }

        private static bool Contains(Slot slot, Vector3 world)
        {
            if (!slot.valid || slot.root == null || slot.face == null
                || !slot.root.gameObject.activeInHierarchy || !slot.face.enabled
                || !slot.face.gameObject.activeInHierarchy)
                return false;

            // Read current bounds for every tap because safe-area fitting can move and scale the tray.
            Bounds bounds = slot.face.bounds;
            bounds.Expand(TouchPadding);
            Vector3 flat = new Vector3(world.x, world.y, bounds.center.z);
            return bounds.Contains(flat);
        }

        private void PlayPress(Slot slot)
        {
            KillPress(slot);
            Transform root = slot.root;
            Vector3 rest = slot.restScale;
            TrackPress(slot, DOTween.Sequence()
                .SetTarget(root).SetUpdate(true).SetRecyclable(true)
                .Append(root.DOScale(rest * PressScale, PressDownSeconds)
                    .SetEase(Ease.OutQuad).SetRecyclable(true))
                .Append(root.DOScale(rest, PressUpSeconds)
                    .SetEase(Ease.OutBack).SetRecyclable(true)));
        }

        private void Refuse(Slot slot, BoosterBarPresenter.BoosterKind kind)
        {
            if (presenter.CanOfferShop(kind)
                && BartenderShopPresenter.TryOpenCurrentScene())
            {
                BsAudio.NotEnoughCoins();
                return;
            }

            PlayRefusal(slot);
        }

        private void PlayRefusal(Slot slot)
        {
            if (!slot.valid || slot.root == null) return;
            KillPress(slot);

            Transform root = slot.root;
            Vector3 rest = slot.restScale;

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

            TrackPress(slot, sequence);

            BsAudio.Instance?.Play(BsSfx.Invalid, RefusalVolume, RefusalPitch);
            BartenderHaptics.Light();
        }

        private static void TrackPress(Slot slot, Tween press)
        {
            slot.press = press;
            press.OnKill(() =>
            {
                // Auto-kill returns the object to DOTween's pool. Release our handle before reuse.
                if (!ReferenceEquals(slot.press, press)) return;
                slot.press = null;
                RestorePressPose(slot);
            });
        }

        private static void KillPress(Slot slot)
        {
            Tween press = slot.press;
            slot.press = null;
            if (press != null && press.IsActive()) press.Kill();
            RestorePressPose(slot);
        }

        private static void RestorePressPose(Slot slot)
        {
            if (slot.root == null || !slot.valid) return;
            slot.root.localScale = slot.restScale;
            slot.root.localRotation = slot.restRotation;
        }

        private static void KillLayout(Slot slot)
        {
            Tween layout = slot.layout;
            slot.layout = null;
            if (layout != null && layout.IsActive()) layout.Kill();
        }

        private static void KillSlotTweens(Slot slot)
        {
            KillPress(slot);
            KillLayout(slot);
        }
    }
}
