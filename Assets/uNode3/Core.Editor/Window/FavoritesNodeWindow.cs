using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;
using MaxyGames.UNode.Editors.UI;

namespace MaxyGames.UNode.Editors {
	public class FavoritesNodeWindow : EditorWindow {
		public static FavoritesNodeWindow window;

		// ── UI ──
		private Toolbar toolbar;
		private DropdownField categoryDropdown;
		private TextField searchField;
		private TreeView entryTreeView;
		private Label statusLabel;
		private VisualElement detailArea;
		private Label detailNameLabel;
		private Label detailTypeLabel;
		private ScrollView memberScroll;
		private Button removeButton;
		private Button addMembersButton;

		// ── State ──
		private DisplayEntry selectedEntry;
		private string currentCategoryID;
		private string searchString = "";
		private Dictionary<string, NodeMenu> nodeMenuCache;
		private Dictionary<int, DisplayEntry> treeIDMap = new Dictionary<int, DisplayEntry>();

		class DisplayEntry {
			public int treeID;
			public FavoritesDataAsset.Entry entry;
			public bool isVirtualChild;
			public List<DisplayEntry> children;
			public int memberCount;
			public float searchScore;   // relevance score (search mode only)
			public string searchPath;   // breadcrumb path shown under the title in search mode
		}

		private int nextTreeID = 1;

		[MenuItem("Tools/uNode/Favorites", false, 104)]
		public static void ShowWindow() {
			window = GetWindow<FavoritesNodeWindow>();
			window.titleContent = new GUIContent("Favorites", uNodeGUIStyle.favoriteIconOn);
			window.minSize = new Vector2(400, 300);
			window.Show();
		}

		// ═══════════════════════════════════════
		//  OnEnable / OnDisable
		// ═══════════════════════════════════════

		private void OnEnable() {
			window = this;
			BuildNodeMenuCache();
			FavoritesManager.onChanged += OnFavoritesChanged;
			currentCategoryID = RestoreLastCategory();
			BuildUI();
			ReloadTreeView();
		}

		const string kLastCategoryKey = "uNode.FavoritesWindow.Category";

		string RestoreLastCategory() {
			var cats = FavoritesManager.GetCategories();
			string saved = SessionState.GetString(kLastCategoryKey, string.Empty);
			if(!string.IsNullOrEmpty(saved) && cats.Any(c => c.id == saved))
				return saved;
			return FavoritesManager.GetDefaultCategory().id;
		}

		void SaveLastCategory() {
			SessionState.SetString(kLastCategoryKey, currentCategoryID ?? string.Empty);
		}

		private void OnDisable() {
			if(window == this)
				window = null;
			FavoritesManager.onChanged -= OnFavoritesChanged;
			rootVisualElement?.UnregisterCallback<KeyDownEvent>(OnWindowKeyDown);
		}

		void OnWindowKeyDown(KeyDownEvent evt) {
			if(evt.actionKey && evt.keyCode == KeyCode.F) {
				searchField?.Focus();
				evt.StopPropagation();
				return;
			}
			if(evt.keyCode == KeyCode.Delete && selectedEntry != null) {
				// Ignore when typing in the search field.
				var focused = rootVisualElement.focusController?.focusedElement as VisualElement;
				while(focused != null) {
					if(focused == searchField)
						return;
					focused = focused.parent;
				}
				RemoveSelected();
				evt.StopPropagation();
			}
		}

		private void OnFavoritesChanged() {
			ReloadTreeView();
		}

		// ═══════════════════════════════════════
		//  Category
		// ═══════════════════════════════════════

		private void UpdateCategoryDropdown() {
			if(categoryDropdown == null) return;
			var cats = FavoritesManager.GetCategories();
			categoryDropdown.choices = cats.Select(c => c.name).ToList();
			var currentCat = cats.FirstOrDefault(c => c.id == currentCategoryID);
			if(currentCat != null)
				categoryDropdown.index = cats.IndexOf(currentCat);
			else if(cats.Count > 0) {
				currentCategoryID = cats[0].id;
				categoryDropdown.index = 0;
			}
		}

		private void OnCategoryChanged(ChangeEvent<string> evt) {
			var cats = FavoritesManager.GetCategories();
			int idx = categoryDropdown.index;
			if(idx >= 0 && idx < cats.Count) {
				currentCategoryID = cats[idx].id;
				SaveLastCategory();
				ReloadTreeView();
			}
		}

