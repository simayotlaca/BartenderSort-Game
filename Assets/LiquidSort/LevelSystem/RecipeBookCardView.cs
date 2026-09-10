using System;
using BartenderSort.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiquidSort.Levels
{
    [DisallowMultipleComponent]
    public sealed class RecipeBookCardView : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI recipeText;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private GameObject lockIcon;
        [SerializeField] private Button replayButton;
        private int replaySlot = -1;
        private bool buttonConnected;
        public event Action<int> ReplayRequested;
        [Tooltip("Optional ingredient swatches, bottom to top for LAYER recipes.")]
        [SerializeField] private Image[] colorSwatches = new Image[0];

        public bool IsReady => titleText != null && recipeText != null && statusText != null;

        internal void Bind(TextMeshProUGUI title, TextMeshProUGUI recipe, TextMeshProUGUI status)
        {
            titleText = title;
            recipeText = recipe;
            statusText = status;
        }

        public void Render(BartenderRecipeBookEntry entry, BsPalette palette)
        {
            EnsureReplayButton();
            replaySlot = entry.CampaignSlot;
            bool canReplay = BartenderLevelController.CanReplayCampaignSlot(replaySlot);
            replayButton.interactable = canReplay && BartenderProgressService.IsAvailable;
            OrderDef recipe = entry.Recipe;
            titleText.text = BsRules.DisplayName(recipe.Glass) + "  /  "
                + (recipe.Kind == OrderKind.Layer ? "LAYER" : "SET");
            recipeText.text = entry.IsUnlocked ? recipe.Describe(palette) : "???";
            statusText.text = entry.IsUnlocked ? "COLLECTED"
                : entry.FirstLevel > 0 ? "DELIVER TO UNLOCK  -  LEVEL " + entry.FirstLevel
                : "DELIVER TO UNLOCK";
            if (canReplay) statusText.text = (entry.IsUnlocked ? "COLLECTED  -  " : "UNLOCK RECIPE  -  ")
                + "PLAY LEVEL " + entry.FirstLevel;
            statusText.color = entry.IsUnlocked
                ? new Color32(139, 226, 140, 255) : new Color32(204, 191, 215, 255);
            if (lockIcon != null) lockIcon.SetActive(!entry.IsUnlocked);
            if (colorSwatches == null) return;
            for (int i = 0; i < colorSwatches.Length; i++)
            {
                Image swatch = colorSwatches[i];
                if (swatch == null) continue;
                bool visible = entry.IsUnlocked && i < recipe.Contents.Count;
                swatch.gameObject.SetActive(visible);
                if (visible) swatch.color = palette != null
                    ? palette.ColorAt(recipe.Contents[i]) : Color.white;
            }
        }

        private void EnsureReplayButton()
        {
            if (buttonConnected) return;
            if (replayButton == null) replayButton = GetComponent<Button>();
            if (replayButton == null)
            {
                Graphic target = GetComponent<Graphic>();
                if (target == null)
                {
                    Image hitArea = gameObject.AddComponent<Image>();
                    hitArea.color = Color.clear;
                    target = hitArea;
                }
                replayButton = gameObject.AddComponent<Button>();
                replayButton.targetGraphic = target;
                target.raycastTarget = true;
            }
            replayButton.onClick.AddListener(HandleReplay);
            buttonConnected = true;
        }

        private void HandleReplay()
        {
            if (BartenderLevelController.CanReplayCampaignSlot(replaySlot))
                ReplayRequested?.Invoke(replaySlot);
        }

        private void OnDestroy()
        {
            if (buttonConnected && replayButton != null)
                replayButton.onClick.RemoveListener(HandleReplay);
        }
    }
}
