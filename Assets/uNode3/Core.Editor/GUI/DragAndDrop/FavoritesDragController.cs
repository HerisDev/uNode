using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MaxyGames.UNode.Editors.UI {
	/// <summary>
	/// Drag-and-drop controller for the favorites tree.
	///
	/// Reads the dragged entry id from the drag payload and resolves the
	/// drop slot against the FavoritesManager data (via callbacks). On drop
	/// it persists the move through the onDrop callback and rebuilds the
	/// tree from persisted data.
	///
	/// It intentionally does NOT call viewController.Move (which would mutate
	/// Unity's internal tree into a state that doesn't match our data source).
	/// </summary>
	internal class FavoritesDragController : BaseReorderableDragAndDropController {
		readonly Action<string, int> onDrop;

		public FavoritesDragController(BaseTreeView view, Action<string, int> onDrop) : base(view) {
			this.onDrop = onDrop;
			enableReordering = true;
		}

		public override DragVisualMode HandleDragAndDrop(IListDragAndDropArgs args) {
			if(!enableReordering)
				return DragVisualMode.Rejected;
			// Only accept drags that originated from this tree.
			return args.dragAndDropData.userData == m_View ? DragVisualMode.Move : DragVisualMode.Rejected;
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
			// persists the move, and rebuilds the tree from data.
			int insertIndex = args.insertAtIndex;
			onDrop?.Invoke(movedID, insertIndex);
		}
	}
}
