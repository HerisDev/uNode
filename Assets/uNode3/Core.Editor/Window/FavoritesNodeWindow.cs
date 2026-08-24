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
		private Label detailNameLabel;
		private Label detailTypeLabel;
		private ScrollView detailScroll;
		private VisualElement detailArea;
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

		// ── Background warming ──
		bool _warming;

		class DisplayEntry {
			public int treeID;
			public FavoritesDataAsset.FavoriteEntry entry;
			public bool isVirtualChild;
			public bool isPlaceholder;  // lazily-warmed stand-in row ("…") keeping the foldout arrow
			public float searchScore;   // relevance score (search mode only)
			public string searchPath;   // breadcrumb path shown under the title in search mode
		}

		/// <summary>Transient marker entry backing a placeholder row.</summary>
		static FavoritesDataAsset.FavoriteEntry CreateLoadingMarker(FavoritesDataAsset.FavoriteEntry owner) {
			return new FavoritesDataAsset.FavoriteEntry {
				id = "[loading]:" + owner.id,
				kind = FavoriteKind.Member,
				isVirtual = true,
				displayName = "…",
			};
		}

		/// <summary>
		/// Main-thread snapshot of one entry: everything the search needs.
		/// Only plain fields — never Unity serialized/native APIs.
		/// </summary>
		class SearchItem {
			public FavoritesDataAsset.FavoriteEntry entry;
			public FavoritesDataAsset.FavoriteEntry parent; // owning container (null = root)
			public FavoriteKind kind;
			public bool isVirtual;
			public string displayName;      // plain text used for scoring/paths
			public string shortTypeName;    // last segment of typeName (fallback scoring)
			public Type resolvedRuntimeType; // captured on the UI thread for deep member search
		}

		/// <summary>Immutable payload produced by a search run.</summary>
		class SearchResult {
			public List<TreeViewItemData<DisplayEntry>> items = new List<TreeViewItemData<DisplayEntry>>();
			public Dictionary<int, DisplayEntry> treeIDMap = new Dictionary<int, DisplayEntry>();
			public List<VisibleRow> rows = new List<VisibleRow>();
		}

		/// <summary>Stable TreeView id derived from the entry id, so expansion state
		/// survives rebuilds and sessions (sequential ids would reshuffle).</summary>
		static int GetStableTreeID(FavoritesDataAsset.FavoriteEntry e) {
			int id = e.id?.GetHashCode() ?? 0;
			if(id == 0)
				id = 1; // 0 is not a valid TreeView id
			return id;
		}

		/// <summary>Instance variant resolving hash collisions against the current map.</summary>
		int AssignStableTreeID(FavoritesDataAsset.FavoriteEntry e, Dictionary<int, DisplayEntry> targetMap) {
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
		//  Lifecycle / Intent / Category
		// ═══════════════════════════════════════

		const string kLastCategoryKey = "uNode.FavoritesWindow.Category";

		/// <summary>Parent for the next item added via a menu action. Null = category root.</summary>
		FavoritesDataAsset.FavoriteEntry pendingParent;

		private void OnEnable() {
			window = this;
			BuildNodeMenuCache();
			FavoritesManager.onChanged += OnFavoritesChanged;
			// Raw reflection caches depend only on loaded assemblies → reset per session.
			FavoritesManager.ClearReflectionCache();
			currentCategoryID = RestoreLastCategory();
			BuildUI();
			ReloadTreeView();
		}

		private void OnDisable() {
			if(window == this)
				window = null;
			try { SnapshotExpandedState(); } catch { }
			FavoritesManager.onChanged -= OnFavoritesChanged;
			rootVisualElement?.UnregisterCallback<KeyDownEvent>(OnWindowKeyDown);
			CancelPendingSearch();
		}

		void SetPendingParent(FavoritesDataAsset.FavoriteEntry owner) {
			pendingParent = owner;
		}

		/// <summary>Consumes the pending parent or falls back to selection if valid.</summary>
		FavoritesDataAsset.FavoriteEntry ResolveIntentParent(Func<FavoritesDataAsset.FavoriteEntry, bool> validParentKinds) {
			var p = pendingParent;
			pendingParent = null;
			if(p == null && selectedEntry != null && selectedEntry.entry != null && validParentKinds(selectedEntry.entry))
				return selectedEntry.entry;
			return p;
		}

		FavoritesDataAsset.FavoriteCategory CurrentCategory {
			get { return FavoritesManager.GetCategories().FirstOrDefault(c => c.id == currentCategoryID); }
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

		void ShowAddMenu() {
			var menu = new GenericMenu();
			var pos = Event.current.mousePosition;
			SetPendingParent(null); // toolbar adds land at the category root
			menu.AddItem(new GUIContent("Folder"), false, () => CreateNewFolder(pos));
			menu.AddItem(new GUIContent("Namespace"), false, () => AddNamespaceFavorite(pos));
			menu.AddItem(new GUIContent("Type or Member"), false, () => OpenItemSelector(pos));
			menu.AddItem(new GUIContent("Node"), false, () => OpenAddNodePopup(pos));
			menu.AddSeparator("");
			menu.AddItem(new GUIContent("Category"), false, () => CreateNewCategory(pos));
			menu.ShowAsContext();
		}

		// ═══════════════════════════════════════
		//  Tree Data
		// ═══════════════════════════════════════

		class VisibleRow {
			public FavoritesDataAsset.FavoriteEntry entry;
			public int depth;          // 0 = root level
			public FavoritesDataAsset.FavoriteEntry parent; // owning entry (null = root)
			public bool inNamespace;   // inside a namespace expansion (fixed order)
		}

		private readonly List<VisibleRow> visibleRows = new List<VisibleRow>();

		private List<TreeViewItemData<DisplayEntry>> BuildTreeData() {
			treeIDMap.Clear();
			visibleRows.Clear();

			var category = CurrentCategory;
			if(category == null || category.roots.Count == 0)
				return new List<TreeViewItemData<DisplayEntry>>();

			if(!string.IsNullOrEmpty(searchString))
				return RunSearch(category);

			List<TreeViewItemData<DisplayEntry>> BuildChildren(
				FavoritesDataAsset.FavoriteEntry parent,
				List<FavoritesDataAsset.FavoriteEntry> entries, int depth, bool inNamespace) {
				var result = new List<TreeViewItemData<DisplayEntry>>();

				for(int i = 0; i < entries.Count; i++) {
					var entry = entries[i];
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
							parent = parent,
							inNamespace = inNamespace,
						});
					}

					var childItems = entry.CanHaveChilds
						? BuildChildren(entry, entry.children, depth + 1, inNamespace)
						: new List<TreeViewItemData<DisplayEntry>>();

					AppendGeneratedChildren(entry, myID, childItems, inNamespace);

					result.Add(new TreeViewItemData<DisplayEntry>(myID, de, childItems));
				}
				return result;
			}

			return BuildChildren(null, category.roots, 0, false);
		}

		/// <summary>
		/// Appends the generated virtual children of namespace/type entries.
		/// Reflection reads the shared cache (instant once warmed); cold owners show
		/// a temporary "…" placeholder that the background warmer swaps out.
		/// </summary>
		void AppendGeneratedChildren(FavoritesDataAsset.FavoriteEntry entry, int myID,
			List<TreeViewItemData<DisplayEntry>> childItems, bool inNamespace) {
			bool isVirtualSource =
				(entry.kind == FavoriteKind.Namespace ||
				 (entry.kind == FavoriteKind.Type && !entry.isVirtual)) && !inNamespace;
			if(!isVirtualSource)
				return;

			if(!FavoritesManager.HasRawCache(entry)) {
				var marker = CreateLoadingMarker(entry);
				int pID = AssignStableTreeID(marker, treeIDMap);
				var ph = new DisplayEntry {
					treeID = pID,
					entry = marker,
					isVirtualChild = true,
					isPlaceholder = true,
				};
				treeIDMap[pID] = ph;
				childItems.Add(new TreeViewItemData<DisplayEntry>(pID, ph));
				return;
			}

			var generated = entry.kind == FavoriteKind.Namespace
				? FavoritesManager.GetVirtualNamespaceChildren(entry)
				: FavoritesManager.GetVirtualTypeMembers(entry);
			foreach(var vc in generated) {
				int vID = AssignStableTreeID(vc, treeIDMap);
				var vde = new DisplayEntry {
					treeID = vID,
					entry = vc,
					isVirtualChild = true,
				};
				treeIDMap[vID] = vde;
				childItems.Add(new TreeViewItemData<DisplayEntry>(vID, vde));
			}
		}

		float ScoreSearchTarget(FavoritesDataAsset.FavoriteEntry e) {
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
		//  Search
		// ═══════════════════════════════════════
		// One implementation drives both paths: typing runs it on a worker thread
		// (cancellable, progress-reporting); other callers invoke it synchronously.

		void OnSearchChanged(string value) {
			searchString = value;
			CancelPendingSearch();
			if(string.IsNullOrEmpty(value)) {
				HideSearchProgress();
				ReloadTreeView(); // instant restore of the hierarchy
				return;
			}
			int generation = ++_searchGeneration;
			_searchCts = new CancellationTokenSource();
			var token = _searchCts.Token;

			ShowSearchProgress();
			var progress = new Progress<float>(v => UpdateSearchProgress(v));

			var snapshot = BuildSearchSnapshot();

			Task.Factory.StartNew(
					state => ComputeSearch(snapshot, value, token, progress),
					null, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
				.ContinueWith(t => {
					if(t.IsFaulted) {
						var ex = t.Exception?.InnerException ?? t.Exception;
						if(!(ex is OperationCanceledException)) {
							Debug.LogException(ex);
						}
						HideSearchProgress();
						return;
					}
					if(t.Status != TaskStatus.RanToCompletion)
						return; // canceled — a newer search superseded it
					InstallSearchResult(t.Result);
					HideSearchProgress();
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
			var category = CurrentCategory;
			if(category == null)
				return snapshot;

			void Walk(FavoritesDataAsset.FavoriteEntry e, FavoritesDataAsset.FavoriteEntry parent) {
				string displayName;
				if(e.kind == FavoriteKind.Member) {
					displayName = e.memberName ?? "(missing)";
				} else {
					try { displayName = GetDisplayName(e); }
					catch { displayName = e.displayName ?? e.id; }
				}
				snapshot.Add(new SearchItem {
					entry = e,
					parent = parent,
					kind = e.kind,
					isVirtual = false,
					displayName = displayName,
					shortTypeName = e.typeName?.Split('.').Last(),
					resolvedRuntimeType = e.kind == FavoriteKind.Type ? ResolveEntryType(e) : null,
				});
				if(e.CanHaveChilds) {
					foreach(var c in e.children)
						Walk(c, e);
				}
			}

			foreach(var root in category.roots)
				Walk(root, null);
			return snapshot;
		}

		/// <summary>
		/// Builds the flat relevance-ranked result from the snapshot (mirrors
		/// ItemSelector's SearchKind.Relevant). Pure CPU work — safe off-thread.
		/// Namespace visibility filters its virtual types; favorited-type modes
		/// filter their deep-searched members.
		/// </summary>
		SearchResult ComputeSearch(List<SearchItem> snapshot, string query, CancellationToken token, IProgress<float> progress) {
			var result = new SearchResult();
			progress?.Report(0f);

			int AssignID(FavoritesDataAsset.FavoriteEntry e) {
				int id = GetStableTreeID(e);
				while(result.treeIDMap.ContainsKey(id))
					id++;
				return id;
			}

			var childrenByParent = new Dictionary<FavoritesDataAsset.FavoriteEntry, List<SearchItem>>();
			var roots = new List<SearchItem>();
			foreach(var item in snapshot) {
				if(item.parent == null) {
					roots.Add(item);
				}
				else if(!childrenByParent.TryGetValue(item.parent, out var bucket)) {
					childrenByParent[item.parent] = new List<SearchItem> { item };
				}
				else {
					bucket.Add(item);
				}
			}

			IEnumerable<SearchItem> ChildrenOf(FavoritesDataAsset.FavoriteEntry parent) =>
				parent != null && childrenByParent.TryGetValue(parent, out var list)
					? list
					: Enumerable.Empty<SearchItem>();

			static string JoinPath(string parentPath, string segment) =>
				string.IsNullOrEmpty(parentPath) ? segment : parentPath + " > " + segment;

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

			/// <summary>Replicates TreeSearcher.IsMatchSearch for members: multi-part
			/// queries require all trailing parts to match, with the per-part bonus.</summary>
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

			var results = new List<(DisplayEntry view, FavoritesDataAsset.FavoriteEntry source)>();
			// Dedupes member results across sources (generated vs deep search).
			var seenMemberKeys = new HashSet<string>();

			void AddResult(SearchItem item, float score, string path) {
				if(item.kind == FavoriteKind.Member) {
					MemberInfo mi = null;
					try { mi = FavoritesManager.GetEntryMember(item.entry); } catch { }
					if(mi == null)
						return;
					string key = (mi.DeclaringType != null ? mi.DeclaringType.FullName : "") +
						"::" + mi.Name + "::" + mi.MetadataToken;
					if(!seenMemberKeys.Add(key))
						return; // duplicate member
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
					parent = null,
					inNamespace = false,
				});
				results.Add((de, item.entry));
			}

			/// <summary>
			/// Deep member search: surfaces matching members of the given type.
			/// When ownerType is a favorited entry its mode-aware visibility applies;
			/// null owner (namespace virtual types) searches everything.
			/// </summary>
			void CollectTypeMembers(Type type, string typePath, FavoritesDataAsset.FavoriteEntry ownerType) {
				if(!deepSearch || type == null || type.IsEnum)
					return;
				token.ThrowIfCancellationRequested();
				MemberInfo[] members;
				try { members = FavoritesManager.GetMembersRaw(type); }
				catch { return; }
				string declName = type.FullName ?? type.Name;
				foreach(var m in members) {
					token.ThrowIfCancellationRequested();
					if(m is EventInfo) continue;
					if(m is ConstructorInfo ctor && ctor.GetParameters().Length > 6) continue;
					if(FavoritesManager.IsAccessorMethod(m)) continue;
					if(!FavoritesManager.IsMemberVisibleIn(ownerType, m))
						continue;
					float score = ScoreMemberName(m.Name);
					if(score < 0f) continue;
					var entry = new FavoritesDataAsset.FavoriteEntry {
						kind = FavoriteKind.Member,
						rawMember = m,
						isVirtual = true,
						displayName = m.Name,
						// AddResult dedupes via the resolved MemberInfo, so the id
						// only needs to be unique/stable for this search session.
						id = "[deep]:" + declName + "::" + m.Name + "::" + m.MetadataToken,
					};
					AddResult(new SearchItem {
						entry = entry,
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
						foreach(var c in ChildrenOf(item.entry))
							CollectEntry(c, folderPath);
						break;
					case FavoriteKind.Namespace: {
						var nsPath = JoinPath(parentPath, item.displayName);
						foreach(var c in ChildrenOf(item.entry))
							CollectEntry(c, nsPath);
						// Virtual namespace types are candidates; wrapper entries are
						// built only for MATCHES to keep per-keystroke allocations tiny.
						foreach(var t in FavoritesManager.GetNamespaceTypesRaw(item.displayName)) {
							token.ThrowIfCancellationRequested();
							float score = -1f;
							try {
								score = Math.Max(
									ItemSelector.GetRelevanceScore(query, t.Name),
									ItemSelector.GetRelevanceScore(query, t.FullName));
							} catch { }
							if(score >= 0f) {
								var vc = new FavoritesDataAsset.FavoriteEntry {
									id = "[ns]:" + t.AssemblyQualifiedName,
									kind = FavoriteKind.Type,
									targetType = new SerializedType(t),
									isVirtual = true,
									displayName = t.Name,
									ownerEntry = item.entry,
								};
								AddResult(new SearchItem {
									entry = vc,
									kind = FavoriteKind.Type,
									isVirtual = true,
									displayName = t.Name,
								}, score, nsPath);
							}
							// Deep member search inside namespace types too
							// (null owner → visibility rules don't apply to them).
							CollectTypeMembers(t, JoinPath(nsPath, t.Name), null);
						}
						break;
					}
					default:
						float s = Score(item);
						if(s >= 0f)
							AddResult(item, s, parentPath);
						// Deep member search inside favorited types — members are
						// generated from the type, so this is their only source.
						if(item.kind == FavoriteKind.Type && !item.isVirtual) {
							var typePath = JoinPath(parentPath, item.displayName);
							foreach(var c in ChildrenOf(item.entry))
								CollectEntry(c, typePath);
							CollectTypeMembers(item.resolvedRuntimeType, typePath, item.entry);
						}
						break;
				}
			}

			int totalRoots = Math.Max(1, roots.Count);
			int processedRoots = 0;
			foreach(var root in roots) {
				token.ThrowIfCancellationRequested();
				CollectEntry(root, null);
				progress?.Report(Math.Min(processedRoots / (float)totalRoots, 0.9f));
				processedRoots++;
			}

			results.Sort((a, b) => {
				int c = b.view.searchScore.CompareTo(a.view.searchScore);
				if(c != 0) return c;
				return string.Compare(
					GetPlainTitle(a.source), GetPlainTitle(b.source),
					StringComparison.OrdinalIgnoreCase);
			});

			result.items.AddRange(results.Select(r => new TreeViewItemData<DisplayEntry>(r.view.treeID, r.view)));
			return result;
		}

		/// <summary>
		/// Synchronous search (used when ReloadTreeView fires while a query is
		/// active): runs the shared core on the UI thread and installs the result.
		/// </summary>
		List<TreeViewItemData<DisplayEntry>> RunSearch(FavoritesDataAsset.FavoriteCategory category) {
			var result = ComputeSearch(BuildSearchSnapshot(), searchString, CancellationToken.None, null);
			treeIDMap = result.treeIDMap;
			visibleRows.Clear();
			visibleRows.AddRange(result.rows);
			return result.items;
		}

		/// <summary>Installs a search result into the live view (ids/rows/items).</summary>
		void InstallSearchResult(SearchResult result) {
			treeIDMap = result.treeIDMap;
			visibleRows.Clear();
			visibleRows.AddRange(result.rows);
			entryTreeView.fixedItemHeight = string.IsNullOrEmpty(searchString) ? 20 : 40;
			entryTreeView.SetRootItems(result.items);
			entryTreeView.Rebuild();
			UpdateStatusLabel();
		}

		// ═══════════════════════════════════════
		//  Slot Resolution / Drag Validation
		// ═══════════════════════════════════════

		/// <summary>Resolve the parent entry + sibling index for an insertion slot.
		/// Returns false when the slot is invalid (inside a namespace expansion).</summary>
		bool ResolveSlot(int insertIndex, out FavoritesDataAsset.FavoriteEntry parent, out int siblingIndex, out int indentDepth) {
			parent = null;
			siblingIndex = insertIndex;
			indentDepth = 0;

			if(visibleRows.Count == 0)
				return insertIndex <= 0;

			if(insertIndex <= 0)
				return true; // root

			// Past the last row: below a folder = INTO the folder (append).
			if(insertIndex >= visibleRows.Count) {
				var last = visibleRows[visibleRows.Count - 1];
				if(last.entry.kind == FavoriteKind.Folder) {
					parent = last.entry;
					siblingIndex = -1;
					indentDepth = last.depth + 1;
				} else {
					parent = last.parent;
					indentDepth = last.depth;
					siblingIndex = CountSiblingsBefore(visibleRows.Count, parent);
				}
				return true;
			}

			var anchor = visibleRows[insertIndex - 1];

			// Inside a fixed namespace expansion: reject.
			if(anchor.inNamespace && anchor.entry.kind == FavoriteKind.Type)
				return false;

			int nextDepth = visibleRows[insertIndex].depth;

			// Anchor folder + deeper row below → drop INTO folder.
			if(anchor.entry.kind == FavoriteKind.Folder && anchor.depth < nextDepth) {
				parent = anchor.entry;
				siblingIndex = CountSiblingsBefore(insertIndex, anchor.entry);
				indentDepth = anchor.depth + 1;
				return true;
			}

			parent = anchor.parent;
			indentDepth = anchor.depth;
			siblingIndex = CountSiblingsBefore(insertIndex, parent);
			return true;
		}

		int CountSiblingsBefore(int insertIndex, FavoritesDataAsset.FavoriteEntry parent) {
			int count = 0;
			for(int i = 0; i < insertIndex; i++) {
				if(ReferenceEquals(visibleRows[i].parent, parent))
					count++;
			}
			return count;
		}

		/// <summary>Validate whether a move is allowed (no cycles, folders only).</summary>
		bool CanMove(FavoritesDataAsset.FavoriteEntry moved, FavoritesDataAsset.FavoriteEntry parent) {
			if(moved == null || moved.isVirtual) return false;
			// Members are bound to their type header and can't be re-parented.
			if(moved.kind == FavoriteKind.Member) return false;
			if(parent == null) return true;
			if(parent.kind == FavoriteKind.Namespace) return false;
			if(!parent.CanBeDropTarget) return false;
			return !FavoritesManager.IsDescendantOf(parent, moved); // no cycles
		}

		// ═══════════════════════════════════════
		//  Display Helpers
		// ═══════════════════════════════════════

		string GetDisplayName(FavoritesDataAsset.FavoriteEntry e) {
			Type typeForDisplay = ResolveEntryType(e);

			switch(e.kind) {
				case FavoriteKind.Folder: return e.displayName ?? "(Folder)";
				case FavoriteKind.Namespace: return e.displayName ?? "(Namespace)";
				case FavoriteKind.Member:
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

		/// <summary>
		/// Formats a member label like ItemSelector does: pretty signature,
		/// extension-method formatting, colored text when the preference enables it.
		/// </summary>
		string GetMemberLabel(FavoritesDataAsset.FavoriteEntry e) {
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
		string GetPlainTitle(FavoritesDataAsset.FavoriteEntry e) {
			try { return GetDisplayName(e); }
			catch { return e.displayName ?? e.id; }
		}

		// ItemSelector's highlight blue at 50% alpha.
		const string kHighlightColorTag = "#3E7DD880";

		/// <summary>
		/// Wraps query matches in rich-text mark tags so TextCore renders a highlight
		/// background behind them — same spans ItemSelector highlights. Input must be
		/// markup-free so character offsets stay valid.
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

		Texture GetIcon(FavoritesDataAsset.FavoriteEntry e) {
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

		/// <summary>Second icon for member rows: value/return type icon.</summary>
		Texture GetMemberValueTypeIcon(FavoritesDataAsset.FavoriteEntry e) {
			if(e.kind != FavoriteKind.Member)
				return null;
			var mi = FavoritesManager.GetEntryMember(e);
			Type t = mi is MethodInfo method ? method.ReturnType
				: mi is PropertyInfo prop ? prop.PropertyType
				: mi is FieldInfo field ? field.FieldType
				: null;
			return t != null ? uNodeEditorUtility.GetTypeIcon(t) : null;
		}

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

			BuildToolbar(root);
			BuildSearchArea(root);
			BuildTreeArea(root);
			BuildDetailArea(root);

			rootVisualElement.Add(root);
			rootVisualElement.RegisterCallback<KeyDownEvent>(OnWindowKeyDown);
			UpdateCategoryDropdown();
			ReloadTreeView();
		}

		void BuildToolbar(VisualElement root) {
			toolbar = new Toolbar();

			categoryDropdown = new DropdownField("Category", new List<string>(), 0) { style = { flexGrow = 1 } };
			categoryDropdown.RegisterValueChangedCallback(OnCategoryChanged);
			toolbar.Add(categoryDropdown);

			toolbar.Add(new ToolbarSpacer());

			toolbar.Add(new ToolbarButton(ShowAddMenu) { text = "+ Add", tooltip = "Add Item" });

			addMembersButton = new ToolbarButton(() => OpenAddMembersPopup(Event.current.mousePosition)) { text = "+ Members", tooltip = "Add Members (type) / Types (namespace)" };
			addMembersButton.SetEnabled(false);
			toolbar.Add(addMembersButton);

			toolbar.Add(new ToolbarSpacer { flex = true });

			toolbar.Add(new ToolbarButton(() => ShowAutoSortMenu()) { text = "Sort" });

			root.Add(toolbar);
		}

		void BuildSearchArea(VisualElement root) {
			searchField = new TextField() { name = "search", tooltip = "Search" };
			searchField.RegisterValueChangedCallback(evt => OnSearchChanged(evt.newValue));
			searchField.style.marginLeft = 4;
			searchField.style.marginRight = 4;
			searchField.style.marginTop = 2;
			searchField.style.marginBottom = 2;
			root.Add(searchField);

			searchProgressBar = new ProgressBar { title = "Searching…" };
			searchProgressBar.style.height = 24;
			searchProgressBar.style.marginLeft = 4;
			searchProgressBar.style.marginRight = 4;
			searchProgressBar.style.display = DisplayStyle.None;
			root.Add(searchProgressBar);
		}

		void BuildTreeArea(VisualElement root) {
			entryTreeView = new TreeView(
				makeItem: () => CreateRowElement(),
				bindItem: (ve, index) => BindRow(ve, index)
			);
			entryTreeView.selectionType = SelectionType.None;
			entryTreeView.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
			entryTreeView.style.flexGrow = 1;
			entryTreeView.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
			entryTreeView.fixedItemHeight = 20;

			var dragger = new TreeViewDragger(entryTreeView);
			dragger.dragAndDropController = new FavoritesReorderController(entryTreeView, OnEntryDropped);

			// Blank-area context menu (row menus stop propagation so this doesn't double up).
			entryTreeView.AddManipulator(new ContextualMenuManipulator(evt => {
				evt.menu.AppendAction("New Folder", a => { SetPendingParent(null); CreateNewFolder(a.eventInfo.mousePosition); });
				evt.menu.AppendAction("Add Namespace", a => { SetPendingParent(null); AddNamespaceFavorite(a.eventInfo.mousePosition); });
				evt.menu.AppendAction("Add Type / Member", a => { SetPendingParent(null); OpenItemSelector(a.eventInfo.mousePosition); });
				evt.menu.AppendAction("Add Node...", a => { SetPendingParent(null); OpenAddNodePopup(a.eventInfo.mousePosition); });
			}));
			// Last-chance snapshot when the tree detaches (window closing).
			entryTreeView.RegisterCallback<DetachFromPanelEvent>(_ => SnapshotExpandedState());
			root.Add(entryTreeView);
		}

		void BuildDetailArea(VisualElement root) {
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
		}

		/// <summary>Recycled row template: [type-icon][text-column(title+path)].</summary>
		PanelElement<FavoritesDataAsset.FavoriteEntry> CreateRowElement() {
			var item = new PanelElement<FavoritesDataAsset.FavoriteEntry>();
			item.style.alignItems = Align.Center;
			// Stretch to fill the recycled item so hover/drag hit-testing is full height.
			item.style.flexGrow = 1;
			item.style.height = Length.Percent(100);

			// Secondary icon slot (member rows): value/return type icon.
			var typeIcon = new Image { name = "type-icon" };
			typeIcon.pickingMode = PickingMode.Ignore;
			typeIcon.style.width = 16;
			typeIcon.style.height = 16;
			typeIcon.style.flexShrink = 0;
			typeIcon.style.display = DisplayStyle.None;
			item.Add(typeIcon);

			// Two-line layout: title on top, breadcrumb path below (search only).
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
		}

		void BindRow(VisualElement ve, int index) {
			if(!(ve is PanelElement<FavoritesDataAsset.FavoriteEntry> item))
				return;
			item.index = index;
			var de = entryTreeView.GetItemDataForIndex<DisplayEntry>(index);
			if(de == null) return;
			item.value = de.entry;
			item.userData = de;

			bool hasSearch = !string.IsNullOrEmpty(searchString);

			if(de.isPlaceholder) {
				BindPlaceholderRow(item, hasSearch);
				return;
			}
			item.label.style.opacity = 1f;

			BindRowInteractions(item, de, hasSearch);
			BindSelection(item, de);
			BindRowVisuals(item, de, hasSearch);
		}

		/// <summary>Lazily-warmed placeholder row: renders as "…" and is inert.</summary>
		void BindPlaceholderRow(PanelElement<FavoritesDataAsset.FavoriteEntry> item, bool hasSearch) {
			item.CanDragFunc = () => false;
			item.CanDragInsideParentFunc = () => false;
			item.CanHaveChildsFunc = () => false;
			item.GetDragGenericData = () => null;
			item.onClick = null;
			item.style.backgroundColor = Color.clear;
			item.label.text = "…";
			item.label.style.opacity = 0.5f;
			item.ShowIcon(null);
			var typeIcon = item.Q<Image>("type-icon");
			if(typeIcon != null) typeIcon.style.display = DisplayStyle.None;
			var pathLabel = item.Q<Label>("path-label");
			if(pathLabel != null) pathLabel.style.display = DisplayStyle.None;
		}

		void BindRowInteractions(PanelElement<FavoritesDataAsset.FavoriteEntry> item,
			DisplayEntry de, bool hasSearch) {
			// Type & Member rows are graph-draggable ("uNode" contract), including
			// generated virtual rows. Reordering is limited to persisted non-virtual
			// rows and disabled while searching; Move rejects virtual entries anyway.
			bool isVirtual = de.isVirtualChild || de.entry.isVirtual;
			var graphPayload = GetGraphDragPayload(de.entry);
			bool canReorder = !isVirtual && !hasSearch;
			bool canDrag = graphPayload != null || canReorder;
			item.CanDragFunc = () => canDrag;
			item.CanDragInsideParentFunc = () => canReorder;
			item.CanHaveChildsFunc = () => de.entry.kind == FavoriteKind.Folder && !de.entry.isVirtual && !hasSearch;

			item.GetDragGenericData = () => {
				if(!canDrag)
					return null;
				var data = new Dictionary<string, object> {
					{ "favoriteEntry", de.entry },
				};
				if(graphPayload != null)
					data["uNode"] = graphPayload;
				return data;
			};

			var captured = de;
			item.onClick = (evt) => {
				selectedEntry = captured;
				UpdateDetailPanel();
				UpdateAddMembersButton();
				entryTreeView.RefreshItems();
				if(evt is MouseUpEvent mouseEvt && mouseEvt.clickCount >= 2) {
					TryCreateNode(captured);
				}
			};
		}

		void BindSelection(PanelElement<FavoritesDataAsset.FavoriteEntry> item, DisplayEntry de) {
			bool isSelected = selectedEntry != null && selectedEntry.entry != null
				&& de.entry != null
				&& de.entry.id == selectedEntry.entry.id
				&& de.isVirtualChild == selectedEntry.isVirtualChild;
			item.style.backgroundColor = isSelected ? new Color(0.24f, 0.49f, 0.91f, 0.35f) : Color.clear;
		}

		void BindRowVisuals(PanelElement<FavoritesDataAsset.FavoriteEntry> item,
			DisplayEntry de, bool hasSearch) {
			// In search mode the title is markup-free so highlight spans stay valid.
			item.label.text = hasSearch
				? ApplySearchHighlight(GetPlainTitle(de.entry))
				: GetDisplayName(de.entry);
			item.ShowIcon(GetIcon(de.entry));
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

			var pathLabel = item.Q<Label>("path-label");
			if(pathLabel != null) {
				bool showPath = hasSearch && !string.IsNullOrEmpty(de.searchPath);
				pathLabel.text = showPath ? ApplySearchHighlight(de.searchPath) : string.Empty;
				pathLabel.style.display = showPath ? DisplayStyle.Flex : DisplayStyle.None;
			}
		}

		void OnEntryDropped(FavoritesDataAsset.FavoriteEntry movedEntry, int insertIndex, bool overItem) {
			if(movedEntry == null) return;
			// No reordering while searching (flat relevance view).
			if(!string.IsNullOrEmpty(searchString)) return;
			FavoritesDataAsset.FavoriteEntry parent;
			int siblingIndex;
			if(overItem) {
				// Dropping ONTO a row nests inside it — only folders accept children.
				var target = entryTreeView.GetItemDataForIndex<DisplayEntry>(insertIndex);
				if(target == null || target.isVirtualChild || target.entry.isVirtual) return;
				if(target.entry.kind != FavoriteKind.Folder) return;
				parent = target.entry;
				siblingIndex = -1; // append as last child
			}
			else {
				// Dropping BETWEEN rows resolves the slot like GraphPanel does.
				if(!ResolveSlot(insertIndex, out var resolvedParent, out var resolvedSibling, out _)) return;
				parent = resolvedParent;
				siblingIndex = resolvedSibling;
			}
			if(!CanMove(movedEntry, parent)) return;
			int sibling = siblingIndex < 0 ? -1 : siblingIndex; // -1 = append
			FavoritesManager.Move(movedEntry, parent, sibling, CurrentCategory);
			ReloadTreeView();
		}

		// ═══════════════════════════════════════
		//  TreeView Rebuild / Expansion / Warming
		// ═══════════════════════════════════════

		void ReloadTreeView() {
			// Data changed — any in-flight background result is now stale.
			CancelPendingSearch();
			HideSearchProgress();
			SnapshotExpandedState(); // capture expansion before the id map is rebuilt
			var items = BuildTreeData();
			entryTreeView.fixedItemHeight = string.IsNullOrEmpty(searchString) ? 20 : 40;
			entryTreeView.SetRootItems(items);
			entryTreeView.Rebuild();
			ApplyExpandedState();
			UpdateStatusLabel();
			KickWarmUp(); // cold caches → warm off-thread, placeholders swap afterwards
		}

		/// <summary>
		/// Warms raw reflection caches for expandable entries of the current
		/// category that aren't cached yet (off-thread), then reloads once done.
		/// </summary>
		void KickWarmUp() {
			if(_warming || CurrentCategory == null)
				return;
			var targets = FavoritesManager.Flatten(CurrentCategory)
				.Where(e => (e.kind == FavoriteKind.Namespace ||
							(e.kind == FavoriteKind.Type && !e.isVirtual)) &&
						   !FavoritesManager.HasRawCache(e))
				.ToList();
			if(targets.Count == 0)
				return;
			_warming = true;
			Task.Run(() => {
				foreach(var t in targets) {
					FavoritesManager.WarmReflectionCache(t);
				}
			}).ContinueWith(_ => {
				_warming = false;
				if(this != null && entryTreeView != null)
					ReloadTreeView();
			}, TaskScheduler.FromCurrentSynchronizationContext());
		}

		/// <summary>Persists current expansion states. Runs BEFORE the id map changes.</summary>
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

		/// <summary>Folders, namespaces, and type items can expand.</summary>
		static bool IsExpandableEntry(FavoritesDataAsset.FavoriteEntry e) {
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
		void ApplyExpandedState() {
			if(entryTreeView == null || entryTreeView.panel == null)
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
			var cat = CurrentCategory;
			int totalCount = cat != null ? FavoritesManager.Flatten(cat).Count() : 0;
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

		// ═══════════════════════════════════════
		//  Row Context Menu
		// ═══════════════════════════════════════

		void BuildRowContextMenu(ContextualMenuPopulateEvent evt, DisplayEntry de) {
			if(de == null || de.entry == null || de.isPlaceholder)
				return;
			var e = de.entry;

			if(de.isVirtualChild || e.isVirtual) {
				AppendVirtualRowMenu(evt, de, e);
				evt.StopPropagation();
				return;
			}

			switch(e.kind) {
				case FavoriteKind.Folder:
					evt.menu.AppendAction("New Folder", a => { selectedEntry = de; SetPendingParent(de.entry); UpdateDetailPanel(); CreateNewFolder(a.eventInfo.mousePosition); });
					evt.menu.AppendAction("Rename", _ => { selectedEntry = de; RenameSelectedFolder(); });
					evt.menu.AppendSeparator();
					evt.menu.AppendAction("Add Namespace", a => { selectedEntry = de; SetPendingParent(de.entry); UpdateDetailPanel(); AddNamespaceFavorite(a.eventInfo.mousePosition); });
					evt.menu.AppendAction("Add Type / Member", a => { selectedEntry = de; SetPendingParent(de.entry); UpdateDetailPanel(); UpdateAddMembersButton(); OpenItemSelector(a.eventInfo.mousePosition); });
					evt.menu.AppendAction("Add Node...", a => { selectedEntry = de; SetPendingParent(de.entry); UpdateDetailPanel(); OpenAddNodePopup(a.eventInfo.mousePosition); });
					break;
				case FavoriteKind.Type:
					evt.menu.AppendAction("Create Node", _ => TryCreateNode(de));
					evt.menu.AppendAction("Add Members...", a => { selectedEntry = de; UpdateDetailPanel(); UpdateAddMembersButton(); OpenAddMembersPopup(a.eventInfo.mousePosition); });
					break;
				case FavoriteKind.Namespace:
					evt.menu.AppendAction("Add Types...", a => { selectedEntry = de; UpdateDetailPanel(); UpdateAddMembersButton(); OpenAddMembersPopup(a.eventInfo.mousePosition); });
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

		/// <summary>Menu for generated (virtual) rows: create-node + remove-to-hide.</summary>
		void AppendVirtualRowMenu(ContextualMenuPopulateEvent evt, DisplayEntry de,
			FavoritesDataAsset.FavoriteEntry e) {
			if(e.kind == FavoriteKind.Type) {
				evt.menu.AppendAction("Create Node", _ => TryCreateNode(de));
				// Virtual types owned by a namespace can be hidden (persisted there).
				var nsOwner = ResolveOwner(e);
				if(nsOwner != null && !string.IsNullOrEmpty(e.displayName)) {
					evt.menu.AppendSeparator();
					evt.menu.AppendAction("Remove", _ => SetListVisibility(nsOwner, e.displayName, null, false));
				}
			} else if(e.kind == FavoriteKind.Member && e.rawMember != null) {
				evt.menu.AppendAction("Create Node", _ => TryCreateNode(de));
				// Removing a generated member persists it in the type's mode list.
				var owner = ResolveOwner(e);
				var ownerMember = FavoritesManager.GetEntryMember(e);
				if(owner != null && ownerMember != null) {
					evt.menu.AppendSeparator();
					evt.menu.AppendAction("Remove", _ => SetListVisibility(owner, null, ownerMember, false));
				}
			}
		}

		// ═══════════════════════════════════════
		//  Selection / Detail Panel
		// ═══════════════════════════════════════

		void UpdateAddMembersButton() {
			if(addMembersButton == null) return;
			bool enabled = selectedEntry != null && !selectedEntry.isVirtualChild &&
				(selectedEntry.entry.kind == FavoriteKind.Type ||
				 selectedEntry.entry.kind == FavoriteKind.Namespace);
			addMembersButton.SetEnabled(enabled);
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

		struct SummaryLine {
			public Texture icon;
			public string text;

			public SummaryLine(Texture icon, string text) {
				this.icon = icon;
				this.text = text;
			}
		}

		/// <summary>
		/// Selection summary mirroring the ItemSelector/NodeBrowser tooltip contents
		/// (declaring type, assembly, target/static, return/value type, XML docs).
		/// </summary>
		List<SummaryLine> BuildSummaryLines(FavoritesDataAsset.FavoriteEntry e) {
			var lines = new List<SummaryLine>();

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
					Type t = ResolveTypeSafe(e);
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
					int count = e.children.Count;
					lines.Add(new SummaryLine(null, count + " items"));
					break;
				}
				case FavoriteKind.Node: {
					if(!string.IsNullOrEmpty(e.nodeMenuName))
						lines.Add(new SummaryLine(null, e.nodeMenuName));
					Type t = ResolveTypeSafe(e);
					if(t != null)
						AppendDocumentation(lines, t);
					break;
				}
			}
			return lines;
		}

		Type ResolveTypeSafe(FavoritesDataAsset.FavoriteEntry e) {
			try { return e.resolvedType; } catch { return null; }
		}

		string BuildEntryPath(FavoritesDataAsset.FavoriteEntry e) {
			var segments = new List<string>();
			var parent = e.parentEntry;
			int depth = 0;
			while(parent != null && depth < 16) {
				segments.Insert(0, GetDisplayName(parent));
				parent = parent.parentEntry;
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
			var parent = ResolveIntentParent(x => x.kind == FavoriteKind.Folder || x.kind == FavoriteKind.Namespace);
			ActionPopupWindow.Show(null, (ref object obj) => {
				EditorGUILayout.LabelField("New Folder", EditorStyles.boldLabel);
				folderName = EditorGUILayout.TextField("Name", folderName);
				if(GUILayout.Button("Create") && !string.IsNullOrWhiteSpace(folderName)) {
					FavoritesManager.AddEntry(CurrentCategory, parent, new FavoritesDataAsset.FavoriteEntry {
						kind = FavoriteKind.Folder,
						displayName = folderName.Trim(),
					});
					ReloadTreeView();
					ActionPopupWindow.CloseLast();
				}
			}).ChangePosition(this.GetMousePositionForMenu(mousePosition));
		}

		void AddNamespaceFavorite(Vector2 mousePosition) {
			string ns = "";
			var parent = ResolveIntentParent(x => x.kind == FavoriteKind.Folder);
			ActionPopupWindow.Show(null, (ref object obj) => {
				EditorGUILayout.LabelField("Add Namespace", EditorStyles.boldLabel);
				ns = EditorGUILayout.TextField("Namespace", ns);
				if(GUILayout.Button("Add") && !string.IsNullOrWhiteSpace(ns)) {
					FavoritesManager.AddEntry(CurrentCategory, parent, new FavoritesDataAsset.FavoriteEntry {
						kind = FavoriteKind.Namespace,
						displayName = ns.Trim(),
					});
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

		/// <summary>Picks go through here: ensure declaring type exists, then show member.</summary>
		void AddMemberDataAsFavorite(MemberData memberData) {
			if(memberData == null) return;

			bool isType = memberData.IsTargetingType
				|| memberData.targetType == MemberData.TargetType.Type
				|| memberData.targetType == MemberData.TargetType.uNodeType
				|| memberData.targetType == MemberData.TargetType.Values;

			var parent = ResolveIntentParent(x => x.kind == FavoriteKind.Folder || x.kind == FavoriteKind.Namespace);
			var container = parent != null ? parent.children : CurrentCategory?.roots;

			if(isType) {
				var type = memberData.startType ?? memberData.type ?? memberData.StartSerializedType.type;
				if(type == null || string.IsNullOrEmpty(type.FullName)) return;
				if(container != null && container.Any(x => x.kind != FavoriteKind.Member && x.typeName == type.FullName)) return;
				FavoritesManager.AddEntry(CurrentCategory, parent, new FavoritesDataAsset.FavoriteEntry {
					kind = FavoriteKind.Type,
					targetType = new SerializedType(type),
				});
			} else {
				var members = memberData.GetMembers(false);
				if(members == null || members.Length == 0) return;
				var last = members[members.Length - 1];
				// Keep generic methods open — GetComponent<T>() stays GetComponent<T>.
				// The graph editor prompts for type args only when used.
				if(last is MethodInfo genericMethod && genericMethod.IsGenericMethod && !genericMethod.IsGenericMethodDefinition)
					last = genericMethod.GetGenericMethodDefinition();
				var declType = last.DeclaringType ?? memberData.startType;
				if(declType == null) return;

				FavoritesDataAsset.FavoriteEntry typeHeader = null;
				bool created = false;
				if(container != null) {
					typeHeader = container.FirstOrDefault(x => x.kind == FavoriteKind.Type && x.typeName == declType.FullName);
					if(typeHeader == null) {
						typeHeader = FavoritesManager.AddEntry(CurrentCategory, parent, new FavoritesDataAsset.FavoriteEntry {
							kind = FavoriteKind.Type,
							targetType = new SerializedType(declType),
						});
						created = true;
					}
				}
				if(typeHeader == null) return;

				if(created) {
					// Brand-new type from a member pick: ExcludeAll so ONLY the picked
					// member shows; flip to Include All in the Members-of popup later.
					typeHeader.memberMode = TypeMemberMode.ExcludeAll;
					if(typeHeader.excludedMembers == null)
						typeHeader.excludedMembers = new List<string>();
					else
						typeHeader.excludedMembers.Clear();
					typeHeader.excludedMembers.Add(last.Name);
					FavoritesManager.Save();
					FavoritesManager.NotifyChanged();
				}
				else {
					SetMemberVisible(typeHeader, last, true);
				}
			}
			ReloadTreeView();
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

		void AutoSort(Comparison<FavoritesDataAsset.FavoriteEntry> comparer) {
			var parent = selectedEntry != null && selectedEntry.entry.CanHaveChilds ? selectedEntry.entry : null;
			FavoritesManager.SortChildren(parent, CurrentCategory, (a, b) => {
				int tc = a.kind.CompareTo(b.kind);
				return tc != 0 ? tc : comparer(a, b);
			});
			ReloadTreeView();
		}

		// ═══════════════════════════════════════
		//  Visibility (generated rows)
		// ═══════════════════════════════════════

		static bool IsMemberVisible(FavoritesDataAsset.FavoriteEntry owner, MemberInfo member) {
			if(owner == null || member == null)
				return false;
			bool inList = owner.excludedMembers != null && owner.excludedMembers.Contains(member.Name);
			return owner.memberMode == TypeMemberMode.ExcludeAll ? inList : !inList;
		}

		/// <summary>Show/hide a generated member (mode-aware write; persists).</summary>
		void SetMemberVisible(FavoritesDataAsset.FavoriteEntry owner, MemberInfo member, bool visible) {
			SetListVisibility(owner, null, member, visible);
		}

		/// <summary>Show/hide a generated virtual type under a namespace favorite.</summary>
		void SetTypeNameVisible(FavoritesDataAsset.FavoriteEntry nsEntry, string typeName, bool visible) {
			SetListVisibility(nsEntry, typeName, null, visible);
		}

		/// <summary>
		/// Mode-aware add/remove of a name in an entry's excludedMembers list.
		/// IncludeAll: listed = hidden. ExcludeAll: listed = visible. Persists.
		/// </summary>
		void SetListVisibility(FavoritesDataAsset.FavoriteEntry owner, string typeName, MemberInfo member, bool visible) {
			if(owner == null)
				return;
			string name = member != null ? member.Name : typeName;
			if(string.IsNullOrEmpty(name))
				return;
			if(owner.excludedMembers == null)
				owner.excludedMembers = new List<string>();
			bool shouldContain = owner.memberMode == TypeMemberMode.ExcludeAll ? visible : !visible;
			bool changed;
			if(shouldContain) {
				if(!owner.excludedMembers.Contains(name)) {
					owner.excludedMembers.Add(name);
					changed = true;
				}
				else {
					changed = false;
				}
			}
			else {
				changed = owner.excludedMembers.Remove(name);
			}
			if(changed) {
				FavoritesManager.Save();
				FavoritesManager.NotifyChanged();
			}
		}

		/// <summary>Owning favorite for a generated row; null for deep-search results.</summary>
		FavoritesDataAsset.FavoriteEntry ResolveOwner(FavoritesDataAsset.FavoriteEntry entry) {
			return entry?.ownerEntry;
		}

		void RemoveSelected() {
			if(selectedEntry == null || selectedEntry.entry == null)
				return;
			var e = selectedEntry.entry;
			// Generated members/virtual types hide via their owner's mode list.
			if(e.isVirtual) {
				var owner = ResolveOwner(e);
				if(owner != null) {
					if(e.kind == FavoriteKind.Member) {
						var mi = FavoritesManager.GetEntryMember(e);
						if(mi != null) {
							SetListVisibility(owner, null, mi, false);
						}
					}
					else if(e.kind == FavoriteKind.Type && !string.IsNullOrEmpty(e.displayName)) {
						SetListVisibility(owner, e.displayName, null, false);
					}
				}
				selectedEntry = null;
				UpdateDetailPanel();
				UpdateAddMembersButton();
				return; // NotifyChanged already reloaded the tree
			}
			FavoritesManager.Remove(e);
			selectedEntry = null;
			ReloadTreeView();
			UpdateDetailPanel();
			UpdateAddMembersButton();
		}

		// ═══════════════════════════════════════
		//  Popups
		// ═══════════════════════════════════════

		/// <summary>Shared popup scaffold: padding, Escape-close, header.</summary>
		VisualElement CreatePopupRoot(string title, float minWidth) {
			var root = new VisualElement();
			root.style.paddingTop = 8;
			root.style.paddingBottom = 8;
			root.style.paddingLeft = 10;
			root.style.paddingRight = 10;
			root.style.minWidth = minWidth;
			root.focusable = true;
			root.RegisterCallback<KeyDownEvent>(evt => {
				if(evt.keyCode == KeyCode.Escape) {
					ActionPopupWindow.CloseLast();
					evt.StopPropagation();
				}
			});
			root.Add(new Label(title) {
				style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 6 }
			});
			return root;
		}

		Button PopupButton(string text, Action onClick) => new Button(onClick) { text = text };

		void OpenAddMembersPopup(Vector2 mousePosition) {
			if(selectedEntry == null || selectedEntry.isVirtualChild) return;
			ActionPopupWindow.Show(() => {
				// Namespace favorites manage their generated TYPE list instead.
				if(selectedEntry.entry.kind == FavoriteKind.Namespace)
					return BuildVisibilityPopup(
						"Types of " + GetDisplayName(selectedEntry.entry),
						selectedEntry.entry, isNamespaceMode: true);
				if(selectedEntry.entry.kind != FavoriteKind.Type || selectedEntry.entry.isVirtual)
					return null;
				var type = ResolveTypeSafe(selectedEntry.entry);
				if(type == null) return null;

				var validMembers = EditorReflectionUtility.GetSortedMembers(type, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
					.Where(m => m is not MethodInfo || m.Name.StartsWith("get_") == false && m.Name.StartsWith("set_") == false).ToArray();
				if(validMembers.Length == 0) {
					EditorUtility.DisplayDialog("No Members", "This type has no public members.", "OK");
					return null;
				}
				return BuildVisibilityPopup(
					"Members of " + GetDisplayName(selectedEntry.entry),
					selectedEntry.entry, isNamespaceMode: false, members: validMembers);
			}).ChangePosition(this.GetMousePositionForMenu(mousePosition));
		}

		/// <summary>
		/// Shared include/exclude popup for a type's members or a namespace's types.
		/// Rows: [icon][toggle][rich label]; toolbar: mode switch + bulk ops.
		/// </summary>
		VisualElement BuildVisibilityPopup(string title,
			FavoritesDataAsset.FavoriteEntry owner, bool isNamespaceMode, MemberInfo[] members = null) {

			Func<string, bool> isVisible = isNamespaceMode
				? (Func<string, bool>)(name => FavoritesManager.IsTypeNameVisibleIn(owner, name))
				: name => {
					var m = Array.Find(members, mm => mm.Name == name);
					return m != null && IsMemberVisible(owner, m);
				};
			void SetVisible(string name, bool visible) {
				if(isNamespaceMode) {
					SetTypeNameVisible(owner, name, visible);
				}
				else {
					var m = Array.Find(members, mm => mm.Name == name);
					if(m != null) SetMemberVisible(owner, m, visible);
				}
			}

			var root = CreatePopupRoot(title, isNamespaceMode ? 400f : 440f);

			var toggles = new List<(Toggle toggle, string name)>();
			void RefreshToggles() {
				foreach(var t in toggles)
					t.toggle.SetValueWithoutNotify(isVisible(t.name));
			}

			// ── Toolbar: mode switch / Select All / Deselect All / spacer / Close ──
			var toolbarRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 6, flexWrap = Wrap.Wrap } };
			var includeBtn = PopupButton("Include All", null);
			var excludeBtn = PopupButton("Exclude All", null);
			void SetModeButtons(TypeMemberMode mode) {
				bool include = mode == TypeMemberMode.IncludeAll;
				includeBtn.SetEnabled(!include);
				excludeBtn.SetEnabled(include);
			}
			void SwitchMode(TypeMemberMode mode) {
				if(owner.memberMode == mode) return;
				owner.memberMode = mode;
				FavoritesManager.Save();
				FavoritesManager.NotifyChanged();
				SetModeButtons(mode);
				RefreshToggles();
			}
			includeBtn.clicked += () => SwitchMode(TypeMemberMode.IncludeAll);
			excludeBtn.clicked += () => SwitchMode(TypeMemberMode.ExcludeAll);
			toolbarRow.Add(includeBtn);
			toolbarRow.Add(excludeBtn);
			SetModeButtons(owner.memberMode);

			void SetAllVisible(bool visible) {
				foreach(var t in toggles)
					SetVisible(t.name, visible);
				RefreshToggles();
			}
			toolbarRow.Add(PopupButton("Select All", () => SetAllVisible(true)));
			toolbarRow.Add(PopupButton("Deselect All", () => SetAllVisible(false)));
			var spacer = new VisualElement { style = { flexGrow = 1 } };
			toolbarRow.Add(spacer);
			toolbarRow.Add(PopupButton("Close", () => ActionPopupWindow.CloseLast()));
			root.Add(toolbarRow);

			// ── Entries ──
			if(isNamespaceMode) {
				var candidates = FavoritesManager.GetVirtualNamespaceChildren(owner, true);
				var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { maxHeight = 340 } };
				foreach(var cand in candidates) {
					var row = CreateToggleRow(
						FavoritesManager.IsTypeNameVisibleIn(owner, cand.displayName),
						uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.FolderIcon)),
						cand.displayName,
						v => SetVisible(cand.displayName, v));
					scroll.Add(row.row);
					toggles.Add((row.toggle, cand.displayName));
				}
				root.Add(scroll);
			}
			else {
				var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { maxHeight = 360 } };
				foreach(var m in members) {
					Texture iconTex = null;
					try { iconTex = uNodeEditorUtility.GetIcon(m); } catch { }
					var row = CreateToggleRow(
						isVisible(m.Name),
						iconTex ?? GetMemberKindIcon(m),
						EditorReflectionUtility.GetRichMemberName(m),
						v => SetVisible(m.Name, v),
						richText: true);
					scroll.Add(row.row);
					toggles.Add((row.toggle, m.Name));
				}
				root.Add(scroll);
			}
			return root;
		}

		(Toggle toggle, VisualElement row) CreateToggleRow(bool value, Texture icon,
			string labelText, Action<bool> onToggle, bool richText = false) {
			var row = new VisualElement {
				style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 1, marginBottom = 1 }
			};
			var toggle = new Toggle { value = value };
			toggle.style.marginRight = 4;
			toggle.RegisterValueChangedCallback(evt => onToggle?.Invoke(evt.newValue));
			row.Add(toggle);
			var img = new Image { image = icon };
			img.style.width = 16;
			img.style.height = 16;
			img.style.flexShrink = 0;
			img.style.marginRight = 4;
			row.Add(img);
			var lbl = new Label(labelText) { enableRichText = richText };
			lbl.style.flexGrow = 1;
			row.Add(lbl);
			return (toggle, row);
		}

		/// <summary>Kind icon for a reflected member (method/property/field).</summary>
		Texture GetMemberKindIcon(MemberInfo member) {
			if(member is MethodInfo) return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.MethodIcon));
			if(member is PropertyInfo) return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.PropertyIcon));
			if(member is FieldInfo) return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.FieldIcon));
			return uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.ExtensionIcon));
		}

		bool IsNodeFavorited(NodeMenu menu) {
			if(menu == null) return false;
			var cat = CurrentCategory;
			if(cat == null) return false;
			return FavoritesManager.Flatten(cat).Any(x =>
				x.kind == FavoriteKind.Node && x.nodeMenuName == menu.name);
		}

		/// <summary>Add/remove a node favorite within the category under the given parent.</summary>
		void SetNodeFavorite(NodeMenu menu, FavoritesDataAsset.FavoriteEntry parent, bool visible) {
			if(menu == null)
				return;
			var cat = CurrentCategory;
			if(cat == null) return;
			var existing = FavoritesManager.Flatten(cat).Where(x =>
				x.kind == FavoriteKind.Node && x.nodeMenuName == menu.name).ToList();
			if(visible) {
				if(existing.Count > 0)
					return; // already favorited
				FavoritesManager.AddEntry(cat, parent, new FavoritesDataAsset.FavoriteEntry {
					kind = FavoriteKind.Node,
					nodeMenuName = menu.name,
					displayName = menu.name,
					// Store the node's type so the menu survives name changes.
					targetType = menu.type != null ? new SerializedType(menu.type) : null,
				});
			}
			else {
				foreach(var ex in existing)
					FavoritesManager.Remove(ex);
			}
		}

		void OpenAddNodePopup(Vector2 mousePosition) {
			var parent = ResolveIntentParent(x => x.kind == FavoriteKind.Folder || x.kind == FavoriteKind.Namespace);
			ActionPopupWindow.Show(() => BuildAddNodePopup(parent))
				.ChangePosition(this.GetMousePositionForMenu(mousePosition));
		}

		/// <summary>Row for the add-node TreeView popup.</summary>
		class NodeToggleRow : VisualElement {
			public Toggle toggle;
			public Image icon;
			public Label label;
			public Label categoryLabel;
			public Action<bool> onToggle;
		}

		/// <summary>Tree item for the add-node popup: a category group or a node leaf.</summary>
		class NodePickerItem {
			public bool isGroup;
			public string groupName;   // full category path (groups only)
			public NodeMenu menu;      // null for groups
		}

		class NodeGroupNode {
			public string fullPath;
			public string segment;
			public Dictionary<string, NodeGroupNode> dirs = new Dictionary<string, NodeGroupNode>(StringComparer.OrdinalIgnoreCase);
			public List<NodeMenu> menus = new List<NodeMenu>();
		}

		/// <summary>
		/// Builds the 'Add Nodes' popup content using a virtualized TreeView with a
		/// search field. Unfiltered view groups nodes by their '/'-separated
		/// categories; searching switches to a flat results list.
		/// </summary>
		VisualElement BuildAddNodePopup(FavoritesDataAsset.FavoriteEntry parent) {
			var root = CreatePopupRoot("Add Nodes", 440);

			var allNodes = nodeMenuCache.Values
				.OrderBy(m => m.category, StringComparer.OrdinalIgnoreCase)
				.ThenBy(m => m.name, StringComparer.OrdinalIgnoreCase)
				.ToList();

			TreeView typeTree = null;
			string filterText = "";
			bool flatMode = false;
			var items = new List<TreeViewItemData<NodePickerItem>>();
			var displayedMenus = new List<NodeMenu>(); // leaves currently shown (bulk ops target)
			var usedIDs = new HashSet<int>();

			bool MatchesFilter(NodeMenu m) {
				if(string.IsNullOrEmpty(filterText))
					return true;
				return (m.name != null && m.name.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0) ||
					(m.category != null && m.category.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0);
			}

			int ProbeID(string key) {
				int id = key.GetHashCode();
				while(!usedIDs.Add(id))
					id++;
				return id;
			}

			void AddLeaf(NodeMenu m, List<TreeViewItemData<NodePickerItem>> into) {
				int id = ProbeID("leaf:" + m.name + ":" + m.category);
				displayedMenus.Add(m);
				into.Add(new TreeViewItemData<NodePickerItem>(id, new NodePickerItem { menu = m }));
			}

			void EmitGroup(NodeGroupNode node, List<TreeViewItemData<NodePickerItem>> into) {
				int id = ProbeID("group:" + node.fullPath);
				var item = new TreeViewItemData<NodePickerItem>(
					id,
					new NodePickerItem { isGroup = true, groupName = node.fullPath },
					EmitChildren(node));
				into.Add(item);
			}

			List<TreeViewItemData<NodePickerItem>> EmitChildren(NodeGroupNode node) {
				var into = new List<TreeViewItemData<NodePickerItem>>();
				foreach(var dir in node.dirs.Values.OrderBy(d => d.segment, StringComparer.OrdinalIgnoreCase)) {
					EmitGroup(dir, into);
				}
				foreach(var m in node.menus.OrderBy(mm => mm.name, StringComparer.OrdinalIgnoreCase)) {
					AddLeaf(m, into);
				}
				return into;
			}

			void ApplyFilter() {
				items.Clear();
				displayedMenus.Clear();
				usedIDs.Clear();
				flatMode = !string.IsNullOrEmpty(filterText);

				if(flatMode) {
					foreach(var m in allNodes) {
						if(!MatchesFilter(m))
							continue;
						AddLeaf(m, items);
					}
				}
				else {
					// Respect the '/' separator: build a category tree.
					var rootNode = new NodeGroupNode();
					foreach(var m in allNodes) {
						var cat = string.IsNullOrEmpty(m.category) ? "Uncategorized" : m.category;
						var cur = rootNode;
						string path = string.Empty;
						foreach(var seg in cat.Split('/')) {
							path = string.IsNullOrEmpty(path) ? seg : path + "/" + seg;
							if(!cur.dirs.TryGetValue(seg, out var next)) {
								next = new NodeGroupNode { segment = seg, fullPath = path };
								cur.dirs[seg] = next;
							}
							cur = next;
						}
						cur.menus.Add(m);
					}
					items.AddRange(EmitChildren(rootNode));
				}

				typeTree?.SetRootItems(items);
				typeTree?.Rebuild();
				if(!flatMode)
					typeTree?.ExpandAll(); // category groups start expanded
			}

			void RefreshRows() {
				typeTree?.RefreshItems();
			}

			void SetAllVisible(bool visible) {
				foreach(var m in displayedMenus)
					SetNodeFavorite(m, parent, visible);
			}

			// ── Search ──
			var searchField = new ToolbarSearchField() { style = { flexGrow = 1, marginBottom = 6 } };
			searchField.RegisterValueChangedCallback(evt => {
				filterText = evt.newValue ?? string.Empty;
				ApplyFilter();
			});
			root.Add(searchField);

			// ── Toolbar ──
			var toolbarRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = 6 } };
			toolbarRow.Add(PopupButton("Select All", () => { SetAllVisible(true); RefreshRows(); }));
			toolbarRow.Add(PopupButton("Deselect All", () => { SetAllVisible(false); RefreshRows(); }));
			var toolbarSpacer = new VisualElement { style = { flexGrow = 1 } };
			toolbarRow.Add(toolbarSpacer);
			toolbarRow.Add(PopupButton("Close", () => ActionPopupWindow.CloseLast()));
			root.Add(toolbarRow);

			// ── Virtualized node list ──
			typeTree = new TreeView(
				makeItem: () => {
					var row = new NodeToggleRow {
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
					row.categoryLabel = new Label() {
						style = {
							color = new Color(.55f, .55f, .55f), fontSize = 9,
							unityTextAlign = TextAnchor.MiddleRight
						}
					};
					row.Add(row.categoryLabel);
					return row;
				},
				bindItem: (ve, index) => {
					if(!(ve is NodeToggleRow row))
						return;
					// index is the FLATTENED row index (groups + leaves), so resolve
					// through the tree itself rather than the root-only items list.
					var data = typeTree.GetItemDataForIndex<NodePickerItem>(index);
					if(data == null)
						return;

					if(data.isGroup) {
						// Category group row: folder icon + name, no toggle.
						row.toggle.SetValueWithoutNotify(false);
						row.onToggle = null;
						row.toggle.style.display = DisplayStyle.None;
						row.icon.image = uNodeEditorUtility.GetTypeIcon(typeof(TypeIcons.FolderIcon));
						row.label.text = data.groupName.Substring(data.groupName.LastIndexOf('/') + 1);
						row.categoryLabel.text = string.Empty;
						return;
					}

					var menu = data.menu;
					row.toggle.style.display = DisplayStyle.Flex;
					row.toggle.SetValueWithoutNotify(IsNodeFavorited(menu));
					row.onToggle = v => SetNodeFavorite(menu, parent, v);
					Texture iconTex = null;
					try {
						var iconType = menu.GetIcon();
						iconTex = iconType != null ? uNodeEditorUtility.GetTypeIcon(iconType) : null;
					}
					catch { }
					row.icon.image = iconTex;
					row.label.text = menu.name;
					// Category hint is useful only in the flat search list.
					row.categoryLabel.text = flatMode ? menu.category : string.Empty;
				}
			);
			typeTree.style.height = 360;
			typeTree.style.flexGrow = 1;
			typeTree.virtualizationMethod = CollectionVirtualizationMethod.FixedHeight;
			typeTree.fixedItemHeight = 20;
			typeTree.selectionType = SelectionType.None;
			typeTree.showAlternatingRowBackgrounds = AlternatingRowBackground.All;
			ApplyFilter();
			root.Add(typeTree);
			return root;
		}

		/// <summary>Double-click/context-menu entry point: validates then spawns the node.</summary>
		void TryCreateNode(DisplayEntry de) {
			if(de == null || de.entry == null || de.isPlaceholder) return;
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
		/// System.Type / MemberInfo / NodeMenu. Null = not graph-draggable.
		/// </summary>
		object GetGraphDragPayload(FavoritesDataAsset.FavoriteEntry e) {
			if(e == null)
				return null;
			switch(e.kind) {
				case FavoriteKind.Type:
					return ResolveTypeSafe(e);
				case FavoriteKind.Member: {
					MemberInfo mi = null;
					try { mi = FavoritesManager.GetEntryMember(e); } catch { }
					if(mi is Type || mi is FieldInfo || mi is PropertyInfo ||
						mi is MethodInfo || mi is ConstructorInfo)
						return mi;
					return null;
				}
				case FavoriteKind.Node:
					return ResolveNodeMenu(e);
				default:
					return null;
			}
		}

		/// <summary>Resolves the NodeMenu primarily by targetType (stable against
		/// registered-name changes), falling back to the menu name.</summary>
		NodeMenu ResolveNodeMenu(FavoritesDataAsset.FavoriteEntry e) {
			if(nodeMenuCache == null)
				return null;
			Type t = ResolveTypeSafe(e);
			if(t != null) {
				var byType = nodeMenuCache.Values.FirstOrDefault(m => m.type == t);
				if(byType != null)
					return byType;
			}
			if(!string.IsNullOrEmpty(e.nodeMenuName) && nodeMenuCache.TryGetValue(e.nodeMenuName, out var byName))
				return byName;
			return null;
		}

		Type ResolveEntryType(FavoritesDataAsset.FavoriteEntry e) {
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
				var menu = ResolveNodeMenu(e);
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
					FavoritesManager.Rename(e, newName);
					UpdateDetailPanel();
					ActionPopupWindow.CloseLast();
				}
			});
		}
	}
}
