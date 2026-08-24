using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
		private ProgressBar searchProgressBar;
		private TreeView entryTreeView;
		private Label statusLabel;
		private VisualElement detailArea;
		private Label detailNameLabel;
		private Label detailTypeLabel;
		private ScrollView detailScroll;
		private Button removeButton;
		private Button addMembersButton;

		// ── State ──
		private DisplayEntry selectedEntry;
		private string currentCategoryID;
		private string searchString = "";
		private Dictionary<string, NodeMenu> nodeMenuCache;
		private Dictionary<int, DisplayEntry> treeIDMap = new Dictionary<int, DisplayEntry>();

		// ── Background search ──
		private CancellationTokenSource _searchCts;
		private int _searchGeneration;

		static readonly BindingFlags s_DeepMemberFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

		class DisplayEntry {
			public int treeID;
			public FavoritesDataAsset.Entry entry;
			public bool isVirtualChild;
			public List<DisplayEntry> children;
			public int memberCount;
			public float searchScore;   // relevance score (search mode only)
			public string searchPath;   // breadcrumb path shown under the title in search mode
		}

		/// <summary>
		/// Main-thread snapshot of one entry: everything the background search needs.
		/// The worker only reads these plain fields — it never touches Unity
		/// serialized/native APIs (SerializedType, MemberData, icons).
		/// </summary>
		class SearchItem {
			public FavoritesDataAsset.Entry entry;  // managed ref, read-only on worker
			public string id;
			public string parentID;
			public int orderIndex;
			public FavoriteKind kind;
			public bool isVirtual;
			public string displayName;      // plain text used for scoring/paths
			public string shortTypeName;    // last segment of typeName (fallback scoring)
			public Type resolvedRuntimeType; // captured on the UI thread for deep member search
		}

		/// <summary>Immutable payload produced by the background search task.</summary>
		class SearchResult {
			public List<TreeViewItemData<DisplayEntry>> items = new List<TreeViewItemData<DisplayEntry>>();
			public Dictionary<int, DisplayEntry> treeIDMap = new Dictionary<int, DisplayEntry>();
			public List<VisibleRow> rows = new List<VisibleRow>();
		}

		/// <summary>
		/// Stable TreeView id derived from the entry id, so expansion state
		/// survives rebuilds and sessions (sequential ids would reshuffle).
		/// </summary>
		static int GetStableTreeID(FavoritesDataAsset.Entry e) {
			int id = e.id?.GetHashCode() ?? 0;
			if(id == 0)
				id = 1; // 0 is not a valid TreeView id
			return id;
		}

		/// <summary>Instance variant resolving hash collisions against the current map.</summary>
		int AssignStableTreeID(FavoritesDataAsset.Entry e, Dictionary<int, DisplayEntry> targetMap) {
			int id = GetStableTreeID(e);
			while(targetMap.ContainsKey(id))
				id++; // rare hash collision — probe deterministically
			return id;
		}

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

		/// <summary>
		/// Explicit parent entry id for the next item added via a context/menu action
		/// (captured when the menu opens, consumed by the add methods). Null = category root.
		/// </summary>
		string pendingParentID;

		void SetPendingParent(FavoritesDataAsset.Entry owner) {
			pendingParentID = owner != null ? owner.id : null;
		}

		/// <summary>
		/// Consumes the pending parent (menu intent) or falls back to the current
		/// selection when it satisfies the given parent kinds. Clears the intent.
		/// </summary>
		string ResolveIntentParent(Func<FavoritesDataAsset.Entry, bool> validParentKinds) {
			var pid = pendingParentID;
			pendingParentID = null;
			if(pid == null && selectedEntry != null && selectedEntry.entry != null && validParentKinds(selectedEntry.entry))
				return selectedEntry.entry.id;
			return pid;
		}

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
			// Persist expansion before the panel tears down (best effort).
			try { SnapshotExpandedState(); } catch { }
			FavoritesManager.onChanged -= OnFavoritesChanged;
			rootVisualElement?.UnregisterCallback<KeyDownEvent>(OnWindowKeyDown);
			// Invalidate any in-flight background search so its apply is rejected.
			CancelPendingSearch();
		}

		void CancelPendingSearch() {
			_searchGeneration++;
			try { _searchCts?.Cancel(); } catch { }
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
			var menu = new GenericMenu();
			var pos = Event.current.mousePosition;
			SetPendingParent(null); // toolbar adds land at the category root
			menu.AddItem(new GUIContent("Folder"), false, () => CreateNewFolder(pos));
			menu.AddItem(new GUIContent("Namespace"), false, () => AddNamespaceFavorite(pos));
			menu.AddItem(new GUIContent("Type or Member"), false, () => OpenItemSelector(pos));
			menu.AddSeparator("");
			menu.AddItem(new GUIContent("Category"), false, () => CreateNewCategory(pos));
			menu.ShowAsContext();
		}

		private void CreateNewCategory(Vector2 mousePosition) {
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
			).ChangePosition(this.GetMousePositionForMenu(mousePosition));
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
					int myID = AssignStableTreeID(entry, treeIDMap);
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
						var virtualChildren = FavoritesManager.GetVirtualNamespaceChildren(entry);
						foreach(var vc in virtualChildren) {
							int vID = AssignStableTreeID(vc, treeIDMap);
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

					// For type entries, append virtual member children — members are
					// bound to their type and never persisted (excludedMembers hides them).
					if(entry.kind == FavoriteKind.Type && !entry.isVirtual && !inNamespace) {
						foreach(var vm in FavoritesManager.GetVirtualTypeMembers(entry)) {
							int vID = AssignStableTreeID(vm, treeIDMap);
							var vde = new DisplayEntry {
								treeID = vID,
								entry = vm,
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

			void AddResult(FavoritesDataAsset.Entry e, string path, float? scoreOverride = null) {
				float score = scoreOverride ?? ScoreSearchTarget(e);
				if(score < 0f)
					return; // no relevance match
				int id = AssignStableTreeID(e, treeIDMap);
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

			var seenSyncMembers = new HashSet<string>();

			/// Sync fallback of the worker's CollectTypeMembers: surface generated
			/// members matching the query under their type's path. When the type is
			/// favorited (ownerType), its mode-aware member visibility is respected;
			/// null owner (namespace virtual types) searches everything.
			void CollectTypeMembersSync(Type type, string typePath, FavoritesDataAsset.Entry ownerType) {
				if(type == null || type.IsEnum || searchString.Length < ItemSelector.MinWordForDeepTypeSearch)
					return;
				MemberInfo[] members;
				try { members = type.GetMembers(s_DeepMemberFlags); }
				catch { return; }
				foreach(var m in members) {
					if(m is EventInfo) continue;
					if(m is ConstructorInfo ctor && ctor.GetParameters().Length > 6) continue;
					if(FavoritesManager.IsAccessorMethod(m)) continue;
					// Respect the owner type's IncludeAll/ExcludeAll visibility.
					if(!FavoritesManager.IsMemberVisibleIn(ownerType, m))
						continue;
					string key = (type.FullName ?? type.Name) + "::" + m.Name + "::" + m.MetadataToken;
					if(!seenSyncMembers.Add(key)) continue;
					float score = -1f;
					var parts = searchString.Split(new[] { '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
					if(parts.Length >= 2) {
						float acc = -1f;
						for(int i = 1; i < parts.Length; i++) {
							var s = ItemSelector.GetRelevanceScore(parts[i], m.Name);
							if(s < 0f) { score = -1f; break; }
							acc = MathF.Max(acc, s) + (i * 0.1f);
						}
						score = acc;
					} else {
						score = ItemSelector.GetRelevanceScore(searchString, m.Name);
					}
					if(score < 0f) continue;
					var entry = new FavoritesDataAsset.Entry {
						kind = FavoriteKind.Member,
						rawMember = m,
						isVirtual = true,
						displayName = m.Name,
						id = "[deep]:" + key,
						parentID = "[deep]",
					};
					AddResult(entry, typePath, score);
				}
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
						// Virtual types from the namespace expansion are searchable too
					// (namespace visibility mode applies).
						foreach(var vc in FavoritesManager.GetVirtualNamespaceChildren(e)) {
							AddResult(vc, nsPath);
							Type vt = null;
							try { vt = vc.targetType?.type; } catch { }
							CollectTypeMembersSync(vt, JoinPath(nsPath, vt?.Name ?? vc.displayName), null);
						}
						break;
					}
					default:
						AddResult(e, parentPath);
						if(e.kind == FavoriteKind.Type && !e.isVirtual) {
							var typePath = JoinPath(parentPath, name);
							foreach(var c in ChildrenOf(e.id))
								CollectEntry(c, typePath);
							Type t = null;
							try { t = e.resolvedType; } catch { }
							// Generated members under this favorited type
							// (hidden members per its mode are skipped).
							CollectTypeMembersSync(t, typePath, e);
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
			if(e.kind == FavoriteKind.Member) {
				// Plain name keeps scoring clean of rich-text label markup.
				Consider(e.memberName);
			} else {
				Consider(GetDisplayName(e));
				if(e.typeName != null)
					Consider(e.typeName.Split('.').Last());
			}
			return best;
		}

		// ═══════════════════════════════════════
		//  Background Search
		// ═══════════════════════════════════════
		// Search runs on a worker thread: the UI thread snapshots plain strings,
		// the task does pure CPU work (scoring/paths/sorting), and results are
		// applied back on the UI thread. Stale generations are discarded, so
		// rapid typing self-cancels.

		void OnSearchChanged(string value) {
			searchString = value;
			CancelPendingSearch();
			if(string.IsNullOrEmpty(value)) {
				HideSearchProgress();
				// Instant restore of the hierarchy — no worker needed.
				ReloadTreeView();
				return;
			}
			int generation = ++_searchGeneration;
			_searchCts = new CancellationTokenSource();
			var token = _searchCts.Token;

			ShowSearchProgress();
			// Progress<T> marshals reports onto the UI thread it was created on.
			var progress = new Progress<float>(v => UpdateSearchProgress(v));

			// Snapshot phase (UI thread): capture all strings the worker needs.
			var snapshot = BuildSearchSnapshot();

			Task.Factory.StartNew(
					state => ComputeFlatSearchInBackground(snapshot, value, token, progress),
					null, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
				.ContinueWith(t => {
					if(t.IsFaulted) {
						// Surface unexpected worker failures; cancellations stay silent.
						var ex = t.Exception?.InnerException ?? t.Exception;
						if(!(ex is OperationCanceledException)) {
							Debug.LogException(ex);
						}
						HideSearchProgress();
						return;
					}
					if(t.Status != TaskStatus.RanToCompletion)
						return; // canceled — a newer search superseded it
					ApplyBackgroundResult(generation, t.Result);
				}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.FromCurrentSynchronizationContext());
		}

		void ShowSearchProgress() {
			if(this == null || searchProgressBar == null)
				return;
			searchProgressBar.value = 0f;
			searchProgressBar.style.display = DisplayStyle.Flex;
		}

		/// <summary>Worker reports 0..0.9; the bar never completes until results apply.</summary>
		void UpdateSearchProgress(float fraction) {
			if(this == null || searchProgressBar == null)
				return;
			searchProgressBar.value = Mathf.Clamp01(fraction) * 95f;
		}

		void HideSearchProgress() {
			if(this == null || searchProgressBar == null)
				return;
			searchProgressBar.style.display = DisplayStyle.None;
		}

		List<SearchItem> BuildSearchSnapshot() {
			var snapshot = new List<SearchItem>();
			if(string.IsNullOrEmpty(currentCategoryID))
				return snapshot;
			foreach(var e in FavoritesManager.asset.entries) {
				if(e.categoryID != currentCategoryID || e.isVirtual)
					continue;
				string displayName;
				if(e.kind == FavoriteKind.Member) {
					displayName = e.memberName ?? "(missing)";
				} else {
					try { displayName = GetDisplayName(e); }
					catch { displayName = e.displayName ?? e.id; }
				}
				snapshot.Add(new SearchItem {
					entry = e,
					id = e.id,
					parentID = e.parentID ?? string.Empty,
					orderIndex = e.orderIndex,
					kind = e.kind,
					isVirtual = false,
					displayName = displayName,
					shortTypeName = e.typeName?.Split('.').Last(),
					resolvedRuntimeType = e.kind == FavoriteKind.Type ? ResolveEntryType(e) : null,
				});
			}
			return snapshot;
		}

		/// <summary>
		/// Worker: builds the flat relevance-ranked result from the snapshot.
		/// Pure string/CPU work only — no Unity native APIs are touched.
		/// </summary>
		SearchResult ComputeFlatSearchInBackground(List<SearchItem> snapshot, string query, CancellationToken token, IProgress<float> progress) {
			var result = new SearchResult();
			progress?.Report(0f);

			// Stable ids (entry-id hash) keep expansion state aligned across
			// search/clear cycles; collisions are probed against the local map.
			int AssignID(FavoritesDataAsset.Entry e) {
				int id = GetStableTreeID(e);
				while(result.treeIDMap.ContainsKey(id))
					id++;
				return id;
			}

			var byParent = snapshot
				.GroupBy(i => i.parentID)
				.ToDictionary(g => g.Key, g => g.OrderBy(x => x.orderIndex).ToList());

			IEnumerable<SearchItem> ChildrenOf(string id) =>
				byParent.TryGetValue(id, out var list) ? list : Enumerable.Empty<SearchItem>();

			static string JoinPath(string parentPath, string segment) =>
				string.IsNullOrEmpty(parentPath) ? segment : parentPath + " > " + segment;

			// Deep search: "trans pos" matches Transform members named like "pos".
			var queryParts = query.Split(new[] { '.', ' ' }, StringSplitOptions.RemoveEmptyEntries);
			bool deepSearch = query.Length >= ItemSelector.MinWordForDeepTypeSearch;

			float Score(SearchItem item) {
				float best = -1f;
				void Consider(string s) {
					if(string.IsNullOrEmpty(s)) return;
					var v = ItemSelector.GetRelevanceScore(query, s);
					if(v > best) best = v;
				}
				// displayName is precomputed as plain text (member name for members).
				Consider(item.displayName);
				Consider(item.shortTypeName);
				return best;
			}

			/// <summary>
			/// Replicates TreeSearcher.IsMatchSearch for members: with a multi-part
			/// query ("trans pos") every trailing part must match the member name,
			/// accumulating ItemSelector's per-part bonus; otherwise the full query
			/// is scored against the name directly.
			/// </summary>
			float ScoreMemberName(string name) {
				if(string.IsNullOrEmpty(name)) return -1f;
				if(queryParts.Length >= 2) {
					float score = -1f;
					for(int i = 1; i < queryParts.Length; i++) {
						var s = ItemSelector.GetRelevanceScore(queryParts[i], name);
						if(s < 0f) return -1f; // all parts must match
						score = MathF.Max(score, s) + (i * 0.1f);
					}
					return score;
				}
				return ItemSelector.GetRelevanceScore(query, name);
			}

			var results = new List<DisplayEntry>();
			// Dedupes member results across sources (persisted children, deep search).
			var seenMemberKeys = new HashSet<string>();

			string MemberKey(MemberInfo mi) {
				return (mi.DeclaringType != null ? mi.DeclaringType.FullName : "") + "::" + mi.Name + "::" + mi.MetadataToken;
			}

			bool TryTrackMember(MemberInfo mi) {
				if(mi == null) return false;
				return seenMemberKeys.Add(MemberKey(mi));
			}

			void AddResult(SearchItem item, float score, string path) {
				if(item.kind == FavoriteKind.Member) {
					MemberInfo mi = null;
					try { mi = FavoritesManager.GetEntryMember(item.entry); } catch { }
					if(!TryTrackMember(mi))
						return; // duplicate member (persisted child or already added)
				}
				int id = AssignID(item.entry);
				var de = new DisplayEntry {
					treeID = id,
					entry = item.entry,
					isVirtualChild = item.isVirtual,
					searchScore = score,
					searchPath = path ?? string.Empty,
				};
				result.treeIDMap[id] = de;
				result.rows.Add(new VisibleRow {
					entry = item.entry,
					depth = 0,
					parentID = string.Empty,
					isLastChild = false,
					inNamespace = false,
				});
				results.Add(de);
			}

			/// <summary>
			/// Deep search: surfaces matching members of the given type that aren't
			/// already favorited under it. Reflection here is pure metadata reading,
			/// which is thread-safe; the transient entries it creates are never
			/// persisted and resolve lazily on the UI thread when bound.
			/// </summary>
			/// <summary>
			/// Deep member search. When the type is a favorited entry (ownerType),
			/// its mode-aware member visibility is respected — hidden members are
			/// skipped. Null owner (namespace virtual types) searches everything.
			/// </summary>
			void CollectTypeMembers(Type type, string typePath, FavoritesDataAsset.Entry ownerType) {
				if(!deepSearch || type == null || type.IsEnum)
					return;
				token.ThrowIfCancellationRequested();
				MemberInfo[] members;
				try { members = type.GetMembers(s_DeepMemberFlags); }
				catch { return; }
				string declName = type.FullName ?? type.Name;
				foreach(var m in members) {
					token.ThrowIfCancellationRequested();
					if(m is EventInfo) continue;
					if(m is ConstructorInfo ctor && ctor.GetParameters().Length > 6) continue;
					if(FavoritesManager.IsAccessorMethod(m)) continue;
					// Respect the owner type's IncludeAll/ExcludeAll visibility.
					if(!FavoritesManager.IsMemberVisibleIn(ownerType, m))
						continue;
					float score = ScoreMemberName(m.Name);
					if(score < 0f) continue;
					var entry = new FavoritesDataAsset.Entry {
						kind = FavoriteKind.Member,
						rawMember = m,
						isVirtual = true,
						displayName = m.Name,
						// AddResult dedupes via the resolved MemberInfo, so the id
						// only needs to be unique/stable for this search session.
						id = "[deep]:" + declName + "::" + m.Name + "::" + m.MetadataToken,
						parentID = "[deep]",
					};
					AddResult(new SearchItem {
						entry = entry,
						id = entry.id,
						parentID = "[deep]",
						orderIndex = int.MaxValue, // tiebreak below persisted entries
						kind = FavoriteKind.Member,
						isVirtual = true,
						displayName = m.Name,
					}, score, typePath);
				}
			}

			void CollectEntry(SearchItem item, string parentPath) {
				switch(item.kind) {
					case FavoriteKind.Folder:
						var folderPath = JoinPath(parentPath, item.displayName);
						foreach(var c in ChildrenOf(item.id))
							CollectEntry(c, folderPath);
						break;
					case FavoriteKind.Namespace: {
						var nsPath = JoinPath(parentPath, item.displayName);
						foreach(var c in ChildrenOf(item.id))
							CollectEntry(c, nsPath);
						// Virtual types of the namespace are searchable candidates.
						// Safe off-thread: pure reflection over loaded assemblies.
						// Namespace visibility mode applies.
						foreach(var vc in FavoritesManager.GetVirtualNamespaceChildren(item.entry)) {
							token.ThrowIfCancellationRequested();
							Type t = null;
							float score = -1f;
							try {
								t = vc.targetType?.type;
								score = Math.Max(
									ItemSelector.GetRelevanceScore(query, t?.Name ?? vc.displayName),
									t == null ? -1f : ItemSelector.GetRelevanceScore(query, t.FullName));
							} catch { }
							if(score >= 0f) {
								AddResult(new SearchItem {
									entry = vc,
									id = vc.id,
									parentID = "[ns]",
									orderIndex = 0,
									kind = FavoriteKind.Type,
									isVirtual = true,
									displayName = vc.targetType?.type?.Name ?? vc.displayName,
								}, score, nsPath);
							}
							// Deep member search inside namespace types too
							// (null owner → visibility rules don't apply to them).
							CollectTypeMembers(t, JoinPath(nsPath, t?.Name ?? vc.displayName), null);
						}
						break;
					}
					default:
						float s = Score(item);
						if(s >= 0f)
							AddResult(item, s, parentPath);
						// Deep member search inside favorited types — members are
						// generated from the type, so this is their only source.
						// Hidden members (per the type's mode) are skipped.
						if(item.kind == FavoriteKind.Type && !item.isVirtual) {
							var typePath = JoinPath(parentPath, item.displayName);
							foreach(var c in ChildrenOf(item.id))
								CollectEntry(c, typePath);
							CollectTypeMembers(item.resolvedRuntimeType, typePath, item.entry);
						}
						break;
				}
			}

			var knownIDs = new HashSet<string>(snapshot.Select(x => x.id));
			var roots = ChildrenOf(string.Empty).ToList();
			var orphans = snapshot.Where(x => x.parentID.Length > 0 && !knownIDs.Contains(x.parentID)).ToList();
			int totalRoots = Math.Max(1, roots.Count + orphans.Count);
			int processedRoots = 0;
			foreach(var root in roots) {
				token.ThrowIfCancellationRequested();
				CollectEntry(root, null);
				progress?.Report(Math.Min(processedRoots / (float)totalRoots, 0.9f));
				processedRoots++;
			}
			foreach(var orphan in orphans) {
				token.ThrowIfCancellationRequested();
				CollectEntry(orphan, null);
				progress?.Report(Math.Min(processedRoots / (float)totalRoots, 0.9f));
				processedRoots++;
			}

			results.Sort((a, b) => {
				int c = b.searchScore.CompareTo(a.searchScore);
				if(c != 0) return c;
				return a.entry.orderIndex.CompareTo(b.entry.orderIndex);
			});

			result.items.AddRange(results.Select(de => new TreeViewItemData<DisplayEntry>(de.treeID, de)));
			return result;
		}

		void ApplyBackgroundResult(int generation, SearchResult result) {
			// Reject stale results from superseded searches / closed windows.
			if(result == null || generation != _searchGeneration || this == null || entryTreeView == null)
				return;
			// Preserve hierarchical expansion before the map is swapped.
			SnapshotExpandedState();
			treeIDMap = result.treeIDMap;
			visibleRows.Clear();
			visibleRows.AddRange(result.rows);
			entryTreeView.fixedItemHeight = string.IsNullOrEmpty(searchString) ? 20 : 40;
			entryTreeView.SetRootItems(result.items);
			entryTreeView.Rebuild();
			ApplyExpandedState();
			HideSearchProgress();
			UpdateStatusLabel();
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
			// Members are bound to their type header and can't be re-parented.
			if(moved.kind == FavoriteKind.Member) return false;
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
					return GetMemberLabel(e) ?? "(missing)";
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

		/// <summary>
		/// Formats a member label like ItemSelector does: pretty method/constructor
		/// signature, extension-method formatting, and rich colored text when the
		/// coloredItem preference is enabled.
		/// </summary>
		string GetMemberLabel(FavoritesDataAsset.Entry e) {
			var member = FavoritesManager.GetEntryMember(e);
			if(member == null)
				return e.memberName;
			if(member is MethodInfo method && method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
				return EditorReflectionUtility.GetPrettyExtensionMethodName(method);
			if(uNodePreference.preferenceData.coloredItem)
				return EditorReflectionUtility.GetRichMemberName(member);
			return EditorReflectionUtility.GetPrettyMemberName(member);
		}

		/// <summary>Plain (markup-free) row title, safe for highlight span math.</summary>
		string GetPlainTitle(FavoritesDataAsset.Entry e) {
			//if(e.kind == FavoriteKind.Member && !isVirtualMember(e))
			//	return e.memberName ?? "(missing)";
			try { return GetDisplayName(e); }
			catch { return e.displayName ?? e.id; }
		}

		// ItemSelector's highlight blue at 50% alpha.
		const string kHighlightColorTag = "#3E7DD880";

		/// <summary>
		/// Wraps the query matches inside rich-text mark tags so TextCore renders a
		/// highlight background behind them — same spans ItemSelector highlights.
		/// The input must be markup-free so character offsets stay valid.
		/// </summary>
		string ApplySearchHighlight(string text) {
			if(string.IsNullOrEmpty(text) || string.IsNullOrEmpty(searchString))
				return text;
			List<(int start, int end)> spans;
			try { spans = ItemSelector.GetSearchHighlight(text, searchString); }
			catch { return text; }
			if(spans == null || spans.Count == 0)
				return text;
			var sb = new System.Text.StringBuilder(text.Length + spans.Count * 20);
			int last = 0;
			foreach(var span in spans) {
				int start = Mathf.Clamp(span.start, 0, text.Length);
				int end = Mathf.Clamp(span.end, 0, text.Length);
				if(end <= start || start < last)
					continue;
				if(start > last)
					sb.Append(text, last, start - last);
				sb.Append("<mark=").Append(kHighlightColorTag).Append('>');
				sb.Append(text, start, end - start);
				sb.Append("</mark>");
				last = end;
			}
			if(last < text.Length)
				sb.Append(text, last, text.Length - last);
			return sb.ToString();
		}

		Texture GetIcon(FavoritesDataAsset.Entry e) {
			Type iconType = ResolveEntryType(e);

			switch(e.kind) {
				case FavoriteKind.Folder:
					return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.FolderIcon));
				case FavoriteKind.Namespace:
					return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.NamespaceIcon));
				case FavoriteKind.Member:
					var member = FavoritesManager.GetEntryMember(e);
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

		/// <summary>
		/// Second icon for member rows: the value/return type icon
		/// (field type, property type, or method return type).
		/// </summary>
		Texture GetMemberValueTypeIcon(FavoritesDataAsset.Entry e) {
			if(e.kind != FavoriteKind.Member)
				return null;
			var mi = FavoritesManager.GetEntryMember(e);
			Type t = null;
			if(mi is MethodInfo method)
				t = method.ReturnType;
			else if(mi is PropertyInfo prop)
				t = prop.PropertyType;
			else if(mi is FieldInfo field)
				t = field.FieldType;
			return t != null ? uNodeEditorUtility.GetTypeIcon(t) : null;
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

			categoryDropdown = new DropdownField("Category", new List<string>(), 0) { style = { flexGrow = 1 } };
			categoryDropdown.RegisterValueChangedCallback(OnCategoryChanged);
			toolbar.Add(categoryDropdown);

			toolbar.Add(new ToolbarSpacer());

			// Combined add button with dropdown menu
			var addMenu = new ToolbarButton(ShowAddMenu) { text = "+ Add", tooltip = "Add Item" };
			toolbar.Add(addMenu);

			addMembersButton = new ToolbarButton(() => OpenAddMembersPopup(Event.current.mousePosition)) { text = "+ Members", tooltip = "Add Members (type) / Types (namespace)" };
			addMembersButton.SetEnabled(false);
			toolbar.Add(addMembersButton);

			toolbar.Add(new ToolbarSpacer { flex = true });

			toolbar.Add(new ToolbarButton(() => ShowAutoSortMenu()) { text = "Sort" });

			root.Add(toolbar);

			// ── Search ──
			searchField = new TextField() { name = "search", tooltip = "Search" };
			searchField.RegisterValueChangedCallback(evt => OnSearchChanged(evt.newValue));
			searchField.style.marginLeft = 4;
			searchField.style.marginRight = 4;
			searchField.style.marginTop = 2;
			searchField.style.marginBottom = 2;
			root.Add(searchField);

			// ── Search progress ──
			searchProgressBar = new ProgressBar { title = "Searching…" };
			searchProgressBar.style.height = 24;
			searchProgressBar.style.marginLeft = 4;
			searchProgressBar.style.marginRight = 4;
			searchProgressBar.style.display = DisplayStyle.None;
			root.Add(searchProgressBar);

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

					// Primary icon slot (member rows): shows the value/return type icon.
					// Hidden unless bind assigns a texture to it.
					var typeIcon = new Image { name = "type-icon" };
					typeIcon.pickingMode = PickingMode.Ignore;
					typeIcon.style.width = 16;
					typeIcon.style.height = 16;
					typeIcon.style.flexShrink = 0;
					typeIcon.style.display = DisplayStyle.None;
					item.Add(typeIcon);

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

					// Drag behavior:
					// - Type & Member rows are graph-draggable (payload key "uNode",
					//   matching the NodeBrowser/graph contract), including virtual
					//   rows generated from namespaces/types.
					// - Reordering stays limited to persisted non-virtual rows and is
					//   disabled while searching; virtual entries can never reorder —
					//   CanMove/MoveEntry reject them, so stray drops are ignored.
					bool isVirtual = de.isVirtualChild || de.entry.isVirtual;
					bool hasSearch = !string.IsNullOrEmpty(searchString);
					var graphPayload = GetGraphDragPayload(de.entry);
					bool isGraphItem = graphPayload != null;
					bool canReorder = !isVirtual && !hasSearch;
					bool canDrag = isGraphItem || canReorder;
					item.CanDragFunc = () => canDrag;
					item.CanDragInsideParentFunc = () => canReorder;
					item.CanHaveChildsFunc = () => de.entry.kind == FavoriteKind.Folder && !de.entry.isVirtual && !hasSearch;

					// Drag payload: reorder keys plus the "uNode" graph contract
					// (System.Type or MemberInfo — what UGraphView/BlockNodeView read).
					item.GetDragGenericData = () => {
						if(!canDrag)
							return null;
						var data = new Dictionary<string, object> {
							{ "favoriteID", de.entry.id },
							{ "favoriteCategory", de.entry.categoryID },
						};
						if(graphPayload != null)
							data["uNode"] = graphPayload;
						return data;
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
					// In search mode the title is markup-free so highlight spans stay valid.
					item.label.text = hasSearch
						? ApplySearchHighlight(GetPlainTitle(de.entry))
						: GetDisplayName(de.entry);
					item.ShowIcon(GetIcon(de.entry));
					// Fixed icon size keeps rows aligned even when a texture is missing.
					if(item.icon != null) {
						item.icon.style.width = 16;
						item.icon.style.height = 16;
						item.icon.style.flexShrink = 0;
					}

					var typeIconImg = item.Q<Image>("type-icon");
					if(typeIconImg != null) {
						var secondTex = GetMemberValueTypeIcon(de.entry);
						typeIconImg.image = secondTex;
						typeIconImg.style.display = secondTex != null ? DisplayStyle.Flex : DisplayStyle.None;
					}

					// Search mode shows the breadcrumb path under the title.
					var pathLabel = item.Q<Label>("path-label");
					if(pathLabel != null) {
						bool showPath = hasSearch && !string.IsNullOrEmpty(de.searchPath);
						pathLabel.text = showPath ? ApplySearchHighlight(de.searchPath) : string.Empty;
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
				evt.menu.AppendAction("New Folder", a => { SetPendingParent(null); CreateNewFolder(a.eventInfo.mousePosition); });
				evt.menu.AppendAction("Add Namespace", a => { SetPendingParent(null); AddNamespaceFavorite(a.eventInfo.mousePosition); });
				evt.menu.AppendAction("Add Type or Member", a => { SetPendingParent(null); OpenItemSelector(a.eventInfo.mousePosition); });
			}));
			// Last-chance snapshot when the tree detaches (window closing).
			entryTreeView.RegisterCallback<DetachFromPanelEvent>(_ => SnapshotExpandedState());
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
			detailScroll = new ScrollView(ScrollViewMode.Vertical) { style = { marginTop = 4, maxHeight = 150 } };
			detailArea.Add(detailNameLabel);
			detailArea.Add(detailTypeLabel);
			detailArea.Add(detailScroll);
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
			// Data changed — any in-flight background result is now stale.
			CancelPendingSearch();
			HideSearchProgress();
			// Capture expansion before the id map is rebuilt.
			SnapshotExpandedState();
			var items = BuildTreeData();
			// Search rows are double height to fit the title + path description
			// (same as ItemSelector's relevance results).
			entryTreeView.fixedItemHeight = string.IsNullOrEmpty(searchString) ? 20 : 40;
			entryTreeView.SetRootItems(items);
			entryTreeView.Rebuild();
			ApplyExpandedState();
			UpdateStatusLabel();
		}

		/// <summary>
		/// Records which expandable rows are currently expanded into the
		/// persistent store. Must run BEFORE the tree id map is replaced,
		/// while the old ids still map to live rows.
		/// </summary>
		void SnapshotExpandedState() {
			if(entryTreeView == null || entryTreeView.panel == null)
				return;
			if(!string.IsNullOrEmpty(searchString))
				return; // flat search view has no hierarchy to snapshot
			foreach(var kv in treeIDMap) {
				var de = kv.Value;
				if(de?.entry == null || !IsExpandableEntry(de.entry))
					continue;
				bool isExpanded;
				try { isExpanded = entryTreeView.IsExpanded(kv.Key); }
				catch { continue; }
				FavoritesManager.SetEntryExpanded(de.entry.id, isExpanded);
			}
		}

		/// <summary>Rows that can expand: folders, namespaces, and type items (with virtual members).</summary>
		static bool IsExpandableEntry(FavoritesDataAsset.Entry e) {
			if(e.isVirtual)
				return false;
			switch(e.kind) {
				case FavoriteKind.Folder:
				case FavoriteKind.Namespace:
				case FavoriteKind.Type:
					return true;
				default:
					return false;
			}
		}

		/// <summary>Re-expands rows persisted as expanded. Runs after Rebuild.</summary>
		void ApplyExpandedState() {			if(entryTreeView == null || entryTreeView.panel == null)
				return;
			if(!string.IsNullOrEmpty(searchString))
				return;
			foreach(var kv in treeIDMap) {
				var de = kv.Value;
				if(de?.entry == null || !IsExpandableEntry(de.entry))
					continue;
				if(FavoritesManager.IsEntryExpanded(de.entry.id)) {
					try { entryTreeView.ExpandItem(kv.Key); }
					catch { }
				}
			}
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
				// Virtual rows are read-only; node creation and (for rows bound to a
				// favorited owner) remove-to-hide are offered.
				if(e.kind == FavoriteKind.Type) {
					evt.menu.AppendAction("Create Node", _ => TryCreateNode(de));
					// Virtual types owned by a favorited namespace can be hidden
					// (persisted in the namespace's mode list).
					var nsOwner = ResolveOwningNamespace(e);
					if(nsOwner != null && !string.IsNullOrEmpty(e.displayName)) {
						evt.menu.AppendSeparator();
						evt.menu.AppendAction("Remove", _ => SetTypeNameVisible(nsOwner, e.displayName, false));
					}
				} else if(e.kind == FavoriteKind.Member && e.rawMember != null) {
					evt.menu.AppendAction("Create Node", _ => TryCreateNode(de));
					// Removing a generated member persists it in the type's mode list.
					var owner = ResolveOwningType(e);
					var ownerMember = FavoritesManager.GetEntryMember(e);
					if(owner != null && ownerMember != null) {
						evt.menu.AppendSeparator();
						evt.menu.AppendAction("Remove", _ => SetMemberVisible(owner, ownerMember, false));
					}
				}
				evt.StopPropagation();
				return;
			}

			switch(e.kind) {
				case FavoriteKind.Folder:
					evt.menu.AppendAction("New Folder", e => { selectedEntry = de; SetPendingParent(de.entry); UpdateDetailPanel(); CreateNewFolder(e.eventInfo.mousePosition); });
					evt.menu.AppendAction("Rename", _ => { selectedEntry = de; RenameSelectedFolder(); });
					evt.menu.AppendSeparator();
					evt.menu.AppendAction("Add Namespace", e => { selectedEntry = de; SetPendingParent(de.entry); UpdateDetailPanel(); AddNamespaceFavorite(e.eventInfo.mousePosition); });
					evt.menu.AppendAction("Add Type or Member", e => { selectedEntry = de; SetPendingParent(de.entry); UpdateDetailPanel(); UpdateAddMembersButton(); OpenItemSelector(e.eventInfo.mousePosition); });
					break;
				case FavoriteKind.Type:
					evt.menu.AppendAction("Create Node", _ => TryCreateNode(de));
					evt.menu.AppendAction("Add Members...", e => { selectedEntry = de; UpdateDetailPanel(); UpdateAddMembersButton(); OpenAddMembersPopup(e.eventInfo.mousePosition); });
					break;
				case FavoriteKind.Namespace:
					evt.menu.AppendAction("Add Types...", e => { selectedEntry = de; UpdateDetailPanel(); UpdateAddMembersButton(); OpenAddMembersPopup(e.eventInfo.mousePosition); });
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
			if(addMembersButton != null) {
				bool isType = selectedEntry != null && selectedEntry.entry.kind == FavoriteKind.Type && !selectedEntry.isVirtualChild;
				bool isNamespace = selectedEntry != null && selectedEntry.entry.kind == FavoriteKind.Namespace;
				addMembersButton.SetEnabled(isType || isNamespace);
			}
		}

		void UpdateDetailPanel() {
			if(selectedEntry == null || selectedEntry.entry == null) {
				detailNameLabel.text = "No selection";
				detailTypeLabel.text = "";
				detailScroll.Clear();
				return;
			}
			var e = selectedEntry.entry;
			detailNameLabel.text = GetDisplayName(e);
			detailTypeLabel.text = e.kind + (e.kind == FavoriteKind.Namespace ? "  —  " + e.displayName : e.kind == FavoriteKind.Type || e.kind == FavoriteKind.Node ? "  —  " + e.typeName : "");

			detailScroll.Clear();
			foreach(var line in BuildSummaryLines(e)) {
				AddSummaryRow(detailScroll, line.icon, line.text);
			}
		}

		// ═══════════════════════════════════════
		//  Detail Summary
		// ═══════════════════════════════════════

		struct SummaryLine {
			public Texture icon;
			public string text;

			public SummaryLine(Texture icon, string text) {
				this.icon = icon;
				this.text = text;
			}
		}

		/// <summary>
		/// Builds the selection summary shown in the detail panel, mirroring the
		/// ItemSelector/NodeBrowser tooltip contents (declaring type, assembly,
		/// target/static, return/value type, XML documentation, parameters).
		/// </summary>
		List<SummaryLine> BuildSummaryLines(FavoritesDataAsset.Entry e) {
			var lines = new List<SummaryLine>();

			// Location breadcrumb (parent chain within the category).
			var path = BuildEntryPath(e);
			if(!string.IsNullOrEmpty(path))
				lines.Add(new SummaryLine(null, path));

			switch(e.kind) {
				case FavoriteKind.Member: {
					var mi = FavoritesManager.GetEntryMember(e);
					if(mi != null) {
						var contents = ItemSelector.Utility.GetTooltipContents(mi);
						// Index 0 duplicates the panel title — skip it.
						for(int i = 1; i < contents.Count; i++) {
							lines.Add(new SummaryLine(contents[i].image, contents[i].text));
						}
					}
					else if(!string.IsNullOrEmpty(e.memberName)) {
						lines.Add(new SummaryLine(GetMemberKindIcon(null), "(unresolved) " + e.memberName));
					}
					break;
				}
				case FavoriteKind.Type: {
					Type t = null;
					try { t = ResolveEntryType(e); } catch { }
					if(t != null) {
						lines.Add(new SummaryLine(uNodeEditorUtility.GetTypeIcon(t), t.PrettyName(true)));
						if(t.Assembly != null)
							lines.Add(new SummaryLine(null, "Assembly: " + t.Assembly.GetName().Name));
						AppendDocumentation(lines, t);
						int memberCount = FavoritesManager.GetVirtualTypeMembers(e).Count;
						lines.Add(new SummaryLine(null, memberCount + " visible members"));
					}
					break;
				}
				case FavoriteKind.Namespace: {
					int count = FavoritesManager.GetVirtualNamespaceChildren(e).Count;
					lines.Add(new SummaryLine(null, count + " types"));
					break;
				}
				case FavoriteKind.Folder: {
					int count = FavoritesManager.GetChildren(e.categoryID, e.id).Count;
					lines.Add(new SummaryLine(null, count + " items"));
					break;
				}
				case FavoriteKind.Node: {
					if(!string.IsNullOrEmpty(e.nodeMenuName))
						lines.Add(new SummaryLine(null, e.nodeMenuName));
					Type t = ResolveOwningNodeTypeSafe(e);
					if(t != null)
						AppendDocumentation(lines, t);
					break;
				}
			}
			return lines;
		}

		Type ResolveOwningNodeTypeSafe(FavoritesDataAsset.Entry e) {
			try { return e.resolvedType; } catch { return null; }
		}

		string BuildEntryPath(FavoritesDataAsset.Entry e) {
			var segments = new List<string>();
			var visited = new HashSet<string>();
			var current = e.parentID;
			int depth = 0;
			while(!string.IsNullOrEmpty(current) && depth < 16 && visited.Add(current)) {
				var parent = FavoritesManager.asset.entries.FirstOrDefault(x => x.id == current);
				if(parent == null)
					break;
				segments.Insert(0, GetDisplayName(parent));
				current = parent.parentID;
				depth++;
			}
			return segments.Count > 0 ? string.Join(" > ", segments) : null;
		}

		void AppendDocumentation(List<SummaryLine> lines, MemberInfo member) {
			if(!XmlDoc.hasLoadDoc || member == null)
				return;
			try {
				var docElement = XmlDoc.XMLFromMember(member)?["summary"];
				if(docElement != null && !string.IsNullOrWhiteSpace(docElement.InnerText)) {
					lines.Add(new SummaryLine(null, "<b>Documentation ▼</b> " + docElement.InnerText.Trim()));
				}
			}
			catch { }
		}

		/// <summary>Renders one summary line as an icon + wrapping rich-text label.</summary>
		void AddSummaryRow(VisualElement parent, Texture icon, string text) {
			if(string.IsNullOrEmpty(text))
				return;
			var row = new VisualElement {
				style = { flexDirection = FlexDirection.Row, alignItems = Align.FlexStart, marginTop = 1, marginBottom = 1 }
			};
			if(icon != null) {
				var img = new Image { image = icon };
				img.style.width = 14;
				img.style.height = 14;
				img.style.flexShrink = 0;
				img.style.marginRight = 4;
				img.style.marginTop = 1;
				row.Add(img);
			}
			var lbl = new Label(text) { enableRichText = true };
			lbl.style.whiteSpace = WhiteSpace.Normal;
			lbl.style.flexGrow = 1;
			lbl.style.fontSize = 11;
			row.Add(lbl);
			parent.Add(row);
		}

		// ═══════════════════════════════════════
		//  Actions
		// ═══════════════════════════════════════

		void CreateNewFolder(Vector2 mousePosition) {
			string folderName = "";
			string parentID = ResolveIntentParent(x => x.kind == FavoriteKind.Folder || x.kind == FavoriteKind.Namespace);
			ActionPopupWindow.Show(null, (ref object obj) => {
				EditorGUILayout.LabelField("New Folder", EditorStyles.boldLabel);
				folderName = EditorGUILayout.TextField("Name", folderName);
				if(GUILayout.Button("Create") && !string.IsNullOrWhiteSpace(folderName)) {
					FavoritesManager.AddFolder(currentCategoryID, folderName.Trim(), parentID);
					ReloadTreeView();
					ActionPopupWindow.CloseLast();
				}
			}).ChangePosition(this.GetMousePositionForMenu(mousePosition));
		}

		void AddNamespaceFavorite(Vector2 mousePosition) {
			string ns = "";
			string parentID = ResolveIntentParent(x => x.kind == FavoriteKind.Folder);
			ActionPopupWindow.Show(null, (ref object obj) => {
				EditorGUILayout.LabelField("Add Namespace", EditorStyles.boldLabel);
				ns = EditorGUILayout.TextField("Namespace", ns);
				if(GUILayout.Button("Add") && !string.IsNullOrWhiteSpace(ns)) {
					FavoritesManager.AddNamespace(currentCategoryID, ns.Trim(), parentID);
					ReloadTreeView();
					ActionPopupWindow.CloseLast();
				}
			}).ChangePosition(this.GetMousePositionForMenu(mousePosition));
		}

		void OpenItemSelector(Vector2 mousePosition) {
			var graphEditor = uNodeEditor.window?.graphEditor;
			var filter = new FilterAttribute {
				Public = true, Instance = true, Static = true,
				MaxMethodParam = int.MaxValue, CanSelectType = true
			};
			ItemSelector.ShowWindow(
				graphEditor != null ? graphEditor.graphData.graph : null,
				filter,
				(MemberData value) => AddMemberDataAsFavorite(value)
			).ChangePosition(this.GetMousePositionForMenu(mousePosition));
		}

		void AddMemberDataAsFavorite(MemberData memberData) {
			if(memberData == null) return;

			bool isType = memberData.IsTargetingType
				|| memberData.targetType == MemberData.TargetType.Type
				|| memberData.targetType == MemberData.TargetType.uNodeType
				|| memberData.targetType == MemberData.TargetType.Values;

			string parentID = ResolveIntentParent(x => x.kind == FavoriteKind.Folder || x.kind == FavoriteKind.Namespace);

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
				string pid = parentID ?? string.Empty;
				var typeHeader = FavoritesManager.asset.entries.FirstOrDefault(x =>
					x.categoryID == currentCategoryID && x.kind == FavoriteKind.Type &&
					x.typeName == declType.FullName && x.parentID == pid);
				bool created = false;
				if(typeHeader == null) {
					FavoritesManager.AddEntry(currentCategoryID, new FavoritesDataAsset.Entry { kind = FavoriteKind.Type, targetType = new SerializedType(declType), parentID = pid });
					typeHeader = FavoritesManager.asset.entries.FirstOrDefault(x =>
						x.categoryID == currentCategoryID && x.kind == FavoriteKind.Type &&
						x.typeName == declType.FullName && x.parentID == pid);
					created = typeHeader != null;
				}
				if(typeHeader == null) return;

				if(created) {
					// Picking a member on a brand-new type: start in ExcludeAll mode
					// so ONLY the picked member is visible; the user can flip to
					// Include All from the Members-of popup at any time.
					typeHeader.memberMode = TypeMemberMode.ExcludeAll;
					typeHeader.excludedMembers?.Clear();
					if(typeHeader.excludedMembers == null)
						typeHeader.excludedMembers = new List<string>();
					typeHeader.excludedMembers.Add(last.Name);
					FavoritesManager.Save();
					FavoritesManager.NotifyChanged();
				}
				else {
					// Existing type item: just make the picked member visible.
					SetMemberVisible(typeHeader, last, true);
				}
			}
			ReloadTreeView();
		}

		/// <summary>
		/// Resolves the favorited type entry that owns a generated member row
		/// (parentID format: "[type]:&lt;entryId&gt;"). Returns null for rows without
		/// a favorited owner (e.g. deep-search results).
		/// </summary>
		FavoritesDataAsset.Entry ResolveOwningType(FavoritesDataAsset.Entry memberEntry) {
			const string prefix = "[type]:";
			if(memberEntry == null || string.IsNullOrEmpty(memberEntry.parentID) || !memberEntry.parentID.StartsWith(prefix))
				return null;
			var ownerID = memberEntry.parentID.Substring(prefix.Length);
			return FavoritesManager.asset.entries.FirstOrDefault(x => x.id == ownerID);
		}

		/// <summary>
		/// Resolves the favorited namespace entry that owns a generated virtual
		/// type row (parentID format: "[nsentry]:&lt;entryId&gt;"). Returns null for
		/// rows without a favorited owner.
		/// </summary>
		FavoritesDataAsset.Entry ResolveOwningNamespace(FavoritesDataAsset.Entry typeEntry) {
			const string prefix = "[nsentry]:";
			if(typeEntry == null || string.IsNullOrEmpty(typeEntry.parentID) || !typeEntry.parentID.StartsWith(prefix))
				return null;
			var ownerID = typeEntry.parentID.Substring(prefix.Length);
			return FavoritesManager.asset.entries.FirstOrDefault(x => x.id == ownerID);
		}

		/// <summary>
		/// Show/hide a generated virtual type under its namespace favorite by
		/// toggling the name in the namespace's mode list (mirrors SetMemberVisible).
		/// </summary>
		void SetTypeNameVisible(FavoritesDataAsset.Entry nsEntry, string typeName, bool visible) {
			if(nsEntry == null || string.IsNullOrEmpty(typeName))
				return;
			if(nsEntry.excludedMembers == null)
				nsEntry.excludedMembers = new List<string>();
			bool shouldContain = nsEntry.memberMode == TypeMemberMode.ExcludeAll ? visible : !visible;
			bool changed;
			if(shouldContain) {
				if(!nsEntry.excludedMembers.Contains(typeName)) {
					nsEntry.excludedMembers.Add(typeName);
					changed = true;
				}
				else {
					changed = false;
				}
			}
			else {
				changed = nsEntry.excludedMembers.Remove(typeName);
			}
			if(changed) {
				FavoritesManager.Save();
				FavoritesManager.NotifyChanged();
			}
		}

		void RemoveSelected() {
			if(selectedEntry == null || selectedEntry.entry == null)
				return;
			var e = selectedEntry.entry;
			// Generated members are removed by persisting their visibility state
			// on the owning type (mode-aware: exclusion in IncludeAll, omission in ExcludeAll).
			if(e.kind == FavoriteKind.Member && e.isVirtual) {
				var owner = ResolveOwningType(e);
				var ownerMember = FavoritesManager.GetEntryMember(e);
				if(owner != null && ownerMember != null) {
					SetMemberVisible(owner, ownerMember, false);
					selectedEntry = null;
					UpdateDetailPanel();
					UpdateAddMembersButton();
					return; // NotifyChanged already reloaded the tree
				}
				// Ownerless (deep-search result): just drop the selection.
				selectedEntry = null;
				UpdateDetailPanel();
				UpdateAddMembersButton();
				return;
			}
			FavoritesManager.RemoveRecursive(e.id);
			selectedEntry = null;
			ReloadTreeView();
			UpdateDetailPanel();
			UpdateAddMembersButton();
		}

		void OpenAddMembersPopup(Vector2 mousePosition) {
			if(selectedEntry == null || selectedEntry.isVirtualChild) return;
			// Namespace favorites manage their generated TYPE list instead.
			if(selectedEntry.entry.kind == FavoriteKind.Namespace) {
				ActionPopupWindow.Show(() => BuildNamespaceTypesUI(selectedEntry.entry))
					.ChangePosition(this.GetMousePositionForMenu(mousePosition));
				return;
			}
			if(selectedEntry.entry.kind != FavoriteKind.Type) return;
			var e = selectedEntry.entry;
			if(e.isVirtual) return;
			var type = e.resolvedType;
			if(type == null) return;

			var validMembers = EditorReflectionUtility.GetSortedMembers(type, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
				.Where(m => m is not MethodInfo || m.Name.StartsWith("get_") == false && m.Name.StartsWith("set_") == false).OrderBy(m => m.Name).ToArray();

			if(validMembers.Length == 0) {
				EditorUtility.DisplayDialog("No Members", "This type has no public members.", "OK");
				return;
			}

			ActionPopupWindow.Show(() => BuildAddMembersUI(e, validMembers))
				.ChangePosition(this.GetMousePositionForMenu(Event.current.mousePosition));
		}

		/// <summary>
		/// Builds the VisualElement UI for the 'Members of' popup
		/// (the window auto-sizes to this content via ActionPopupWindow).
		/// </summary>
		VisualElement BuildAddMembersUI(FavoritesDataAsset.Entry typeEntry, MemberInfo[] members) {
			var root = new VisualElement();
			root.style.paddingTop = 8;
			root.style.paddingBottom = 8;
			root.style.paddingLeft = 10;
			root.style.paddingRight = 10;
			root.focusable = true;
			root.RegisterCallback<KeyDownEvent>(evt => {
				if(evt.keyCode == KeyCode.Escape) {
					ActionPopupWindow.CloseLast();
					evt.StopPropagation();
				}
			});

			root.Add(new Label("Members of " + GetDisplayName(typeEntry)) {
				style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 6 }
			});

			var toggles = new List<(Toggle toggle, MemberInfo member)>();
			void RefreshToggles() {
				foreach(var entry in toggles) {
					entry.toggle.SetValueWithoutNotify(IsMemberVisible(typeEntry, entry.member));
				}
			}

			// ── Toolbar: mode switch / Select All / Deselect All / spacer / Close ──
			var toolbarRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 6, flexWrap = Wrap.Wrap } };
			Button CreateToolbarButton(string text, Action onClick) {
				return new Button(onClick) { text = text };
			}

			// Mode switch: Include All ⇄ Exclude All. The persisted name list is
			// preserved — its meaning (hidden ⇄ visible) flips with the mode.
			var includeBtn = CreateToolbarButton("Include All", null);
			var excludeBtn = CreateToolbarButton("Exclude All", null);
			void SetModeButtons(TypeMemberMode mode) {
				bool include = mode == TypeMemberMode.IncludeAll;
				includeBtn.SetEnabled(!include);
				excludeBtn.SetEnabled(include);
			}
			includeBtn.clicked += () => {
				if(typeEntry.memberMode == TypeMemberMode.IncludeAll) return;
				typeEntry.memberMode = TypeMemberMode.IncludeAll;
				FavoritesManager.Save();
				FavoritesManager.NotifyChanged();
				SetModeButtons(typeEntry.memberMode);
				RefreshToggles();
			};
			excludeBtn.clicked += () => {
				if(typeEntry.memberMode == TypeMemberMode.ExcludeAll) return;
				typeEntry.memberMode = TypeMemberMode.ExcludeAll;
				FavoritesManager.Save();
				FavoritesManager.NotifyChanged();
				SetModeButtons(typeEntry.memberMode);
				RefreshToggles();
			};
			toolbarRow.Add(includeBtn);
			toolbarRow.Add(excludeBtn);
			SetModeButtons(typeEntry.memberMode);

			void SelectAllVisible(bool visible) {
				foreach(var m in members)
					SetMemberVisible(typeEntry, m, visible);
				RefreshToggles();
			}
			toolbarRow.Add(CreateToolbarButton("Select All", () => SelectAllVisible(true)));
			toolbarRow.Add(CreateToolbarButton("Deselect All", () => SelectAllVisible(false)));
			var spacer = new VisualElement { style = { flexGrow = 1 } };
			toolbarRow.Add(spacer);
			toolbarRow.Add(CreateToolbarButton("Close", () => ActionPopupWindow.CloseLast()));
			root.Add(toolbarRow);

			// ── Member list ──
			var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { maxHeight = 360 } };
			foreach(var m in members) {
				bool current = IsMemberVisible(typeEntry, m);
				var row = new VisualElement {
					style = {
						flexDirection = FlexDirection.Row, alignItems = Align.Center,
						marginTop = 1, marginBottom = 1
					}
				};
				var toggle = new Toggle() { value = current };
				MemberInfo captured = m;
				toggle.RegisterValueChangedCallback(evt => SetMemberVisible(typeEntry, captured, evt.newValue));
				row.Add(toggle);

				var icon = new Image { image = GetMemberKindIcon(m) };
				icon.style.width = 16;
				icon.style.height = 16;
				icon.style.flexShrink = 0;
				icon.style.marginRight = 4;
				row.Add(icon);

				var label = new Label(EditorReflectionUtility.GetRichMemberName(m)) { enableRichText = true };
				row.Add(label);

				scroll.Add(row);
				toggles.Add((toggle, m));
			}
			root.Add(scroll);
			return root;
		}

		/// <summary>Kind icon for a reflected member (method/property/field).</summary>
		Texture GetMemberKindIcon(MemberInfo member) {
			if(member is MethodInfo) return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.MethodIcon));
			if(member is PropertyInfo) return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.PropertyIcon));
			if(member is FieldInfo) return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.FieldIcon));
			return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.ExtensionIcon));
		}

		/// <summary>Recycled row for the namespace types TreeView popup.</summary>
		class TypeToggleRow : VisualElement {
			public Toggle toggle;
			public Image icon;
			public Label label;
			public Action<bool> onToggle;
		}

		/// <summary>
		/// Builds the 'Types of' popup content using a virtualized TreeView —
		/// namespaces can expose many types, so rows must be recycled.
		/// </summary>
		VisualElement BuildNamespaceTypesUI(FavoritesDataAsset.Entry nsEntry) {
			var root = new VisualElement();
			root.style.paddingTop = 8;
			root.style.paddingBottom = 8;
			root.style.paddingLeft = 10;
			root.style.paddingRight = 10;
			root.style.minWidth = 400;
			root.focusable = true;
			root.RegisterCallback<KeyDownEvent>(evt => {
				if(evt.keyCode == KeyCode.Escape) {
					ActionPopupWindow.CloseLast();
					evt.StopPropagation();
				}
			});

			root.Add(new Label("Types of " + GetDisplayName(nsEntry)) {
				style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 6 }
			});

			var candidates = FavoritesManager.GetVirtualNamespaceChildren(nsEntry, true);

			// Stable ids derived from the entry id (hash + collision probe), built
			// up-front so bindItem can resolve data by index without a self-reference.
			var usedIDs = new HashSet<int>();
			var items = new List<TreeViewItemData<FavoritesDataAsset.Entry>>(candidates.Count);
			foreach(var cand in candidates) {
				int id = cand.id.GetHashCode();
				while(!usedIDs.Add(id))
					id++;
				items.Add(new TreeViewItemData<FavoritesDataAsset.Entry>(id, cand));
			}

			TreeView typeTree = null;
			void RefreshRows() {
				typeTree?.RefreshItems();
			}

			// ── Toolbar: mode switch / Select All / Deselect All / spacer / Close ──
			var toolbarRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 6, flexWrap = Wrap.Wrap } };
			Button CreateToolbarButton(string text, Action onClick) => new Button(onClick) { text = text };

			var includeBtn = CreateToolbarButton("Include All", null);
			var excludeBtn = CreateToolbarButton("Exclude All", null);
			void SetModeButtons(TypeMemberMode mode) {
				bool include = mode == TypeMemberMode.IncludeAll;
				includeBtn.SetEnabled(!include);
				excludeBtn.SetEnabled(include);
			}
			includeBtn.clicked += () => {
				if(nsEntry.memberMode == TypeMemberMode.IncludeAll) return;
				nsEntry.memberMode = TypeMemberMode.IncludeAll;
				FavoritesManager.Save();
				FavoritesManager.NotifyChanged();
				SetModeButtons(nsEntry.memberMode);
				RefreshRows();
			};
			excludeBtn.clicked += () => {
				if(nsEntry.memberMode == TypeMemberMode.ExcludeAll) return;
				nsEntry.memberMode = TypeMemberMode.ExcludeAll;
				FavoritesManager.Save();
				FavoritesManager.NotifyChanged();
				SetModeButtons(nsEntry.memberMode);
				RefreshRows();
			};

			void SetAllVisible(bool visible) {
				foreach(var c in candidates)
					SetTypeNameVisible(nsEntry, c.displayName, visible);
			}
			toolbarRow.Add(includeBtn);
			toolbarRow.Add(excludeBtn);
			toolbarRow.Add(CreateToolbarButton("Select All", () => { SetAllVisible(true); RefreshRows(); }));
			toolbarRow.Add(CreateToolbarButton("Deselect All", () => { SetAllVisible(false); RefreshRows(); }));
			var toolbarSpacer = new VisualElement { style = { flexGrow = 1 } };
			toolbarRow.Add(toolbarSpacer);
			toolbarRow.Add(CreateToolbarButton("Close", () => ActionPopupWindow.CloseLast()));
			root.Add(toolbarRow);

			// ── Virtualized type list ──
			typeTree = new TreeView(
				makeItem: () => {
					var row = new TypeToggleRow {
						style = { flexDirection = FlexDirection.Row, alignItems = Align.Center }
					};
					row.toggle = new Toggle();
					row.toggle.style.marginRight = 4;
					row.toggle.RegisterValueChangedCallback(evt => row.onToggle?.Invoke(evt.newValue));
					row.Add(row.toggle);
					row.icon = new Image();
					row.icon.style.width = 16;
					row.icon.style.height = 16;
					row.icon.style.flexShrink = 0;
					row.icon.style.marginRight = 4;
					row.Add(row.icon);
					row.label = new Label();
					row.label.style.flexGrow = 1;
					row.Add(row.label);
					return row;
				},
				bindItem: (ve, index) => {
					if(!(ve is TypeToggleRow row))
						return;
					if(index < 0 || index >= items.Count)
						return;
					var entry = items[index].data;
					Type t = null;
					try { t = entry.targetType?.type; } catch { }
					string typeName = entry.displayName ?? t?.Name ?? string.Empty;
					row.toggle.SetValueWithoutNotify(FavoritesManager.IsTypeNameVisibleIn(nsEntry, typeName));
					row.onToggle = v => SetTypeNameVisible(nsEntry, typeName, v);
					row.icon.image = t != null ? uNodeEditorUtility.GetTypeIcon(t) : null;
					row.label.text = t != null ? t.PrettyName() : typeName;
				}
			);
			typeTree.style.height = 340;
			typeTree.style.flexGrow = 1;
			typeTree.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
			typeTree.fixedItemHeight = 20;
			typeTree.selectionType = SelectionType.None;
			typeTree.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
			typeTree.SetRootItems(items);
			typeTree.Rebuild();
			root.Add(typeTree);
			return root;
		}

		static bool IsMemberVisible(FavoritesDataAsset.Entry typeEntry, MemberInfo member) {
			if(typeEntry == null || member == null)
				return false;
			bool inList = typeEntry.excludedMembers != null && typeEntry.excludedMembers.Contains(member.Name);
			return typeEntry.memberMode == TypeMemberMode.ExcludeAll ? inList : !inList;
		}

		/// <summary>
		/// Shows/hides a generated member. The write flips with the type's
		/// memberMode so the persisted name list keeps a single meaning per mode:
		/// hidden names in IncludeAll, visible names in ExcludeAll.
		/// </summary>
		void SetMemberVisible(FavoritesDataAsset.Entry typeEntry, MemberInfo member, bool visible) {
			if(typeEntry == null || member == null)
				return;
			if(typeEntry.excludedMembers == null)
				typeEntry.excludedMembers = new List<string>();
			string name = member.Name;
			bool shouldContain = typeEntry.memberMode == TypeMemberMode.ExcludeAll ? visible : !visible;
			bool changed;
			if(shouldContain) {
				if(!typeEntry.excludedMembers.Contains(name)) {
					typeEntry.excludedMembers.Add(name);
					changed = true;
				}
				else {
					changed = false;
				}
			}
			else {
				changed = typeEntry.excludedMembers.Remove(name);
			}
			if(changed) {
				FavoritesManager.Save();
				FavoritesManager.NotifyChanged();
			}
		}

		void ShowAutoSortMenu() {
			var menu = new GenericMenu();
			menu.AddItem(new GUIContent("Name (A-Z)"), false, () => AutoSort((a, b) => string.Compare(GetDisplayName(a), GetDisplayName(b), StringComparison.OrdinalIgnoreCase)));
			menu.AddItem(new GUIContent("Name (Z-A)"), false, () => AutoSort((a, b) => string.Compare(GetDisplayName(b), GetDisplayName(a), StringComparison.OrdinalIgnoreCase)));
			menu.AddItem(new GUIContent("Kind"), false, () => AutoSort((a, b) => {
				int c = ((int)a.kind).CompareTo((int)b.kind);
				return c != 0 ? c : string.Compare(GetDisplayName(a), GetDisplayName(b), StringComparison.OrdinalIgnoreCase);
			}));
			menu.ShowAsContext();
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
			// Virtual rows: types and deep-search members (with a valid target)
			// can spawn nodes; other virtual rows are read-only.
			if(de.isVirtualChild && !(kind == FavoriteKind.Type || (kind == FavoriteKind.Member && de.entry.rawMember != null)))
				return;
			var graphEditor = uNodeEditor.window?.graphEditor;
			if(graphEditor == null || graphEditor.graphData == null || !graphEditor.graphData.CanAddNode) {
				EditorUtility.DisplayDialog("Create Node", "Open a graph editor first to create nodes.", "OK");
				return;
			}
			CreateNode(de);
		}

		/// <summary>
		/// Payload for dragging an entry onto the graph editor — matches the
		/// NodeBrowser/graph contract under the "uNode" generic key:
		/// System.Type for type items, reflected MemberInfo for member items.
		/// Returns null when the item cannot be dragged to a graph.
		/// </summary>
		object GetGraphDragPayload(FavoritesDataAsset.Entry e) {
			if(e == null)
				return null;
			switch(e.kind) {
				case FavoriteKind.Type: {
					try { return ResolveEntryType(e); }
					catch { return null; }
				}
				case FavoriteKind.Member: {
					MemberInfo mi = null;
					try { mi = FavoritesManager.GetEntryMember(e); } catch { }
					if(mi is Type || mi is FieldInfo || mi is PropertyInfo ||
						mi is MethodInfo || mi is ConstructorInfo)
						return mi;
					return null;
				}
				default:
					return null;
			}
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
				var mi = FavoritesManager.GetEntryMember(e);
				if(mi != null) {
					// Wrap the raw MemberInfo into MemberData only at use time —
					// open generics resolve fine in-memory for node creation.
					GraphEditor.CreateNodeProcessor(MemberData.CreateFromMember(mi), graphEditor.graphData, pos);
				}
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
