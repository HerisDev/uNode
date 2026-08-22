using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace MaxyGames.UNode.Editors.UI {
	/// <summary>
	/// TreeView row for a favorites Entry, modeled after GraphPanel's PanelElement.
	/// Uses ClickableElement + LeftMouseClickable for manual selection handling
	/// (matching GraphPanel's pattern where selectionType = SelectionType.None).
	/// </summary>
	internal class FavoritesTreeItem : ClickableElement, ITreeViewItemElement {
		public FavoritesDataAsset.Entry entry;
		public bool isVirtualChild;

		public FavoritesTreeItem() : base("") {
			name = "content";
			// Replace the default clickable with LeftMouseClickable (GraphPanel pattern).
			this.RemoveManipulator(clickable);
			this.AddManipulator(new LeftMouseClickable(evt => {
				if(onClick != null && !evt.shiftKey) {
					onClick(evt);
				}
			}) { stopPropagationOnClick = false });
			// Stretch to fill the full row wrapper so there are no dead zones.
			style.flexGrow = 1;
			style.alignSelf = Align.Stretch;
			style.height = 20;
			style.minHeight = 20;
			style.flexDirection = FlexDirection.Row;
			style.alignItems = Align.Center;
			style.paddingLeft = 4;
			style.paddingRight = 4;
			Add(new Image { name = "icon", style = { width = 16, height = 16, marginRight = 6 } });
			Add(new Label { name = "label", style = { flexGrow = 1 } });
			Add(new Label { name = "kind", style = { fontSize = 9, color = new Color(.5f, .5f, .5f), marginRight = 4 } });
		}

		public Func<Dictionary<string, object>> GetDragGenericData;
		public Func<IEnumerable<UnityEngine.Object>> GetDragReferences;

		public int index { get; set; }

		public bool CanDragInsideParent() {
			return !isVirtualChild && !(entry != null && entry.isVirtual);
		}

		public bool CanHaveChilds() {
			return entry != null && entry.kind == FavoriteKind.Folder && !entry.isVirtual;
		}

		public bool CanDrag() {
			return !isVirtualChild && !(entry != null && entry.isVirtual);
		}

		Dictionary<string, object> ITreeViewItemElement.GetDragGenericData() {
			return GetDragGenericData != null ? GetDragGenericData() : null;
		}

		IEnumerable<UnityEngine.Object> ITreeViewItemElement.GetDraggedReferences() {
			return GetDragReferences != null ? GetDragReferences() : null;
		}
	}
}
