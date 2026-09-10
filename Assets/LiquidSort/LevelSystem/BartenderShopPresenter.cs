using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Routes the menu's coin shop and page visibility. Package labels and prices are display art only.
    /// </summary>
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

        /// <summary>True while the authored menu shop is visible.</summary>
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
                reason = "BartenderMainMenuPresenter referansı eksik.";
                return false;
            }
            if (screen == null || !screen.transform.IsChildOf(transform))
            {
                reason = "Authored Shop Page menü hiyerarşisine bağlı değil.";
                return false;
            }
            if (fade == null || (fade.transform != screen.transform
                && !fade.transform.IsChildOf(screen.transform)))
            {
                reason = "Shop CanvasGroup referansı eksik veya yanlış ekrana ait.";
                return false;
            }

            reason = null;
            return true;
        }

        [ContextMenu("Validate Authored Shop Bindings")]
        private void ValidateFromContextMenu()
        {
            if (ValidateBindings(out string reason))
                Debug.Log("Authored menu shop bindings are valid.", this);
            else
                Debug.LogError("Authored menu shop binding error: " + reason, this);
        }

        /// <summary>Closes the visible authored menu shop, if one owns the page.</summary>
        public static bool CloseAny()
        {
            BartenderShopPresenter target = active != null ? active : currentMenu;
            if (target == null || !target.IsOpen) return false;
            target.Close();
            return true;
        }

        /// <summary>Shows the authored menu shop.</summary>
        public bool Open()
        {
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

        /// <summary>Closes Shop and restores Home in the same frame.</summary>
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

        /// <summary>
        /// Opens the active scene's shop. Returns false if none can take over so the caller can show
        /// rejection feedback.
        /// </summary>
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
