using System;
using System.Collections.Generic;
using BartenderSort.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    /// <summary>Fixed authored card slots form a paged collection under the bottom tabs.</summary>
    [DisallowMultipleComponent]
    public sealed class BartenderRecipeBookView : MonoBehaviour
    {
        [SerializeField] private GameObject pageRoot;
        [SerializeField] private TextMeshProUGUI collectionCount;
        [SerializeField] private TextMeshProUGUI pageNumber;
        [SerializeField] private TextMeshProUGUI emptyText;
        [SerializeField] private Button previousButton;
        [SerializeField] private Button nextButton;
        [SerializeField] private Button closeButton;
        [SerializeField] private RecipeBookCardView[] cards = new RecipeBookCardView[0];

        public GameObject PageRoot => pageRoot;
        public bool IsOpen => pageRoot != null && pageRoot.activeInHierarchy;
        public int PageSize => cards?.Length ?? 0;
        public bool IsReady => pageRoot != null && collectionCount != null
            && pageNumber != null && previousButton != null && nextButton != null
            && PageSize > 0 && Array.TrueForAll(cards, card => card != null && card.IsReady);

        internal void Bind(GameObject root, TextMeshProUGUI count, TextMeshProUGUI page,
            TextMeshProUGUI empty, Button previous, Button next, RecipeBookCardView[] slots)
        {
            pageRoot = root;
            collectionCount = count;
            pageNumber = page;
            emptyText = empty;
            previousButton = previous;
            nextButton = next;
            cards = slots;
        }

        internal void Connect(BartenderRecipeBookPresenter presenter)
        {
            previousButton.onClick.AddListener(presenter.PreviousPage);
            nextButton.onClick.AddListener(presenter.NextPage);
            if (closeButton != null) closeButton.onClick.AddListener(presenter.Close);
            foreach (RecipeBookCardView card in cards) card.ReplayRequested += presenter.ReplayLevel;
        }

        internal void Disconnect(BartenderRecipeBookPresenter presenter)
        {
            if (previousButton != null) previousButton.onClick.RemoveListener(presenter.PreviousPage);
            if (nextButton != null) nextButton.onClick.RemoveListener(presenter.NextPage);
            if (closeButton != null) closeButton.onClick.RemoveListener(presenter.Close);
            if (cards != null)
                foreach (RecipeBookCardView card in cards)
                    if (card != null) card.ReplayRequested -= presenter.ReplayLevel;
        }

        public void SetFeedback(string text)
        {
            if (emptyText != null)
            {
                emptyText.gameObject.SetActive(true);
                emptyText.text = text;
            }
            else if (collectionCount != null) collectionCount.text = text;
        }

        public void Render(IReadOnlyList<BartenderRecipeBookEntry> entries, int page,
            int unlockedCount, BsPalette palette, bool available)
        {
            if (!IsReady) return;
            int pageCount = Mathf.Max(1, Mathf.CeilToInt((float)entries.Count / PageSize));
            page = Mathf.Clamp(page, 0, pageCount - 1);
            collectionCount.text = available ? unlockedCount + " / " + entries.Count + " COLLECTED"
                : "PLAYER PROGRESS COULD NOT BE LOADED";
            pageNumber.text = (page + 1) + " / " + pageCount;
            previousButton.interactable = page > 0;
            nextButton.interactable = page + 1 < pageCount;
            if (emptyText != null)
            {
                emptyText.gameObject.SetActive(unlockedCount == 0);
                emptyText.text = "Deliver cocktails in normal levels to fill your recipe book.";
            }
            for (int i = 0; i < PageSize; i++)
            {
                int index = page * PageSize + i;
                cards[i].gameObject.SetActive(index < entries.Count);
                if (index < entries.Count) cards[i].Render(entries[index], palette);
            }
        }
    }
}
