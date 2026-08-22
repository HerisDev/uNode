using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEditor;

namespace MaxyGames.UNode.Editors.UI {
	/// <summary>
	/// Generic tree-view row element modeled after GraphPanel's PanelElement.
	/// Extends ClickableElement (handles clicks/selection) and implements
	/// ITreeViewItemElement for drag-and-drop support.
	///
	/// Usage:
	/// <code>
	/// makeItem: () => new PanelElement&lt;MyData&gt;(),
	/// bindItem: (ve, index) => {
	///     var item = ve as PanelElement&lt;MyData&gt;;
	///     item.index = index;
	///     item.value = treeView.GetItemDataForIndex&lt;MyData&gt;(index);
	///     item.GetDragGenericData = () => new Dictionary&lt;string,object&gt; { {"myData", item.value} };
	///     item.CanDrag = () => true;
	///     item.CanHaveChilds = () => item.value is Folder;
	///     item.onClick = (_) => { selected = item.value; };
	/// }
	/// </code>
	/// </summary>
	internal class PanelElement<T> : ClickableElement, ITreeViewItemElement {
		public T value;

		private ClickableElement removeElement;

		public PanelElement() : base("") {
			name = "content";
			Init();
		}

		public PanelElement(string text) : base(text) {
			name = "content";
			Init();
		}

		public PanelElement(string text, Action onClick) : base(text, onClick) {
			name = "content";
			Init();
		}

		void Init() {
			this.RemoveManipulator(clickable);
			this.AddManipulator(new LeftMouseClickable(evt => {
				if(onClick != null && !evt.shiftKey) {
					onClick(evt);
				}
			}) { stopPropagationOnClick = false });
		}

		/// <summary>Set in bindItem: returns the drag payload for this row. Return null to make the row non-draggable.</summary>
		public Func<Dictionary<string, object>> GetDragGenericData;
		public Func<IEnumerable<UnityEngine.Object>> GetDragReferences;

		/// <summary>
		/// Drag behavior overrides. If not set, all rows are draggable
		/// and no rows accept child drops.
		/// </summary>
		public Func<bool> CanDragFunc;
		public Func<bool> CanHaveChildsFunc;
		public Func<bool> CanDragInsideParentFunc;

		public Action removeAction {
			set {
				if(value != null) {
					if(removeElement == null) {
						this.Add(new VisualElement() { name = "spacer" });
						removeElement = new ClickableElement("-") {
							name = "content-button-remove",
						};
						this.Add(removeElement);
					}
					removeElement.onClick = (_) => value.Invoke();
				}
				else if(removeElement != null) {
					removeElement.RemoveFromHierarchy();
					removeElement = null;
				}
			}
		}

		public int index { get; set; }

		public bool CanDragInsideParent() {
			if(CanDragInsideParentFunc != null)
				return CanDragInsideParentFunc();
			return true;
		}

		public bool CanHaveChilds() {
			if(CanHaveChildsFunc != null)
				return CanHaveChildsFunc();
			return false;
		}

		public bool CanDrag() {
			if(CanDragFunc != null)
				return CanDragFunc();
			return true;
		}

		Dictionary<string, object> ITreeViewItemElement.GetDragGenericData() {
			return GetDragGenericData != null ? GetDragGenericData() : null;
		}

		IEnumerable<UnityEngine.Object> ITreeViewItemElement.GetDraggedReferences() {
			return GetDragReferences != null ? GetDragReferences() : null;
		}
	}
}
