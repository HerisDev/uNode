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
			currentCategoryID = FavoritesManager.GetDefaultCategory().id;
			BuildUI();
			ReloadTreeView();
		}

		private void OnDisable() {
			if(window == this)
				window = null;
			FavoritesManager.onChanged -= OnFavoritesChanged;
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
						UpdateCategoryDropdown();
						ReloadTreeView();
						ActionPopupWindow.CloseLast();
					}
				}
			);
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

			// Apply search filter — only prune non-ancestor nodes; keep ancestors of matches.
			HashSet<string> keptEntryIDs = null;
			if(!string.IsNullOrEmpty(searchString)) {
				keptEntryIDs = new HashSet<string>();
				string lower = searchString.ToLowerInvariant();
				foreach(var e in allEntries) {
					if(MatchesSearch(e, lower))
						MarkAncestors(e, allEntries, keptEntryIDs);
				}
			}

			// Group children by parentID.
			var childrenOf = allEntries
				.Where(e => keptEntryIDs == null || keptEntryIDs.Contains(e.id))
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

		bool MatchesSearch(FavoritesDataAsset.Entry e, string lower) {
			var name = GetDisplayName(e);
			if(name != null && name.ToLowerInvariant().Contains(lower))
				return true;
			if(e.typeName != null && e.typeName.ToLowerInvariant().Contains(lower))
				return true;
			if(e.kind == FavoriteKind.Namespace && e.displayName != null &&
				e.displayName.ToLowerInvariant().Contains(lower))
				return true;
			return false;
		}

		void MarkAncestors(FavoritesDataAsset.Entry e, List<FavoritesDataAsset.Entry> all, HashSet<string> kept) {
			kept.Add(e.id);
			if(string.IsNullOrEmpty(e.parentID)) return;
			if(kept.Contains(e.parentID)) return;
			var parent = all.FirstOrDefault(x => x.id == e.parentID);
			if(parent != null)
				MarkAncestors(parent, all, kept);
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

			// At the very top or past the end: always root, depth 0.
			if(insertIndex <= 0 || insertIndex >= visibleRows.Count) {
				parentID = "";
				siblingIndex = insertIndex;
				indentDepth = insertIndex <= 0 ? 0 : visibleRows[visibleRows.Count - 1].depth;
				return true;
			}

			// Anchor on the row above the insertion slot.
			var anchor = visibleRows[Mathf.Clamp(insertIndex - 1, 0, visibleRows.Count - 1)];

			// Inside a fixed namespace expansion: reject.
			if(anchor.inNamespace && anchor.entry.kind == FavoriteKind.Type)
				return false;

			int nextDepth = visibleRows[insertIndex].depth;

			// Anchor is a folder and the row below is deeper → drop INTO folder.
			if(anchor.entry.kind == FavoriteKind.Folder && anchor.depth < nextDepth) {
				parentID = anchor.entry.id;
				siblingIndex = CountDescendantsVisibleBefore(insertIndex, anchor.entry.id);
				indentDepth = anchor.depth + 1;
				return true;
			}

			// Anchor is a folder and the row below is same or shallower → next to the folder.
			parentID = anchor.parentID ?? "";
			indentDepth = anchor.depth;
			siblingIndex = CountSiblingsBefore(insertIndex, parentID);
			if(nextDepth > anchor.depth) {
				// Actually inserting between two items that share the folder as parent.
				siblingIndex = CountDescendantsVisibleBefore(insertIndex, parentID);
			}
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

		/// <summary>Count descendants of parentID that appear before insertIndex.</summary>
		int CountDescendantsVisibleBefore(int insertIndex, string parentID) {
			int count = 0;
			for(int i = 0; i < insertIndex; i++) {
				if(visibleRows[i].parentID == parentID)
					count++;
			}
			return count;
		}

		/// <summary>Get the tree depth of the row that would appear at insertIndex.</summary>
		int GetDepthOfIndex(int insertIndex) {
			if(visibleRows.Count == 0) return 0;
			if(insertIndex <= 0) return 0;
			if(insertIndex >= visibleRows.Count)
				return visibleRows[visibleRows.Count - 1].depth;
			return visibleRows[insertIndex].depth;
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
			Type typeForDisplay = e.resolvedType;
			if(typeForDisplay == null && e.targetType != null && e.targetType.isAssigned)
				typeForDisplay = e.targetType.type;

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
			Type iconType = e.resolvedType;
			if(iconType == null && e.targetType != null && e.targetType.isAssigned)
				iconType = e.targetType.type;

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

			categoryDropdown = new DropdownField("Category", new List<string>(), 0) { style = { width = 180 } };
			categoryDropdown.RegisterValueChangedCallback(OnCategoryChanged);
			toolbar.Add(categoryDropdown);

			// Combined add button with dropdown menu
			var addMenu = new ToolbarButton(ShowAddMenu) { text = "+", tooltip = "Add Item" };
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
				makeItem: () => new FavoritesTreeItem(),
				bindItem: (ve, index) => {
					if(!(ve is FavoritesTreeItem item))
						return;
					item.index = index;
					var de = entryTreeView.GetItemDataForIndex<DisplayEntry>(index);
					if(de == null) return;
					item.entry = de.entry;
					item.isVirtualChild = de.isVirtualChild;

					// Set drag payload (null for virtual rows = read-only).
					item.GetDragGenericData = () => {
						if(de.isVirtualChild || de.entry.isVirtual)
							return null;
						return new Dictionary<string, object> {
							{ "favoriteID", de.entry.id },
							{ "favoriteCategory", de.entry.categoryID },
						};
					};

					// Manual selection handling (GraphPanel pattern).
					var captured = de; // capture for closure
					item.onClick = (_) => {
						selectedEntry = captured;
						UpdateDetailPanel();
						UpdateAddMembersButton();
						entryTreeView.RefreshItems();
					};

					// Selection highlight
					bool isSelected = selectedEntry != null && selectedEntry.entry != null
						&& captured.entry != null
						&& captured.entry.id == selectedEntry.entry.id
						&& captured.isVirtualChild == selectedEntry.isVirtualChild;
					item.style.backgroundColor = isSelected ? new Color(0.24f, 0.49f, 0.91f, 0.35f) : Color.clear;

					// Update visual content.
					var icon = item.Q<Image>("icon");
					if(icon != null) icon.image = GetIcon(de.entry);
					var label = item.Q<Label>("label");
					if(label != null) {
						label.text = de.isVirtualChild || de.entry.kind == FavoriteKind.Member
							? "    " + GetDisplayName(de.entry)
							: GetDisplayName(de.entry);
					}
					var kindLabel = item.Q<Label>("kind");
					if(kindLabel != null) kindLabel.text = de.entry.kind.ToString();
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

			// Drag-and-drop: standard TreeViewDragger + custom controller that
			// resolves drops against our data and rebuilds the tree.
			var dragger = new TreeViewDragger(entryTreeView);
			dragger.dragAndDropController = new FavoritesDragController(entryTreeView, (movedID, insertIndex) => {
				if(string.IsNullOrEmpty(movedID)) return;
				if(!ResolveSlot(insertIndex, out var parentID, out var siblingIndex, out _)) return;
				parentID = parentID ?? "";
				if(!CanMove(movedID, parentID)) return;
				int sibling = siblingIndex < 0 ? int.MaxValue : siblingIndex;
				FavoritesManager.MoveEntry(movedID, parentID, sibling);
				ReloadTreeView();
			});
			root.Add(entryTreeView);

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
			entryTreeView.SetRootItems(items);
			entryTreeView.Rebuild();
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
					menu = nodeMenuCache.Values.FirstOrDefault(m => m.type == e.resolvedType);
				if(menu != null) { NodeEditorUtility.AddNewNode<Node>(graphEditor.graphData, menu.nodeName, menu.type, pos); graphEditor.Refresh(); }
			} else if(e.kind == FavoriteKind.Type) {
				var type = e.resolvedType;
				if(type != null) NodeEditorUtility.AddNewNode<MultipurposeNode>(graphEditor.graphData, pos, n => { n.target = MemberData.CreateFromType(type); graphEditor.Refresh(); });
			} else if(e.kind == FavoriteKind.Member) {
				GraphEditor.CreateNodeProcessor(e.targetMember, graphEditor.graphData, pos);
				graphEditor.Refresh();
			}
		}
	}
}
