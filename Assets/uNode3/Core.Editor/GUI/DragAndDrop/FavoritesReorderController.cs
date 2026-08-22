using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MaxyGames.UNode.Editors.UI {
	/// <summary>
	/// Drag-and-drop controller for the favorites tree.
	///
	/// Extends the standard TreeViewCustomDragAndDropController so that all
	/// hit-detection, drag-start, and indicator rendering come from Unity's
	/// built-in implementation (which works now that favorites rows use the
	/// same PanelElement pattern as GraphPanel).
	///
	/// The only override is OnDrop: instead of calling viewController.Move
	/// (which would mutate Unity's internal tree into a state that doesn't
	/// match our data source), it reads the dragged entry id from the drag
	/// payload, resolves the drop slot via a callback, persists the move
	/// through FavoritesManager, and lets the window rebuild the tree.
	/// </summary>
	internal class FavoritesReorderController : TreeViewCustomDragAndDropController {
		readonly Action<string, int> onDrop;

		public FavoritesReorderController(BaseTreeView view, Action<string, int> onDrop) : base(view, null) {
			this.onDrop = onDrop;
		}

		public override void OnDrop(IListDragAndDropArgs args) {
			// Read the dragged entry id from the drag payload.
			string movedID = null;
			try {
				var data = DragAndDropUtility.dragAndDrop.data;
				movedID = data?.GetGenericData("favoriteID") as string;
			} catch { }
			if(string.IsNullOrEmpty(movedID))
				return;

			// Pass the insert index to the window; it resolves parent + sibling,
			// validates, persists the move, and rebuilds the tree from data.
			onDrop?.Invoke(movedID, args.insertAtIndex);
		}
	}
}
