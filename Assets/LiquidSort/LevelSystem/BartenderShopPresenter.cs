using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class BartenderShopPresenter : MonoBehaviour
    {
        [Header("Authored menu shop")]
        [SerializeField] private BartenderMainMenuPresenter menuPresenter;
        [SerializeField] private GameObject screen;
        [SerializeField] private CanvasGroup fade;

        private static BartenderShopPresenter currentMenu;
        private static BartenderShopPresenter active;

        private bool menuChromeSuppressed;

        public bool IsOpen => screen != null && screen.activeSelf;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            currentMenu = null;
            active = null;
        }

        private void OnEnable()
        {
            currentMenu = this;

            if (!ValidateBindings(out string reason))
            {
                Debug.LogError("Authored menu shop binding error: " + reason, this);
                return;
            }

            if (screen.activeSelf)
            {
                active = this;
                menuPresenter.EnterShopPresentation();
                menuChromeSuppressed = true;
                fade.alpha = 1f;
                fade.interactable = true;
                fade.blocksRaycasts = true;
            }
        }

        private void OnDisable()
        {
            if (screen != null) screen.SetActive(false);
            RestoreMenuChrome();
            if (active == this) active = null;
            if (currentMenu == this) currentMenu = null;
        }

        private void OnDestroy()
        {
            RestoreMenuChrome();
            if (active == this) active = null;
            if (currentMenu == this) currentMenu = null;
        }

        public bool ValidateBindings(out string reason)
        {
            if (menuPresenter == null)
            {
                reason = "Main menu presenter missing.";
                return false;
            }
            if (screen == null || !screen.transform.IsChildOf(transform))
            {
                reason = "Shop page is outside the menu.";
                return false;
            }
            if (fade == null || (fade.transform != screen.transform
                && !fade.transform.IsChildOf(screen.transform)))
            {
                reason = "Shop CanvasGroup is invalid.";
                return false;
            }

            reason = null;
            return true;
        }

        [ContextMenu("Validate Authored Shop Bindings")]
        private void ValidateFromContextMenu()
        {
            if (!ValidateBindings(out string reason))
                Debug.LogError("Authored menu shop binding error: " + reason, this);
        }

        public static bool CloseAny()
        {
            BartenderShopPresenter target = active != null ? active : currentMenu;
            if (target == null || !target.IsOpen) return false;
            target.Close();
            return true;
        }

        public bool Open()
        {
            if (menuPresenter != null && menuPresenter.MenuSavePending) return false;
            if (!ValidateBindings(out string reason))
            {
                Debug.LogError("Authored menu shop binding error: " + reason, this);
                return false;
            }

            menuPresenter.EnterShopPresentation();
            menuChromeSuppressed = true;
            screen.SetActive(true);
            active = this;

            fade.alpha = 1f;
            fade.interactable = true;
            fade.blocksRaycasts = true;
            return true;
        }

        public void Close()
        {
            if (screen == null || fade == null || !screen.activeSelf)
            {
                RestoreMenuChrome();
                if (active == this) active = null;
                return;
            }

            fade.interactable = false;
            fade.blocksRaycasts = false;
            screen.SetActive(false);
            RestoreMenuChrome();
            if (active == this) active = null;
        }

        public static bool TryOpenCurrentScene()
        {
            try
            {
                return TryOpenCurrentSceneCore();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return false;
            }
        }

        private static bool TryOpenCurrentSceneCore()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return false;

            BartenderShopPresenter menu = currentMenu;
            if (menu != null && menu.gameObject.scene == scene
                && menu.isActiveAndEnabled && menu.menuPresenter != null
                && menu.menuPresenter.Visible)
                return menu.Open();

            BartenderInGameShopOverlay overlay =
                BartenderInGameShopOverlay.FindInScene();
            if (overlay != null)
            {
                overlay.Open();
                return overlay.IsOpen;
            }

            return false;
        }

        private void RestoreMenuChrome()
        {
            if (!menuChromeSuppressed || menuPresenter == null) return;
            menuPresenter.ExitShopPresentation();
            menuChromeSuppressed = false;
        }
    }
}
