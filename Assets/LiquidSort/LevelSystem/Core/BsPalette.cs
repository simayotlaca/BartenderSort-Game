using System;
using System.Collections.Generic;
using UnityEngine;

namespace BartenderSort.Core
{
    /// <summary>
    /// Drink colours used by editor swatches and gameplay liquid. Similar coffee tones are reserved for
    /// later levels.
    /// </summary>
    [CreateAssetMenu(menuName = "Bartender Sort/Palette", fileName = "BsPalette")]
    public class BsPalette : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string Name;
            public Color Color;
        }

        public List<Entry> Colors = new List<Entry>();

        public int Count => Colors.Count;

        public Color ColorAt(int index)
        {
            if (index < 0 || index >= Colors.Count) return Color.magenta;
            return Colors[index].Color;
        }

        public string NameAt(int index)
        {
            if (index < 0 || index >= Colors.Count) return "?";
            var n = Colors[index].Name;
            return string.IsNullOrEmpty(n) ? ("Color " + index) : n;
        }
    }
}
