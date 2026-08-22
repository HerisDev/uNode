using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace MaxyGames.UNode.Editors {
	public class FavoritesNodeWindow : EditorWindow {
		public static FavoritesNodeWindow window;

		// ── Data ──
		class DisplayEntry {
			public string id;
			public string displayName;
			public string typeFullName;
			public uNodeEditor.uNodeEditorData.FavoriteItemKind kind;
			public Texture icon;
			public string tooltip;
			public int memberCount;
		}

		// ── UI ──
		private Toolbar toolbar;
		private DropdownField categoryDropdown;
		private TextField searchField;
		private ListView entryListView;
		private VisualElement detailArea;
		private Label detailNameLabel;
		private Label detailTypeLabel;
		private ScrollView memberScroll;
		private Button addButton;
		private Button removeButton;
		private Button addCategoryButton;
		private Button addMembersButton;

		// ── State ──
		private List<DisplayEntry> displayEntries = new List<DisplayEntry>();
		private DisplayEntry selectedEntry;
		private string currentCategoryID;
		private string searchString = "";
		private Dictionary<string, NodeMenu> nodeMenuCache;

		private const float ITEM_HEIGHT = 28f;

		[MenuItem("Tools/uNode/Favorites", false, 104)]
		public static void ShowWindow() {
			window = GetWindow<FavoritesNodeWindow>();
			window.titleContent = new GUIContent("Favorites", uNodeGUIStyle.favoriteIconOn);
			window.minSize = new Vector2(400, 300);
			window.Show();
		}

		private void OnEnable() {
			window = this;
			BuildNodeMenuCache();
			uNodeEditor.SavedData.OnFavoritesChanged += Refresh;
			EnsureDefaultCategory();
			BuildUI();
			Refresh();
		}

		private void OnDisable() {
			if(window == this) {
				window = null;
			}
			var savedData = uNodeEditor.SavedData;
			if(savedData != null) {
				savedData.OnFavoritesChanged -= Refresh;
			}
		}

		// ═══════════════════════════════════════
		//  Category
		// ═══════════════════════════════════════

		private void EnsureDefaultCategory() {
			var savedData = uNodeEditor.SavedData;
			if(savedData.favoritesData.categories.Count == 0) {
				savedData.GetOrCreateCategory("General");
			}
			currentCategoryID = savedData.GetDefaultCategory().id;
		}

		private void Refresh() {
			if(savedData == null) return;
			UpdateCategoryDropdown();
			LoadEntries();
		}

		private uNodeEditor.uNodeEditorData savedData => uNodeEditor.SavedData;

		private void UpdateCategoryDropdown() {
			if(categoryDropdown == null) return;
			var cats = savedData.favoritesData.categories.OrderBy(c => c.orderIndex).ToList();
			var names = cats.Select(c => c.name).ToList();
			categoryDropdown.choices = names;

			var currentCat = savedData.favoritesData.categories.FirstOrDefault(c => c.id == currentCategoryID);
			if(currentCat != null) {
				categoryDropdown.index = cats.IndexOf(currentCat);
			} else if(cats.Count > 0) {
				currentCategoryID = cats[0].id;
				categoryDropdown.index = 0;
			}
		}

		private void OnCategoryChanged(ChangeEvent<string> evt) {
			var savedData = uNodeEditor.SavedData;
			var cats = savedData.favoritesData.categories.OrderBy(c => c.orderIndex).ToList();
			var selectedIndex = categoryDropdown.index;

			if(selectedIndex >= 0 && selectedIndex < cats.Count) {
				currentCategoryID = cats[selectedIndex].id;
				LoadEntries();
			}
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
						var savedData = uNodeEditor.SavedData;
						var cat = savedData.GetOrCreateCategory(categoryName.Trim());
						currentCategoryID = cat.id;
						UpdateCategoryDropdown();
						LoadEntries();
						ActionPopupWindow.CloseLast();
					}
				}
			);
		}

		// ═══════════════════════════════════════
		//  Data Loading
		// ═══════════════════════════════════════

		private void LoadEntries() {
			displayEntries.Clear();
			var savedData = uNodeEditor.SavedData;
			if(savedData == null || string.IsNullOrEmpty(currentCategoryID)) return;

			var entries = savedData.GetEntriesForCategory(currentCategoryID);

			if(!string.IsNullOrEmpty(searchString)) {
				var lowerSearch = searchString.ToLowerInvariant();
				entries = entries.Where(e =>
					e.displayName.ToLowerInvariant().Contains(lowerSearch) ||
					e.typeFullName.ToLowerInvariant().Contains(lowerSearch)
				).ToList();
			}

			// Group entries by typeFullName
			var grouped = entries.GroupBy(e => e.typeFullName).OrderBy(g => g.Min(e => e.orderIndex));

			foreach(var group in grouped) {
				// Always find the type entry (kind=Type) for this group
				var typeEntry = group.FirstOrDefault(e => e.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Type);
				var memberEntries = group.Where(e => e.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Member).OrderBy(e => e.orderIndex);

				// Show type entry
				if(typeEntry != null) {
					displayEntries.Add(new DisplayEntry {
						id = typeEntry.id,
						displayName = typeEntry.displayName,
						typeFullName = typeEntry.typeFullName,
						kind = uNodeEditor.uNodeEditorData.FavoriteItemKind.Type,
						tooltip = typeEntry.typeFullName,
						memberCount = memberEntries.Count(),
					});
				} else if(memberEntries.Any()) {
					// Fallback: no type entry, create display from first member's type
					var firstMember = memberEntries.First();
					displayEntries.Add(new DisplayEntry {
						id = firstMember.id,
						displayName = TypeSerializer.Deserialize(firstMember.typeFullName, false)?.PrettyName() ?? firstMember.typeFullName.Split('.').Last(),
						typeFullName = firstMember.typeFullName,
						kind = uNodeEditor.uNodeEditorData.FavoriteItemKind.Type,
						tooltip = firstMember.typeFullName,
						memberCount = memberEntries.Count(),
					});
				}

				// Show member sub-entries indented
				foreach(var memberEntry in memberEntries) {
					displayEntries.Add(new DisplayEntry {
						id = memberEntry.id,
						displayName = memberEntry.displayName,
						typeFullName = memberEntry.typeFullName,
						kind = uNodeEditor.uNodeEditorData.FavoriteItemKind.Member,
						tooltip = memberEntry.displayName,
						memberCount = 0,
					});
				}
			}

			if(entryListView != null) {
				entryListView.Rebuild();
			}
			UpdateDetailPanel();
		}

		// ═══════════════════════════════════════
		//  Cache
		// ═══════════════════════════════════════

		private void BuildNodeMenuCache() {
			nodeMenuCache = new Dictionary<string, NodeMenu>();
			foreach(var menu in NodeEditorUtility.FindNodeMenu()) {
				if(menu.type != null) {
					nodeMenuCache[menu.type.FullName] = menu;
				}
			}
		}

		// ═══════════════════════════════════════
		//  UI Construction
		// ═══════════════════════════════════════

		private void BuildUI() {
			rootVisualElement.Clear();

			var root = new VisualElement();
			root.style.flexGrow = 1;
			root.style.flexDirection = FlexDirection.Column;

			// ── Toolbar ──
			toolbar = new Toolbar();
			toolbar.style.flexShrink = 0;

			// Category dropdown
			categoryDropdown = new DropdownField("Category", new List<string>(), 0);
			categoryDropdown.style.width = 180;
			categoryDropdown.RegisterValueChangedCallback(OnCategoryChanged);
			toolbar.Add(categoryDropdown);

			// Add category button
			addCategoryButton = new ToolbarButton(() => CreateNewCategory()) {
				text = "+",
				tooltip = "New Category"
			};
			addCategoryButton.style.width = 24;
			addCategoryButton.style.marginLeft = 2;
			toolbar.Add(addCategoryButton);

			toolbar.Add(new ToolbarSpacer());

			// Add button
			addButton = new ToolbarButton(() => OpenItemSelector()) {
				text = "+ Add",
				tooltip = "Add favorite from Item Selector"
			};
			toolbar.Add(addButton);

			// Remove button
			removeButton = new ToolbarButton(() => RemoveSelected()) {
				text = "Remove",
				tooltip = "Remove selected favorite"
			};
			toolbar.Add(removeButton);

			// Add Members button (sub-members of selected type)
			addMembersButton = new ToolbarButton(() => OpenAddMembersPopup()) {
				text = "+ Members",
				tooltip = "Add sub-members to selected type favorite"
			};
			addMembersButton.SetEnabled(false);
			toolbar.Add(addMembersButton);

			toolbar.Add(new ToolbarSpacer { flex = true });

			// Auto-sort button
			var autoSortButton = new ToolbarButton(() => ShowAutoSortMenu()) {
				text = "Sort",
				tooltip = "Auto reorder items"
			};
			toolbar.Add(autoSortButton);

			root.Add(toolbar);

			// ── Search ──
			searchField = new TextField() { name = "search-field", tooltip = "Search favorites" };
			searchField.RegisterValueChangedCallback(evt => {
				searchString = evt.newValue;
				LoadEntries();
			});
			searchField.style.flexShrink = 0;
			searchField.style.marginLeft = 4;
			searchField.style.marginRight = 4;
			searchField.style.marginTop = 2;
			searchField.style.marginBottom = 2;
			root.Add(searchField);

			// ── ListView ──
			entryListView = new ListView(
				displayEntries,
				ITEM_HEIGHT,
				MakeEntryItem,
				BindEntryItem
			);
			entryListView.selectionType = SelectionType.Single;
			entryListView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
			entryListView.style.flexGrow = 1;
			entryListView.reorderable = true;
			entryListView.itemIndexChanged += OnEntryReordered;
#if UNITY_2022_3_OR_NEWER
			entryListView.selectionChanged += OnEntrySelectionChanged;
			entryListView.itemsChosen += OnEntryDoubleClick;
#else
			entryListView.onSelectionChange += OnEntrySelectionChanged;
			entryListView.onItemsChosen += OnEntryDoubleClick;
#endif
			root.Add(entryListView);

			// ── Detail Area ──
			detailArea = new VisualElement();
			detailArea.style.flexShrink = 0;
			detailArea.style.paddingTop = 6;
			detailArea.style.paddingBottom = 6;
			detailArea.style.paddingLeft = 8;
			detailArea.style.paddingRight = 8;
			detailArea.style.borderTopWidth = 1;
			detailArea.style.borderTopColor = new Color(0.3f, 0.3f, 0.3f);

			detailNameLabel = new Label("No selection") {
				name = "detail-name"
			};
			detailNameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
			detailArea.Add(detailNameLabel);

			detailTypeLabel = new Label("") { name = "detail-type" };
			detailTypeLabel.style.marginTop = 2;
			detailTypeLabel.style.color = new Color(0.6f, 0.6f, 0.6f);
			detailArea.Add(detailTypeLabel);

			memberScroll = new ScrollView(ScrollViewMode.Vertical);
			memberScroll.style.marginTop = 4;
			memberScroll.style.maxHeight = 150;
			detailArea.Add(memberScroll);

			root.Add(detailArea);

			rootVisualElement.Add(root);

			UpdateCategoryDropdown();
			LoadEntries();
		}

		private VisualElement MakeEntryItem() {
			var ve = new VisualElement();
			ve.name = "entry-item";
			ve.style.flexDirection = FlexDirection.Row;
			ve.style.alignItems = Align.Center;
			ve.style.paddingLeft = 6;
			ve.style.paddingRight = 6;

			var icon = new Image { name = "entry-icon" };
			icon.style.width = 18;
			icon.style.height = 18;
			icon.style.marginRight = 8;
			ve.Add(icon);

			var name = new Label { name = "entry-name" };
			name.style.flexGrow = 1;
			ve.Add(name);

			var kind = new Label { name = "entry-kind" };
			kind.style.fontSize = 9;
			kind.style.color = new Color(0.5f, 0.5f, 0.5f);
			kind.style.marginRight = 4;
			ve.Add(kind);

			return ve;
		}

		private void BindEntryItem(VisualElement ve, int index) {
			if(index < 0 || index >= displayEntries.Count) return;

			var entry = displayEntries[index];
			var icon = ve.Q<Image>("entry-icon");
			var nameLabel = ve.Q<Label>("entry-name");
			var kindLabel = ve.Q<Label>("entry-kind");

			if(icon == null || nameLabel == null || kindLabel == null) return;

			icon.image = entry.icon;

			// Indent member entries under their type
			if(entry.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Member) {
				nameLabel.text = "    " + (entry.displayName ?? "");
			} else {
				nameLabel.text = entry.displayName ?? "";
			}

			if(entry.memberCount > 0 && entry.kind != uNodeEditor.uNodeEditorData.FavoriteItemKind.Member) {
				kindLabel.text = entry.kind.ToString() + " ("
					+ entry.memberCount + ")";
			} else {
				kindLabel.text = entry.kind.ToString();
			}
		}

		// ═══════════════════════════════════════
		//  Selection
		// ═══════════════════════════════════════

		private void OnEntrySelectionChanged(IEnumerable<object> objs) {
			var selected = objs.FirstOrDefault();
			if(selected is DisplayEntry de) {
				selectedEntry = de;
			} else {
				selectedEntry = null;
			}
			UpdateDetailPanel();
			UpdateAddMembersButton();
		}

		private void OnEntryDoubleClick(IEnumerable<object> objs) {
			var selected = objs.FirstOrDefault();
			if(selected is DisplayEntry de) {
				CreateNode(de);
			}
		}

		private void OnEntryReordered(int oldIndex, int newIndex) {
			if(oldIndex == newIndex) return;
			if(oldIndex < 0 || oldIndex >= displayEntries.Count) return;
			if(newIndex < 0 || newIndex >= displayEntries.Count) return;

			var moved = displayEntries[newIndex];
			if(moved == null || string.IsNullOrEmpty(moved.id)) return;

			// Members may only be reordered inside their own type group:
			// the entry directly above the dropped member must be its type header
			// or a sibling member sharing the same typeFullName.
			if(moved.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Member) {
				var above = newIndex > 0 ? displayEntries[newIndex - 1] : null;
				if(above == null || above.typeFullName != moved.typeFullName) {
					// Invalid drop outside its own type group, snap back without saving.
					LoadEntries();
					return;
				}
			}

			var savedData = uNodeEditor.SavedData;
			var entries = savedData.GetEntriesForCategory(currentCategoryID);

			int savedFrom = entries.FindIndex(e => e.id == moved.id);
			if(savedFrom < 0) return;

			// Determine the target position from the neighbors in visual order.
			// The moved item is at newIndex; anchor on the entry above/below it.
			string anchorAbove = newIndex > 0 ? displayEntries[newIndex - 1]?.id : null;

			int savedTo;
			if(anchorAbove != null && anchorAbove != moved.id) {
				int anchorIdx = entries.FindIndex(e => e.id == anchorAbove);
				savedTo = anchorIdx < savedFrom ? anchorIdx + 1 : anchorIdx;
			} else {
				// Dropped at top: place before the first other visible entry.
				savedTo = 0;
				foreach(var d in displayEntries) {
					if(d?.id != null && d.id != moved.id) {
						int idx = entries.FindIndex(e => e.id == d.id);
						if(idx >= 0) {
							savedTo = idx > savedFrom ? idx - 1 : idx;
						}
						break;
					}
				}
			}

			if(savedTo < 0 || savedTo >= entries.Count) return;
			savedData.ReorderEntries(currentCategoryID, savedFrom, savedTo);
		}

		private void UpdateAddMembersButton() {
			if(addMembersButton == null) return;
			addMembersButton.SetEnabled(selectedEntry != null
				&& selectedEntry.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Type);
		}

		private void OpenAddMembersPopup() {
			if(selectedEntry == null) return;
			if(selectedEntry.kind != uNodeEditor.uNodeEditorData.FavoriteItemKind.Type) return;

			Type type = null;
			type = TypeSerializer.Deserialize(selectedEntry.typeFullName, false);
			if(type == null) {
				EditorUtility.DisplayDialog("Invalid Type", "Cannot resolve type: " + selectedEntry.typeFullName, "OK");
				return;
			}

			var savedData = uNodeEditor.SavedData;
			var typeEntry = savedData.favoritesData.entries.FirstOrDefault(e => e.id == selectedEntry.id);
			if(typeEntry == null) return;

			var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
			Array.Sort(members, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

			var validMembers = new List<MemberInfo>();
			foreach(var m in members) {
				if(m is EventInfo) continue;
				if(m is ConstructorInfo ctor && ctor.GetParameters().Length > 6) continue;
				validMembers.Add(m);
			}

			if(validMembers.Count == 0) {
				EditorUtility.DisplayDialog("No Members", "This type has no public members to add.", "OK");
				return;
			}

			string typeFullName = selectedEntry.typeFullName;

			ActionPopupWindow.Show(
				null,
				(ref object obj) => {
					EditorGUILayout.LabelField("Members of " + selectedEntry.displayName, EditorStyles.boldLabel);
					EditorGUILayout.Space(4);

					// Select All / Deselect All - applied instantly
					EditorGUILayout.BeginHorizontal();
					if(GUILayout.Button("Select All")) {
						foreach(var m in validMembers) {
							SetMemberFavorite(typeFullName, m.Name, true);
						}
					}
					if(GUILayout.Button("Deselect All")) {
						foreach(var m in validMembers) {
							SetMemberFavorite(typeFullName, m.Name, false);
						}
					}
					EditorGUILayout.EndHorizontal();
					EditorGUILayout.Space(4);

					// Member checkboxes - read live state & apply instantly on toggle
					foreach(var m in validMembers) {
						bool current = IsMemberFavorite(typeFullName, m.Name);
						bool updated = EditorGUILayout.ToggleLeft(m.Name + "  :  " + m.MemberType, current);
						if(updated != current) {
							SetMemberFavorite(typeFullName, m.Name, updated);
						}
					}
				},
				null,
				(ref object obj) => {
					if(GUILayout.Button("Close")) {
						ActionPopupWindow.CloseLast();
					}
				}
			);
		}

		private bool IsMemberFavorite(string typeFullName, string memberName) {
			return uNodeEditor.SavedData.favoritesData.entries.Any(e =>
				e.categoryID == currentCategoryID && e.typeFullName == typeFullName &&
				e.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Member && e.displayName == memberName);
		}

		private void SetMemberFavorite(string typeFullName, string memberName, bool value) {
			var savedData = uNodeEditor.SavedData;
			if(value) {
				if(!IsMemberFavorite(typeFullName, memberName)) {
					var memEntry = new uNodeEditor.uNodeEditorData.FavoriteEntry {
						typeFullName = typeFullName,
						kind = uNodeEditor.uNodeEditorData.FavoriteItemKind.Member,
						displayName = memberName,
					};
					// AddFavoriteEntry saves options & fires OnFavoritesChanged -> list refreshes
					savedData.AddFavoriteEntry(currentCategoryID, memEntry);
				}
			} else {
				var toRemove = savedData.favoritesData.entries.FirstOrDefault(e =>
					e.categoryID == currentCategoryID && e.typeFullName == typeFullName &&
					e.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Member && e.displayName == memberName);
				if(toRemove != null) {
					// RemoveFavoriteEntry saves options & fires OnFavoritesChanged -> list refreshes
					savedData.RemoveFavoriteEntry(toRemove.id);
				}
			}
		}

		// ═══════════════════════════════════════
		//  Detail Panel
		// ═══════════════════════════════════════

		private void UpdateDetailPanel() {
			if(selectedEntry == null) {
				detailNameLabel.text = "No selection";
				detailTypeLabel.text = "";
				memberScroll.Clear();
				return;
			}

			detailNameLabel.text = selectedEntry.displayName;
			detailTypeLabel.text = selectedEntry.kind + "  —  " + selectedEntry.typeFullName;

			memberScroll.Clear();

			// For type entries, show member toggles
			if(selectedEntry.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Type) {
				Type type = null;
				type = TypeSerializer.Deserialize(selectedEntry.typeFullName, false);

				if(type != null) {
					var savedData = uNodeEditor.SavedData;
					var entry = savedData.favoritesData.entries.FirstOrDefault(e => e.id == selectedEntry.id);

					var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
					Array.Sort(members, (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

					bool hasMembers = false;
					foreach(var member in members) {
						if(member is EventInfo) continue;
						if(member is ConstructorInfo ctor && ctor.GetParameters().Length > 6) continue;

						hasMembers = true;
						bool isExcluded = entry != null && entry.excludedMembers.Contains(member.Name);

						var row = new VisualElement();
						row.style.flexDirection = FlexDirection.Row;
						row.style.alignItems = Align.Center;
						row.style.marginTop = 1;
						row.style.marginBottom = 1;

						var toggle = new Toggle(member.Name);
						toggle.value = !isExcluded;
						toggle.style.flexGrow = 1;
						toggle.RegisterValueChangedCallback(evt => {
							if(evt.newValue) {
								entry?.excludedMembers.Remove(member.Name);
							} else {
								if(entry != null && !entry.excludedMembers.Contains(member.Name)) {
									entry.excludedMembers.Add(member.Name);
								}
							}
							uNodeEditor.SaveOptions();
						});
						row.Add(toggle);
						memberScroll.Add(row);
					}

					if(!hasMembers) {
						memberScroll.Add(new Label("No public members") { style = { color = new Color(0.5f, 0.5f, 0.5f) } });
					}
				}
			}
		}

		// ═══════════════════════════════════════
		//  Actions
		// ═══════════════════════════════════════

		private void OpenItemSelector() {
			var graphEditor = uNodeEditor.window?.graphEditor;
			if(graphEditor == null) {
				EditorUtility.DisplayDialog("No Graph Open", "Open a graph editor first.", "OK");
				return;
			}

			var filter = new FilterAttribute();
			filter.Public = true;
			filter.Instance = true;
			filter.Static = true;
			filter.MaxMethodParam = int.MaxValue;
			filter.CanSelectType = true;

			var pos = graphEditor.window.position.position + graphEditor.window.position.size * 0.3f;

			ItemSelector.ShowWindow(
				graphEditor.graphData.graph,
				filter,
				(MemberData value) => {
					AddMemberDataAsFavorite(value);
				}
			).ChangePosition(pos);
		}

		private void AddMemberDataAsFavorite(MemberData memberData) {
			if(memberData == null) return;

			var savedData = uNodeEditor.SavedData;

			string typeFullName;
			string displayName;

			var startType = memberData.startType;
			bool isTypeFavorite = memberData.targetType == MemberData.TargetType.Type || memberData.targetType == MemberData.TargetType.uNodeType;

			if(isTypeFavorite) {
				typeFullName = startType.FullName;
				displayName = startType.PrettyName();
			} else {
				// Member: resolve the declaring type
				var members = memberData.GetMembers(false);
				if(members != null && members.Length > 0) {
					var lastMember = members[members.Length - 1];
					typeFullName = lastMember.DeclaringType?.FullName ?? startType.FullName;
				} else {
					typeFullName = startType.FullName;
				}
				var t = TypeSerializer.Deserialize(typeFullName, false);
				displayName = t?.PrettyName() ?? typeFullName.Split('.').Last();
			}

			// Ensure the type entry exists
			var typeEntry = savedData.favoritesData.entries.FirstOrDefault(e =>
				e.categoryID == currentCategoryID && e.typeFullName == typeFullName && e.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Type);
			if(typeEntry == null) {
				typeEntry = new uNodeEditor.uNodeEditorData.FavoriteEntry {
					typeFullName = typeFullName,
					kind = uNodeEditor.uNodeEditorData.FavoriteItemKind.Type,
					displayName = displayName,
				};
				savedData.AddFavoriteEntry(currentCategoryID, typeEntry);
			}

			// If it was a member selection, also add a Member entry
			if(!isTypeFavorite) {
				var members = memberData.GetMembers(false);
				if(members != null && members.Length > 0) {
					var lastMember = members[members.Length - 1];
					var memberName = lastMember.Name;
					var memberFullName = (lastMember.DeclaringType ?? startType).FullName;

					var memExisting = savedData.favoritesData.entries.FirstOrDefault(e =>
						e.categoryID == currentCategoryID && e.typeFullName == memberFullName && e.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Member && e.displayName == memberName);
					if(memExisting == null) {
						var memEntry = new uNodeEditor.uNodeEditorData.FavoriteEntry {
							typeFullName = memberFullName,
							kind = uNodeEditor.uNodeEditorData.FavoriteItemKind.Member,
							displayName = memberName,
						};
						savedData.AddFavoriteEntry(currentCategoryID, memEntry);
					}
				}
			}
			LoadEntries();
		}

		private void RemoveSelected() {
			if(selectedEntry == null) return;
			var savedData = uNodeEditor.SavedData;
			savedData.RemoveFavoriteEntry(selectedEntry.id);
			selectedEntry = null;
			LoadEntries();
			UpdateDetailPanel();
			UpdateAddMembersButton();
		}

		private void ShowAutoSortMenu() {
			var menu = new GenericDropdownMenu();
			menu.AddItem("Name (A-Z)", false, () => AutoSortEntries(Comparer<uNodeEditor.uNodeEditorData.FavoriteEntry>.Create((a, b) =>
				string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase))));
			menu.AddItem("Name (Z-A)", false, () => AutoSortEntries(Comparer<uNodeEditor.uNodeEditorData.FavoriteEntry>.Create((a, b) =>
				string.Compare(b.displayName, a.displayName, StringComparison.OrdinalIgnoreCase))));
			menu.AddItem("Type name (A-Z)", false, () => AutoSortEntries(Comparer<uNodeEditor.uNodeEditorData.FavoriteEntry>.Create((a, b) =>
				string.Compare(a.typeFullName, b.typeFullName, StringComparison.OrdinalIgnoreCase))));
			menu.AddItem("Kind", false, () => AutoSortEntries(Comparer<uNodeEditor.uNodeEditorData.FavoriteEntry>.Create((a, b) => {
				int kindCompare = ((int)a.kind).CompareTo((int)b.kind);
				if(kindCompare != 0) return kindCompare;
				return string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase);
			})));
			menu.DropDown(new Rect(Event.current.mousePosition, Vector2.zero), toolbar);
		}

		private void AutoSortEntries(IComparer<uNodeEditor.uNodeEditorData.FavoriteEntry> comparer) {
			var savedData = uNodeEditor.SavedData;
			var entries = savedData.GetEntriesForCategory(currentCategoryID);
			if(entries.Count <= 1) return;

			// Sort all entries in this category, keeping members grouped under their type
			// by sorting on typeFullName first so the group structure stays intact.
			var sorted = entries.OrderBy(e => e.orderIndex).ToList();
			sorted.Sort((a, b) => {
				int typeCompare = string.Compare(a.typeFullName, b.typeFullName, StringComparison.OrdinalIgnoreCase);
				if(typeCompare != 0) return typeCompare;
				return comparer.Compare(a, b);
			});

			for(int i = 0; i < sorted.Count; i++) {
				sorted[i].orderIndex = i;
			}

			uNodeEditor.SaveOptions();
			savedData.RaiseFavoritesChanged();
			LoadEntries();
		}

		private void CreateNode(DisplayEntry entry) {
			var graphEditor = uNodeEditor.window?.graphEditor;
			if(graphEditor == null) {
				EditorUtility.DisplayDialog("No Graph Open", "Open a graph editor to add nodes.", "OK");
				return;
			}
			if(!graphEditor.graphData.CanAddNode) return;

			var position = graphEditor.mousePositionInScreen;

			if(entry.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Node) {
				NodeMenu menu;
				if(nodeMenuCache != null) {
					nodeMenuCache.TryGetValue(entry.typeFullName, out menu);
				} else {
					menu = null;
				}
				if(menu != null) {
					NodeEditorUtility.AddNewNode<Node>(
						graphEditor.graphData,
						menu.nodeName,
						menu.type,
						position);
					graphEditor.Refresh();
				}
			} else if(entry.kind == uNodeEditor.uNodeEditorData.FavoriteItemKind.Type) {
				Type type;
				type = TypeSerializer.Deserialize(entry.typeFullName, false);
				if(type != null) {
					NodeEditorUtility.AddNewNode<MultipurposeNode>(
						graphEditor.graphData, position, (n) => {
							n.target = MemberData.CreateFromType(type);
							graphEditor.Refresh();
						});
				}
			}
		}
	}
}