		void ShowAddMenu() {
			var menu = new GenericDropdownMenu();
			menu.AddItem("Folder", false, () => CreateNewFolder());
			menu.AddItem("Namespace", false, () => AddNamespaceFavorite());
			menu.AddItem("Type / Member", false, () => OpenItemSelector());
			menu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero), toolbar);
		}

		private void CreateNewCategory() {
			string categoryName = "";
			ActionPopupWindow.Show(
				null,
				(ref object obj) => {
					EditorGUILayout.LabelField("New Category", EditorStyles.boldLabel);
					EditorGUILayout.Space(4);
					categoryName = EditorGUILayout.TextField("Name", categoryName);
					EditorGUILayout.Space(4);
					if(GUILayout.Button("Create") && !string.IsNullOrWhiteSpace(categoryName)) {
						var cat = FavoritesManager.GetOrCreateCategory(categoryName.Trim());
						currentCategoryID = cat.id;
						SaveLastCategory();
						UpdateCategoryDropdown();
						ReloadTreeView();
						ActionPopupWindow.CloseLast();
					}
				}
			);
		}

		private void RemoveSelectedCategory() {
			var cats = FavoritesManager.GetCategories();
			if(cats.Count <= 1) {
				EditorUtility.DisplayDialog("Cannot Remove", "At least one category must remain.", "OK");
				return;
			}
			if(!EditorUtility.DisplayDialog("Remove Category", $"Remove category '{cats.FirstOrDefault(c => c.id == currentCategoryID)?.name}' and all its items?", "Yes", "Cancel"))
				return;
			string removedID = currentCategoryID;
			FavoritesManager.RemoveCategory(removedID);
			// Switch to the first remaining category.
			var remaining = FavoritesManager.GetCategories();
			if(remaining.Count > 0) {
				currentCategoryID = remaining[0].id;
			}
			SaveLastCategory();
			UpdateCategoryDropdown();
			ReloadTreeView();
		}

		// ═══════════════════════════════════════
		//  Tree Data
		// ═══════════════════════════════════════

		class VisibleRow {
			public FavoritesDataAsset.Entry entry;
			public int depth;          // 0 = root level
			public string parentID;    // owning entry id ("" for root)
			public bool isLastChild;   // last sibling in its parent (for slot resolution)
			public bool inNamespace;   // inside a namespace expansion (fixed order)
		}

		/// <summary>Flat depth-first list of rows currently shown (rebuilt on each reload).</summary>
		private readonly List<VisibleRow> visibleRows = new List<VisibleRow>();

		private List<TreeViewItemData<DisplayEntry>> BuildTreeData() {
			treeIDMap.Clear();
			nextTreeID = 1;
			visibleRows.Clear();

			List<FavoritesDataAsset.Entry> allEntries = null;
			if(!string.IsNullOrEmpty(currentCategoryID))
				allEntries = FavoritesManager.asset.entries
					.Where(e => e.categoryID == currentCategoryID)
					.OrderBy(e => e.orderIndex)
					.ToList();

			if(allEntries == null || allEntries.Count == 0)
				return new List<TreeViewItemData<DisplayEntry>>();

			// Search mode: flatten every matching entry into one relevance-ranked list
			// (mirrors ItemSelector's SearchKind.Relevant results).
			if(!string.IsNullOrEmpty(searchString))
				return BuildFlatSearchRows(allEntries);

			// Group children by parentID.
			var childrenOf = allEntries
				.GroupBy(e => e.parentID ?? string.Empty)
				.ToDictionary(g => g.Key, g => g.OrderBy(x => x.orderIndex).ToList());

			// Recursively build tree items + the flat visible-row list.
			List<TreeViewItemData<DisplayEntry>> BuildChildren(string parentID, int depth, bool inNamespace) {
				var result = new List<TreeViewItemData<DisplayEntry>>();
				if(!childrenOf.TryGetValue(parentID, out var entries))
					return result;

				for(int i = 0; i < entries.Count; i++) {
					var entry = entries[i];
					bool lastChild = i == entries.Count - 1;
					int myID = nextTreeID++;
					var de = new DisplayEntry {
						treeID = myID,
						entry = entry,
						isVirtualChild = entry.isVirtual,
					};
					treeIDMap[myID] = de;

					if(!entry.isVirtual) {
						visibleRows.Add(new VisibleRow {
							entry = entry,
							depth = depth,
							parentID = parentID,
							isLastChild = lastChild,
							inNamespace = inNamespace,
						});
					}

					var childItems = BuildChildren(entry.id, depth + 1, inNamespace);

					// For namespace entries, append virtual type children (not reorderable).
					if(entry.kind == FavoriteKind.Namespace && !entry.isVirtual && !inNamespace) {
						var virtualChildren = FavoritesManager.GetVirtualNamespaceChildren(entry.displayName);
						foreach(var vc in virtualChildren) {
							int vID = nextTreeID++;
							var vde = new DisplayEntry {
								treeID = vID,
								entry = vc,
								isVirtualChild = true,
								memberCount = 0,
							};
							treeIDMap[vID] = vde;
							childItems.Add(new TreeViewItemData<DisplayEntry>(vID, vde));
						}
					}

					result.Add(new TreeViewItemData<DisplayEntry>(myID, de, childItems));
				}
				return result;
			}

			var roots = BuildChildren(string.Empty, 0, false);
			return roots;
		}

		/// <summary>
		/// Builds the flat relevance-ranked search list (mirrors ItemSelector's
		/// SearchKind.Relevant results): every matching entry is scored via
		/// ItemSelector's TreeSearcher, sorted by score, and shown without hierarchy.
		/// Folders/namespaces act as path breadcrumbs rather than rows.
		/// </summary>
		List<TreeViewItemData<DisplayEntry>> BuildFlatSearchRows(List<FavoritesDataAsset.Entry> allEntries) {
			var results = new List<DisplayEntry>();

			IEnumerable<FavoritesDataAsset.Entry> ChildrenOf(string id) =>
				allEntries.Where(c => c.parentID == id).OrderBy(x => x.orderIndex);

			static string JoinPath(string parentPath, string segment) =>
				string.IsNullOrEmpty(parentPath) ? segment : parentPath + " > " + segment;

			void AddResult(FavoritesDataAsset.Entry e, string path) {
				float score = ScoreSearchTarget(e);
				if(score < 0f)
					return; // no relevance match
				int id = nextTreeID++;
				var de = new DisplayEntry {
					treeID = id,
					entry = e,
					isVirtualChild = e.isVirtual,
					searchScore = score,
					searchPath = path ?? string.Empty,
				};
				treeIDMap[id] = de;
				results.Add(de);
				visibleRows.Add(new VisibleRow {
					entry = e,
					depth = 0,
					parentID = string.Empty,
					isLastChild = false,
					inNamespace = false,
				});
			}

			void CollectEntry(FavoritesDataAsset.Entry e, string parentPath) {
				string name = GetDisplayName(e);
				switch(e.kind) {
					case FavoriteKind.Folder:
						var folderPath = JoinPath(parentPath, name);
						foreach(var c in ChildrenOf(e.id))
							CollectEntry(c, folderPath);
						break;
					case FavoriteKind.Namespace: {
						var nsPath = JoinPath(parentPath, name);
						foreach(var c in ChildrenOf(e.id))
							CollectEntry(c, nsPath);
						// Virtual types from the namespace expansion are searchable too.
						foreach(var vc in FavoritesManager.GetVirtualNamespaceChildren(name))
							AddResult(vc, nsPath);
						break;
					}
					default:
						AddResult(e, parentPath);
						if(e.kind == FavoriteKind.Type && !e.isVirtual) {
							var typePath = JoinPath(parentPath, name);
							foreach(var c in ChildrenOf(e.id))
								CollectEntry(c, typePath);
						}
						break;
				}
			}

			var knownIDs = new HashSet<string>(allEntries.Select(x => x.id));
			foreach(var root in ChildrenOf(string.Empty)) {
				CollectEntry(root, null);
			}
			// Orphans (broken parent links) are walked as roots so they stay searchable.
			foreach(var orphan in allEntries.Where(e => !string.IsNullOrEmpty(e.parentID) && !knownIDs.Contains(e.parentID))) {
				CollectEntry(orphan, null);
			}

			results.Sort((a, b) => {
				int c = b.searchScore.CompareTo(a.searchScore);
				if(c != 0) return c;
				return a.entry.orderIndex.CompareTo(b.entry.orderIndex);
			});

			return results.Select(de => new TreeViewItemData<DisplayEntry>(de.treeID, de)).ToList();
		}

		float ScoreSearchTarget(FavoritesDataAsset.Entry e) {
			float best = -1f;
			string query = searchString;
			void Consider(string str) {
				if(string.IsNullOrEmpty(str)) return;
				var s = ItemSelector.GetRelevanceScore(query, str);
				if(s > best) best = s;
			}
			Consider(GetDisplayName(e));
			if(e.kind == FavoriteKind.Member)
				Consider(e.memberName);
			else if(e.typeName != null)
				Consider(e.typeName.Split('.').Last());
			return best;
		}

		// ═══════════════════════════════════════
		//  Display Helpers
		// ═══════════════════════════════════════
		/// <summary>
		/// Resolve the parent entry id and sibling index for an insertion slot.
		/// </summary>
		/// <param name="insertIndex">The visible-row index at which an item would be inserted.</param>
		/// <param name="parentID">The resolved parent entry id ("" = category root).</param>
		/// <param name="siblingIndex">The sibling index within that parent (-1 = append).</param>
		/// <returns>false if the slot is invalid (inside a fixed namespace expansion).</returns>
		bool ResolveSlot(int insertIndex, out string parentID, out int siblingIndex, out int indentDepth) {
			parentID = "";
			siblingIndex = insertIndex;
			indentDepth = 0;

			if(visibleRows.Count == 0)
				return insertIndex <= 0;

			// At the very top: root.
			if(insertIndex <= 0) {
				parentID = "";
				siblingIndex = 0;
				indentDepth = 0;
				return true;
			}

			// Past the last visible row: anchor on the last row. If it's a folder,
			// dropping below it means dropping INTO the folder (as its last child).
			if(insertIndex >= visibleRows.Count) {
				var last = visibleRows[visibleRows.Count - 1];
				if(last.entry.kind == FavoriteKind.Folder) {
					parentID = last.entry.id;
					siblingIndex = -1; // append
					indentDepth = last.depth + 1;
				} else {
					parentID = last.parentID ?? "";
					indentDepth = last.depth;
					siblingIndex = CountSiblingsBefore(visibleRows.Count, parentID);
				}
				return true;
			}

			// Anchor on the row above the insertion slot.
			var anchor = visibleRows[insertIndex - 1];

			// Inside a fixed namespace expansion: reject.
			if(anchor.inNamespace && anchor.entry.kind == FavoriteKind.Type)
				return false;

			int nextDepth = visibleRows[insertIndex].depth;

			// Anchor is a folder and the row below is deeper → drop INTO folder.
			if(anchor.entry.kind == FavoriteKind.Folder && anchor.depth < nextDepth) {
				parentID = anchor.entry.id;
				siblingIndex = CountSiblingsBefore(insertIndex, anchor.entry.id);
				indentDepth = anchor.depth + 1;
				return true;
			}

			// Anchor is a folder and the row below is same or shallower → next to the folder.
			parentID = anchor.parentID ?? "";
			indentDepth = anchor.depth;
			siblingIndex = CountSiblingsBefore(insertIndex, parentID);
			return true;
		}

		/// <summary>Count visible rows before insertIndex that share the given parentID.</summary>
		int CountSiblingsBefore(int insertIndex, string parentID) {
			int count = 0;
			for(int i = 0; i < insertIndex; i++) {
				if(visibleRows[i].parentID == parentID)
					count++;
			}
			return count;
		}

		/// <summary>Validate whether a move is allowed (no cycles, no into namespaces).</summary>
		bool CanMove(string movedID, string parentID) {
			var moved = FavoritesManager.asset.entries.FirstOrDefault(e => e.id == movedID);
			if(moved == null || moved.isVirtual) return false;
			if(string.IsNullOrEmpty(parentID)) return true;
			var parent = FavoritesManager.asset.entries.FirstOrDefault(e => e.id == parentID);
			if(parent == null) return false;
			if(parent.kind == FavoriteKind.Namespace) return false;
			if(!parent.CanHaveChilds) return false;
			return true;
		}



		string GetDisplayName(FavoritesDataAsset.Entry e) {
			// For virtual namespace types, targetType is populated but resolvedType is null
			// (resolvedType skips isVirtual). Read it directly.
			Type typeForDisplay = ResolveEntryType(e);

			switch(e.kind) {
				case FavoriteKind.Folder: return e.displayName ?? "(Folder)";
				case FavoriteKind.Namespace: return e.displayName ?? "(Namespace)";
				case FavoriteKind.Member:
					if(isVirtualMember(e))
						return typeForDisplay?.PrettyName() ?? "(Type)";
					return e.memberName ?? "(missing)";
				case FavoriteKind.Node:
					if(!string.IsNullOrEmpty(e.nodeMenuName)) return e.nodeMenuName.Split('.').Last();
					return typeForDisplay?.PrettyName() ?? "(Node)";
				default: // Type
					if(typeForDisplay != null) return typeForDisplay.PrettyName();
					if(!string.IsNullOrEmpty(e.displayName)) return e.displayName;
					return e.typeName.Split('.').Last();
			}
		}

		bool isVirtualMember(FavoritesDataAsset.Entry e) {
			return e.isVirtual && e.kind == FavoriteKind.Type;
		}

		Texture GetIcon(FavoritesDataAsset.Entry e) {
			// Resolve the type so virtual namespace-type rows get a real type icon
			// (resolvedType returns null for isVirtual entries).
			Type iconType = ResolveEntryType(e);

			switch(e.kind) {
				case FavoriteKind.Folder:
					return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.FolderIcon));
				case FavoriteKind.Namespace:
					return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.NamespaceIcon));
				case FavoriteKind.Member:
					if(e.isVirtual) goto default;
					var member = e.targetMember?.GetMembers(false)?.LastOrDefault();
					if(member is MethodInfo) return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.MethodIcon));
					if(member is PropertyInfo) return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.PropertyIcon));
					if(member is FieldInfo) return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.FieldIcon));
					return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.ExtensionIcon));
				case FavoriteKind.Node:
					if(!string.IsNullOrEmpty(e.nodeMenuName) && nodeMenuCache != null
						&& nodeMenuCache.TryGetValue(e.nodeMenuName, out var menu) && menu != null)
						return uNodeEditorUtility.GetTypeIcon(menu.GetIcon());
					return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.FlowIcon));
				default:
					return iconType != null ? uNodeEditorUtility.GetTypeIcon(iconType) : uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.ExtensionIcon));
			}
		}

		// ═══════════════════════════════════════
		//  BuildNodeMenuCache
		// ═══════════════════════════════════════

		void BuildNodeMenuCache() {
			nodeMenuCache = new Dictionary<string, NodeMenu>();
			foreach(var menu in NodeEditorUtility.FindNodeMenu()) {
				if(menu.type != null)
					nodeMenuCache[menu.name] = menu;
			}
		}

		// ═══════════════════════════════════════
		//  UI Construction
		// ═══════════════════════════════════════

		void BuildUI() {
			rootVisualElement.Clear();
			var root = new VisualElement { style = { flexGrow = 1, flexDirection = FlexDirection.Column } };

			// ── Toolbar ──
			toolbar = new Toolbar();

			categoryDropdown = new DropdownField("Category", new List<string>(), 0) { style = { width = 160 } };
			categoryDropdown.RegisterValueChangedCallback(OnCategoryChanged);
			toolbar.Add(categoryDropdown);

			// Category add/remove
			var addCategoryBtn = new ToolbarButton(() => CreateNewCategory()) { text = "+", tooltip = "New Category" };
			addCategoryBtn.style.width = 24;
			addCategoryBtn.style.marginLeft = 2;
			toolbar.Add(addCategoryBtn);

			var removeCategoryBtn = new ToolbarButton(() => RemoveSelectedCategory()) { text = "-", tooltip = "Remove Category" };
			removeCategoryBtn.style.width = 24;
			toolbar.Add(removeCategoryBtn);

			toolbar.Add(new ToolbarSpacer());

			// Combined add button with dropdown menu
			var addMenu = new ToolbarButton(ShowAddMenu) { text = "+ Add", tooltip = "Add Item" };
			toolbar.Add(addMenu);

			removeButton = new ToolbarButton(() => RemoveSelected()) { text = "Remove", tooltip = "Remove selected" };
			toolbar.Add(removeButton);

			addMembersButton = new ToolbarButton(() => OpenAddMembersPopup()) { text = "+ Members", tooltip = "Add sub-members" };
			addMembersButton.SetEnabled(false);
			toolbar.Add(addMembersButton);

			toolbar.Add(new ToolbarSpacer { flex = true });

			toolbar.Add(new ToolbarButton(() => ShowAutoSortMenu()) { text = "Sort" });

			root.Add(toolbar);

			// ── Search ──
			searchField = new TextField() { name = "search", tooltip = "Search" };
			searchField.RegisterValueChangedCallback(evt => { searchString = evt.newValue; ReloadTreeView(); });
			searchField.style.marginLeft = 4;
			searchField.style.marginRight = 4;
			searchField.style.marginTop = 2;
			searchField.style.marginBottom = 2;
			root.Add(searchField);

			// ── TreeView ──
			// Modeled after GraphPanel.DrawElements: makeItem returns a PanelElement-like
			// "content" row; the TreeView wraps it with itemUssClassName + itemContentContainerUssClassName.
			entryTreeView = new TreeView(
				// The row manipulator is registered once per recycled element here (not in
				// bindItem) to avoid stacking duplicate manipulators on rebind.
				makeItem: () => {
					var item = new PanelElement<FavoritesDataAsset.Entry>();
					item.style.alignItems = Align.Center;
					// Stretch the row content to fully fill the recycled TreeView item
					// (fixedItemHeight) so there is no dead space above/below the
					// content — keeps hover highlight and drag hit-testing full height.
					item.style.flexGrow = 1;
					item.style.height = Length.Percent(100);
					// Two-line layout: title on top, breadcrumb path below.
					// The path label stays hidden outside search mode.
					var textColumn = new VisualElement { name = "text-column" };
					textColumn.pickingMode = PickingMode.Ignore;
					textColumn.style.flexDirection = FlexDirection.Column;
					textColumn.style.flexGrow = 1;
					textColumn.style.justifyContent = Justify.Center;
					textColumn.Add(item.label);
					var pathLabel = new Label(string.Empty) { name = "path-label" };
					pathLabel.pickingMode = PickingMode.Ignore;
					pathLabel.style.fontSize = 9;
					pathLabel.style.color = new Color(0.55f, 0.55f, 0.55f);
					pathLabel.style.display = DisplayStyle.None;
					textColumn.Add(pathLabel);
					item.Add(textColumn);
					item.AddManipulator(new ContextualMenuManipulator(evt => BuildRowContextMenu(evt, item.userData as DisplayEntry)));
					return item;
				},
				bindItem: (ve, index) => {
					if(!(ve is PanelElement<FavoritesDataAsset.Entry> item))
						return;
					item.index = index;
					var de = entryTreeView.GetItemDataForIndex<DisplayEntry>(index);
					if(de == null) return;
					item.value = de.entry;
					item.userData = de;

					// Drag behavior: virtual rows are read-only (non-draggable, no drop inside).
					// Reordering is also disabled while searching (flat relevance view).
					bool isVirtual = de.isVirtualChild || de.entry.isVirtual;
					bool hasSearch = !string.IsNullOrEmpty(searchString);
					item.CanDragFunc = () => !isVirtual && !hasSearch;
					item.CanDragInsideParentFunc = () => !isVirtual && !hasSearch;
					item.CanHaveChildsFunc = () => de.entry.kind == FavoriteKind.Folder && !de.entry.isVirtual && !hasSearch;

					// Set drag payload (null for virtual rows = read-only).
					item.GetDragGenericData = () => {
						if(isVirtual)
							return null;
						return new Dictionary<string, object> {
							{ "favoriteID", de.entry.id },
							{ "favoriteCategory", de.entry.categoryID },
						};
					};

					// Manual selection handling (GraphPanel pattern).
					var captured = de; // capture for closure
					item.onClick = (evt) => {
						selectedEntry = captured;
						UpdateDetailPanel();
						UpdateAddMembersButton();
						entryTreeView.RefreshItems();
						if(evt is MouseUpEvent mouseEvt && mouseEvt.clickCount >= 2) {
							TryCreateNode(captured);
						}
					};

					// Selection highlight
					bool isSelected = selectedEntry != null && selectedEntry.entry != null
						&& captured.entry != null
						&& captured.entry.id == selectedEntry.entry.id
						&& captured.isVirtualChild == selectedEntry.isVirtualChild;
					item.style.backgroundColor = isSelected ? new Color(0.24f, 0.49f, 0.91f, 0.35f) : Color.clear;

					// Update visual content using ClickableElement's built-in label/icon
					// (matching GraphPanel's SetupPanelElement pattern).
					item.label.text = GetDisplayName(de.entry);
					item.ShowIcon(GetIcon(de.entry));
					// Fixed icon size keeps rows aligned even when a texture is missing.
					if(item.icon != null) {
						item.icon.style.width = 16;
						item.icon.style.height = 16;
						item.icon.style.flexShrink = 0;
					}

					// Search mode shows the breadcrumb path under the title.
					var pathLabel = item.Q<Label>("path-label");
					if(pathLabel != null) {
						bool showPath = hasSearch && !string.IsNullOrEmpty(de.searchPath);
						pathLabel.text = de.searchPath ?? string.Empty;
						pathLabel.style.display = showPath ? DisplayStyle.Flex : DisplayStyle.None;
					}
				}
			);
			// Handle selection manually (like GraphPanel) so clicks don't conflict
			// with the TreeView's built-in selection.
			entryTreeView.selectionType = SelectionType.None;
			entryTreeView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
			entryTreeView.style.flexGrow = 1;
			// Fixed row height ensures every row is the same size, eliminating
			// hit-test gaps between rows where drag cannot start.
			entryTreeView.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
			entryTreeView.fixedItemHeight = 20;

			// Drag-and-drop: standard TreeViewDragger + custom controller
			// (same pattern as GraphPanel's TreeViewUGraphElementDragAndDropController).
			var dragger = new TreeViewDragger(entryTreeView);
			// The controller forwards the dragged entry id plus the drop position;
			// we resolve parent + sibling from it, validate, persist, and rebuild.
			dragger.dragAndDropController = new FavoritesReorderController(entryTreeView, (movedID, insertIndex, overItem) => {
				if(string.IsNullOrEmpty(movedID)) return;
				// No reordering while searching (flat relevance view).
				if(!string.IsNullOrEmpty(searchString)) return;
				string parentID;
				int siblingIndex;
				if(overItem) {
					// Dropping ONTO a row nests inside it — only folders accept children.
					var target = entryTreeView.GetItemDataForIndex<DisplayEntry>(insertIndex);
					if(target == null || target.isVirtualChild || target.entry.isVirtual) return;
					if(target.entry.kind != FavoriteKind.Folder) return;
					parentID = target.entry.id;
					siblingIndex = -1; // append as last child
				}
				else {
					// Dropping BETWEEN rows resolves the slot like GraphPanel does.
					if(!ResolveSlot(insertIndex, out var resolvedParent, out var resolvedSibling, out _)) return;
					parentID = resolvedParent ?? "";
					siblingIndex = resolvedSibling;
				}
				if(!CanMove(movedID, parentID)) return;
				int sibling = siblingIndex < 0 ? int.MaxValue : siblingIndex;
				FavoritesManager.MoveEntry(movedID, parentID, sibling);
				ReloadTreeView();
			});

			// Blank-area context menu (row menus stop propagation so this doesn't double up).
			entryTreeView.AddManipulator(new ContextualMenuManipulator(evt => {
				evt.menu.AppendAction("New Folder", _ => CreateNewFolder());
				evt.menu.AppendAction("Add Namespace", _ => AddNamespaceFavorite());
				evt.menu.AppendAction("Add Type / Member", _ => OpenItemSelector());
			}));
			root.Add(entryTreeView);

			// ── Status / empty state ──
			statusLabel = new Label("") {
				style = {
					flexShrink = 0, unityTextAlign = TextAnchor.MiddleCenter,
					color = new Color(.5f, .5f, .5f), paddingTop = 12,
					display = DisplayStyle.None
				}
			};
			root.Add(statusLabel);

			// ── Detail ──
			detailArea = new VisualElement {
				style = {
					flexShrink = 0, paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8,
					borderTopWidth = 1, borderTopColor = new Color(.3f, .3f, .3f)
				}
			};
			detailNameLabel = new Label("No selection") { style = { unityFontStyleAndWeight = FontStyle.Bold } };
			detailTypeLabel = new Label("") { style = { marginTop = 2, color = new Color(.6f, .6f, .6f) } };
			memberScroll = new ScrollView(ScrollViewMode.Vertical) { style = { marginTop = 4, maxHeight = 150 } };
			detailArea.Add(detailNameLabel);
			detailArea.Add(detailTypeLabel);
			detailArea.Add(memberScroll);
			root.Add(detailArea);

			rootVisualElement.Add(root);
			rootVisualElement.RegisterCallback<KeyDownEvent>(OnWindowKeyDown);
			UpdateCategoryDropdown();
			ReloadTreeView();
		}

		string GetEntryIDForTreeViewID(int treeID) {
			if(treeID <= 0) return string.Empty;
			return treeIDMap.TryGetValue(treeID, out var de) ? de.entry.id : string.Empty;
		}

		// ═══════════════════════════════════════
		//  TreeView Rebuild
		// ═══════════════════════════════════════

		void ReloadTreeView() {
			var items = BuildTreeData();
			// Search rows are double height to fit the title + path description
			// (same as ItemSelector's relevance results).
			entryTreeView.fixedItemHeight = string.IsNullOrEmpty(searchString) ? 20 : 40;
			entryTreeView.SetRootItems(items);
			entryTreeView.Rebuild();
			UpdateStatusLabel();
		}

		void UpdateStatusLabel() {
			if(statusLabel == null) return;
			int totalCount = 0;
			if(!string.IsNullOrEmpty(currentCategoryID))
				totalCount = FavoritesManager.asset.entries.Count(e => e.categoryID == currentCategoryID);
			if(visibleRows.Count > 0) {
				statusLabel.style.display = DisplayStyle.None;
			} else if(totalCount > 0) {
				statusLabel.text = "No matching results";
				statusLabel.style.display = DisplayStyle.Flex;
			} else {
				statusLabel.text = "No favorites yet — use '+ Add'";
				statusLabel.style.display = DisplayStyle.Flex;
			}
		}

		/// <summary>
		/// Right-click menu for a tree row (the manipulator is attached once per row element;
		/// the current entry is passed via userData). Stops propagation so the blank-area
		/// menu on the TreeView doesn't also populate.
		/// </summary>
		void BuildRowContextMenu(ContextualMenuPopulateEvent evt, DisplayEntry de) {
			if(de == null || de.entry == null)
				return;
			var e = de.entry;

			if(de.isVirtualChild || e.isVirtual) {
				// Virtual rows are read-only; only node creation is offered.
				if(e.kind == FavoriteKind.Type) {
					evt.menu.AppendAction("Create Node", _ => TryCreateNode(de));
				}
				evt.StopPropagation();
				return;
			}

			switch(e.kind) {
				case FavoriteKind.Folder:
					evt.menu.AppendAction("New Folder", _ => { selectedEntry = de; UpdateDetailPanel(); CreateNewFolder(); });
					evt.menu.AppendAction("Rename", _ => { selectedEntry = de; RenameSelectedFolder(); });
					break;
				case FavoriteKind.Type:
					evt.menu.AppendAction("Create Node", _ => TryCreateNode(de));
					evt.menu.AppendAction("Add Members...", _ => { selectedEntry = de; UpdateDetailPanel(); UpdateAddMembersButton(); OpenAddMembersPopup(); });
					break;
				case FavoriteKind.Node:
				case FavoriteKind.Member:
					evt.menu.AppendAction("Create Node", _ => TryCreateNode(de));
					break;
			}
			evt.menu.AppendSeparator();
			evt.menu.AppendAction("Remove", _ => { selectedEntry = de; RemoveSelected(); });
			evt.StopPropagation();
		}

		// ═══════════════════════════════════════
		//  Selection / Detail
		// ═══════════════════════════════════════

		void UpdateAddMembersButton() {
			if(addMembersButton != null)
				addMembersButton.SetEnabled(selectedEntry != null && selectedEntry.entry.kind == FavoriteKind.Type && !selectedEntry.isVirtualChild);
		}

		void UpdateDetailPanel() {
			if(selectedEntry == null) {
				detailNameLabel.text = "No selection";
				detailTypeLabel.text = "";
				memberScroll.Clear();
				return;
			}
			var e = selectedEntry.entry;
			detailNameLabel.text = GetDisplayName(e);
			detailTypeLabel.text = e.kind + (e.kind == FavoriteKind.Namespace ? "  —  " + e.displayName : e.kind == FavoriteKind.Type || e.kind == FavoriteKind.Node ? "  —  " + e.typeName : "");

			memberScroll.Clear();
			// Show member toggles for type entries.
			if(e.kind == FavoriteKind.Type && !e.isVirtual) {
				var type = e.resolvedType;
				if(type != null) {
					var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
					Array.Sort(members, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
					bool has = false;
					foreach(var m in members) {
						if(m is EventInfo) continue;
						if(m is ConstructorInfo ctor && ctor.GetParameters().Length > 6) continue;
						has = true;
						bool isExcluded = e.excludedMembers.Contains(m.Name);
						var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 1, marginBottom = 1 } };
						var toggle = new Toggle(m.Name) { value = !isExcluded, style = { flexGrow = 1 } };
						bool captured = isExcluded;
						toggle.RegisterValueChangedCallback(evt => {
							if(evt.newValue) e.excludedMembers.Remove(m.Name);
							else if(!e.excludedMembers.Contains(m.Name)) e.excludedMembers.Add(m.Name);
							FavoritesManager.Save();
						});
						row.Add(toggle);
						memberScroll.Add(row);
					}
					if(!has)
						memberScroll.Add(new Label("No public members") { style = { color = new Color(.5f, .5f, .5f) } });
				}
			}
		}

		// ═══════════════════════════════════════
		//  Actions
		// ═══════════════════════════════════════

		void CreateNewFolder() {
			string folderName = "";
			string parentID = selectedEntry != null && (selectedEntry.entry.kind == FavoriteKind.Folder || selectedEntry.entry.kind == FavoriteKind.Namespace)
				? selectedEntry.entry.id : null;
			ActionPopupWindow.Show(null, (ref object obj) => {
				EditorGUILayout.LabelField("New Folder", EditorStyles.boldLabel);
				folderName = EditorGUILayout.TextField("Name", folderName);
				if(GUILayout.Button("Create") && !string.IsNullOrWhiteSpace(folderName)) {
					FavoritesManager.AddFolder(currentCategoryID, folderName.Trim(), parentID);
					ReloadTreeView();
					ActionPopupWindow.CloseLast();
				}
			});
		}

		void AddNamespaceFavorite() {
			string ns = "";
			string parentID = selectedEntry != null && selectedEntry.entry.kind == FavoriteKind.Folder
				? selectedEntry.entry.id : null;
			ActionPopupWindow.Show(null, (ref object obj) => {
				EditorGUILayout.LabelField("Add Namespace", EditorStyles.boldLabel);
				ns = EditorGUILayout.TextField("Namespace", ns);
				if(GUILayout.Button("Add") && !string.IsNullOrWhiteSpace(ns)) {
					FavoritesManager.AddNamespace(currentCategoryID, ns.Trim(), parentID);
					ReloadTreeView();
					ActionPopupWindow.CloseLast();
				}
			});
		}

		void OpenItemSelector() {
			var graphEditor = uNodeEditor.window?.graphEditor;
			var filter = new FilterAttribute {
				Public = true, Instance = true, Static = true,
				MaxMethodParam = int.MaxValue, CanSelectType = true
			};
			ItemSelector.ShowWindow(
				graphEditor != null ? graphEditor.graphData.graph : null,
				filter,
				(MemberData value) => AddMemberDataAsFavorite(value)
			);
		}

		void AddMemberDataAsFavorite(MemberData memberData) {
			if(memberData == null) return;

			bool isType = memberData.IsTargetingType
				|| memberData.targetType == MemberData.TargetType.Type
				|| memberData.targetType == MemberData.TargetType.uNodeType
				|| memberData.targetType == MemberData.TargetType.Values;

			string parentID = selectedEntry != null && (selectedEntry.entry.kind == FavoriteKind.Folder || selectedEntry.entry.kind == FavoriteKind.Namespace)
				? selectedEntry.entry.id : null;

			if(isType) {
				var type = memberData.startType ?? memberData.type ?? memberData.StartSerializedType.type;
				if(type == null || string.IsNullOrEmpty(type.FullName)) return;
				if(FavoritesManager.asset.entries.Any(x => x.categoryID == currentCategoryID && x.kind != FavoriteKind.Member && x.typeName == type.FullName && (parentID == null || x.parentID == parentID))) return;
				FavoritesManager.AddEntry(currentCategoryID, new FavoritesDataAsset.Entry { kind = FavoriteKind.Type, targetType = new SerializedType(type), parentID = parentID });
			} else {
				var members = memberData.GetMembers(false);
				if(members == null || members.Length == 0) return;
				var last = members[members.Length - 1];
				var declType = last.DeclaringType ?? memberData.startType;
				if(declType == null) return;
				// Ensure type header exists.
				if(!FavoritesManager.asset.entries.Any(x => x.categoryID == currentCategoryID && x.kind == FavoriteKind.Type && x.typeName == declType.FullName && (parentID == null || x.parentID == parentID))) {
					FavoritesManager.AddEntry(currentCategoryID, new FavoritesDataAsset.Entry { kind = FavoriteKind.Type, targetType = new SerializedType(declType), parentID = parentID });
				}
				string typeID = FavoritesManager.asset.entries.FirstOrDefault(x => x.categoryID == currentCategoryID && x.kind == FavoriteKind.Type && x.typeName == declType.FullName && (parentID == null || x.parentID == parentID))?.id;
				if(FavoritesManager.asset.entries.Any(x => x.kind == FavoriteKind.Member && x.typeName == declType.FullName && x.memberName == last.Name)) return;
				FavoritesManager.AddEntry(currentCategoryID, new FavoritesDataAsset.Entry {
					kind = FavoriteKind.Member,
					targetMember = MemberData.CreateFromMember(last),
					parentID = typeID ?? parentID
				});
			}
			ReloadTreeView();
		}

		void RemoveSelected() {
			if(selectedEntry == null) return;
			FavoritesManager.RemoveRecursive(selectedEntry.entry.id);
			selectedEntry = null;
			ReloadTreeView();
			UpdateDetailPanel();
			UpdateAddMembersButton();
		}

		void OpenAddMembersPopup() {
			if(selectedEntry == null || selectedEntry.entry.kind != FavoriteKind.Type) return;
			var e = selectedEntry.entry;
			if(e.isVirtual) return;
			var type = e.resolvedType;
			if(type == null) return;

			var validMembers = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
				.Where(m => m is not EventInfo && !(m is ConstructorInfo ctor && ctor.GetParameters().Length > 6))
				.OrderBy(m => m.Name).ToList();

			if(validMembers.Count == 0) {
				EditorUtility.DisplayDialog("No Members", "This type has no public members.", "OK");
				return;
			}

			ActionPopupWindow.Show(null, (ref object obj) => {
				EditorGUILayout.LabelField("Members of " + GetDisplayName(e), EditorStyles.boldLabel);
				EditorGUILayout.Space(4);
				EditorGUILayout.BeginHorizontal();
				if(GUILayout.Button("Select All")) {
					foreach(var m in validMembers) SetMemberFavorite(e, m, true);
				}
				if(GUILayout.Button("Deselect All")) {
					foreach(var m in validMembers) SetMemberFavorite(e, m, false);
				}
				EditorGUILayout.EndHorizontal();
				EditorGUILayout.Space(4);
				foreach(var m in validMembers) {
					bool current = FavoritesManager.asset.entries.Any(x => x.kind == FavoriteKind.Member && x.typeName == e.typeName && x.memberName == m.Name);
					bool updated = EditorGUILayout.ToggleLeft(m.Name + "  :  " + m.MemberType, current);
					if(updated != current) SetMemberFavorite(e, m, updated);
				}
			}, null, (ref object obj) => { if(GUILayout.Button("Close")) ActionPopupWindow.CloseLast(); });
		}

		void SetMemberFavorite(FavoritesDataAsset.Entry typeEntry, MemberInfo member, bool value) {
			if(value) {
				if(!FavoritesManager.asset.entries.Any(x => x.kind == FavoriteKind.Member && x.typeName == typeEntry.typeName && x.memberName == member.Name)) {
					FavoritesManager.AddEntry(currentCategoryID, new FavoritesDataAsset.Entry {
						kind = FavoriteKind.Member,
						targetMember = MemberData.CreateFromMember(member),
						parentID = typeEntry.parentID
					});
				}
			} else {
				var toRemove = FavoritesManager.asset.entries.FirstOrDefault(x => x.kind == FavoriteKind.Member && x.typeName == typeEntry.typeName && x.memberName == member.Name);
				if(toRemove != null) FavoritesManager.RemoveEntry(toRemove.id);
			}
		}

		void ShowAutoSortMenu() {
			var menu = new GenericDropdownMenu();
			menu.AddItem("Name (A-Z)", false, () => AutoSort((a, b) => string.Compare(GetDisplayName(a), GetDisplayName(b), StringComparison.OrdinalIgnoreCase)));
			menu.AddItem("Name (Z-A)", false, () => AutoSort((a, b) => string.Compare(GetDisplayName(b), GetDisplayName(a), StringComparison.OrdinalIgnoreCase)));
			menu.AddItem("Kind", false, () => AutoSort((a, b) => {
				int c = ((int)a.kind).CompareTo((int)b.kind);
				return c != 0 ? c : string.Compare(GetDisplayName(a), GetDisplayName(b), StringComparison.OrdinalIgnoreCase);
			}));
			menu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero), toolbar);
		}

		void AutoSort(Comparison<FavoritesDataAsset.Entry> comparer) {
			string parentID = selectedEntry != null && selectedEntry.entry.CanHaveChilds ? selectedEntry.entry.id : null;
			FavoritesManager.ReorderSiblings(currentCategoryID, parentID, (a, b) => {
				int tc = a.kind.CompareTo(b.kind);
				return tc != 0 ? tc : comparer(a, b);
			});
			ReloadTreeView();
		}

		/// <summary>Double-click/context-menu entry point: validates then spawns the node.</summary>
		void TryCreateNode(DisplayEntry de) {
			if(de == null || de.entry == null) return;
			var kind = de.entry.kind;
			if(kind != FavoriteKind.Node && kind != FavoriteKind.Type && kind != FavoriteKind.Member)
				return;
			if(de.isVirtualChild && kind != FavoriteKind.Type)
				return;
			var graphEditor = uNodeEditor.window?.graphEditor;
			if(graphEditor == null || graphEditor.graphData == null || !graphEditor.graphData.CanAddNode) {
				EditorUtility.DisplayDialog("Create Node", "Open a graph editor first to create nodes.", "OK");
				return;
			}
			CreateNode(de);
		}

		Type ResolveEntryType(FavoritesDataAsset.Entry e) {
			var t = e.resolvedType;
			if(t == null && e.targetType != null && e.targetType.isAssigned)
				t = e.targetType.type;
			return t;
		}

		void CreateNode(DisplayEntry de) {
			var graphEditor = uNodeEditor.window?.graphEditor;
			if(graphEditor == null || !graphEditor.graphData.CanAddNode) return;
			var e = de.entry;
			var pos = graphEditor.mousePositionInScreen;
			if(e.kind == FavoriteKind.Node) {
				NodeMenu menu = null;
				if(!string.IsNullOrEmpty(e.nodeMenuName) && nodeMenuCache != null)
					nodeMenuCache.TryGetValue(e.nodeMenuName, out menu);
				if(menu == null && nodeMenuCache != null)
					menu = nodeMenuCache.Values.FirstOrDefault(m => m.type == ResolveEntryType(e));
				if(menu != null) { NodeEditorUtility.AddNewNode<Node>(graphEditor.graphData, menu.nodeName, menu.type, pos); graphEditor.Refresh(); }
			} else if(e.kind == FavoriteKind.Type) {
				var type = ResolveEntryType(e);
				if(type != null) NodeEditorUtility.AddNewNode<MultipurposeNode>(graphEditor.graphData, pos, n => { n.target = MemberData.CreateFromType(type); graphEditor.Refresh(); });
			} else if(e.kind == FavoriteKind.Member) {
				GraphEditor.CreateNodeProcessor(e.targetMember, graphEditor.graphData, pos);
				graphEditor.Refresh();
			}
		}

		void RenameSelectedFolder() {
			if(selectedEntry == null || selectedEntry.entry.kind != FavoriteKind.Folder)
				return;
			var e = selectedEntry.entry;
			string newName = e.displayName ?? "";
			ActionPopupWindow.Show(null, (ref object obj) => {
				EditorGUILayout.LabelField("Rename Folder", EditorStyles.boldLabel);
				EditorGUILayout.Space(4);
				newName = EditorGUILayout.TextField("Name", newName);
				EditorGUILayout.Space(4);
				if(GUILayout.Button("Rename") && !string.IsNullOrWhiteSpace(newName)) {
					FavoritesManager.RenameFolder(e.id, newName);
					UpdateDetailPanel();
					ActionPopupWindow.CloseLast();
				}
			});
		}
	}
}
