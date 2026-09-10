using System;
using System.Collections.Generic;
using BartenderSort.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class BartenderRecipeBookPresenter : MonoBehaviour
    {
        [SerializeField] private BartenderRecipeBookView authoredView;
        [SerializeField] private BsPalette palette;
        private BartenderMainMenuPresenter menu;
        private readonly List<string> catalogue = new List<string>();
        private readonly Dictionary<string, int> firstLevels = new Dictionary<string, int>();
        private readonly Dictionary<string, int> firstSlots = new Dictionary<string, int>();
        private readonly List<BartenderRecipeBookEntry> entries = new List<BartenderRecipeBookEntry>();
        private bool catalogueLoaded;
        private bool connected;
        private bool subscribed;
        private int page;
        private int unlockedCount;

        internal static void Ensure(BartenderMainMenuPresenter owner)
        {
            var presenter = owner.GetComponent<BartenderRecipeBookPresenter>();
            if (presenter == null) presenter = owner.gameObject.AddComponent<BartenderRecipeBookPresenter>();
            presenter.menu = owner;
            if (presenter.authoredView == null)
                presenter.authoredView = owner.GetComponentInChildren<BartenderRecipeBookView>(true);
            if (presenter.authoredView != null) presenter.ConnectView();
            if (presenter.isActiveAndEnabled) presenter.Subscribe();
        }

        private void OnEnable() => Subscribe();
        private void OnDisable()
        {
            if (subscribed) BartenderProgressService.RecipeBookChanged -= Refresh;
            subscribed = false;
        }
        private void OnDestroy()
        {
            if (subscribed) BartenderProgressService.RecipeBookChanged -= Refresh;
            if (connected && authoredView != null) authoredView.Disconnect(this);
        }
        private void Update()
        {
            if (connected && authoredView.IsOpen && Input.GetKeyDown(KeyCode.Escape)) Close();
        }
        private void Subscribe()
        {
            if (subscribed || menu == null) return;
            BartenderProgressService.RecipeBookChanged += Refresh;
            subscribed = true;
        }

        private bool ConnectView()
        {
            if (connected) return true;
            if (authoredView == null || !authoredView.IsReady)
            {
                Debug.LogError("Recipe Book prefab bindings are incomplete.", this);
                return false;
            }
            menu.RegisterRecipePage(authoredView.PageRoot);
            authoredView.Connect(this);
            connected = true;
            return true;
        }

        public void Open()
        {
            if (menu == null || !menu.CanUseMenuFeatures) return;
            if (authoredView == null) authoredView = BuildDefaultView();
            if (!ConnectView()) return;
            if (palette == null) palette = Resources.Load<BsPalette>("BsPalette");
            Refresh(BartenderProgressService.RecipeBook);
            menu.EnterRecipeBookPresentation();
        }

        public void Close() { if (menu != null) menu.ExitShopPresentation(); }
        public void ReplayLevel(int slot)
        {
            if (!connected || !authoredView.IsOpen || menu == null) return;
            if (!menu.TryStartRecipeReplay(slot, out string reason))
                authoredView.SetFeedback(reason ?? "LEVEL COULD NOT BE STARTED");
        }
        public void PreviousPage() { if (page > 0) { page--; Render(); } }
        public void NextPage()
        {
            if (connected && (page + 1) * authoredView.PageSize < entries.Count) { page++; Render(); }
        }

        private void Refresh(BartenderRecipeBookSnapshot snapshot)
        {
            if (!catalogueLoaded)
            {
                IReadOnlyList<BsLevel> levels = BartenderLevelController.RecipeCatalogueLevels;
                for (int campaignSlot = 0; campaignSlot < levels.Count; campaignSlot++)
                {
                    BsLevel level = levels[campaignSlot];
                    if (level == null || level.Orders == null) continue;
                    foreach (OrderDef order in level.Orders)
                    {
                        string key = BartenderRecipeKey.From(order);
                        if (string.IsNullOrEmpty(key) || firstLevels.ContainsKey(key)) continue;
                        catalogue.Add(key);
                        firstLevels.Add(key, level.Index);
                        firstSlots.Add(key, campaignSlot);
                    }
                }
                catalogueLoaded = true;
            }
            entries.Clear();
            // Collected recipes come first; previous art/catalogue revisions remain visible.
            foreach (string key in snapshot.Keys)
            {
                if (BartenderRecipeKey.TryDecode(key, out OrderDef collectedRecipe))
                    entries.Add(new BartenderRecipeBookEntry(key, collectedRecipe,
                        firstLevels.TryGetValue(key, out int firstLevel) ? firstLevel : 0, true,
                        firstSlots.TryGetValue(key, out int slot) ? slot : -1));
            }
            unlockedCount = entries.Count;
            foreach (string key in catalogue)
            {
                if (!snapshot.IsUnlocked(key) && BartenderRecipeKey.TryDecode(key, out OrderDef lockedRecipe))
                    entries.Add(new BartenderRecipeBookEntry(key, lockedRecipe,
                        firstLevels[key], false, firstSlots[key]));
            }
            Render();
        }

        private void Render()
        {
            if (!connected) return;
            page = Mathf.Clamp(page, 0, Mathf.Max(0, (entries.Count - 1) / authoredView.PageSize));
            authoredView.Render(entries, page, unlockedCount, palette, BartenderProgressService.IsAvailable);
        }

        /// <summary>Functional code-only layout until the authored book page is connected.</summary>
        private BartenderRecipeBookView BuildDefaultView()
        {
            GameObject root = Ui("Recipe Book Page", menu.FeaturePageParent);
            RectTransform rect = root.GetComponent<RectTransform>();
            RectTransform template = menu.FeaturePageTemplate;
            if (template != null)
            {
                rect.anchorMin = template.anchorMin;
                rect.anchorMax = template.anchorMax;
                rect.pivot = template.pivot;
                rect.sizeDelta = template.sizeDelta;
                rect.anchoredPosition = template.anchoredPosition;
                rect.localScale = template.localScale;
            }
            else Stretch(rect);
            Image background = root.AddComponent<Image>();
            background.color = new Color32(25, 19, 56, 255);

            TMP_FontAsset font = TMP_Settings.defaultFontAsset;
            foreach (TextMeshProUGUI text in menu.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (text.font != null) { font = text.font; break; }
            Label("Title", root.transform, "RECIPE BOOK", font, 48f, 0.05f, 0.88f, 0.95f, 0.96f);
            TextMeshProUGUI count = Label("CollectionCount", root.transform, "", font, 28f,
                0.05f, 0.84f, 0.95f, 0.89f);
            TextMeshProUGUI empty = Label("CollectionHint", root.transform, "", font, 24f,
                0.07f, 0.76f, 0.93f, 0.83f);
            var rows = new RecipeBookCardView[6];
            for (int i = 0; i < rows.Length; i++)
            {
                GameObject card = Ui("Recipe_" + (i + 1), root.transform);
                Anchor(card.GetComponent<RectTransform>(), 0.06f, 0.65f - i * 0.103f,
                    0.94f, 0.745f - i * 0.103f);
                card.AddComponent<Image>().color = i % 2 == 0
                    ? new Color32(58, 40, 91, 255) : new Color32(36, 48, 89, 255);
                var title = Label("Glass", card.transform, "", font, 26f, 0.03f, 0.64f, 0.97f, 0.96f);
                var recipe = Label("Recipe", card.transform, "", font, 23f, 0.03f, 0.29f, 0.97f, 0.67f);
                var status = Label("Status", card.transform, "", font, 21f, 0.03f, 0.02f, 0.97f, 0.30f);
                rows[i] = card.AddComponent<RecipeBookCardView>();
                rows[i].Bind(title, recipe, status);
            }
            Button previous = PageButton("Previous", "<", root.transform, font, 0.10f, 0.30f);
            Button next = PageButton("Next", ">", root.transform, font, 0.70f, 0.90f);
            TextMeshProUGUI pageLabel = Label("Page", root.transform, "", font, 28f,
                0.32f, 0.055f, 0.68f, 0.115f);
            var view = root.AddComponent<BartenderRecipeBookView>();
            view.Bind(root, count, pageLabel, empty, previous, next, rows);
            root.SetActive(false);
            return view;
        }

        private static Button PageButton(string name, string caption, Transform parent,
            TMP_FontAsset font, float left, float right)
        {
            GameObject root = Ui(name, parent);
            Anchor(root.GetComponent<RectTransform>(), left, 0.055f, right, 0.115f);
            Image image = root.AddComponent<Image>();
            image.color = new Color32(67, 46, 108, 255);
            Button button = root.AddComponent<Button>();
            button.targetGraphic = image;
            Label("Label", root.transform, caption, font, 34f, 0f, 0f, 1f, 1f);
            return button;
        }

        private static TextMeshProUGUI Label(string name, Transform parent, string value,
            TMP_FontAsset font, float size, float left, float bottom, float right, float top)
        {
            TextMeshProUGUI text = Ui(name, parent).AddComponent<TextMeshProUGUI>();
            Anchor(text.rectTransform, left, bottom, right, top);
            text.font = font;
            text.fontSize = size;
            text.enableAutoSizing = true;
            text.fontSizeMin = size * 0.7f;
            text.fontSizeMax = size;
            text.color = new Color32(255, 242, 211, 255);
            text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private static GameObject Ui(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = 5;
            go.transform.SetParent(parent, false);
            return go;
        }
        private static void Stretch(RectTransform rect) => Anchor(rect, 0f, 0f, 1f, 1f);
        private static void Anchor(RectTransform rect, float left, float bottom, float right, float top)
        {
            rect.anchorMin = new Vector2(left, bottom);
            rect.anchorMax = new Vector2(right, top);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }
    }
}
